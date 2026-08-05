using System;
using System.Runtime.InteropServices;

namespace DesktopSwitcher
{
    /// <summary>Win10 19041+ layout. Verified on 19045.7548 - do not reorder.</summary>
    [ComImport, Guid("C179334C-4295-40D3-BEA1-C654D965605A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopNotification
    {
        void VirtualDesktopCreated(IVirtualDesktop desktop);
        void VirtualDesktopDestroyBegin(IVirtualDesktop destroyed, IVirtualDesktop fallback);
        void VirtualDesktopDestroyFailed(IVirtualDesktop destroyed, IVirtualDesktop fallback);
        void VirtualDesktopDestroyed(IVirtualDesktop destroyed, IVirtualDesktop fallback);
        void ViewVirtualDesktopChanged(IApplicationView view);
        void CurrentVirtualDesktopChanged(IVirtualDesktop oldDesktop, IVirtualDesktop newDesktop);
    }

    [ComImport, Guid("0CD45E71-D927-4F15-8B0A-8FEF525337BF"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopNotificationService
    {
        int Register(IVirtualDesktopNotification sink);
        void Unregister(int cookie);
    }

    /// <summary>
    /// Subscribes to shell virtual desktop change notifications.
    ///
    /// THREADING CONTRACT: every event below is raised on an arbitrary COM RPC thread,
    /// never the UI thread. Subscribers must marshal before touching UI or shared state.
    /// DesktopService is the only intended subscriber and does exactly that.
    ///
    /// The shell hands IVirtualDesktop pointers to the callbacks; their Guid is read
    /// immediately and the pointer is dropped, because those objects do not stay valid.
    /// </summary>
    sealed class VirtualDesktopNotify : IDisposable
    {
        static readonly Guid SVC_Notification =
            new Guid("A501FDEC-4A09-464C-AE4E-1B9C21B84918");
        static Guid _iidNotificationService =
            new Guid("0CD45E71-D927-4F15-8B0A-8FEF525337BF");

        readonly VirtualDesktopApi _api;

        // Rooted so the CLR does not collect the object the shell holds a pointer to.
        Sink _sink;
        IVirtualDesktopNotificationService _service;
        int _cookie;

        public VirtualDesktopNotify(VirtualDesktopApi api)
        {
            _api = api;
        }

        /// <summary>A desktop was created. Argument is its id.</summary>
        public event Action<Guid> DesktopCreated;

        /// <summary>A desktop was destroyed. Arguments are (destroyed, fallback).</summary>
        public event Action<Guid, Guid> DesktopDestroyed;

        /// <summary>The active desktop changed. Arguments are (from, to).</summary>
        public event Action<Guid, Guid> CurrentChanged;

        public bool IsRegistered { get { return _cookie != 0; } }

        /// <summary>
        /// Registers the sink. Safe to call repeatedly - a live registration is kept.
        /// Returns false if the shell is not serving notifications yet, which happens
        /// while Explorer restarts; the caller retries on its next watchdog tick.
        /// </summary>
        public bool Register()
        {
            if (IsRegistered) return true;

            try
            {
                Guid svc = SVC_Notification;
                _service = (IVirtualDesktopNotificationService)
                    _api.Shell.QueryService(ref svc, ref _iidNotificationService);

                _sink = new Sink(this);
                _cookie = _service.Register(_sink);

                if (_cookie == 0)
                {
                    Log.Write("notify: Register returned cookie 0 - treating as failure");
                    _service = null;
                    _sink = null;
                    return false;
                }

                Log.Write(delegate { return "notify: registered, cookie=" + _cookie; });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("notify: registration failed - " + ex.Message);
                _service = null;
                _sink = null;
                _cookie = 0;
                return false;
            }
        }

        /// <summary>
        /// Drops the registration. Errors are swallowed: after an Explorer restart the
        /// old cookie is already meaningless and Unregister is expected to fail.
        /// </summary>
        public void Unregister()
        {
            if (_cookie == 0)
            {
                _service = null;
                _sink = null;
                return;
            }

            int cookie = _cookie;
            _cookie = 0;

            try
            {
                if (_service != null) _service.Unregister(cookie);
                Log.Write(delegate { return "notify: unregistered cookie=" + cookie; });
            }
            catch (Exception ex)
            {
                Log.Write("notify: unregister failed (expected after Explorer restart) - " + ex.Message);
            }
            finally
            {
                _service = null;
                _sink = null;
            }
        }

        /// <summary>Forces a fresh registration, e.g. after COM objects were re-acquired.</summary>
        public bool Reregister()
        {
            Unregister();
            return Register();
        }

        public void Dispose()
        {
            Unregister();
        }

        // --- raising ----------------------------------------------------------
        //
        // A callback that throws would propagate into the shell's RPC call. Every
        // handler invocation is therefore guarded.

        void RaiseCreated(Guid id)
        {
            Log.Write(delegate { return "notify: created " + Short(id); });
            Action<Guid> h = DesktopCreated;
            if (h == null) return;
            try { h(id); }
            catch (Exception ex) { Log.Write("notify: DesktopCreated handler threw - " + ex.Message); }
        }

        void RaiseDestroyed(Guid destroyed, Guid fallback)
        {
            Log.Write(delegate { return "notify: destroyed " + Short(destroyed) + " -> " + Short(fallback); });
            Action<Guid, Guid> h = DesktopDestroyed;
            if (h == null) return;
            try { h(destroyed, fallback); }
            catch (Exception ex) { Log.Write("notify: DesktopDestroyed handler threw - " + ex.Message); }
        }

        void RaiseCurrentChanged(Guid from, Guid to)
        {
            Log.Write(delegate { return "notify: current " + Short(from) + " -> " + Short(to); });
            Action<Guid, Guid> h = CurrentChanged;
            if (h == null) return;
            try { h(from, to); }
            catch (Exception ex) { Log.Write("notify: CurrentChanged handler threw - " + ex.Message); }
        }

        static string Short(Guid id)
        {
            return id == Guid.Empty ? "(none)" : id.ToString().Substring(0, 8);
        }

        /// <summary>Reads the id straight out, never retaining the shell's object.</summary>
        static Guid IdOf(IVirtualDesktop desktop)
        {
            if (desktop == null) return Guid.Empty;
            try { return desktop.GetId(); }
            catch { return Guid.Empty; }
        }

        // --- the COM-visible sink ---------------------------------------------

        sealed class Sink : IVirtualDesktopNotification
        {
            readonly VirtualDesktopNotify _owner;

            public Sink(VirtualDesktopNotify owner)
            {
                _owner = owner;
            }

            public void VirtualDesktopCreated(IVirtualDesktop desktop)
            {
                _owner.RaiseCreated(IdOf(desktop));
            }

            public void VirtualDesktopDestroyBegin(IVirtualDesktop destroyed, IVirtualDesktop fallback)
            {
                // Windows raises Begin before the desktop actually goes away. The set is
                // still stale at this point, so the removal is reported from Destroyed.
                Log.Write(delegate { return "notify: destroyBegin " + Short(IdOf(destroyed)); });
            }

            public void VirtualDesktopDestroyFailed(IVirtualDesktop destroyed, IVirtualDesktop fallback)
            {
                Log.Write(delegate { return "notify: destroyFAILED " + Short(IdOf(destroyed)); });
            }

            public void VirtualDesktopDestroyed(IVirtualDesktop destroyed, IVirtualDesktop fallback)
            {
                _owner.RaiseDestroyed(IdOf(destroyed), IdOf(fallback));
            }

            public void ViewVirtualDesktopChanged(IApplicationView view)
            {
                // Fires when a window moves between desktops. Not needed for the strip:
                // it changes neither the desktop set nor which one is active.
            }

            public void CurrentVirtualDesktopChanged(IVirtualDesktop oldDesktop, IVirtualDesktop newDesktop)
            {
                _owner.RaiseCurrentChanged(IdOf(oldDesktop), IdOf(newDesktop));
            }
        }
    }
}
