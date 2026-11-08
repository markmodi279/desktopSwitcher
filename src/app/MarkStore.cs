using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopSwitcher
{
    /// <summary>
    /// Every desktop's mark, by desktop GUID, and the marks recently let go of - kept in
    /// %APPDATA%\DesktopSwitcher\marks.ini.
    ///
    /// Its own file rather than a section of config.ini. config.ini is rewritten whole from
    /// Config.All, the settings table, and watched for hand edits by ConfigWatcher; a block
    /// of GUIDs that changes every time a desktop is reordered or removed would need special
    /// handling in both, and would make a hand-edited config.ini liable to be rewritten under
    /// the person editing it. A second small file needs neither. It is read once at startup:
    /// a hand edit while the app runs is overwritten by the next change, and that is stated
    /// at the top of the file.
    ///
    /// KEYED BY GUID, AND GUIDS CHANGE. A desktop's GUID is its only stable identity - the
    /// index renumbers on every removal - but VirtualDesktopApi.Reorder recreates the desktops
    /// it moves, under new GUIDs. Explorer's names survive that only because Reorder copies
    /// them across, and marks have to be carried the same way: Carry, fed from
    /// DesktopService.Recreated, which is raised for each recreated desktop before the model
    /// hears about the new order. So the strip never draws a moved desktop unmarked.
    ///
    /// A desktop that is simply gone - removed from the strip, Task View, or a close-all -
    /// takes its mark with it, but the mark goes to the front of Recent, so putting the same
    /// one on its replacement is one click in the picker. That is Prune, and it must never
    /// run while a reorder is half done, when an old GUID has gone and its replacement has
    /// not been carried yet; the controller holds it off around a reorder.
    ///
    /// Every entry also records the position its desktop last had, for one case: Explorer
    /// keeps the desktops' GUIDs in the registry across a reboot, but the app was written on
    /// the premise that Windows can forget the desktops themselves (see Config.LastCount), and
    /// desktops recreated at login come back with new GUIDs. Adopt, run only in the first
    /// seconds after start, gives a mark whose desktop has vanished to the unmarked desktop
    /// now standing where it stood. Outside that window it never guesses.
    /// </summary>
    sealed class MarkStore
    {
        /// <summary>How many let-go marks the picker offers. One row of chips.</summary>
        public const int RecentMax = 8;

        sealed class Entry
        {
            public DesktopMark Mark;
            public int Slot;   // 0-based position the desktop had when last seen
        }

        readonly Dictionary<Guid, Entry> _marks = new Dictionary<Guid, Entry>();
        readonly List<DesktopMark> _recent = new List<DesktopMark>();

        public static string FilePath { get { return Path.Combine(Config.Directory, "marks.ini"); } }

        /// <summary>How many desktops are marked, live or not.</summary>
        public int Count { get { return _marks.Count; } }

        /// <summary>This desktop's mark, or null.</summary>
        public DesktopMark Get(Guid id)
        {
            Entry e;
            return _marks.TryGetValue(id, out e) ? e.Mark : null;
        }

        /// <summary>Most recent first. A copy; the store's own list is not handed out.</summary>
        public IList<DesktopMark> Recent { get { return _recent.ToArray(); } }

        /// <summary>Every marked GUID, for --marks.</summary>
        public ICollection<Guid> Ids { get { return new List<Guid>(_marks.Keys); } }

        public int SlotOf(Guid id)
        {
            Entry e;
            return _marks.TryGetValue(id, out e) ? e.Slot : -1;
        }

        /// <summary>
        /// Sets or, with null, clears a desktop's mark. True if anything changed. A mark set
        /// goes to the front of Recent as well, so a mark used on one desktop is there to
        /// reuse on the next; a mark cleared goes there too, so Clear is undoable.
        /// </summary>
        public bool Set(Guid id, DesktopMark mark, int slot)
        {
            Entry e;
            bool had = _marks.TryGetValue(id, out e);

            if (mark == null)
            {
                if (!had) return false;
                _marks.Remove(id);
                Remember(e.Mark);
                return true;
            }

            if (had && DesktopMark.Same(e.Mark, mark) && e.Slot == slot) return false;

            _marks[id] = new Entry { Mark = mark, Slot = slot };
            Remember(mark);
            return true;
        }

        void Remember(DesktopMark mark)
        {
            if (mark == null) return;
            _recent.RemoveAll(m => m.Equals(mark));
            _recent.Insert(0, mark);
            if (_recent.Count > RecentMax) _recent.RemoveRange(RecentMax, _recent.Count - RecentMax);
        }

        /// <summary>
        /// A reorder recreated <paramref name="oldId"/> as <paramref name="newId"/>: the mark
        /// moves with it. True if there was one to move.
        /// </summary>
        public bool Carry(Guid oldId, Guid newId)
        {
            Entry e;
            if (!_marks.TryGetValue(oldId, out e)) return false;

            _marks.Remove(oldId);
            _marks[newId] = e;
            return true;
        }

        /// <summary>
        /// Forgets marks whose desktop is no longer in <paramref name="live"/>, sending each
        /// to Recent, and brings every surviving entry's slot up to date. True if anything
        /// changed and the file wants writing.
        ///
        /// Does nothing on an empty list: the service hands back nothing while the shell is
        /// unavailable, and that is not every desktop having been removed.
        ///
        /// With <paramref name="forget"/> false only the slots are updated and nothing is let
        /// go - for the startup window, when a desktop missing now may be one Adopt is about
        /// to hand on once the desktops restored at login arrive.
        /// </summary>
        public bool Prune(IList<Desktop> live, bool forget)
        {
            if (live == null || live.Count == 0) return false;

            var slots = new Dictionary<Guid, int>();
            for (int i = 0; i < live.Count; i++) slots[live[i].Id] = i;

            bool changed = false;
            foreach (Guid id in new List<Guid>(_marks.Keys))
            {
                int slot;
                Entry e = _marks[id];
                if (!slots.TryGetValue(id, out slot))
                {
                    if (!forget) continue;
                    Log.Write(delegate { return "marks: desktop " + id + " gone, " + e.Mark + " to recent"; });
                    _marks.Remove(id);
                    Remember(e.Mark);
                    changed = true;
                }
                else if (e.Slot != slot)
                {
                    e.Slot = slot;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Startup only: each mark whose desktop is not in <paramref name="live"/> goes to the
        /// desktop now at its recorded slot, if that desktop has no mark of its own. True if
        /// any mark moved. See the class comment for why, and why only at startup.
        /// </summary>
        public bool Adopt(IList<Desktop> live)
        {
            if (live == null || live.Count == 0) return false;

            var present = new HashSet<Guid>();
            foreach (Desktop d in live) present.Add(d.Id);

            bool changed = false;
            foreach (Guid id in new List<Guid>(_marks.Keys))
            {
                if (present.Contains(id)) continue;

                Entry e = _marks[id];
                if (e.Slot < 0 || e.Slot >= live.Count) continue;

                Guid heir = live[e.Slot].Id;
                if (_marks.ContainsKey(heir)) continue;

                Log.Write(delegate { return "marks: " + e.Mark + " adopted by desktop " + (e.Slot + 1) + " (" + heir + ")"; });
                _marks.Remove(id);
                _marks[heir] = e;
                changed = true;
            }
            return changed;
        }

        // --- file -----------------------------------------------------------------

        public static MarkStore Load()
        {
            string path = FilePath;
            if (!File.Exists(path)) return new MarkStore();

            try
            {
                return FromText(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Write("marks: load failed, starting empty - " + ex.Message);
                return new MarkStore();
            }
        }

        /// <summary>
        /// Reads what Render writes. A line it cannot read is skipped, never fatal: losing one
        /// mark to a bad hand edit is better than losing all of them.
        /// </summary>
        public static MarkStore FromText(string text)
        {
            var store = new MarkStore();
            var recent = new List<DesktopMark>();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (key.Equals("recent", StringComparison.OrdinalIgnoreCase))
                {
                    DesktopMark m = DesktopMark.Parse(value);
                    if (m != null && recent.Count < RecentMax && !recent.Contains(m)) recent.Add(m);
                    continue;
                }

                Guid id;
                if (!Guid.TryParse(key, out id)) continue;

                // "letters:DS, 3" - the slot after a comma, 1-based in the file to match the
                // number on the button. A missing or unreadable slot just disables Adopt for it.
                int slot = -1;
                int comma = value.LastIndexOf(',');
                if (comma > 0)
                {
                    int n;
                    if (int.TryParse(value.Substring(comma + 1).Trim(), out n) && n > 0) slot = n - 1;
                    value = value.Substring(0, comma).Trim();
                }

                DesktopMark mark = DesktopMark.Parse(value);
                if (mark != null) store._marks[id] = new Entry { Mark = mark, Slot = slot };
            }

            store._recent.AddRange(recent);
            return store;
        }

        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DesktopSwitcher desktop marks - what each desktop shows beside its number.");
            sb.AppendLine("# Set them from a desktop's right-click menu: Set mark...");
            sb.AppendLine("#");
            sb.AppendLine("# {desktop GUID}=kind:value, position");
            sb.AppendLine("#   kind:value is letters:DS, glyph:E943 (a Segoe MDL2 Assets codepoint),");
            sb.AppendLine("#   or color:#16C60C. Position is where the desktop last stood, 1 = first.");
            sb.AppendLine("# recent= lines are the picker's Recent row, newest first.");
            sb.AppendLine("#");
            sb.AppendLine("# Read at startup only. Edit it with the app stopped, or the next change");
            sb.AppendLine("# made from the menu writes over the edit.");
            sb.AppendLine();

            var ordered = new List<KeyValuePair<Guid, Entry>>(_marks);
            ordered.Sort((a, b) => a.Value.Slot.CompareTo(b.Value.Slot));
            foreach (var kv in ordered)
            {
                string slot = kv.Value.Slot >= 0 ? ", " + (kv.Value.Slot + 1) : "";
                sb.AppendLine(kv.Key.ToString("B").ToUpperInvariant() + "=" + kv.Value.Mark.Format() + slot);
            }

            if (_recent.Count > 0) sb.AppendLine();
            foreach (DesktopMark m in _recent)
                sb.AppendLine("recent=" + m.Format());

            return sb.ToString();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Config.Directory)) Directory.CreateDirectory(Config.Directory);
                File.WriteAllText(FilePath, Render());
            }
            catch (Exception ex)
            {
                Log.Write("marks: save failed - " + ex.Message);
            }
        }
    }
}
