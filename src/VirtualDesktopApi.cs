using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopSwitcher
{
    // =====================================================================
    //  COM interop for the undocumented Windows 10 virtual desktop API.
    //
    //  Every GUID and vtable layout below was verified against this machine
    //  (Windows 10 22H2, build 19045.7548) before being written down.
    //  19045 is the terminal Windows 10 build, so these are stable here.
    // =====================================================================

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    /// <summary>Opaque - we only ever pass these straight back into the shell.</summary>
    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationView
    {
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationViewCollection
    {
        // Only GetViewForHwnd is used; the earlier slots exist purely to keep the
        // vtable aligned and must not be reordered or removed.
        [PreserveSig] int GetViews(out IObjectArray array);
        [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
        [PreserveSig] int GetViewsByAppUserModelId(string id, out IObjectArray array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport, Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktop
    {
        void IsViewVisible(IApplicationView view, out int visible);
        Guid GetId();
    }

    /// <summary>Win10 build 19041+ layout. Verified.</summary>
    [ComImport, Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopId);
    }

    /// <summary>The public, documented interface. Used first for window moves.</summary>
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid id);
    }

    // =====================================================================

    /// <summary>
    /// The shell's virtual desktop objects cannot be obtained right now - almost always
    /// because Explorer is mid-restart and has not re-registered its COM classes yet.
    ///
    /// This is expected and transient. Callers should keep their last known state and
    /// try again on the next tick rather than treating it as a hard error.
    /// </summary>
    sealed class ShellUnavailableException : Exception
    {
        public ShellUnavailableException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Low-level virtual desktop operations. No policy, no caching of desktop state,
    /// no UI concerns - DesktopService (M4) owns all of that.
    ///
    /// Two rules drive the design:
    ///   1. IVirtualDesktop pointers are never held across calls. They go stale the
    ///      moment a desktop is added or removed, so every operation re-resolves from
    ///      a Guid.
    ///   2. An Explorer restart invalidates the shell service objects. Every call runs
    ///      through Do(), which drops the cached objects and retries once on COM failure.
    /// </summary>
    sealed class VirtualDesktopApi
    {
        static readonly Guid CLSID_ImmersiveShell =
            new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid CLSID_VirtualDesktopManagerInternal =
            new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        static readonly Guid CLSID_VirtualDesktopManager =
            new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");
        static readonly Guid SVC_ApplicationViewCollection =
            new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

        static Guid _iidManagerInternal = new Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6");
        static Guid _iidViewCollection  = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
        static Guid _iidVirtualDesktop  = new Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4");

        const string DesktopsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

        IServiceProvider10 _shell;
        IVirtualDesktopManagerInternal _manager;
        IApplicationViewCollection _views;
        IVirtualDesktopManager _public;

        // --- lifetime ---------------------------------------------------------

        /// <summary>Exposed so the notification sink (M3) can share the shell provider.</summary>
        public IServiceProvider10 Shell
        {
            get { EnsureAcquired(); return _shell; }
        }

        /// <summary>
        /// Acquires every shell object into locals and only publishes them to fields once
        /// all four have succeeded. A partial acquisition would leave _manager non-null,
        /// causing later calls to skip re-acquisition and stay permanently wedged.
        /// </summary>
        /// <exception cref="ShellUnavailableException">Explorer is not serving COM yet.</exception>
        void EnsureAcquired()
        {
            if (_manager != null) return;

            try
            {
                var shell = (IServiceProvider10)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(CLSID_ImmersiveShell));

                Guid clsidInternal = CLSID_VirtualDesktopManagerInternal;
                var manager = (IVirtualDesktopManagerInternal)
                    shell.QueryService(ref clsidInternal, ref _iidManagerInternal);

                Guid svcViews = SVC_ApplicationViewCollection;
                var views = (IApplicationViewCollection)
                    shell.QueryService(ref svcViews, ref _iidViewCollection);

                var pub = (IVirtualDesktopManager)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager));

                _shell = shell;
                _manager = manager;
                _views = views;
                _public = pub;

                Log.Write("com: acquired shell interfaces");
            }
            catch (Exception ex)
            {
                Drop();
                Log.Write("com: acquisition failed - " + ex.Message);
                throw new ShellUnavailableException(
                    "virtual desktop shell interfaces are unavailable", ex);
            }
        }

        /// <summary>
        /// Fetches a fresh IApplicationViewCollection. This interface is thread-affine
        /// and short-lived, so it is never cached across calls.
        /// </summary>
        IApplicationViewCollection QueryViewCollection()
        {
            Guid svcViews = SVC_ApplicationViewCollection;
            return (IApplicationViewCollection)
                _shell.QueryService(ref svcViews, ref _iidViewCollection);
        }

        /// <summary>Forget the cached shell objects; the next call re-acquires.</summary>
        public void Drop()
        {
            _shell = null;
            _manager = null;
            _views = null;
            _public = null;
        }

        static bool IsComFailure(Exception ex)
        {
            return ex is COMException
                || ex is InvalidCastException
                || ex is NullReferenceException
                || ex is InvalidComObjectException;
        }

        delegate T Op<T>();

        /// <summary>
        /// Runs an operation, re-acquiring and retrying once if the shell objects have
        /// gone stale. Operations must read the _manager/_views fields directly rather
        /// than capturing them, so the retry picks up the fresh objects.
        /// </summary>
        T Do<T>(Op<T> op, string what)
        {
            EnsureAcquired();
            try
            {
                return op();
            }
            catch (Exception ex)
            {
                if (!IsComFailure(ex)) throw;

                Log.Write("com: " + what + " failed (" + ex.GetType().Name + ": "
                          + ex.Message + ") - reacquiring");
                Drop();
            }

            // Second attempt. EnsureAcquired may throw ShellUnavailableException if
            // Explorer has not come back yet - that is transient and callers are
            // expected to keep their last known state and retry on the next tick.
            EnsureAcquired();
            try
            {
                T result = op();
                Log.Write("com: recovered - " + what + " succeeded after reacquire");
                return result;
            }
            catch (Exception ex)
            {
                if (!IsComFailure(ex)) throw;

                Drop();
                throw new ShellUnavailableException(what + " failed after reacquire", ex);
            }
        }

        void Do(Action op, string what)
        {
            Do<object>(delegate { op(); return null; }, what);
        }

        // --- queries ----------------------------------------------------------

        public int GetCount()
        {
            return Do(delegate { return _manager.GetCount(); }, "GetCount");
        }

        public Guid GetCurrentId()
        {
            return Do(delegate { return _manager.GetCurrentDesktop().GetId(); }, "GetCurrentDesktop");
        }

        public Guid[] GetDesktopIds()
        {
            return Do(delegate
            {
                IObjectArray array;
                _manager.GetDesktops(out array);

                int count;
                array.GetCount(out count);

                var ids = new Guid[count];
                for (int i = 0; i < count; i++)
                {
                    object raw;
                    array.GetAt(i, ref _iidVirtualDesktop, out raw);
                    ids[i] = ((IVirtualDesktop)raw).GetId();
                }
                return ids;
            }, "GetDesktops");
        }

        /// <summary>
        /// The desktop a window lives on, or false if it has none.
        ///
        /// Windows on other desktops stay enumerable, so this is what turns a flat
        /// EnumWindows sweep into a per-desktop inventory. A false result is the normal
        /// answer for shell ghosts - suspended UWP frames and the like - which report
        /// GUID_NULL or fail outright, and is how they are told apart from real windows
        /// that merely happen to be on another desktop.
        /// </summary>
        public bool TryGetWindowDesktop(IntPtr hwnd, out Guid id)
        {
            Guid found = Guid.Empty;
            bool ok = Do(delegate
            {
                Guid result;
                int hr = _public.GetWindowDesktopId(hwnd, out result);
                if (hr < 0 || result == Guid.Empty) return false;
                found = result;
                return true;
            }, "GetWindowDesktopId");

            id = found;
            return ok;
        }

        // --- mutations --------------------------------------------------------

        public void SwitchTo(Guid id)
        {
            Do(delegate
            {
                Guid target = id;
                IVirtualDesktop desktop = _manager.FindDesktop(ref target);
                if (desktop == null)
                {
                    Log.Write("switch: desktop not found, ignoring");
                    return;
                }
                _manager.SwitchDesktop(desktop);
            }, "SwitchDesktop");

            NudgeFocus();
        }

        public Guid Create()
        {
            Guid id = Do(delegate { return _manager.CreateDesktop().GetId(); }, "CreateDesktop");
            Log.Write(delegate { return "desktop created " + id; });
            return id;
        }

        /// <summary>
        /// Removes a desktop, handing its windows to the neighbour - the same fallback
        /// Windows itself picks. Refuses to remove the last remaining desktop.
        /// </summary>
        public bool Remove(Guid id)
        {
            return Do(delegate
            {
                Guid[] ids = GetDesktopIdsUnwrapped();
                if (ids.Length <= 1)
                {
                    Log.Write("remove: refused, only one desktop remains");
                    return false;
                }

                int index = Array.IndexOf(ids, id);
                if (index < 0)
                {
                    Log.Write("remove: desktop not found, ignoring");
                    return false;
                }

                Guid fallbackId = index > 0 ? ids[index - 1] : ids[1];

                Guid target = id;
                Guid fallback = fallbackId;
                IVirtualDesktop toRemove = _manager.FindDesktop(ref target);
                IVirtualDesktop toKeep = _manager.FindDesktop(ref fallback);
                if (toRemove == null || toKeep == null) return false;

                _manager.RemoveDesktop(toRemove, toKeep);
                Log.Write(delegate { return "desktop removed " + id + ", fallback " + fallbackId; });
                return true;
            }, "RemoveDesktop");
        }

        /// <summary>
        /// Moves a window to a desktop. Prefers the documented API (no vtable risk),
        /// falling back to the internal one for windows it refuses.
        /// </summary>
        public bool MoveWindow(IntPtr hwnd, Guid id)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

            return Do(delegate
            {
                Guid target = id;
                int hr = _public.MoveWindowToDesktop(hwnd, ref target);
                if (hr >= 0) return true;

                Log.Write(delegate
                {
                    return string.Format("move: public API refused (hr=0x{0:X8}), using view collection", hr);
                });

                // Queried fresh rather than reused. A cached IApplicationViewCollection
                // goes stale and GetViewForHwnd then fails with TYPE_E_ELEMENTNOTFOUND
                // (0x8002802B) even for perfectly ordinary top-level windows.
                IApplicationViewCollection views = QueryViewCollection();

                IApplicationView view;
                int hr2 = views.GetViewForHwnd(hwnd, out view);
                if (hr2 < 0 || view == null)
                {
                    Log.Write(delegate { return string.Format("move: GetViewForHwnd failed hr=0x{0:X8}", hr2); });
                    return false;
                }

                Guid target2 = id;
                IVirtualDesktop desktop = _manager.FindDesktop(ref target2);
                if (desktop == null) return false;

                _manager.MoveViewToDesktop(view, desktop);
                return true;
            }, "MoveWindow");
        }

        // Internal helper used inside an existing Do() - must not re-enter Do().
        Guid[] GetDesktopIdsUnwrapped()
        {
            IObjectArray array;
            _manager.GetDesktops(out array);

            int count;
            array.GetCount(out count);

            var ids = new Guid[count];
            for (int i = 0; i < count; i++)
            {
                object raw;
                array.GetAt(i, ref _iidVirtualDesktop, out raw);
                ids[i] = ((IVirtualDesktop)raw).GetId();
            }
            return ids;
        }

        // --- names ------------------------------------------------------------

        /// <summary>
        /// User-assigned desktop name, or null. Read on demand only - never polled.
        /// Windows stores these per-GUID under the Explorer key.
        /// </summary>
        public static string GetName(Guid id)
        {
            try
            {
                string key = DesktopsRegistryPath + "\\" + id.ToString("B").ToUpperInvariant();
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(key))
                {
                    if (rk == null) return null;
                    string name = rk.GetValue("Name") as string;
                    return string.IsNullOrEmpty(name) ? null : name;
                }
            }
            catch (Exception ex)
            {
                Log.Write("name lookup failed: " + ex.Message);
                return null;
            }
        }

        // --- focus ------------------------------------------------------------

        /// <summary>
        /// SwitchDesktop does not reliably move keyboard focus, which leaves the new
        /// desktop with no active window. Activate the topmost real window there.
        /// </summary>
        void NudgeFocus()
        {
            try
            {
                IntPtr hwnd = Native.GetTopWindow(IntPtr.Zero);
                while (hwnd != IntPtr.Zero)
                {
                    if (Native.IsAltTabWindow(hwnd) && IsOnCurrentDesktop(hwnd))
                    {
                        Native.ForceForeground(hwnd);
                        return;
                    }
                    hwnd = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
                }
            }
            catch (Exception ex)
            {
                Log.Write("focus nudge failed: " + ex.Message);
            }
        }

        public bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            try
            {
                int onCurrent;
                int hr = _public.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent);
                return hr >= 0 && onCurrent != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
