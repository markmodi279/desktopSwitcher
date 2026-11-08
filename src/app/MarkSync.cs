using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// The rules that keep the marks in step with the desktops there are: carry them through
    /// a reorder's new GUIDs, hold pruning off while a reorder is half done, adopt by position
    /// only in the first seconds after start, let go of a removed desktop's mark after that,
    /// and write the file when anything changed.
    ///
    /// Apart from Controller so --markstress can drive exactly these rules against the real
    /// shell, with Persist off so it never writes marks.ini. The controller owns one, feeds it
    /// DesktopService.Recreated, brackets every reorder with Begin/EndReorder, and calls Sync
    /// whenever the desktops may have changed.
    /// </summary>
    sealed class MarkSync
    {
        /// <summary>
        /// How long after start a mark whose desktop has vanished may be adopted by the
        /// desktop at its old position, instead of being let go. Long enough for EnsureCount's
        /// desktops to arrive at login; short enough that a desktop removed and another
        /// created soon after cannot inherit by accident. See MarkStore.Adopt.
        /// </summary>
        public const int AdoptMs = 15000;

        public readonly MarkStore Store;

        int _since;
        int _adoptMs = AdoptMs;
        bool _dirty;
        int _reordering;

        public MarkSync(MarkStore store)
        {
            Store = store;
            Persist = true;
            _since = Environment.TickCount;
        }

        /// <summary>Whether a change is written to marks.ini. Off for the selftests.</summary>
        public bool Persist { get; set; }

        /// <summary>
        /// Starts the adoption window now, lasting <paramref name="adoptMs"/>. The controller
        /// calls it once the service is up; --markstress passes 0 to test the rules that hold
        /// for the rest of a session.
        /// </summary>
        public void Start(int adoptMs)
        {
            _since = Environment.TickCount;
            _adoptMs = adoptMs;
        }

        public bool Settling { get { return unchecked(Environment.TickCount - _since) < _adoptMs; } }

        /// <summary>
        /// A reorder is running. Nested calls are counted, so an inner End cannot reopen
        /// pruning under an outer reorder still running.
        ///
        /// Why it is held off: COM's modal loop can in principle deliver a queued notification
        /// mid-run, and a DesktopsChanged from there shows a row with an old GUID gone and its
        /// replacement not yet carried - which Prune would read as a desktop removed, and let
        /// its mark go.
        /// </summary>
        public void BeginReorder() { _reordering++; }

        public void EndReorder() { if (_reordering > 0) _reordering--; }

        public bool Reordering { get { return _reordering > 0; } }

        /// <summary>DesktopService.Recreated: the mark follows its desktop to the new GUID.</summary>
        public void Recreated(Guid oldId, Guid newId)
        {
            if (Store.Carry(oldId, newId)) _dirty = true;
        }

        /// <summary>
        /// Brings the store into line with <paramref name="live"/>, and writes it if anything
        /// changed. In the adoption window a vanished desktop's mark may pass to whichever
        /// unmarked desktop now stands where it stood, and nothing is let go; after it, a
        /// vanished desktop's mark goes to Recent. Does nothing inside a reorder. True when a
        /// live desktop's mark changed - which only adoption does - and the strip wants telling.
        /// </summary>
        public bool Sync(IList<Desktop> live)
        {
            if (Reordering) return false;

            bool settling = Settling;
            bool adopted = settling && Store.Adopt(live);
            bool changed = Store.Prune(live, !settling);

            if (adopted || changed || _dirty)
            {
                if (Persist) Store.Save();
                _dirty = false;
            }
            return adopted;
        }

        /// <summary>Sets or clears a desktop's mark and writes the file. True if anything changed.</summary>
        public bool Set(Guid id, DesktopMark mark, int slot)
        {
            if (!Store.Set(id, mark, slot)) return false;
            if (Persist) Store.Save();
            return true;
        }
    }
}
