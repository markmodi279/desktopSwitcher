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
            _notify.ViewChanged += OnViewChanged;
        }

        /// <summary>The desktop set changed - rebuild buttons and reposition.</summary>
        public event EventHandler DesktopsChanged;

        /// <summary>Only the active desktop changed - repaint, no relayout.</summary>
        public event EventHandler CurrentChanged;

        /// <summary>A desktop was renamed - repaint, no relayout. The set is unchanged.</summary>
        public event EventHandler NamesChanged;

        /// <summary>
        /// A window moved between desktops. The model is unchanged; this is for whoever
        /// tracks what is on each desktop. Raised on the UI thread like the rest.
        /// </summary>
        public event EventHandler WindowsMoved;

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
        /// Longest the model goes unchecked while the notification sink is live.
        ///
        /// The reconcile ran on every tick, every two seconds, and it is not free: listing the
        /// desktops and asking which is current is a handful of cross-process calls into
        /// Explorer, about a dozen context switches each time, which made it the largest thing
        /// the process did while idle. And it never found anything. Six weeks of diagnostics
        /// log - 138 starts - hold 36 corrections to the current desktop and 558 to the set,
        /// and every one of them had the matching notification within 150 ms of it, often just
        /// after: the tick had landed between Explorer switching and its notification arriving,
        /// and applied the same answer a few milliseconds early. Not one missed notification.
        ///
        /// So with the sink live this is a backstop and runs every thirty seconds; with it
        /// down - not registered yet, or lost to an Explorer restart - every tick reconciles,
        /// as before, since then nothing else is keeping the model current.
        /// </summary>
        public const int BackstopMs = 30000;

        readonly System.Diagnostics.Stopwatch _sinceReconcile = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Watchdog tick. Re-registers a dead sink and reconciles the model against
        /// reality - every time while the sink is down, and every BackstopMs while it is up.
        /// </summary>
        public void Tick()
        {
            // A sink registered just now heard nothing from before it existed - an Explorer
            // restart drops the old one and calls straight in here - so this tick reconciles
            // whatever the clock says. Skipping on the clock alone would leave the model
            // unchecked after a restart for up to BackstopMs.
            bool fresh = false;
            if (!_notify.IsRegistered && !DisableNotifications)
            {
                if (_notify.Register())
                {
                    Log.Write("service: notification sink re-registered");
                    fresh = true;
                }
            }

            if (!fresh && _notify.IsRegistered && _sinceReconcile.IsRunning
                && _sinceReconcile.ElapsedMilliseconds < BackstopMs)
                return;

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

            // Only a reconcile that reached the shell counts: one that failed above leaves
            // the clock where it was, so the next tick tries again rather than in 30 s.
            _sinceReconcile.Restart();

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

        void OnViewChanged()
        {
            Post(delegate { Raise(WindowsMoved); });
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

        /// <summary>
        /// Swaps one desktop's name in the model, for the same reason and in the same way
        /// as ApplyCurrent: the set has not moved and the new value is already known, so
        /// there is nothing to ask the shell.
        ///
        /// Deliberately not a re-read. Names live in the registry and SetName is an RPC
        /// into Explorer, so a read that lands before the write is visible would put the
        /// old name straight back over the new one.
        /// </summary>
        void ApplyName(Guid id, string name)
        {
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
                list.Add(new Desktop(d.Id, d.Index, i == index ? name : d.Name, d.IsCurrent));
            }

            _desktops = list;
            Raise(NamesChanged);
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

        /// <summary>Whether the shell will accept a rename. False also means "not right now".</summary>
        public bool CanRename { get { return _api.CanRename; } }

        /// <summary>
        /// Renames a desktop, correcting the model as it goes.
        ///
        /// Nothing re-reads names between set changes, so without the second half of this
        /// the hover panel and the menu would go on saying the old name until the next
        /// desktop switch - which is to say, a rename you cannot see.
        /// </summary>
        public void Rename(Guid id, string name)
        {
            bool renamed = false;
            Guarded(delegate { renamed = _api.SetName(id, name); }, "rename");

            // Only a rename the shell actually took, or the model would start disagreeing
            // with the registry every subsequent reconcile puts back.
            if (renamed) ApplyName(id, name);
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

            // InvokeRequired is only as truthful as the marshaller's window: with no handle
            // it answers false from every thread, and this quietly becomes a direct call on
            // the RPC thread. Controller creates its handle up front for exactly that
            // reason - see the comment there before changing either end of this.
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
