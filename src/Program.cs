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
            Config cfg = Config.Load();
            Log.Init(Config.LogPath, cfg.Diagnostics);

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "--list";
            var api = new VirtualDesktopApi();

            try
            {
                switch (command)
                {
                    case "--list":    return CmdList(api);
                    case "--switch":  return CmdSwitch(api, args);
                    case "--create":  return CmdCreate(api);
                    case "--remove":  return CmdRemove(api, args);
                    case "--move":    return CmdMove(api, args);
                    case "--where":   return CmdWhere(api, args);
                    case "--soak":    return CmdSoak(api, args);
                    case "--watch":   return CmdWatch(api, args);
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
            Console.WriteLine("DesktopSwitcher - M2 selftest");
            Console.WriteLine();
            Console.WriteLine("  --list                 show all desktops (default)");
            Console.WriteLine("  --switch N             switch to desktop N (1-based)");
            Console.WriteLine("  --create               create a new desktop");
            Console.WriteLine("  --remove N             remove desktop N");
            Console.WriteLine("  --move N <title>       move a window whose title contains <title>");
            Console.WriteLine("  --where <title>        report whether that window is on the current desktop");
            Console.WriteLine("  --soak N               poll for N seconds; survives an Explorer restart");
            Console.WriteLine("  --watch N              listen for change notifications for N seconds");
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
