using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// Entry point, and nothing else.
    ///
    /// Two ways in. No arguments runs the app: one Controller, which owns everything from
    /// there. Anything else is a selftest command, handled by SelfTest against the real
    /// shell - see that class for why they exist.
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

            return SelfTest.Run(args);
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
    }
}
