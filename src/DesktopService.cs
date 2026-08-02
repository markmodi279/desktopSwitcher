using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DesktopSwitcher
{
    /// <summary>
    /// The only type the UI talks to. Owns the authoritative desktop model and hides
    /// every COM detail behind it.
    ///
    /// State arrives from two sources and this class merges them:
    ///   - shell notifications, the primary path, delivering changes immediately but
    ///     on arbitrary RPC threads
    ///   - a slow reconcile tick, the safety net, covering a dead sink, missed events
    ///     and Explorer restarts
    ///
    /// Everything is marshalled onto the UI thread before the model is touched, so the
    /// model and both events are single-threaded from a subscriber's point of view.
    /// </summary>
    sealed class DesktopService : IDisposable
    {
        readonly VirtualDesktopApi _api;
        readonly VirtualDesktopNotify _notify;
        readonly ISynchronizeInvoke _marshaller;

        List<Desktop> _desktops = new List<Desktop>();
        Guid _currentId = Guid.Empty;
        bool _disposed;

        public DesktopService(VirtualDesktopApi api, ISynchronizeInvoke marshaller)
        {
            _api = api;
            _marshaller = marshaller;
            _notify = new VirtualDesktopNotify(api);

            _notify.DesktopCreated += OnDesktopCreated;
            _notify.DesktopDestroyed += OnDesktopDestroyed;
            _notify.CurrentChanged += OnCurrentChanged;
        }

        /// <summary>The desktop set changed - rebuild buttons and reposition.</summary>
        public event EventHandler DesktopsChanged;

        /// <summary>Only the active desktop changed - repaint, no relayout.</summary>
        public event EventHandler CurrentChanged;

        /// <summary>Current model. Only valid on the UI thread.</summary>
        public IList<Desktop> Desktops { get { return _desktops; } }

        public int Count { get { return _desktops.Count; } }

        public Desktop Current
        {
            get
            {
                for (int i = 0; i < _desktops.Count; i++)
                    if (_desktops[i].IsCurrent) return _desktops[i];
                return null;
            }
        }

        /// <summary>True when notifications are live; false means we are running on the tick alone.</summary>
        public bool NotificationsActive { get { return _notify.IsRegistered; } }

        // --- lifecycle --------------------------------------------------------

        /// <summary>
        /// Test hook: when true the notification sink is never registered, forcing the
        /// reconcile tick to drive everything. Used to prove the fallback path works
        /// rather than assuming it.
        /// </summary>
        public bool DisableNotifications { get; set; }

        public void Start()
        {
            Reconcile();
            if (!DisableNotifications) _notify.Register();
        }

        /// <summary>
        /// Watchdog tick. Re-registers a dead sink and reconciles the model against
        /// reality. Cheap enough to run every couple of seconds.
        /// </summary>
        public void Tick()
        {
            if (!_notify.IsRegistered && !DisableNotifications)
            {
                if (_notify.Register())
                    Log.Write("service: notification sink re-registered");
            }

            Reconcile();
        }

        /// <summary>Drops the sink so the next Tick rebuilds it. Used after Explorer restarts.</summary>
        public void InvalidateNotifications()
        {
            _notify.Unregister();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _notify.Dispose();
        }

        // --- reconciliation ---------------------------------------------------

        /// <summary>
        /// Compares the cached model against the shell and corrects any divergence.
        /// While Explorer is restarting the shell is unavailable; the last known model
        /// is kept rather than blanking the UI.
        /// </summary>
        public void Reconcile()
        {
            Guid[] ids;
            Guid current;
            try
            {
                ids = _api.GetDesktopIds();
                current = _api.GetCurrentId();
            }
            catch (ShellUnavailableException)
            {
                Log.Write("service: shell unavailable, holding last known model");
                return;
            }
            catch (Exception ex)
            {
                Log.Write("service: reconcile failed - " + ex.Message);
                return;
            }

            bool setChanged = !SameIds(ids, _desktops);
            bool currentChanged = current != _currentId;
            if (!setChanged && !currentChanged) return;

            if (Log.Enabled)
            {
                if (setChanged)
                    Log.Write("service: set changed, " + _desktops.Count + " -> " + ids.Length);
                else
                    Log.Write(delegate
                    {
                        return "service: current changed to " + current.ToString().Substring(0, 8);
                    });
            }

            Rebuild(ids, current);

            // A set change implies a possible current change too, but subscribers treat
            // DesktopsChanged as "relayout and repaint", so one event is enough.
            if (setChanged) Raise(DesktopsChanged);
            else Raise(CurrentChanged);
        }

        void Rebuild(Guid[] ids, Guid current)
        {
            var list = new List<Desktop>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                // Names are read here and only here - on set changes, never on a timer.
                string name = VirtualDesktopApi.GetName(ids[i]);
                list.Add(new Desktop(ids[i], i, name, ids[i] == current));
            }

            _desktops = list;
            _currentId = current;
        }

        static bool SameIds(Guid[] ids, List<Desktop> desktops)
        {
            if (ids.Length != desktops.Count) return false;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] != desktops[i].Id) return false;
            return true;
        }

        // --- notification handlers (RPC threads) -------------------------------

        void OnDesktopCreated(Guid id)
        {
            Post(Reconcile);
        }

        void OnDesktopDestroyed(Guid destroyed, Guid fallback)
        {
            Post(Reconcile);
        }

        void OnCurrentChanged(Guid from, Guid to)
        {
            Post(delegate { ApplyCurrent(to); });
        }

        /// <summary>
        /// Fast path for a switch: the set is unchanged, so swap the IsCurrent flags
        /// without a COM round trip. Falls back to a full reconcile if the target is
        /// not in the model, which means the set moved too.
        /// </summary>
        void ApplyCurrent(Guid id)
        {
            if (id == _currentId) return;

            int index = -1;
            for (int i = 0; i < _desktops.Count; i++)
                if (_desktops[i].Id == id) { index = i; break; }

            if (index < 0)
            {
                Reconcile();
                return;
            }

            var list = new List<Desktop>(_desktops.Count);
            for (int i = 0; i < _desktops.Count; i++)
            {
                Desktop d = _desktops[i];
                list.Add(new Desktop(d.Id, d.Index, d.Name, i == index));
            }

            _desktops = list;
            _currentId = id;
            Raise(CurrentChanged);
        }

        // --- operations -------------------------------------------------------

        public void SwitchTo(Guid id)
        {
            Guarded(delegate { _api.SwitchTo(id); }, "switch");
        }

        public void Create()
        {
            Guarded(delegate { _api.Create(); }, "create");
        }

        public void Remove(Guid id)
        {
            Guarded(delegate { _api.Remove(id); }, "remove");
        }

        public void MoveWindow(IntPtr hwnd, Guid id)
        {
            Guarded(delegate { _api.MoveWindow(hwnd, id); }, "move window");
        }

        /// <summary>
        /// Creates desktops until at least <paramref name="target"/> exist. Windows
        /// forgets the desktop count across a reboot, so this restores it at login.
        /// </summary>
        public void EnsureCount(int target)
        {
            Guarded(delegate
            {
                int count = _api.GetCount();
                if (count >= target) return;

                Log.Write("service: restoring desktop count " + count + " -> " + target);
                while (count < target)
                {
                    _api.Create();
                    count++;
                }
            }, "ensure count");

            Reconcile();
        }

        /// <summary>
        /// Operations run on the UI thread in response to a click, so a transient COM
        /// failure must not propagate into the message loop and kill the process.
        /// </summary>
        void Guarded(Action action, string what)
        {
            try
            {
                action();
            }
            catch (ShellUnavailableException)
            {
                Log.Write("service: " + what + " skipped, shell unavailable");
            }
            catch (Exception ex)
            {
                Log.Write("service: " + what + " failed - " + ex.Message);
            }
        }

        // --- plumbing ---------------------------------------------------------

        /// <summary>
        /// Moves work onto the UI thread. Notification callbacks arrive on RPC threads,
        /// so every path that touches the model goes through here.
        /// </summary>
        void Post(Action action)
        {
            ISynchronizeInvoke inv = _marshaller;
            if (inv == null || !inv.InvokeRequired)
            {
                action();
                return;
            }

            try
            {
                inv.BeginInvoke(action, null);
            }
            catch (Exception ex)
            {
                // Handle not created yet, or being torn down. Losing the event is fine:
                // the reconcile tick picks the change up shortly.
                Log.Write("service: marshal failed - " + ex.Message);
            }
        }

        void Raise(EventHandler handler)
        {
            if (handler == null) return;
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("service: event handler threw - " + ex.Message);
            }
        }
    }
}
