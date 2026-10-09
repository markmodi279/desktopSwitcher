using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// Notices config.ini being saved by something other than this app and hands over
    /// what it now says, so a hand edit applies the moment Notepad saves it rather than at
    /// the next "Reload settings".
    ///
    /// Three things make this less simple than a FileSystemWatcher and a callback:
    ///
    /// One save is several events. Notepad truncates and then writes, other editors write
    /// a temporary file and rename it over the original, and every one of those steps is
    /// an event. Acting on the first would read a half-written or empty file, so events
    /// only restart a short timer and the file is read once it has gone quiet.
    ///
    /// The file can still be held when the timer fires - an editor mid-write, an antivirus
    /// scan. An IOException is a "not yet", not a failure: it re-arms the timer, a few
    /// times, before giving up until the next event.
    ///
    /// The app writes this file itself, constantly: a desktop added or removed saves the
    /// count, and the Settings window saves on every change. Each of those raises the same
    /// events as a hand edit, and reloading them would re-apply - and rebuild the strip
    /// for - a config that is already live. So the text read back is compared with
    /// Config.LastWritten, the text this process last wrote, and a match means there is
    /// nothing new. A save that straddles the quiet period and is read twice gets past
    /// that, and is caught by the controller instead: what it hands over is the same as
    /// what is live.
    ///
    /// Events arrive on the UI thread: SynchronizingObject is the Controller, which is a
    /// Form, so the FileSystemWatcher marshals them there itself and the timer is an
    /// ordinary WinForms one.
    /// </summary>
    sealed class ConfigWatcher : IDisposable
    {
        const int QuietMs = 300;
        const int MaxRetries = 5;

        readonly Action<Config> _changed;
        readonly Timer _quiet;
        FileSystemWatcher _fsw;
        int _retries;

        public ConfigWatcher(ISynchronizeInvoke ui, Action<Config> changed)
        {
            _changed = changed;

            _quiet = new Timer();
            _quiet.Interval = QuietMs;
            _quiet.Tick += OnQuiet;

            // The directory exists by now - the controller writes the file on first run
            // before it starts this - but a watcher on a missing directory throws, and a
            // settings convenience is no reason for the app not to start.
            try
            {
                _fsw = new FileSystemWatcher(Config.Directory, Path.GetFileName(Config.FilePath));
                _fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                _fsw.SynchronizingObject = ui;
                _fsw.Changed += OnEvent;
                _fsw.Created += OnEvent;
                _fsw.Renamed += OnEvent;
                _fsw.Error += OnError;
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Write("config watcher: not watching - " + ex.Message);
                if (_fsw != null) { _fsw.Dispose(); _fsw = null; }
            }
        }

        void OnEvent(object sender, FileSystemEventArgs e)
        {
            _retries = 0;
            _quiet.Stop();
            _quiet.Start();
        }

        /// <summary>
        /// The watcher's buffer overflowed or the directory went away. Either way some
        /// event may have been lost, so look at the file once more rather than assume.
        /// </summary>
        void OnError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            Log.Write("config watcher: " + (ex != null ? ex.Message : "error") + " - rechecking");
            OnEvent(sender, null);
        }

        void OnQuiet(object sender, EventArgs e)
        {
            _quiet.Stop();

            string text;
            try
            {
                if (!File.Exists(Config.FilePath)) return;   // deleted; the next save recreates it
                text = File.ReadAllText(Config.FilePath);
            }
            catch (IOException)
            {
                if (++_retries <= MaxRetries) _quiet.Start();
                else Log.Write("config watcher: file stayed locked, skipped this change");
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Write("config watcher: cannot read the file - " + ex.Message);
                return;
            }

            if (text == Config.LastWritten) return;
            Config.Adopt(text);

            _changed(Config.FromText(text));
        }

        public void Dispose()
        {
            _quiet.Stop();
            _quiet.Dispose();
            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
        }
    }
}
