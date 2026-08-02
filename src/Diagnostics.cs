using System;
using System.Globalization;
using System.IO;

namespace DesktopSwitcher
{
    /// <summary>
    /// Rolling diagnostic log, disabled by default.
    ///
    /// When disabled the cost of a call site is a single bool test. Callers that would
    /// need to build a string must either pass a Func&lt;string&gt; (evaluated only when
    /// enabled) or guard with `if (Log.Enabled)`, so no throwaway strings are formatted
    /// in the common case.
    ///
    /// Notification callbacks arrive on arbitrary RPC threads, so writes are serialised.
    /// </summary>
    static class Log
    {
        const long MaxBytes = 1024 * 1024;

        static readonly object Gate = new object();
        static string _path;
        static string _backupPath;
        static bool _enabled;

        public static bool Enabled { get { return _enabled; } }

        public static void Init(string path, bool enabled)
        {
            lock (Gate)
            {
                _path = path;
                _backupPath = path + ".1";
                _enabled = enabled;

                if (!_enabled) return;
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch
                {
                    // Logging must never take the app down.
                    _enabled = false;
                }
            }
        }

        public static void Write(string message)
        {
            if (!_enabled) return;
            Append(message);
        }

        /// <summary>Deferred form - the delegate runs only when logging is enabled.</summary>
        public static void Write(Func<string> message)
        {
            if (!_enabled) return;
            string text;
            try { text = message(); }
            catch (Exception ex) { text = "<log delegate threw: " + ex.Message + ">"; }
            Append(text);
        }

        static void Append(string message)
        {
            lock (Gate)
            {
                if (!_enabled) return;
                try
                {
                    Roll();
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",
                                      CultureInfo.InvariantCulture)
                                + "  " + message + Environment.NewLine;
                    File.AppendAllText(_path, line);
                }
                catch
                {
                    // Disk full, file locked, whatever - never propagate.
                }
            }
        }

        static void Roll()
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            if (File.Exists(_backupPath)) File.Delete(_backupPath);
            File.Move(_path, _backupPath);
        }
    }
}
