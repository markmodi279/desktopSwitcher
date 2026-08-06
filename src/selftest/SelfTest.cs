using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace DesktopSwitcher
{
    /// <summary>
    /// The console selftest harness: every --command the exe accepts.
    ///
    /// These exist because almost nothing here can be unit tested. The shell's virtual
    /// desktop interfaces are undocumented, live only in a running Explorer, and have no
    /// stand-in worth writing; the taskbar geometry is whatever the machine's taskbar
    /// happens to be. So each layer got a command that drives it against the real shell
    /// and prints what it saw, and those commands stayed - they are how a change is
    /// checked today, and how a new Windows build is proved out.
    ///
    /// Kept apart from Program for the plain reason that it dwarfs it: this is the largest
    /// file in the tree and none of it runs in the shipped app, which is exactly the sort
    /// of thing that misleads anyone sizing the codebase up.
    ///
    /// Only reachable from a console build - build.cmd console. The windowed build has
    /// nowhere to print.
    /// </summary>
    static class SelfTest
    {
        /// <summary>
        /// Dispatches a command line. Failure is reported and swallowed rather than thrown:
        /// a selftest that dies with a stack trace has told you less than one that says
        /// which call failed and returns non-zero.
        /// </summary>
        public static int Run(string[] args)
        {
            string command = args[0].ToLowerInvariant();
            var api = new VirtualDesktopApi();

            try
            {
                switch (command)
                {
                    case "--list":    return CmdList(api);
                    case "--desktops": return CmdDesktops(api);
                    case "--switch":  return CmdSwitch(api, args);
                    case "--create":  return CmdCreate(api);
                    case "--remove":  return CmdRemove(api, args);
                    case "--rename":  return CmdRename(api, args);
                    case "--move":    return CmdMove(api, args);
                    case "--where":   return CmdWhere(api, args);
                    case "--soak":    return CmdSoak(api, args);
                    case "--watch":   return CmdWatch(api, args);
                    case "--service": return CmdService(api, args);
                    case "--taskbar": return CmdTaskbar(api);
                    case "--testwindow": return CmdTestWindow(args);
                    case "--strip":   return CmdStrip(api, args);
                    case "--anim":    return CmdAnim(args);
                    case "--slide":   return CmdSlide(args);
                    case "--help":
                    case "-h":
                    case "/?":        Usage(); return 0;
                    default:
                        Console.WriteLine("Unknown command: " + command);
                        Console.WriteLine();
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("ERROR  " + ex.GetType().Name + ": " + ex.Message);
                Log.Write("selftest failed: " + ex);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("DesktopSwitcher - run with no arguments to start the app.");
            Console.WriteLine();
            Console.WriteLine("Selftest commands:");
            Console.WriteLine("  --list                 show all desktops");
            Console.WriteLine("  --desktops             show all desktops with the windows on each");
            Console.WriteLine("  --switch N             switch to desktop N (1-based)");
            Console.WriteLine("  --create               create a new desktop");
            Console.WriteLine("  --remove N             remove desktop N");
            Console.WriteLine("  --rename N <name>      rename desktop N; no name clears it");
            Console.WriteLine("  --move N <title>       move a window whose title contains <title>");
            Console.WriteLine("  --where <title>        report whether that window is on the current desktop");
            Console.WriteLine("  --soak N               poll for N seconds; survives an Explorer restart");
            Console.WriteLine("  --watch N              listen for change notifications for N seconds");
            Console.WriteLine("  --service N            run DesktopService for N seconds and report its events");
            Console.WriteLine("  --taskbar              print taskbar geometry and computed strip bounds");
            Console.WriteLine("  --testwindow N         dock a plain marker window in the taskbar for N seconds");
            Console.WriteLine("  --strip N              run the real switcher strip for N seconds");
            Console.WriteLine("  --anim [ms]            step the strip's easing headlessly, frame by frame");
            Console.WriteLine("  --slide [ms]           step the hover panel's travel between buttons");
            Console.WriteLine();
        }

        // --- commands ---------------------------------------------------------

        static int CmdList(VirtualDesktopApi api)
        {
            IList<Desktop> desktops = Snapshot(api);

            Console.WriteLine("Desktops (" + desktops.Count + "):");
            Console.WriteLine();
            foreach (Desktop d in desktops)
            {
                Console.WriteLine(string.Format("  {0}  {1,-20} {2}{3}",
                    d.Number,
                    d.DisplayName,
                    d.Id,
                    d.IsCurrent ? "   <== CURRENT" : ""));
            }
            Console.WriteLine();
            return 0;
        }

        /// <summary>
        /// Every desktop with the windows on it - the exact content the hover tooltip
        /// shows, printed before any UI exists to draw it.
        ///
        /// The point is to check the filter against Task View by eye: no suspended UWP
        /// ghosts, nothing real missing, and windows pinned to all desktops landing
        /// somewhere sensible. Cloaking cannot make that distinction; see WindowInventory.
        /// </summary>
        static int CmdDesktops(VirtualDesktopApi api)
        {
            IList<Desktop> desktops = Snapshot(api);
            var inventory = new WindowInventory(api);

            Console.WriteLine("Desktops (" + desktops.Count + "):");
            Console.WriteLine();

            int total = 0;
            foreach (Desktop d in desktops)
            {
                IList<WindowEntry> windows = inventory.WindowsOn(d.Id);
                total += windows.Count;

                Console.WriteLine(string.Format("  [{0}] {1}  ({2} window{3}){4}",
                    d.Number,
                    d.DisplayName,
                    windows.Count,
                    windows.Count == 1 ? "" : "s",
                    d.IsCurrent ? "   <== CURRENT" : ""));

                if (windows.Count == 0)
                {
                    Console.WriteLine("         - empty -");
                }
                else
                {
                    // App first, as the tooltip shows it - so this selftest is also how you
                    // check app resolution across every open window at once.
                    foreach (WindowEntry w in windows)
                        Console.WriteLine("         " + (w.App.Length == 0 ? "?" : w.App) + "  -  " + w.Title);
                }
                Console.WriteLine();
            }

            Console.WriteLine("  " + total + " windows total. Compare against Win+Tab.");
            Console.WriteLine();
            return 0;
        }

        static int CmdSwitch(VirtualDesktopApi api, string[] args)
        {
            IList<Desktop> desktops = Snapshot(api);
            Desktop target = Pick(desktops, args, 1);
            if (target == null) return 2;

            Console.WriteLine("Switching to " + target.Number + " (" + target.DisplayName + ") ...");
            api.SwitchTo(target.Id);

            Guid now = api.GetCurrentId();
            bool ok = now == target.Id;
            Console.WriteLine(ok ? "OK - current desktop is now " + target.Number
                                 : "MISMATCH - current is " + now);
            return ok ? 0 : 1;
        }

        static int CmdCreate(VirtualDesktopApi api)
        {
            int before = api.GetCount();
            Guid id = api.Create();
            int after = api.GetCount();

            Console.WriteLine("Created " + id);
            Console.WriteLine("Count " + before + " -> " + after);
            return after == before + 1 ? 0 : 1;
        }

        static int CmdRemove(VirtualDesktopApi api, string[] args)
        {
            IList<Desktop> desktops = Snapshot(api);
            Desktop target = Pick(desktops, args, 1);
            if (target == null) return 2;

            int before = api.GetCount();
            Console.WriteLine("Removing " + target.Number + " (" + target.DisplayName + ") ...");

            bool removed = api.Remove(target.Id);
            int after = api.GetCount();

            Console.WriteLine("Removed: " + removed + "   count " + before + " -> " + after);
            return removed && after == before - 1 ? 0 : 1;
        }

        /// <summary>
        /// Renames a desktop and reads the name back out of the registry to prove the
        /// shell took it. The read-back is what makes this worth having: SetName reaches
        /// an undocumented vtable slot, and a slot that is wrong will not say so.
        /// </summary>
        static int CmdRename(VirtualDesktopApi api, string[] args)
        {
            IList<Desktop> desktops = Snapshot(api);
            Desktop target = Pick(desktops, args, 1);
            if (target == null) return 2;

            if (!api.CanRename)
            {
                Console.WriteLine("This shell has no naming support (needs Win10 2004+).");
                return 1;
            }

            // Everything after the number, so a name with spaces needs no quoting.
            string name = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";

            Console.WriteLine("Renaming " + target.Number + " (" + target.DisplayName
                              + ") to \"" + name + "\" ...");

            if (!api.SetName(target.Id, name))
            {
                Console.WriteLine("FAILED - the shell refused");
                return 1;
            }

            string now = VirtualDesktopApi.GetName(target.Id);
            bool ok = name.Length == 0 ? string.IsNullOrEmpty(now) : now == name;

            Console.WriteLine(ok ? "OK - name is now \"" + (now != null ? now : "") + "\""
                                 : "MISMATCH - registry says \"" + (now != null ? now : "") + "\"");
            return ok ? 0 : 1;
        }

        static int CmdMove(VirtualDesktopApi api, string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: --move N <title substring>");
                return 2;
            }

            IList<Desktop> desktops = Snapshot(api);
            Desktop target = Pick(desktops, args, 1);
            if (target == null) return 2;

            string needle = args[2];
            IntPtr hwnd = FindWindowByTitle(needle);
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("No visible window with title containing \"" + needle + "\"");
                return 2;
            }

            Console.WriteLine("Moving \"" + Native.GetText(hwnd) + "\" to desktop " + target.Number + " ...");
            bool ok = api.MoveWindow(hwnd, target.Id);
            Console.WriteLine(ok ? "OK" : "FAILED");
            return ok ? 0 : 1;
        }

        static int CmdWhere(VirtualDesktopApi api, string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: --where <title substring>");
                return 2;
            }

            IntPtr hwnd = FindWindowByTitle(args[1]);
            if (hwnd == IntPtr.Zero)
            {
                // Not finding it is itself the answer: windows on other desktops stay
                // enumerable, so a miss means no such window exists at all.
                Console.WriteLine("No visible window with title containing \"" + args[1] + "\"");
                return 2;
            }

            // Force acquisition before querying; IsOnCurrentDesktop does not self-acquire.
            api.GetCount();

            bool onCurrent = api.IsOnCurrentDesktop(hwnd);
            Console.WriteLine("\"" + Native.GetText(hwnd) + "\"");
            Console.WriteLine("  on current desktop: " + (onCurrent ? "YES" : "NO"));
            return onCurrent ? 0 : 1;
        }

        /// <summary>
        /// Holds the COM objects and polls, so an Explorer restart mid-run exercises the
        /// re-acquire path in VirtualDesktopApi.Do(). A short-lived command cannot test
        /// this, because it acquires fresh objects every invocation.
        /// </summary>
        static int CmdSoak(VirtualDesktopApi api, string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 30;

            Console.WriteLine("Soaking " + seconds + "s - restart Explorer now to test recovery.");
            Console.WriteLine();

            int hardErrors = 0;
            int unavailableTicks = 0;
            int lastCount = -1;

            for (int i = 0; i < seconds; i++)
            {
                try
                {
                    int count = api.GetCount();
                    Guid current = api.GetCurrentId();

                    string note = "";
                    if (lastCount >= 0 && count != lastCount)
                        note = "   (count changed " + lastCount + " -> " + count + ")";
                    lastCount = count;

                    Console.WriteLine(string.Format("  {0,3}s  count={1}  current={2}{3}",
                        i, count, current.ToString().Substring(0, 8), note));
                }
                catch (ShellUnavailableException)
                {
                    // Expected while Explorer is down. The real app keeps its last known
                    // model and repaints nothing, rather than blanking the strip.
                    unavailableTicks++;
                    Console.WriteLine(string.Format(
                        "  {0,3}s  shell unavailable (Explorer restarting) - holding last state",
                        i));
                }
                catch (Exception ex)
                {
                    hardErrors++;
                    Console.WriteLine(string.Format("  {0,3}s  HARD ERROR {1}: {2}",
                        i, ex.GetType().Name, ex.Message));
                }

                System.Threading.Thread.Sleep(1000);
            }

            Console.WriteLine();
            Console.WriteLine("Ticks with shell unavailable : " + unavailableTicks + "  (expected during restart)");
            Console.WriteLine("Hard errors                  : " + hardErrors);
            return hardErrors == 0 ? 0 : 1;
        }

        /// <summary>
        /// Registers the notification sink and reports callbacks as they arrive.
        /// Needs a message pump, since COM delivers to an STA via window messages.
        /// The reported thread id proves callbacks do NOT arrive on the UI thread.
        /// </summary>
        static int CmdWatch(VirtualDesktopApi api, string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 30;

            int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int events = 0;

            using (var notify = new VirtualDesktopNotify(api))
            {
                notify.DesktopCreated += delegate(Guid id)
                {
                    events++;
                    Report("Created", id.ToString().Substring(0, 8), uiThread);
                };
                notify.DesktopDestroyed += delegate(Guid gone, Guid fallback)
                {
                    events++;
                    Report("Destroyed", gone.ToString().Substring(0, 8) + " -> "
                                        + fallback.ToString().Substring(0, 8), uiThread);
                };
                notify.CurrentChanged += delegate(Guid from, Guid to)
                {
                    events++;
                    Report("CurrentChanged", from.ToString().Substring(0, 8) + " -> "
                                             + to.ToString().Substring(0, 8), uiThread);
                };

                if (!notify.Register())
                {
                    Console.WriteLine("Registration FAILED.");
                    return 1;
                }

                Console.WriteLine("Registered. UI thread = " + uiThread);
                Console.WriteLine("Listening " + seconds + "s ...");
                Console.WriteLine();

                var timer = new System.Windows.Forms.Timer();
                timer.Interval = seconds * 1000;
                timer.Tick += delegate { System.Windows.Forms.Application.ExitThread(); };
                timer.Start();
                System.Windows.Forms.Application.Run();
                timer.Stop();
            }

            Console.WriteLine();
            Console.WriteLine("Events received: " + events);
            return events > 0 ? 0 : 1;
        }

        static void Report(string name, string detail, int uiThread)
        {
            int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Console.WriteLine(string.Format("  {0}  {1,-16} {2,-26} thread={3}{4}",
                DateTime.Now.ToString("HH:mm:ss.fff"), name, detail, thread,
                thread == uiThread ? "" : "  (NOT the UI thread)"));
        }

        /// <summary>
        /// Runs the full service the way the real app will. The thread id printed with
        /// each event is the point of the test: notifications arrive on RPC threads, so
        /// seeing the UI thread here proves the marshalling works.
        /// </summary>
        static int CmdService(VirtualDesktopApi api, string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 30;

            int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int setEvents = 0, currentEvents = 0, offThread = 0;

            // Stand-in for the controller window that owns marshalling in M7.
            using (var pump = new System.Windows.Forms.Form())
            {
                IntPtr forceHandle = pump.Handle;
                GC.KeepAlive(forceHandle);

                var service = new DesktopService(api, pump);

                // "nonotify" proves the reconcile tick alone can drive the UI, which is
                // what happens if the sink dies or Explorer restarts.
                bool noNotify = args.Length > 2
                    && string.Equals(args[2], "nonotify", StringComparison.OrdinalIgnoreCase);
                service.DisableNotifications = noNotify;

                service.DesktopsChanged += delegate
                {
                    setEvents++;
                    int t = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (t != uiThread) offThread++;
                    Console.WriteLine(string.Format("  {0}  DesktopsChanged  count={1}  thread={2}{3}",
                        DateTime.Now.ToString("HH:mm:ss.fff"), service.Count, t,
                        t == uiThread ? "  (UI)" : "  *** OFF UI THREAD ***"));
                    Dump(service);
                };

                service.CurrentChanged += delegate
                {
                    currentEvents++;
                    int t = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (t != uiThread) offThread++;
                    Desktop cur = service.Current;
                    Console.WriteLine(string.Format("  {0}  CurrentChanged   -> {1}  thread={2}{3}",
                        DateTime.Now.ToString("HH:mm:ss.fff"),
                        cur == null ? "(none)" : cur.Number + " " + cur.DisplayName,
                        t, t == uiThread ? "  (UI)" : "  *** OFF UI THREAD ***"));
                };

                service.Start();

                Console.WriteLine("Service started. UI thread = " + uiThread);
                Console.WriteLine("Notifications active: " + service.NotificationsActive);
                Console.WriteLine("Initial model:");
                Dump(service);
                Console.WriteLine();

                var ticker = new System.Windows.Forms.Timer();
                ticker.Interval = 2000;
                ticker.Tick += delegate { service.Tick(); };
                ticker.Start();

                var stop = new System.Windows.Forms.Timer();
                stop.Interval = seconds * 1000;
                stop.Tick += delegate { System.Windows.Forms.Application.ExitThread(); };
                stop.Start();

                System.Windows.Forms.Application.Run();

                ticker.Stop();
                stop.Stop();
                service.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("DesktopsChanged events : " + setEvents);
            Console.WriteLine("CurrentChanged events  : " + currentEvents);
            Console.WriteLine("Events off UI thread   : " + offThread + "  (must be 0)");
            return offThread == 0 ? 0 : 1;
        }

        static void Dump(DesktopService service)
        {
            IList<Desktop> list = service.Desktops;
            var sb = new StringBuilder("        ");
            foreach (Desktop d in list)
            {
                sb.Append("[");
                sb.Append(d.Number);
                if (d.IsCurrent) sb.Append("*");
                sb.Append(" ");
                sb.Append(d.DisplayName);
                sb.Append("] ");
            }
            Console.WriteLine(sb.ToString());
        }

        static int CmdTaskbar(VirtualDesktopApi api)
        {
            var host = new TaskbarHost();
            if (!host.Locate())
            {
                Console.WriteLine("Shell_TrayWnd not found.");
                return 1;
            }

            Console.WriteLine(host.Describe());
            Console.WriteLine("  DPI scale      " + host.DpiScale);
            Console.WriteLine();

            // Widths for 2..5 buttons plus the '+' button, at DPI-scaled sizes.
            var cfg = Config.Load();
            for (int n = 2; n <= 5; n++)
            {
                int width = n * host.Scale(cfg.ButtonWidth) + host.Scale(cfg.PlusWidth);
                System.Drawing.Rectangle bounds;
                if (host.TryComputeBounds(width, host.Scale(cfg.Margin), out bounds))
                    Console.WriteLine(string.Format(
                        "  {0} desktops -> width {1,3}  client bounds {2}", n, width, bounds));
                else
                    Console.WriteLine("  " + n + " desktops -> bounds computation FAILED");
            }

            Console.WriteLine();

            // The live desktop count, not a guess at it. This command is normally run with
            // the app already going, so a probe sized for two desktops when there are four
            // lands inside the running strip and reports the strip's own colour back as the
            // taskbar's - which is the failure this command exists to catch, so it must not
            // be the failure the command has.
            int count = Snapshot(api).Count;
            if (count < 1) count = 1;

            System.Drawing.Color sampled;
            int probeWidth = count * host.Scale(cfg.ButtonWidth) + host.Scale(cfg.PlusWidth);
            Console.WriteLine("  probing left of a " + count + "-desktop strip (" + probeWidth + "px)");

            if (host.TrySampleBackground(probeWidth, host.Scale(cfg.Margin), out sampled))
                Console.WriteLine("  sampled taskbar colour: " + sampled
                    + string.Format("  #{0:X2}{1:X2}{2:X2}", sampled.R, sampled.G, sampled.B));
            else
                Console.WriteLine("  taskbar colour sampling FAILED");

            return 0;
        }

        /// <summary>
        /// Docks a plain marker window in the taskbar. Proves reparenting, geometry and
        /// z-order in isolation, before any rendering or input logic exists to confuse
        /// the picture.
        /// </summary>
        static int CmdTestWindow(string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 15;

            var host = new TaskbarHost();
            if (!host.Locate())
            {
                Console.WriteLine("Shell_TrayWnd not found.");
                return 1;
            }

            var cfg = Config.Load();
            int width = 4 * host.Scale(cfg.ButtonWidth) + host.Scale(cfg.PlusWidth);

            System.Drawing.Rectangle bounds;
            if (!host.TryComputeBounds(width, host.Scale(cfg.Margin), out bounds))
            {
                Console.WriteLine("Bounds computation FAILED");
                return 1;
            }

            var marker = new MarkerWindow(host.TrayWindow, bounds);
            try
            {
                IntPtr handle = marker.Handle;
                Native.RECT screen;
                Native.GetWindowRect(handle, out screen);

                Console.WriteLine("Marker handle        : " + handle);
                Console.WriteLine("Parent is tray       : " + (Native.GetParent(handle) == host.TrayWindow));
                Console.WriteLine("Visible              : " + Native.IsWindowVisible(handle));
                Console.WriteLine("Client bounds        : " + bounds);
                Console.WriteLine(string.Format("Screen rect          : ({0},{1})-({2},{3})",
                    screen.Left, screen.Top, screen.Right, screen.Bottom));
                Console.WriteLine("Holding " + seconds + "s - look left of the tray icons.");

                var stop = new System.Windows.Forms.Timer();
                stop.Interval = seconds * 1000;
                stop.Tick += delegate { System.Windows.Forms.Application.ExitThread(); };
                stop.Start();
                System.Windows.Forms.Application.Run();
                stop.Stop();
            }
            finally
            {
                marker.Destroy();
            }

            Console.WriteLine("Done.");
            return 0;
        }

        /// <summary>
        /// Solid magenta block - deliberately impossible to miss.
        ///
        /// A NativeWindow, not a Form: WinForms forces a Form to be top-level no matter
        /// what CreateParams.Parent says, so it never becomes a taskbar child.
        /// </summary>
        sealed class MarkerWindow : System.Windows.Forms.NativeWindow
        {
            public MarkerWindow(IntPtr parent, System.Drawing.Rectangle bounds)
            {
                var cp = new System.Windows.Forms.CreateParams();
                cp.Caption = "DesktopSwitcherMarker";
                cp.X = bounds.X;
                cp.Y = bounds.Y;
                cp.Width = bounds.Width;
                cp.Height = bounds.Height;
                cp.Parent = parent;
                cp.Style = Native.WS_CHILD | Native.WS_VISIBLE;
                cp.ExStyle = Native.WS_EX_NOACTIVATE;
                CreateHandle(cp);
            }

            public void Destroy()
            {
                if (Handle != IntPtr.Zero) DestroyHandle();
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == Native.WM_PAINT)
                {
                    Native.PAINTSTRUCT ps;
                    IntPtr hdc = Native.BeginPaint(Handle, out ps);
                    try
                    {
                        using (var g = System.Drawing.Graphics.FromHdc(hdc))
                            g.Clear(System.Drawing.Color.Magenta);
                    }
                    finally
                    {
                        Native.EndPaint(Handle, ref ps);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }

                base.WndProc(ref m);
            }
        }

        /// <summary>
        /// Runs the real strip wired to the real service. Everything M7 needs except
        /// the tray icon, the watchdog and the config persistence.
        /// </summary>
        static int CmdStrip(VirtualDesktopApi api, string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 30;

            var host = new TaskbarHost();
            if (!host.Locate())
            {
                Console.WriteLine("Shell_TrayWnd not found.");
                return 1;
            }

            Config cfg = Config.Load();
            int buttonWidth = host.Scale(cfg.ButtonWidth);
            int plusWidth = host.Scale(cfg.PlusWidth);
            int margin = host.Scale(cfg.Margin);
            int barHeight = host.Scale(3);

            System.Drawing.Color background = cfg.BackgroundColor;
            if (background.IsEmpty)
            {
                System.Drawing.Color sampled;
                int probe = SwitcherStrip.MeasureWidth(2, buttonWidth, plusWidth);
                if (!host.TrySampleBackground(probe, margin, out sampled))
                    sampled = System.Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F);
                background = sampled;
            }

            Console.WriteLine("DPI scale   " + host.DpiScale);
            Console.WriteLine("background  " + background);
            Console.WriteLine("button/plus " + buttonWidth + "/" + plusWidth);

            // Hidden, never shown - exists only to marshal onto the UI thread.
            using (var pump = new System.Windows.Forms.Form())
            {
                pump.ShowInTaskbar = false;
                IntPtr pumpHandle = pump.Handle;
                GC.KeepAlive(pumpHandle);

                var service = new DesktopService(api, pump);
                var foreground = new ForegroundTracker();

                int initialWidth = SwitcherStrip.MeasureWidth(2, buttonWidth, plusWidth);
                System.Drawing.Rectangle bounds;
                host.TryComputeBounds(initialWidth, margin, out bounds);

                var strip = new SwitcherStrip(host.TrayWindow, bounds,
                                              buttonWidth, plusWidth, barHeight,
                                              background, cfg.HighlightColor,
                                              (uint)cfg.TooltipDelayMs, cfg.TooltipWidth,
                                              cfg.AnimationMs, host.DpiScale);

                // Hover panels, so --strip exercises them too. A compact echo of
                // Controller.BuildTooltip - enough to check placement, delay and content.
                var inventory = new WindowInventory(api);
                strip.TooltipProvider = delegate(int index)
                {
                    IList<Desktop> list = service.Desktops;
                    if (index >= list.Count)
                        return new TooltipContent("New desktop", new List<string> { "Win+Ctrl+D" });
                    if (index < 0) return null;

                    Desktop d = list[index];
                    IList<WindowEntry> windows = inventory.WindowsOn(d.Id);

                    var lines = new List<string>();
                    if (windows.Count == 0) lines.Add("- empty -");
                    else for (int i = 0; i < windows.Count && i < cfg.TooltipMaxWindows; i++)
                        lines.Add(Controller.WindowLine(windows[i]));

                    Console.WriteLine("  tooltip -> [" + d.Number + "] " + windows.Count + " window(s)");
                    return new TooltipContent(d.DisplayName + (d.IsCurrent ? "   (current)" : ""), lines);
                };

                EventHandler relayout = delegate
                {
                    strip.SetDesktops(service.Desktops);
                    host.Reassert(strip.Handle, strip.Width, margin);
                    Console.WriteLine("  relayout -> " + service.Count + " buttons, width " + strip.Width);
                };

                service.DesktopsChanged += relayout;
                service.CurrentChanged += delegate
                {
                    strip.SetDesktops(service.Desktops);
                    Desktop cur = service.Current;
                    Console.WriteLine("  current  -> " + (cur == null ? "(none)" : cur.Number.ToString()));
                };

                strip.SwitchRequested += delegate(Guid id) { service.SwitchTo(id); };
                strip.CreateRequested += delegate { service.Create(); };
                strip.RemoveRequested += delegate(Guid id) { service.Remove(id); };
                strip.MoveWindowRequested += delegate(Guid id)
                {
                    IntPtr target = foreground.Resolve();
                    if (target == IntPtr.Zero)
                    {
                        Console.WriteLine("  move    -> no candidate window");
                        return;
                    }
                    Console.WriteLine("  move    -> \"" + Native.GetText(target) + "\"");
                    service.MoveWindow(target, id);
                };

                // The menu itself is the controller's - its items need the model, the
                // inventory and the tracker at once, and a second copy here would drift from
                // the real one. What this covers is the gesture reaching the strip at all,
                // and the button lighting up and going out again either side of it.
                strip.ContextMenuRequested += delegate(int index, System.Drawing.Rectangle anchor)
                {
                    Console.WriteLine("  menu    -> button " + index + " at " + anchor);
                    strip.MenuClosed();
                };

                foreground.Ignore(strip.Handle);

                service.Start();
                relayout(null, EventArgs.Empty);

                var ticker = new System.Windows.Forms.Timer();
                ticker.Interval = cfg.ReconcileMs;
                ticker.Tick += delegate { service.Tick(); };
                ticker.Start();

                // Faster than the reconcile tick: focus changes need catching promptly
                // so a right-click targets the window the user just left.
                var focusSampler = new System.Windows.Forms.Timer();
                focusSampler.Interval = 300;
                focusSampler.Tick += delegate { foreground.Sample(); };
                focusSampler.Start();

                var stop = new System.Windows.Forms.Timer();
                stop.Interval = seconds * 1000;
                stop.Tick += delegate { System.Windows.Forms.Application.ExitThread(); };
                stop.Start();

                Console.WriteLine("Strip live for " + seconds + "s - click the buttons.");
                System.Windows.Forms.Application.Run();

                ticker.Stop();
                focusSampler.Stop();
                stop.Stop();
                strip.Dispose();
                service.Dispose();
            }

            Console.WriteLine("Done.");
            return 0;
        }

        /// <summary>
        /// The strip's easing, stepped headlessly and printed frame by frame.
        ///
        /// How the animation looks can only be judged by eye. The arithmetic under it
        /// cannot be judged by eye at all, and it is the part that fails quietly: a value
        /// that settles at 0.996 instead of 1 draws an identical pixel and leaves a 60Hz
        /// timer repainting for the rest of the session. So this asks the four questions
        /// the eye cannot - does it converge, does it land exactly on the target, does it
        /// ever report itself finished, and does a late frame cost time or only smoothness
        /// - and answers them without a shell, a taskbar or a window.
        /// </summary>
        static int CmdAnim(string[] args)
        {
            int animationMs;
            if (args.Length < 2 || !int.TryParse(args[1], out animationMs))
                animationMs = Config.Load().AnimationMs;

            // The strip's own interval, not a number of this file's choosing - a headless
            // frame that is not the frame the strip runs would quietly stop describing it.
            const int Frame = Motion.FrameMs;
            bool ok = true;

            Console.WriteLine("animationMs   " + animationMs + (animationMs <= 0 ? "   (animation off)" : ""));
            Console.WriteLine("frame         " + Frame + "ms");
            Console.WriteLine("rate/frame    " + Fixed(Motion.Rate(Frame, animationMs)) +
                              "   of whatever distance is left");
            Console.WriteLine("epsilon       " + Fixed(Motion.ToneEpsilon) + " tone, " +
                              Fixed(Motion.PixelEpsilon) + " pixel");
            Console.WriteLine();

            // 1. The ordinary case: a hover fading in, every frame on time.
            int frames, ms;
            float settled;
            ms = RunEase("1. hover fades in - 0 to 1, every frame on time", 0f, 1f,
                         Motion.ToneEpsilon, animationMs,
                         delegate(int i) { return Frame; }, out frames, out settled);

            if (settled != 1f)
            {
                Console.WriteLine("   FAIL  settled at " + Fixed(settled) + ", not exactly 1");
                ok = false;
            }

            // 2. The same ease with the UI thread stalled for one frame. The reconcile
            //    tick, the watchdog and the inventory sweep all share this thread, so a
            //    frame arriving 120ms late is the normal case rather than the odd one.
            int stalledFrames;
            float stalledSettled;
            int stalledMs = RunEase("2. the same, with one frame arriving 120ms late", 0f, 1f,
                                    Motion.ToneEpsilon, animationMs,
                                    delegate(int i) { return i == 3 ? 120 : Frame; },
                                    out stalledFrames, out stalledSettled);

            Console.WriteLine("   on time  " + frames + " frames, " + ms + "ms");
            Console.WriteLine("   stalled  " + stalledFrames + " frames, " + stalledMs + "ms");
            Console.WriteLine("   the stall cost frames, not time - which is what stepping by");
            Console.WriteLine("   elapsed time rather than a fixed amount per tick buys.");
            Console.WriteLine();

            if (stalledSettled != 1f)
            {
                Console.WriteLine("   FAIL  stalled run settled at " + Fixed(stalledSettled));
                ok = false;
            }

            // 3. The bar, retargeted twice before it arrives - Win+Ctrl+Right held down.
            if (!RunTravel(animationMs, Frame)) ok = false;

            Console.WriteLine(ok ? "All checks passed." : "CHECKS FAILED.");
            Console.WriteLine();
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Eases one value to its target, printing every frame that moved, and returns the
        /// milliseconds it took. <paramref name="frameMs"/> is asked how long each frame
        /// was, so a caller can stall one.
        ///
        /// The loop condition is the animator's own: it runs while Ease reports movement,
        /// exactly as the frame timer does, so an ease that never reports itself finished
        /// hangs here too - which the frame cap turns into a failure rather than a hang.
        /// </summary>
        static int RunEase(string label, float value, float target, float epsilon,
                           int animationMs, Func<int, int> frameMs,
                           out int frames, out float settled)
        {
            const int Cap = 500;

            Console.WriteLine(label);
            Console.WriteLine();
            Console.WriteLine("     frame     at      step     value");

            int ms = 0;
            frames = 0;

            while (frames < Cap)
            {
                int elapsed = frameMs(frames + 1);
                float before = value;

                if (!Motion.Ease(ref value, target, Motion.Rate(elapsed, animationMs), epsilon))
                    break;

                frames++;
                ms += elapsed;

                Console.WriteLine(string.Format("     {0,5}  {1,5}ms   {2,7}   {3}",
                    frames, ms, Fixed(value - before), Fixed(value)));
            }

            settled = value;

            if (frames >= Cap)
            {
                Console.WriteLine("     FAIL  still moving after " + Cap + " frames - it does not converge");
                return ms;
            }

            Console.WriteLine();
            Console.WriteLine("     settled at " + Fixed(value) + " after " + frames +
                              " frames (" + ms + "ms); frame " + (frames + 1) +
                              " moved nothing and stops the timer");
            Console.WriteLine();
            return ms;
        }

        /// <summary>
        /// The travelling bar, retargeted twice mid-flight. Holding Win+Ctrl+Right changes
        /// the current desktop several times before anything settles, and the requirement
        /// is that this reads as one continuous movement.
        ///
        /// What proves it is the value column across a retarget line: exponential smoothing
        /// keeps heading somewhere new from wherever it had got to, so there is nothing to
        /// re-base and nothing to get wrong. A fixed-duration tween has to reset its start
        /// value and its start time on every one of these, and the frame it forgets is the
        /// frame the bar jumps.
        /// </summary>
        static bool RunTravel(int animationMs, int frameMs)
        {
            const float ButtonWidth = 34f;   // the default, at 96 DPI
            const int Cap = 500;

            Console.WriteLine("3. the bar travels from button 1, retargeted twice on the way");
            Console.WriteLine();
            Console.WriteLine("     frame     at      step         x");

            float x = 0f;
            float target = ButtonWidth;
            float biggestStep = 0f;
            int ms = 0, frames = 0;

            while (frames < Cap)
            {
                // Two more switches land while the bar is still moving.
                if (frames == 2 || frames == 4)
                {
                    float was = target;
                    target += ButtonWidth;
                    Console.WriteLine("           retarget: target " + Fixed(was) + " -> " + Fixed(target) +
                                      ", bar stays at " + Fixed(x));
                }

                float before = x;
                if (!Motion.Ease(ref x, target, Motion.Rate(frameMs, animationMs),
                                        Motion.PixelEpsilon))
                    break;

                frames++;
                ms += frameMs;

                float step = x - before;
                if (step > biggestStep) biggestStep = step;

                Console.WriteLine(string.Format("     {0,5}  {1,5}ms   {2,7}   {3}",
                    frames, ms, Fixed(step), Fixed(x)));
            }

            Console.WriteLine();

            if (frames >= Cap)
            {
                Console.WriteLine("     FAIL  the bar never arrives");
                Console.WriteLine();
                return false;
            }

            Console.WriteLine("     settled at x=" + Fixed(x) + " after " + frames +
                              " frames (" + ms + "ms), largest step " + Fixed(biggestStep) + "px");
            Console.WriteLine();

            if (x != target)
            {
                Console.WriteLine("     FAIL  settled at " + Fixed(x) + ", not on the button edge at " +
                                  Fixed(target));
                Console.WriteLine();
                return false;
            }

            return true;
        }

        /// <summary>
        /// The hover panel's travel between buttons, stepped headlessly.
        ///
        /// --anim asks whether the strip's easing converges and lands; this asks the
        /// questions that only arise once a whole window is the thing being eased, and every
        /// one of them is a mistake that was actually made and caught here first:
        ///
        ///   - does the panel move at all? Place centres a wide panel on a narrow button and
        ///     then clamps it into the work area, so past a certain point every button on the
        ///     right resolves to the same x and the slide is silently a no-op.
        ///   - does the bottom edge hold still while the height changes? Not "is y+h
        ///     constant" - that is an identity the compiler would honour whatever the code
        ///     did - but is the rounded, device-pixel bottom edge the same integer on every
        ///     single frame.
        ///   - do Measure and Render still agree about where the rows start, now that one
        ///     builds the height from the top and the other lays out from the bottom.
        ///   - does the accent stub travel with the panel rather than ahead of it.
        ///
        /// Synthetic geometry throughout: no shell, no taskbar, no window. The numbers are
        /// the real defaults from config, scaled the way TaskbarHost would.
        /// </summary>
        static int CmdSlide(string[] args)
        {
            int animationMs;
            if (args.Length < 2 || !int.TryParse(args[1], out animationMs))
                animationMs = Config.Load().AnimationMs;

            const int Frame = Motion.FrameMs;
            Config cfg = Config.Load();
            bool ok = true;

            Console.WriteLine("animationMs   " + animationMs + (animationMs <= 0 ? "   (animation off)" : ""));
            Console.WriteLine("frame         " + Frame + "ms");
            Console.WriteLine("epsilon       " + Fixed(Motion.PixelEpsilon) + " pixel");
            Console.WriteLine();

            if (!SlidePlacement(cfg, 1.25)) ok = false;
            if (!SlideLayout(cfg, 1.25)) ok = false;
            if (!SlideTravel(cfg, 1.25, animationMs, Frame)) ok = false;
            if (!SlideRegime(cfg, 1.25)) ok = false;

            Console.WriteLine(ok ? "All checks passed." : "CHECKS FAILED.");
            Console.WriteLine();
            return ok ? 0 : 1;
        }

        /// <summary>Sizes authored at 96 DPI, scaled as TaskbarHost.Scale would.</summary>
        static int SlideScale(int value, double scale)
        {
            return (int)Math.Round(value * scale);
        }

        /// <summary>
        /// The strip, right-anchored ahead of the clock exactly as TaskbarHost computes it,
        /// and one anchor rectangle per button.
        /// </summary>
        static Rectangle[] SlideAnchors(Config cfg, double scale, Rectangle work, int trayLeft,
                                        int desktops, out int buttonWidth)
        {
            buttonWidth = SlideScale(cfg.ButtonWidth, scale);
            int plusWidth = SlideScale(cfg.PlusWidth, scale);
            int margin = SlideScale(cfg.Margin, scale);

            int width = SwitcherStrip.MeasureWidth(desktops, buttonWidth, plusWidth);
            int left = trayLeft - margin - width;

            var anchors = new Rectangle[desktops + 1];
            for (int i = 0; i < desktops; i++)
                anchors[i] = new Rectangle(left + i * buttonWidth, work.Bottom,
                                           buttonWidth, 1);

            anchors[desktops] = new Rectangle(left + desktops * buttonWidth, work.Bottom,
                                              plusWidth, 1);
            return anchors;
        }

        /// <summary>
        /// Every button must place the panel somewhere different, or there is nothing for the
        /// slide to do. This is the check that would have caught the feature being dead on
        /// the buttons nearest the clock - which is where the pointer usually is.
        /// </summary>
        static bool SlidePlacement(Config cfg, double scale)
        {
            var work = new Rectangle(0, 0, 1920, 1030);
            int panelWidth = SlideScale(cfg.TooltipWidth, scale);
            int gap = SlideScale(4, scale);

            int buttonWidth;
            Rectangle[] anchors = SlideAnchors(cfg, scale, work, 1597, 3, out buttonWidth);
            var size = new Size(panelWidth, 120);

            Console.WriteLine("1. every button places the panel somewhere different");
            Console.WriteLine();
            Console.WriteLine("   work area " + work.Width + "x" + work.Height +
                              ", panel " + panelWidth + "px wide, buttons " + buttonWidth + "px");
            Console.WriteLine();
            Console.WriteLine("     button    centre        x    right   moved");

            bool ok = true;
            int previous = int.MinValue;

            for (int i = 0; i < anchors.Length; i++)
            {
                bool accentAtTop;
                Point origin = TooltipWindow.Place(size, anchors[i], work, gap, out accentAtTop);

                int centre = anchors[i].Left + anchors[i].Width / 2;
                int moved = i == 0 ? 0 : origin.X - previous;

                Console.WriteLine(string.Format("     {0,6}    {1,6}   {2,6}   {3,6}   {4,5}",
                    i < anchors.Length - 1 ? (i + 1).ToString() : "+",
                    centre, origin.X, origin.X + size.Width, i == 0 ? "-" : moved.ToString()));

                if (i > 0 && moved == 0)
                {
                    Console.WriteLine("     FAIL  same x as the button before it - the panel cannot slide here");
                    ok = false;
                }

                previous = origin.X;
            }

            Console.WriteLine();
            if (ok)
                Console.WriteLine("     the panel is clear of the work-area edge on every button.");
            Console.WriteLine();
            return ok;
        }

        /// <summary>
        /// Measure builds the height downward from the top; Render lays the rows out upward
        /// from the bottom, because the bottom is the edge that holds still. They have to
        /// meet exactly, or every panel is a pixel or two out at rest - a bug that would look
        /// like bad padding rather than like a layout disagreement.
        /// </summary>
        static bool SlideLayout(Config cfg, double scale)
        {
            int padY = SlideScale(9, scale);
            int accent = SlideScale(3, scale);
            bool ok = true;

            Console.WriteLine("2. Measure and Render agree about where the rows start");
            Console.WriteLine();
            Console.WriteLine("     rows   rowsPx   height   origin   want");

            // Through the overflow row: past tooltipMaxWindows the panel stops growing a row
            // per window and adds a "+N more" line instead, so that is the last height it
            // ever takes and the one most worth landing exactly.
            int[] counts = { 1, 2, 5, cfg.TooltipMaxWindows, cfg.TooltipMaxWindows + 1 };

            for (int i = 0; i < counts.Length; i++)
            {
                int rowsHeight = counts[i] * SlideScale(19, scale);
                int height = TooltipWindow.MeasureHeight(padY, accent, rowsHeight);
                int origin = TooltipWindow.RowOrigin(height, padY, accent, rowsHeight, false);

                Console.WriteLine(string.Format("     {0,4}   {1,6}   {2,6}   {3,6}   {4,4}",
                    counts[i], rowsHeight, height, origin, padY));

                if (origin != padY)
                {
                    Console.WriteLine("     FAIL  row block starts at " + origin + ", not " + padY);
                    ok = false;
                }
            }

            Console.WriteLine();
            return ok;
        }

        /// <summary>
        /// The slide itself, retargeted mid-flight the way a pointer sweeping along the strip
        /// retargets it, and with the row count changing so the height has to ease too.
        ///
        /// Two invariants are watched on every frame rather than at the end, because both
        /// fail transiently or not at all: the bottom edge must be the same integer
        /// throughout, and the stub's offset inside the panel must not change, since panel
        /// and stub are supposed to be one object.
        /// </summary>
        static bool SlideTravel(Config cfg, double scale, int animationMs, int frameMs)
        {
            var work = new Rectangle(0, 0, 1920, 1030);
            int panelWidth = SlideScale(cfg.TooltipWidth, scale);
            int gap = SlideScale(4, scale);
            int padY = SlideScale(9, scale);
            int accent = SlideScale(3, scale);
            int row = SlideScale(19, scale);

            int buttonWidth;
            Rectangle[] anchors = SlideAnchors(cfg, scale, work, 1597, 3, out buttonWidth);

            // Button 1 with four rows, then button 2 with eight - a desktop with more windows
            // on it, which is what makes the height move as well as the position.
            var from = new Size(panelWidth, TooltipWindow.MeasureHeight(padY, accent, 4 * row));
            var to = new Size(panelWidth, TooltipWindow.MeasureHeight(padY, accent, 8 * row));

            bool topA, topB;
            Point a = TooltipWindow.Place(from, anchors[0], work, gap, out topA);
            Point b = TooltipWindow.Place(to, anchors[1], work, gap, out topB);
            Point c = TooltipWindow.Place(to, anchors[2], work, gap, out topB);

            Console.WriteLine("3. the panel travels, retargeted mid-flight, growing as it goes");
            Console.WriteLine();
            Console.WriteLine("     from button 1 at " + a.X + " (" + from.Height + "px tall)" +
                              " to button 2 at " + b.X + " (" + to.Height + "px)");
            Console.WriteLine();
            Console.WriteLine("     frame     at       left        top   bottom   stub");

            float left = a.X, top = a.Y, bottom = a.Y + from.Height;
            float centre = anchors[0].Left + anchors[0].Width / 2;

            float leftT = b.X, topT = b.Y, bottomT = b.Y + to.Height;
            float centreT = anchors[1].Left + anchors[1].Width / 2;

            int wantBottom = (int)Math.Round(bottom);
            int wantStub = (int)Math.Round(centre - left);

            const int Cap = 500;
            int frames = 0, ms = 0;
            bool ok = true;

            while (frames < Cap)
            {
                // The pointer keeps going: button 3 while the panel is still short of 2.
                if (frames == 3)
                {
                    leftT = c.X;
                    centreT = anchors[2].Left + anchors[2].Width / 2;
                    Console.WriteLine("           retarget: left -> " + Fixed(leftT) +
                                      ", panel is at " + Fixed(left));
                }

                float rate = Motion.Rate(frameMs, animationMs);
                bool moved = false;
                moved |= Motion.Ease(ref left, leftT, rate, Motion.PixelEpsilon);
                moved |= Motion.Ease(ref top, topT, rate, Motion.PixelEpsilon);
                moved |= Motion.Ease(ref bottom, bottomT, rate, Motion.PixelEpsilon);
                moved |= Motion.Ease(ref centre, centreT, rate, Motion.PixelEpsilon);
                if (!moved) break;

                frames++;
                ms += frameMs;

                int gotBottom = (int)Math.Round(bottom);
                int gotStub = (int)Math.Round(centre - left);

                Console.WriteLine(string.Format("     {0,5}  {1,5}ms   {2,8}   {3,8}   {4,6}   {5,4}",
                    frames, ms, Fixed(left), Fixed(top), gotBottom, gotStub));

                if (gotBottom != wantBottom)
                {
                    Console.WriteLine("     FAIL  bottom edge moved to " + gotBottom +
                                      ", should be pinned at " + wantBottom);
                    ok = false;
                }

                if (gotStub != wantStub)
                {
                    Console.WriteLine("     FAIL  stub offset drifted to " + gotStub +
                                      ", should ride with the panel at " + wantStub);
                    ok = false;
                }
            }

            Console.WriteLine();

            if (frames >= Cap)
            {
                Console.WriteLine("     FAIL  the panel never arrives");
                Console.WriteLine();
                return false;
            }

            Console.WriteLine("     settled after " + frames + " frames (" + ms + "ms); frame " +
                              (frames + 1) + " moved nothing and stops the timer");

            if (left != leftT || top != topT || bottom != bottomT || centre != centreT)
            {
                Console.WriteLine("     FAIL  settled off target - left " + Fixed(left) + "/" + Fixed(leftT) +
                                  ", top " + Fixed(top) + "/" + Fixed(topT));
                ok = false;
            }
            else
            {
                Console.WriteLine("     landed exactly on button 3, height " +
                                  ((int)Math.Round(bottom) - (int)Math.Round(top)) + "px");
            }

            Console.WriteLine();
            return ok;
        }

        /// <summary>
        /// A panel too tall to fit above the strip flips below it, and then the edge that
        /// holds still is the top rather than the bottom. Easing across that is not a slide -
        /// it is the panel crossing the screen - so Show snaps instead, and this proves the
        /// two placements really are far enough apart to be worth the special case.
        /// </summary>
        static bool SlideRegime(Config cfg, double scale)
        {
            var work = new Rectangle(0, 0, 1920, 1030);
            int panelWidth = SlideScale(cfg.TooltipWidth, scale);
            int gap = SlideScale(4, scale);
            int padY = SlideScale(9, scale);
            int accent = SlideScale(3, scale);
            int row = SlideScale(19, scale);

            int buttonWidth;
            Rectangle[] anchors = SlideAnchors(cfg, scale, work, 1597, 3, out buttonWidth);

            var small = new Size(panelWidth, TooltipWindow.MeasureHeight(padY, accent, 3 * row));
            var huge = new Size(panelWidth, TooltipWindow.MeasureHeight(padY, accent, 60 * row));

            bool topSmall, topHuge;
            Point a = TooltipWindow.Place(small, anchors[0], work, gap, out topSmall);
            Point b = TooltipWindow.Place(huge, anchors[0], work, gap, out topHuge);

            Console.WriteLine("4. a panel that no longer fits above flips below, and must snap");
            Console.WriteLine();
            Console.WriteLine(string.Format("     {0,5}px tall  -> y {1,4}, accent at {2}",
                small.Height, a.Y, topSmall ? "top" : "bottom"));
            Console.WriteLine(string.Format("     {0,5}px tall  -> y {1,4}, accent at {2}",
                huge.Height, b.Y, topHuge ? "top" : "bottom"));
            Console.WriteLine();

            if (topSmall == topHuge)
            {
                Console.WriteLine("     no flip at this screen height - the regime check is inert here.");
                Console.WriteLine();
                return true;
            }

            int jump = Math.Abs(b.Y - a.Y);
            Console.WriteLine("     the flip moves the panel " + jump + "px, which is why Show snaps");
            Console.WriteLine("     rather than easing across it.");
            Console.WriteLine();

            if (jump < small.Height)
            {
                Console.WriteLine("     FAIL  flip only moves " + jump + "px - snapping may be unnecessary");
                Console.WriteLine();
                return false;
            }

            return true;
        }

        // --- helpers ----------------------------------------------------------

        /// <summary>Three decimals, invariant - these are numbers to compare, not to read as prose.</summary>
        static string Fixed(float value)
        {
            return value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Builds the Desktop model list, exactly as DesktopService will in M4.</summary>
        static IList<Desktop> Snapshot(VirtualDesktopApi api)
        {
            Guid[] ids = api.GetDesktopIds();
            Guid current = api.GetCurrentId();

            var list = new List<Desktop>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
                list.Add(new Desktop(ids[i], i, VirtualDesktopApi.GetName(ids[i]), ids[i] == current));

            return list;
        }

        static Desktop Pick(IList<Desktop> desktops, string[] args, int argIndex)
        {
            int n;
            if (args.Length <= argIndex || !int.TryParse(args[argIndex], out n))
            {
                Console.WriteLine("Expected a desktop number (1.." + desktops.Count + ")");
                return null;
            }
            if (n < 1 || n > desktops.Count)
            {
                Console.WriteLine("Desktop " + n + " does not exist (have " + desktops.Count + ")");
                return null;
            }
            return desktops[n - 1];
        }

        static IntPtr FindWindowByTitle(string needle)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                if (!Native.IsAltTabWindow(hwnd)) return true;

                string title = Native.GetText(hwnd);
                if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }
    }
}
