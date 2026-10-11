using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// Install and uninstall, for people who were handed a zip rather than a repo.
    ///
    /// "Installing" is only: copy the exe to %LOCALAPPDATA%\DesktopSwitcher, point the Run
    /// key at it, start it. That is exactly where build.cmd already puts the exe, so a
    /// developer's build and an installed release are the same thing and this code never
    /// fires for the former - it is already running from the install path.
    ///
    /// The reason to copy at all is that a zip unpacks into Downloads or a temp folder.
    /// Autostart pointing there breaks the day that folder is tidied, and the running exe
    /// would be locked so the user could not delete or replace it either.
    ///
    /// No admin rights anywhere: everything is under the user's own profile and HKCU.
    /// </summary>
    static class Installer
    {
        const string ExeName = "DesktopSwitcher.exe";

        /// <summary>Registered window message that makes a running copy exit cleanly.</summary>
        public const string QuitMessageName = "DesktopSwitcher.Quit";

        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopSwitcher");
            }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }

        /// <summary>True when this process is the installed copy (or a developer build, which
        /// writes to the same path).</summary>
        public static bool IsInstalledCopy
        {
            get
            {
                return string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe,
                                     StringComparison.OrdinalIgnoreCase);
            }
        }

        // "No thanks" is remembered here so the question is not asked at every launch of a
        // portable copy. The tray menu still offers Install, so the answer is not final.
        static string DeclinedFlag { get { return Path.Combine(Config.Directory, "portable.flag"); } }

        /// <summary>
        /// Called at startup. If this is not the installed copy, asks once whether to install
        /// it. Returns true when the installed copy has been started and this process should
        /// just exit; false to carry on running from here.
        /// </summary>
        public static bool OfferInstall()
        {
            if (IsInstalledCopy) return false;

            // The "don't ask again" answer belongs to the first-install question only. An
            // installed copy means this is an update, and an update is always offered: with
            // the flag honoured here, a newer download did nothing visible at all - no dialog,
            // and the new copy then exited on the single-instance lock - so one stray No made
            // every later update silently dead.
            bool update = File.Exists(InstalledExe);

            if (!update)
            {
                try { if (File.Exists(DeclinedFlag)) return false; }
                catch (Exception) { }
            }

            string text = update
                ? "Desktop Switcher is already installed.\n\nReplace it with this version and restart it?"
                : "Install Desktop Switcher?\n\nThis copies it to:\n" + InstallDir
                  + "\n\nand starts it with Windows. No administrator rights are needed, and you can "
                  + "remove it later from the tray icon's menu.\n\n"
                  + "Yes: install it.\n"
                  + "No: just try it for now. Nothing is installed, and it stops when you exit.";

            DialogResult answer = MessageBox.Show(text, "Desktop Switcher",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer == DialogResult.Yes) return Install(!update);

            // Declining an update is not a decision about installing, so it is not remembered.
            if (update) return false;

            try
            {
                Directory.CreateDirectory(Config.Directory);
                File.WriteAllText(DeclinedFlag, "Delete this file to be asked about installing again.");
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Copies this exe into place and starts it. <paramref name="enableAutostart"/> is
        /// true for a first install; an update leaves the user's existing choice alone.
        /// Returns true when the installed copy is running, so the caller should exit.
        /// </summary>
        public static bool Install(bool enableAutostart)
        {
            try
            {
                StopInstalledCopy();
                Directory.CreateDirectory(InstallDir);

                // The old process can take a moment to let go of its exe after it dies.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Copy(Application.ExecutablePath, InstalledExe, true);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 10) throw;
                        Thread.Sleep(300);
                    }
                }

                if (enableAutostart || Controller.IsAutostartEnabled())
                    Controller.SetAutostart(true, InstalledExe);

                try { if (File.Exists(DeclinedFlag)) File.Delete(DeclinedFlag); }
                catch (Exception) { }

                Process.Start(InstalledExe);
                Log.Write("installer: installed to " + InstalledExe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("installer: install failed - " + ex);
                MessageBox.Show("Could not install Desktop Switcher.\n\n" + ex.Message
                    + "\n\nIt will keep running from where it is.",
                    "Desktop Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // Windows will not overwrite a running exe, and a second instance would exit on the
        // mutex anyway, so the old one has to go first. Matched by full path so an unrelated
        // process that happens to share the name is left alone.
        static void StopInstalledCopy()
        {
            StopOtherInstances(InstalledExe, true);
        }

        /// <summary>
        /// Stops every other running copy - only the one at <paramref name="onlyPath"/> when
        /// that is given - and returns how many are still alive.
        ///
        /// Asked politely first, killed only if that fails. A killed process never runs its
        /// shutdown, so its tray icon is never removed: Explorer keeps drawing it until the
        /// pointer crosses it. That left a dead icon beside the live one, which widened the
        /// notification area and shoved the strip left until someone opened the overflow
        /// flyout and hovered. taskkill without /F does not help - it only signals windows
        /// that are visible, and this one is hidden - so a message is posted to it directly.
        /// </summary>
        public static int StopOtherInstances(string onlyPath, bool allowKill)
        {
            int self = Process.GetCurrentProcess().Id;
            int remaining = 0;
            foreach (Process p in Process.GetProcessesByName("DesktopSwitcher"))
            {
                try
                {
                    if (p.Id == self) continue;
                    if (onlyPath != null && !string.Equals(p.MainModule.FileName, onlyPath,
                                                           StringComparison.OrdinalIgnoreCase))
                        continue;

                    RequestClose(p.Id);
                    if (p.WaitForExit(3000)) continue;

                    if (allowKill)
                    {
                        Log.Write("installer: pid " + p.Id + " did not close when asked - killing");
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    else remaining++;
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            return remaining;
        }

        // The quit message to every top-level window the process owns, hidden ones included:
        // the controller's own window is never shown, and it is the one that acts on it.
        //
        // Not WM_CLOSE: posting that to the process's windows ended it without the
        // controller's shutdown ever running, so the tray icon was left behind anyway.
        static void RequestClose(int pid)
        {
            uint quit = Native.RegisterWindowMessage(QuitMessageName);
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                uint owner;
                Native.GetWindowThreadProcessId(hwnd, out owner);
                if (owner == (uint)pid) Native.PostMessage(hwnd, (int)quit, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// Asks, then removes autostart and schedules the files for deletion. Returns true
        /// when the caller should close the app now; the delete has to wait for that, since
        /// a running exe cannot delete itself.
        /// </summary>
        public static bool ConfirmUninstall()
        {
            if (MessageBox.Show("Uninstall Desktop Switcher?\n\nIt will stop starting with Windows "
                    + "and its files will be removed.", "Desktop Switcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            bool wipeSettings = MessageBox.Show("Also delete your settings?\n\nChoose No to keep "
                    + "them in case you reinstall.", "Desktop Switcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

            try
            {
                Controller.SetAutostart(false, null);

                // The ping is a sleep: cmd has none, and the app needs a moment to finish
                // exiting (it saves lastCount on the way out, which must not recreate
                // settings we are about to delete).
                string cmd = "/c ping 127.0.0.1 -n 4 >nul & del /f /q \"" + InstalledExe + "\" & rmdir \""
                           + InstallDir + "\"";
                if (wipeSettings) cmd += " & rmdir /s /q \"" + Config.Directory + "\"";

                var psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log.Write("installer: uninstall scheduled");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not uninstall.\n\n" + ex.Message, "Desktop Switcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
    }
}
