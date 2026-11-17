using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// What a close-all actually closes once CloseAllWindow's confirmation has been answered.
    ///
    /// Apart from Controller so --closeall can drive exactly this rule against real windows,
    /// the way MarkSync is apart for --markstress. The controller takes the closeable list
    /// again after the answer (the window is modeless, so the desktop moved on under it)
    /// and hands that list and the ticked handles here.
    /// </summary>
    static class CloseSelection
    {
        /// <summary>
        /// Removing: the whole of <paramref name="current"/>, whatever was ticked. The
        /// dialog only offers remove with every row ticked, and a desktop that is to be
        /// removed has to empty - anything left on it would be carried to a neighbour by
        /// the shell, which is what greying the button out with a row unticked prevents.
        /// A window that opened while the dialog was up is closed with the rest, as it was
        /// before the checklist; the run's own sweep would catch it a moment later anyway.
        ///
        /// Keeping the desktop: only the windows in <paramref name="current"/> that were
        /// ticked, in current's order. That drops a ticked window that closed or left the
        /// desktop while the dialog was up, and it leaves alone a window that arrived in
        /// that time. The user never saw that one in the list, so they never chose to close
        /// it. Before the checklist it would have been closed, since the count was all
        /// there was to go on and "all" meant all.
        /// </summary>
        public static List<WindowEntry> Resolve(bool removing, ICollection<IntPtr> ticked,
                                                List<WindowEntry> current)
        {
            if (removing) return new List<WindowEntry>(current);

            var result = new List<WindowEntry>();
            if (ticked == null) return result;

            for (int i = 0; i < current.Count; i++)
                if (ticked.Contains(current[i].Handle)) result.Add(current[i]);

            return result;
        }
    }
}
