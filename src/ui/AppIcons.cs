using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopSwitcher
{
    /// <summary>
    /// The icon beside each window row in the modern panel, cached for the life of the
    /// process.
    ///
    /// The exe's own art comes first, asked for at the row's exact pixel size, so it is
    /// crisp at 125% rather than a 16 stretched or a 32 squashed. Only when the exe gives
    /// nothing - a packaged app's exe often carries no useful icon, and an elevated process
    /// cannot even be opened for its path - does it fall back to the icon the window shows
    /// for itself, which is the same picture the taskbar uses.
    ///
    /// Cached because a hover is the only thing that asks and it asks a lot: moving along
    /// the strip rebuilds the panel at every button, and PrivateExtractIcons opens and
    /// parses the file each time. Keyed by path, so ten Chrome windows are one entry. A
    /// window-icon fallback has no path to key by and is keyed by its handle instead; those
    /// are the entries that could pile up, so the whole cache is dropped once it passes a
    /// size no ordinary session reaches.
    ///
    /// Null means "no icon to be had", and that answer is cached too, so a process that
    /// cannot be read is not retried on every hover. The panel draws a letter tile then.
    /// </summary>
    static class AppIcons
    {
        const int MaxEntries = 256;

        static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        public static Bitmap Get(WindowEntry window, int size)
        {
            if (window == null || size <= 0) return null;

            string key = window.Path.Length > 0
                ? window.Path + "|" + size
                : "hwnd:" + window.Handle.ToInt64() + "|" + size;

            Bitmap icon;
            if (Cache.TryGetValue(key, out icon)) return icon;

            if (Cache.Count >= MaxEntries) Clear();

            icon = FromExe(window.Path, size);
            if (icon == null) icon = FromWindow(window.Handle);

            Cache[key] = icon;
            return icon;
        }

        static Bitmap FromExe(string path, int size)
        {
            IntPtr handle = Native.ExtractIcon(path, size);
            if (handle == IntPtr.Zero) return null;

            try
            {
                return Copy(handle);
            }
            finally
            {
                Native.DestroyIcon(handle);
            }
        }

        /// <summary>Borrowed from the window, so copied and never destroyed.</summary>
        static Bitmap FromWindow(IntPtr hwnd)
        {
            IntPtr handle = Native.GetWindowIcon(hwnd);
            if (handle == IntPtr.Zero) return null;
            return Copy(handle);
        }

        /// <summary>
        /// A Bitmap of its own, alpha intact. Icon.FromHandle wraps the handle without owning
        /// it, so the copy has to be made before the caller's DestroyIcon - drawing straight
        /// from the wrapper later would read a freed icon.
        /// </summary>
        static Bitmap Copy(IntPtr handle)
        {
            try
            {
                using (Icon icon = Icon.FromHandle(handle))
                    return icon.ToBitmap();
            }
            catch (Exception ex)
            {
                Log.Write("icons: copy failed - " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Forgets every entry without disposing any. The panel on screen holds some of these
        /// in its rows, and a disposed Bitmap there would throw on the next frame; dropped
        /// here, they are freed once nothing draws them.
        /// </summary>
        static void Clear()
        {
            Cache.Clear();
        }
    }
}
