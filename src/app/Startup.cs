using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// What runs before the app is allowed to touch the shell: is this a Windows build the
    /// COM interfaces were verified on, and if anything goes wrong anywhere, leave a trace.
    ///
    /// Both exist because the app is now handed to other people. On the author's machine a
    /// failure shows up in a console; on someone else's it would be a strip that never
    /// appears, or Explorer-adjacent COM calls against vtables that no longer match, with
    /// nothing to say why. A refusal with a reason, and a crash file to ask for, are the
    /// difference between a bug report and a shrug.
    /// </summary>
    static class Startup
    {
        // The virtual desktop interfaces are undocumented and re-GUIDed between Windows
        // builds. 19041 is where the layout in VirtualDesktopApi begins ("Win10 build
        // 19041+"), and 19045 is the terminal Windows 10 build, where it was verified.
        // 22000 is Windows 11, which has different GUIDs and must not be attempted.
        const int MinBuild = 19041;
        const int MaxBuild = 19045;
        const int VerifiedBuild = 19045;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OsVersionInfo
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        // Not Environment.OSVersion: with no application manifest, .NET Framework reports
        // the compatibility-shimmed version (6.2 on anything newer), which would make every
        // machine look like Windows 8. RtlGetVersion is not shimmed.
        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref OsVersionInfo info);

        /// <summary>
        /// Null when this machine is one the app can run on; otherwise a sentence for the
        /// person at the keyboard saying why not.
        /// </summary>
        public static string CheckWindows(out string versionText)
        {
            var info = new OsVersionInfo();
            info.Size = Marshal.SizeOf(typeof(OsVersionInfo));
            versionText = "unknown";
            try
            {
                if (RtlGetVersion(ref info) != 0) return null; // cannot tell: let it try
            }
            catch (Exception)
            {
                return null; // cannot tell: let it try, the crash handler is still armed
            }

            versionText = info.Major + "." + info.Minor + "." + info.Build;

            if (info.Major != 10)
                return "Desktop Switcher needs Windows 10 (22H2). This is Windows version "
                     + versionText + ".";

            if (info.Build >= 22000)
                return "Desktop Switcher does not support Windows 11 (build " + info.Build + "). "
                     + "It relies on Windows 10 shell interfaces that Windows 11 changed.";

            if (info.Build < MinBuild || info.Build > MaxBuild)
                return "Desktop Switcher needs Windows 10 version 22H2 (build " + VerifiedBuild
                     + "). This is build " + info.Build + ".\n\n"
                     + "Windows Update can bring Windows 10 up to 22H2 for free.";

            return null;
        }

        /// <summary>
        /// Arms the last-resort handlers. After this, an exception nobody caught writes
        /// crash.txt beside config.ini and tells the user, rather than WinForms' default
        /// dialog (which offers to continue into an unknown state) or silence.
        /// </summary>
        public static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Report("UI thread", e.Exception, false);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Report("background thread", e.ExceptionObject as Exception, true);
            };
        }

        public static string CrashPath
        {
            get { return Path.Combine(Config.Directory, "crash.txt"); }
        }

        /// <summary>Writes crash.txt and shows one message. Never throws.</summary>
        public static void Report(string where, Exception ex, bool fatal)
        {
            string path = null;
            try
            {
                string osText;
                CheckWindows(out osText);
                var sb = new StringBuilder();
                sb.AppendLine("Desktop Switcher crash report");
                sb.AppendLine("When:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Where:   " + where);
                sb.AppendLine("Version: " + typeof(Startup).Assembly.GetName().Version);
                sb.AppendLine("Windows: " + osText);
                sb.AppendLine();
                sb.AppendLine(ex == null ? "(no exception object)" : ex.ToString());

                Directory.CreateDirectory(Config.Directory);
                path = CrashPath;
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception)
            {
                path = null; // reporting must never take a second thing down
            }

            try
            {
                string msg = "Desktop Switcher hit an unexpected error"
                           + (fatal ? " and has to close." : ".")
                           + (ex == null ? "" : "\n\n" + ex.Message)
                           + (path == null ? "" : "\n\nDetails were saved to:\n" + path);
                MessageBox.Show(msg, "Desktop Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }

            if (fatal) Environment.Exit(1);
        }

        public static void ShowUnsupported(string reason)
        {
            MessageBox.Show(reason, "Desktop Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
