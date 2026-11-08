using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopSwitcher
{
    /// <summary>
    /// --marks: the desktop marks, checked without changing anything.
    ///
    /// Three parts. The text form - every mark written and read back, and what must be
    /// refused. The store - carrying a mark through a reorder, letting a removed desktop's
    /// mark go to Recent, the startup adoption by position, Recent's cap - against synthetic
    /// GUIDs, never marks.ini. And the strip's arithmetic over buttons of mixed widths, which
    /// is the part most likely to fail quietly: a drop slot or a step-aside a few pixels out
    /// draws a row that looks nearly right and lands the dropped button with a jump.
    ///
    /// Then, for the eye, what marks.ini holds against the desktops there are. The file is
    /// read, never written.
    /// </summary>
    static class MarksTest
    {
        static int _failures;

        public static int Run(VirtualDesktopApi api)
        {
            _failures = 0;

            Console.WriteLine("Mark text form:");
            TextForm();
            Console.WriteLine();

            Console.WriteLine("Mark store:");
            Store();
            Console.WriteLine();

            Console.WriteLine("Strip arithmetic over mixed widths:");
            Arithmetic(new[] { 34, 44, 34, 44 });
            Arithmetic(new[] { 44, 34, 34, 34, 44 });
            Arithmetic(new[] { 34, 34, 34 });
            Arithmetic(new[] { 55, 34, 44, 34, 34, 55 });
            Widths();
            Console.WriteLine();

            Live(api);

            Console.WriteLine(_failures == 0 ? "OK" : "FAILED - " + _failures + " check(s)");
            return _failures == 0 ? 0 : 1;
        }

        static void Check(bool ok, string what)
        {
            if (ok) return;
            _failures++;
            Console.WriteLine("  FAIL  " + what);
        }

        // --- text ---------------------------------------------------------------

        static void TextForm()
        {
            var marks = new List<DesktopMark>
            {
                DesktopMark.Letters("DS"), DesktopMark.Letters("c"), DesktopMark.Letters("9z"),
                DesktopMark.Swatch(Color.FromArgb(0x16, 0xC6, 0x0C)), DesktopMark.Swatch(Color.FromArgb(128, 1, 2, 3)),
            };
            foreach (char c in MarkPainter.Glyphs) marks.Add(DesktopMark.Glyph(c));
            foreach (Color c in MarkPainter.Colors) marks.Add(DesktopMark.Swatch(c));

            int n = 0;
            foreach (DesktopMark m in marks)
            {
                DesktopMark back = DesktopMark.Parse(m.Format());
                Check(DesktopMark.Same(m, back), "round trip " + m.Format() + " -> " + (back == null ? "null" : back.Format()));
                n++;
            }
            Console.WriteLine("  " + n + " marks written and read back");

            Check(DesktopMark.Letters("c").Text == "C", "letters upper-cased");
            Check(DesktopMark.Letters("campsez_web").Text == "CA", "letters cut to two");
            Check(DesktopMark.Letters("a-b").Text == "AB", "punctuation dropped from letters");
            Check(DesktopMark.Letters("--") == null, "letters with nothing left is no mark");
            Check(DesktopMark.Swatch(Color.FromArgb(128, 1, 2, 3)).Color.A == 255, "colour stored opaque");

            string[] refused = { "", "letters:", "glyph:0041", "glyph:zz", "color:#12345", "color:red", "shape:circle", "DS" };
            foreach (string text in refused)
                Check(DesktopMark.Parse(text) == null, "should refuse \"" + text + "\"");
            Console.WriteLine("  " + refused.Length + " malformed forms refused");

            Check(DesktopMark.Parse(" Letters : ds ").Text == "DS", "kind and value trimmed, case-insensitive kind");
        }

        // --- store --------------------------------------------------------------

        static List<Desktop> Row(params Guid[] ids)
        {
            var list = new List<Desktop>();
            for (int i = 0; i < ids.Length; i++) list.Add(new Desktop(ids[i], i, null, i == 0));
            return list;
        }

        static void Store()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
            DesktopMark ds = DesktopMark.Letters("DS"), code = DesktopMark.Glyph('\uE943');
            DesktopMark green = DesktopMark.Swatch(Color.FromArgb(0x16, 0xC6, 0x0C));

            var store = new MarkStore();
            store.Set(a, ds, 0);
            store.Set(b, code, 1);
            store.Set(c, green, 2);

            // Round trip through the file's text.
            MarkStore back = MarkStore.FromText(store.Render());
            Check(back.Count == 3, "file round trip keeps three marks, got " + back.Count);
            Check(DesktopMark.Same(back.Get(b), code) && back.SlotOf(b) == 1, "file round trip keeps mark and slot");
            Check(back.Recent.Count == 3 && DesktopMark.Same(back.Recent[0], green), "file round trip keeps Recent in order");
            Console.WriteLine("  marks.ini text round-trips (" + store.Render().Split('\n').Length + " lines)");

            // A reorder recreates b as d.
            Check(store.Carry(b, d), "carry reports the mark it moved");
            Check(store.Get(b) == null && DesktopMark.Same(store.Get(d), code), "carried mark is on the new GUID only");
            Check(!store.Carry(Guid.NewGuid(), Guid.NewGuid()), "carrying an unmarked desktop changes nothing");

            // Slots follow the row; nothing is lost while forget is off.
            Check(store.Prune(Row(d, a, c), false), "slot update reports a change");
            Check(store.SlotOf(d) == 0 && store.SlotOf(a) == 1, "slots follow the new order");
            store.Prune(Row(a, c), false);
            Check(store.Count == 3, "startup prune kept every mark");

            // c removed: its mark goes to the front of Recent.
            store.Prune(Row(d, a, c), true);
            Check(store.Prune(Row(d, a), true), "removal reports a change");
            Check(store.Get(c) == null && DesktopMark.Same(store.Recent[0], green), "removed desktop's mark is first in Recent");
            Check(!store.Prune(new List<Desktop>(), true) && store.Count == 2, "an empty row (shell away) forgets nothing");
            Console.WriteLine("  carry, slot update and removal behave");

            // Startup adoption: d's desktop has vanished and an unmarked e stands in its slot.
            Guid e = Guid.NewGuid(), f = Guid.NewGuid();
            Check(store.Adopt(Row(e, a)), "adoption reports a change");
            Check(DesktopMark.Same(store.Get(e), code) && store.Get(d) == null, "vanished desktop's mark adopted by its slot's heir");
            Check(!store.Adopt(Row(e, a, f)), "a marked slot never adopts again");
            store.Set(f, ds, 5);
            Check(!store.Adopt(Row(e, a)), "an orphan whose slot is past the row stays put");
            Console.WriteLine("  startup adoption by position behaves");

            // Recent: deduplicated, newest first, capped.
            var r = new MarkStore();
            for (int i = 0; i < MarkStore.RecentMax + 4; i++) r.Set(Guid.NewGuid(), DesktopMark.Glyph(MarkPainter.Glyphs[i]), i);
            r.Set(Guid.NewGuid(), DesktopMark.Glyph(MarkPainter.Glyphs[MarkStore.RecentMax + 3]), 0);
            Check(r.Recent.Count == MarkStore.RecentMax, "Recent capped at " + MarkStore.RecentMax + ", got " + r.Recent.Count);
            int copies = 0;
            foreach (DesktopMark m in r.Recent)
                if (m.Equals(DesktopMark.Glyph(MarkPainter.Glyphs[MarkStore.RecentMax + 3]))) copies++;
            Check(copies == 1, "Recent holds a mark once");
            Console.WriteLine("  Recent deduplicated and capped at " + MarkStore.RecentMax);

            // A bad line costs that line only.
            MarkStore rough = MarkStore.FromText(
                "{" + a + "}=letters:OK, 1\nnot a guid=letters:NO\n{" + b + "}=shape:circle\n{" + c + "}=color:#00B7C3\nrecent=glyph:zz\n");
            Check(rough.Count == 2, "unreadable lines skipped, readable ones kept - got " + rough.Count);
        }

        // --- arithmetic -----------------------------------------------------------

        static void Arithmetic(int[] widths)
        {
            int n = widths.Length;
            int total = 0;
            var lefts = new int[n];
            for (int i = 0; i < n; i++) { lefts[i] = total; total += widths[i]; }

            int pairs = 0;
            for (int from = 0; from < n; from++)
            {
                for (int to = 0; to < n; to++)
                {
                    // The row after the move, laid out as the strip will lay it out.
                    var order = new List<int>();
                    for (int i = 0; i < n; i++) order.Add(i);
                    order.RemoveAt(from);
                    order.Insert(to, from);
                    var newLeft = new int[n];   // by original index
                    int x = 0;
                    foreach (int i in order) { newLeft[i] = x; x += widths[i]; }

                    // Where SlotLeft says the dragged button lands is where it will be drawn.
                    Check(SwitcherStrip.SlotLeft(widths, from, to) == newLeft[from],
                          Describe(widths) + " " + from + "->" + to + ": SlotLeft " + SwitcherStrip.SlotLeft(widths, from, to) + ", laid out " + newLeft[from]);

                    // Held exactly over that slot, the drop slot is that slot.
                    Check(SwitcherStrip.DropSlot(widths, from, newLeft[from]) == to,
                          Describe(widths) + " " + from + "->" + to + ": DropSlot at the slot's own edge gave " + SwitcherStrip.DropSlot(widths, from, newLeft[from]));

                    // Every other button, stepped aside, is exactly where the new row puts it,
                    // so the drop has nothing to jump.
                    for (int i = 0; i < n; i++)
                    {
                        if (i == from) continue;
                        int drawn = lefts[i] + SwitcherStrip.PassOffset(widths, from, to, i);
                        Check(drawn == newLeft[i],
                              Describe(widths) + " " + from + "->" + to + ": button " + i + " drawn at " + drawn + ", laid out " + newLeft[i]);
                    }
                    pairs++;
                }

                // Swept from one end to the other, the drop slot never goes backwards.
                int last = 0;
                for (int left = 0; left <= total - widths[from]; left++)
                {
                    int slot = SwitcherStrip.DropSlot(widths, from, left);
                    Check(slot >= last, Describe(widths) + " from " + from + ": drop slot went back at x=" + left);
                    last = slot;
                }
                Check(last == n - 1, Describe(widths) + " from " + from + ": the far end does not reach the last slot");
            }
            Console.WriteLine("  " + Describe(widths) + ": " + pairs + " moves land without a jump");
        }

        static void Widths()
        {
            Check(SwitcherStrip.MeasureWidth(4, 0, 34, 44, 26, 38) == SwitcherStrip.MeasureWidth(4, 34, 26, 38),
                  "no marks measures as before marks");
            Check(SwitcherStrip.MeasureWidth(4, 2, 34, 44, 26, 38) == 34 * 2 + 44 * 2 + 26 + 38, "two marked of four");
            Check(SwitcherStrip.MeasureWidth(2, 5, 34, 44, 26, 0) == 44 * 2 + 26, "marked count clamped to the desktops");
            Console.WriteLine("  MeasureWidth counts marked buttons");
        }

        static string Describe(int[] widths)
        {
            return "[" + string.Join(",", Array.ConvertAll(widths, w => w.ToString())) + "]";
        }

        // --- live -----------------------------------------------------------------

        static void Live(VirtualDesktopApi api)
        {
            MarkStore store = MarkStore.Load();
            Console.WriteLine("Saved marks (" + MarkStore.FilePath + ", read only):");

            Guid[] ids = api.GetDesktopIds();
            var live = new HashSet<Guid>(ids);
            for (int i = 0; i < ids.Length; i++)
            {
                DesktopMark m = store.Get(ids[i]);
                string name = VirtualDesktopApi.GetName(ids[i]);
                Console.WriteLine(string.Format("  [{0}] {1,-22} {2}{3}", i + 1,
                    string.IsNullOrEmpty(name) ? "Desktop " + (i + 1) : name,
                    m == null ? "-" : m.Format(),
                    m != null && store.SlotOf(ids[i]) != i ? "   (file says position " + (store.SlotOf(ids[i]) + 1) + ")" : ""));
            }

            foreach (Guid id in store.Ids)
                if (!live.Contains(id))
                    Console.WriteLine("  orphan  " + id + "  " + store.Get(id).Format() + "  (no such desktop; let go on the next change)");

            IList<DesktopMark> recent = store.Recent;
            if (recent.Count > 0)
            {
                var parts = new List<string>();
                foreach (DesktopMark m in recent) parts.Add(m.Format());
                Console.WriteLine("  recent  " + string.Join("  ", parts.ToArray()));
            }
            Console.WriteLine();
        }
    }
}
