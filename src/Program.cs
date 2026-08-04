using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopSwitcher
{
    /// <summary>
    /// M2: console selftest harness for the COM layer. The real controller window
    /// replaces the default (no-argument) path in M7.
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Before any window exists, or the taskbar strip lands at the wrong
            // coordinates on a scaled display.
            Native.EnableDpiAwareness();

            Config cfg = Config.Load();
            Log.Init(Config.LogPath, cfg.Diagnostics);

            // No arguments means "run the app". Everything else is selftest.
            if (args.Length == 0) return RunApp(cfg);

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
                    case "--move":    return CmdMove(api, args);
                    case "--where":   return CmdWhere(api, args);
                    case "--soak":    return CmdSoak(api, args);
                    case "--watch":   return CmdWatch(api, args);
                    case "--service": return CmdService(api, args);
                    case "--taskbar": return CmdTaskbar();
                    case "--testwindow": return CmdTestWindow(args);
                    case "--strip":   return CmdStrip(api, args);
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

        /// <summary>
        /// Normal operation. A single instance only: two controllers would dock two
        /// strips into the taskbar and fight over position.
        /// </summary>
        static int RunApp(Config cfg)
        {
            bool isFirst;
            using (var single = new System.Threading.Mutex(true, "Local\\DesktopSwitcherInstance", out isFirst))
            {
                if (!isFirst)
                {
                    Log.Write("startup: another instance is already running");
                    return 0;
                }

                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                using (var controller = new Controller(cfg))
                {
                    System.Windows.Forms.Application.Run(controller);
                }

                GC.KeepAlive(single);
                return 0;
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
            Console.WriteLine("  --move N <title>       move a window whose title contains <title>");
            Console.WriteLine("  --where <title>        report whether that window is on the current desktop");
            Console.WriteLine("  --soak N               poll for N seconds; survives an Explorer restart");
            Console.WriteLine("  --watch N              listen for change notifications for N seconds");
            Console.WriteLine("  --service N            run DesktopService for N seconds and report its events");
            Console.WriteLine("  --taskbar              print taskbar geometry and computed strip bounds");
            Console.WriteLine("  --testwindow N         dock a plain marker window in the taskbar for N seconds");
            Console.WriteLine("  --strip N              run the real switcher strip for N seconds");
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
                IList<string> windows = inventory.WindowsOn(d.Id);
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
                    foreach (string title in windows)
                        Console.WriteLine("         " + title);
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

        static int CmdTaskbar()
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
            System.Drawing.Color sampled;
            int probeWidth = 2 * host.Scale(cfg.ButtonWidth) + host.Scale(cfg.PlusWidth);
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
                                              (uint)cfg.TooltipDelayMs, host.DpiScale);

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
                    IList<string> windows = inventory.WindowsOn(d.Id);

                    var lines = new List<string>();
                    if (windows.Count == 0) lines.Add("- empty -");
                    else for (int i = 0; i < windows.Count && i < cfg.TooltipMaxWindows; i++)
                        lines.Add(windows[i]);

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

        // --- helpers ----------------------------------------------------------

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
