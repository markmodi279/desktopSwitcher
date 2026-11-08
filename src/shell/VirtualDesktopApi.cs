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

    /// <summary>
    /// Naming, added in Win10 2004. A second interface off the same CLSID rather than
    /// more slots on the one above: a distinct IID means QueryService either hands back a
    /// vtable that matches this layout or fails outright, instead of us guessing at slots
    /// past the end of an interface that already works.
    ///
    /// Verified on 19045.7548 - do not reorder. The first ten slots repeat
    /// IVirtualDesktopManagerInternal exactly and exist only to keep the vtable aligned;
    /// SetName is the only method called.
    /// </summary>
    [ComImport, Guid("0F3A72B0-4566-487E-9A33-4ED302F6D6CE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal2
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
        void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop,
                                                 out IObjectArray unknown1, out IObjectArray unknown2);
        void SetName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
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
        static Guid _iidManagerInternal2 = new Guid("0F3A72B0-4566-487E-9A33-4ED302F6D6CE");
        static Guid _iidViewCollection  = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
        static Guid _iidVirtualDesktop  = new Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4");

        const string DesktopsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";

        IServiceProvider10 _shell;
        IVirtualDesktopManagerInternal _manager;
        IApplicationViewCollection _views;
        IVirtualDesktopManager _public;

        /// <summary>Null when this shell cannot name desktops. Optional, unlike the four above.</summary>
        IVirtualDesktopManagerInternal2 _names;

        // How long after a switch the focus fallback looks. Explorer's own activation
        // lands within a few milliseconds; this leaves it ample room and is still too
        // short for a person to start typing into nothing.
        const int NudgeDelayMs = 200;
        System.Windows.Forms.Timer _nudge;

        /// <summary>
        /// How many times in a row the nudge has waited for modifiers to come up - see
        /// NudgeFocus. Capped, so a key wedged down cannot hold it off for ever.
        /// </summary>
        int _nudgeWaits;
        const int MaxNudgeWaits = 15;

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

                // Deliberately outside the all-or-nothing publish above, and after it:
                // naming arrived in Win10 2004, and a shell that cannot serve it must
                // still switch, create and remove. Renaming is simply offered or not.
                try
                {
                    Guid clsidNames = CLSID_VirtualDesktopManagerInternal;
                    _names = (IVirtualDesktopManagerInternal2)
                        shell.QueryService(ref clsidNames, ref _iidManagerInternal2);
                }
                catch (Exception ex)
                {
                    _names = null;
                    Log.Write("com: naming interface unavailable - " + ex.Message);
                }
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
            _names = null;
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
            HandForegroundToShell();

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

            ScheduleNudge();
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

        // --- reordering -------------------------------------------------------

        /// <summary>
        /// One desktop sent to the end of the row by Reorder: the desktop it was, the one
        /// that replaced it, and what each of the three calls cost. Kept so --reorder can
        /// print where the time went, which is the question that decides whether a drop
        /// feels instant.
        /// </summary>
        public sealed class ReorderStep
        {
            public Guid OldId;
            public Guid NewId;
            public string Name;
            public bool WasCurrent;

            // The old desktop is gone and its windows are on NewId: from here on anything
            // keyed to OldId belongs to NewId instead. False only for the step a run stopped
            // on, whose new desktop was taken back off again.
            public bool Carried;

            public double CreateMs;
            public double RemoveMs;
            public double RenameMs;
        }

        /// <summary>
        /// Moves the desktop at <paramref name="from"/> to <paramref name="to"/> (both
        /// 0-based), shifting the ones between along by one. Returns the steps taken -
        /// empty when there was nothing to do - or null if the indices are out of range.
        ///
        /// Windows 10 has no call for this. The interface on 19045 can only list, switch,
        /// create and remove; Windows 11 added MoveDesktop, and 10 will never get it. So the
        /// order is rebuilt instead, out of the one placement the shell does offer: a new
        /// desktop always goes on the end. Sending a desktop to the end is three calls -
        /// create a new one, remove the old one with the new one as its fallback, give the
        /// new one the old one's name - and any order is a run of those.
        ///
        /// The fallback is the whole reason this is safe. RemoveDesktop has the shell itself
        /// carry every window on the removed desktop over to the fallback, in one call, with
        /// nothing for us to enumerate or miss. The alternative - leaving the desktops where
        /// they are and moving their windows between them - costs a call per window, and a
        /// window that refuses its move (or opens mid-run) leaves a desktop holding half of
        /// two desktops' windows, which nothing can sort back out. Done this way, every
        /// window is on a real desktop at every moment; a run that fails halfway leaves a
        /// half-applied order and possibly one empty desktop on the end, and loses nothing.
        /// Hidden windows need no carrying either way: on 19045 a hidden window belongs to
        /// no desktop and comes back on whichever is current when shown (--reordertest
        /// checks this against a control every run).
        ///
        /// Measured on 19045 by --reordertest: about 1 ms per call, so a step is 2-4 ms and
        /// even a nine-desktop rotation is well under a frame's worth of a person noticing.
        ///
        /// The cost is that the desktops sent to the end come back with new GUIDs. Explorer
        /// keys names by GUID, hence the rename; anything else holding an old GUID - a
        /// close-all run, a menu built a moment ago - must be dropped or refused around a
        /// reorder. Windows 10 has no per-desktop wallpaper, so nothing else breaks.
        ///
        /// Only desktops at or after the target position in the final order are touched,
        /// and of those, the leading run that is already in place is skipped: the kept
        /// desktops stay in front in their own order, so the most that can be kept is the
        /// longest start of the final order that the current order already contains in
        /// sequence. Moving the last two desktops past each other is two steps however many
        /// desktops there are.
        ///
        /// When the current desktop is among those recreated, removing it makes the shell
        /// switch to the fallback - its own replacement, with its own windows on it - so the
        /// user ends up where they were, under a new GUID.
        /// </summary>
        public List<ReorderStep> Reorder(int from, int to)
        {
            var steps = new List<ReorderStep>();
            return Reorder(from, to, steps) ? steps : null;
        }

        /// <summary>
        /// Reorder, adding each step to <paramref name="steps"/> the moment its old desktop is
        /// gone rather than handing back a list at the end. False if the indices are out of
        /// range. For DesktopService, which has to tell the marks about every desktop that was
        /// recreated even when a later step throws - a run that dies in its third step has
        /// still given two desktops new GUIDs, and a list returned at the end would never
        /// arrive to say so.
        /// </summary>
        public bool Reorder(int from, int to, List<ReorderStep> steps)
        {
            Guid[] ids = GetDesktopIds();
            if (from < 0 || from >= ids.Length || to < 0 || to >= ids.Length) return false;

            if (from == to) return true;

            var final = new List<Guid>(ids);
            Guid moving = final[from];
            final.RemoveAt(from);
            final.Insert(to, moving);

            // The longest start of the final order that the current order holds as a
            // subsequence: those stay, everything after them goes to the end in turn.
            int kept = 0;
            for (int i = 0; i < ids.Length && kept < final.Count; i++)
                if (ids[i] == final[kept]) kept++;

            Guid current = GetCurrentId();
            var clock = new System.Diagnostics.Stopwatch();

            Log.Write(delegate
            {
                return "reorder: " + (from + 1) + " -> " + (to + 1) + ", keeping " + kept
                       + " of " + ids.Length + ", " + (ids.Length - kept) + " to recreate";
            });

            for (int i = kept; i < final.Count; i++)
            {
                var step = new ReorderStep();
                step.OldId = final[i];
                step.Name = GetName(step.OldId);
                step.WasCurrent = step.OldId == current;

                clock.Restart();
                step.NewId = Create();
                step.CreateMs = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                Guid oldId = step.OldId, newId = step.NewId;
                bool removed = Do(delegate
                {
                    Guid a = oldId, b = newId;
                    IVirtualDesktop old = _manager.FindDesktop(ref a);
                    IVirtualDesktop replacement = _manager.FindDesktop(ref b);
                    if (old == null || replacement == null) return false;
                    _manager.RemoveDesktop(old, replacement);
                    return true;
                }, "RemoveDesktop");
                step.RemoveMs = clock.Elapsed.TotalMilliseconds;

                if (!removed)
                {
                    // The new desktop is already on the end and empty. Take it back off
                    // rather than leave a stray behind, and stop: the order is half-applied
                    // but every window is where it was.
                    Log.Write("reorder: old desktop vanished mid-run, stopping");
                    Remove(newId);
                    steps.Add(step);
                    return true;
                }

                // Recorded before the rename, which can throw: by now the swap has happened.
                step.Carried = true;
                steps.Add(step);

                // Only a name that was set: an unnamed desktop is "Desktop N" by position,
                // and should stay unnamed so it takes its new position's number.
                clock.Restart();
                if (step.Name != null) SetName(step.NewId, step.Name);
                step.RenameMs = clock.Elapsed.TotalMilliseconds;
            }

            Log.Write("reorder: done");
            return true;
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

        /// <summary>
        /// True for a window shown on every desktop - Task View's "Show this window on all
        /// desktops", or "Show windows from this app on all desktops".
        ///
        /// Asked by visibility rather than through IVirtualDesktopPinnedApps. That interface
        /// answers the two kinds of pinning separately, and the app kind wants the view's
        /// AppUserModelId, which means declaring IApplicationView's long vtable - several
        /// dozen slots of risk for one string. A pinned view is visible on a desktop that is
        /// not its own whichever way it was pinned, and IsViewVisible sits in a slot
        /// IVirtualDesktop already declares. So: visible on any desktop besides the one
        /// GetWindowDesktopId names means pinned.
        ///
        /// False with a single desktop, where there is no other to look at - and where
        /// "on every desktop" and "on this one" are the same thing anyway. False too for a
        /// window the view collection does not know; better to treat an unknown window as
        /// belonging to its desktop than to protect it by guesswork.
        /// </summary>
        public bool IsPinned(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

            Guid home;
            if (!TryGetWindowDesktop(hwnd, out home)) return false;

            return Do(delegate
            {
                // Fresh, for the same TYPE_E_ELEMENTNOTFOUND reason MoveWindow queries it.
                IApplicationView view;
                int hr = QueryViewCollection().GetViewForHwnd(hwnd, out view);
                if (hr < 0 || view == null) return false;

                Guid[] ids = GetDesktopIdsUnwrapped();
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == home) continue;

                    Guid other = ids[i];
                    IVirtualDesktop desktop = _manager.FindDesktop(ref other);
                    if (desktop == null) continue;

                    int visible;
                    desktop.IsViewVisible(view, out visible);
                    if (visible != 0) return true;
                }
                return false;
            }, "IsPinned");
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
        /// Whether this shell will accept a rename at all. False while Explorer is away,
        /// so callers get "not now" rather than an exception they would only swallow.
        /// </summary>
        public bool CanRename
        {
            get
            {
                try
                {
                    EnsureAcquired();
                }
                catch (ShellUnavailableException)
                {
                    return false;
                }
                return _names != null;
            }
        }

        /// <summary>
        /// Renames a desktop. An empty name clears it, which puts the desktop back to
        /// "Desktop N" - Desktop.DisplayName already treats blank that way.
        ///
        /// Goes through the shell rather than writing the registry value GetName reads,
        /// so Explorer is told rather than found out: Task View picks the name up, and
        /// its own cache does not go stale behind us.
        /// </summary>
        public bool SetName(Guid id, string name)
        {
            return Do(delegate
            {
                if (_names == null)
                {
                    Log.Write("rename: shell has no naming support, ignoring");
                    return false;
                }

                Guid target = id;
                IVirtualDesktop desktop = _manager.FindDesktop(ref target);
                if (desktop == null)
                {
                    Log.Write("rename: desktop not found, ignoring");
                    return false;
                }

                _names.SetName(desktop, name != null ? name : "");
                Log.Write(delegate { return "desktop renamed " + id + " to \"" + name + "\""; });
                return true;
            }, "SetName");
        }

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
        /// Gives the foreground to the taskbar before a switch, unless Explorer already
        /// holds it.
        ///
        /// SwitchDesktop activates the new desktop's last active window itself. When
        /// Explorer holds the foreground, as it does after any click on the strip because
        /// the strip lives inside the taskbar, that activation is clean. When another
        /// app's window holds it, as it does when a shortcut is pressed while typing in
        /// VS Code, Explorer also flashes the window it activates (HSHELL_FLASH, then
        /// HSHELL_RUDEAPPACTIVATED a moment later). The activation normally clears the
        /// flash at once. But switch away again within about half a second, as quick
        /// Ctrl+Alt+arrows do, and the flash keeps going and ends with the button stuck
        /// orange. Windows 10 shows a flashing window's button on every desktop, so the
        /// result is another desktop's window sitting orange on this desktop's taskbar,
        /// and clicking it jumps over there. Measured on 19045: twelve hotkey switches
        /// 0.4 s apart left one stuck every run, and none with this step in front.
        ///
        /// The step is allowed because the process that receives WM_HOTKEY may set the
        /// foreground. Where it is refused, on a switch with no input behind it, the
        /// switch goes ahead as before and may flash; nothing worse.
        /// </summary>
        static void HandForegroundToShell()
        {
            try
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                if (IsExplorers(Native.GetForegroundWindow(), tray)) return;

                bool ok = Native.ForceForeground(tray);
                if (!ok) Log.Write("switch: taskbar refused the foreground, switch may flash");
            }
            catch (Exception ex)
            {
                Log.Write("switch: foreground handoff failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether a window belongs to Explorer's process - the taskbar, a secondary
        /// taskbar, the desktop (Progman). Any of them holding the foreground keeps the
        /// switch's own activation clean.
        /// </summary>
        static bool IsExplorers(IntPtr hwnd, IntPtr tray)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == tray) return true;
            uint pid, trayPid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            Native.GetWindowThreadProcessId(tray, out trayPid);
            return pid != 0 && pid == trayPid;
        }

        /// <summary>
        /// A fallback for when the switch left focus behind on the old desktop, or nowhere:
        /// activate the topmost real window on the new one.
        ///
        /// Only a fallback. SwitchDesktop activates the new desktop's last active window
        /// itself, and an unconditional nudge got in its way twice over. It walks the
        /// z-order while Explorer's activation is still landing, so it saw a stale order
        /// and moved focus from VS Code, which Explorer had just picked, to the terminal
        /// beside it. And a SetForegroundWindow that is refused flashes its target - the
        /// stuck orange button HandForegroundToShell exists to prevent, by another road.
        /// So it acts only when the foreground is not a window on the new desktop.
        /// </summary>
        void ScheduleNudge()
        {
            // The check cannot run the moment SwitchDesktop returns: Explorer's activation
            // lands a few milliseconds later, so the foreground still reads as the taskbar
            // HandForegroundToShell gave it to, and a nudge then overrides Explorer's pick.
            // Seen on 19045 with the check inline: every switch to a desktop holding
            // KeePassXC (last used) and a Chrome window (higher in z-order) ended on Chrome.
            //
            // A WinForms timer, so the tick comes back on this thread - the one the COM
            // objects were created on. A selftest switching from a thread with no message
            // loop never gets the tick, and needs no focus anyway. A switch inside the
            // wait restarts it: only the last desktop's focus matters.
            if (_nudge == null)
            {
                _nudge = new System.Windows.Forms.Timer();
                _nudge.Interval = NudgeDelayMs;
                _nudge.Tick += delegate { _nudge.Stop(); NudgeFocus(); };
            }
            _nudgeWaits = 0;
            _nudge.Stop();
            _nudge.Start();
        }

        void NudgeFocus()
        {
            // Not while a modifier is down. Quick Ctrl+Alt+arrows, each released before the
            // next, put this tick just as the next chord is going down - and that key press
            // goes to the taskbar, which then owns "the last input", so the forced
            // SetForegroundWindow below is refused. A refused one flashes its target: the
            // stuck orange button HandForegroundToShell exists to prevent. Measured on 19045:
            // eight presses 200 ms apart gave seven "(refused)" in the log and, on one run of
            // two, two VS Code buttons left orange on every desktop. A held chord tapping the
            // arrow was never refused, and neither was a pause; this waits for the chord to
            // come up instead, and a switch arriving meanwhile restarts the whole wait.
            if (Native.ModifiersDown() && _nudgeWaits < MaxNudgeWaits)
            {
                _nudgeWaits++;
                _nudge.Start();
                return;
            }

            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && Native.IsAltTabWindow(fg) && !Native.IsCloaked(fg)
                    && IsOnCurrentDesktop(fg))
                    return;

                IntPtr hwnd = Native.GetTopWindow(IntPtr.Zero);
                while (hwnd != IntPtr.Zero)
                {
                    // Cloaked is never a target, though it passes both other tests: a
                    // suspended packaged app's CoreWindow is visible, titled and reported on
                    // the current desktop wherever you are. Seen on 19045 with Notepads
                    // suspended: every switch to an empty desktop handed the keyboard to a
                    // Notepads window nobody could see.
                    if (Native.IsAltTabWindow(hwnd) && !Native.IsCloaked(hwnd) && IsOnCurrentDesktop(hwnd))
                    {
                        bool ok = Native.ForceForeground(hwnd);
                        Log.Write(delegate
                        {
                            return "switch: focus was not on the new desktop, nudged 0x"
                                + hwnd.ToString("X") + (ok ? "" : " (refused)");
                        });
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
