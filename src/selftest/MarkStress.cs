using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// --markstress: desktop marks pushed at their edges, three ways.
    ///
    /// INPUT. A real SwitcherStrip, parented to a test window instead of the taskbar, driven
    /// with synthetic mouse messages over hundreds of random rows of marked and unmarked
    /// buttons: drags to every slot and past both ends, drags cancelled off the strip, clicks
    /// on every button's first and last pixel, right clicks, buttons pressed mid-drag, the
    /// marks or the row changing under a drag. Each drag's (from, to) is checked against an
    /// oracle written separately from the strip's own arithmetic, the row's geometry against
    /// the marks after every change, and - by reading the screen - that the underline sits
    /// under exactly the current desktop's button once it settles.
    ///
    /// LOOK. A sheet of awkward rows - two-digit numbers, the widest letters, a yellow square
    /// on a light taskbar, every look, three DPI scales, the narrowest buttonWidth - saved as
    /// a PNG for an eye to go over. Nothing checks it automatically.
    ///
    /// SHELL. MarkSync, the controller's own rules, against the real shell on four scratch
    /// desktops on the end of the row: random reorders among them, some back to back with no
    /// pumping between, desktops removed and added, each checked for every mark still on its
    /// desktop (by name, since reorders change GUIDs), nothing let go by mistake, and every
    /// removed desktop's mark in Recent. In memory only - marks.ini is never written - and
    /// the scratch desktops are removed afterwards, whatever happened.
    ///
    /// The strip tests take the mouse capture for each drag, and real pointer movement in
    /// the middle of one can cancel it, so the pointer is best left alone while it runs.
    /// </summary>
    static class MarkStress
    {
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        static int _failures;
        static int _checks;
        static Random _rng;

        static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failures++;
            if (_failures <= 40) Console.WriteLine("  FAIL  " + what);
            else if (_failures == 41) Console.WriteLine("  (further failures not printed)");
        }

        public static int Run(VirtualDesktopApi api, string[] args)
        {
            int seed = args.Length > 1 ? int.Parse(args[1]) : Environment.TickCount;
            _rng = new Random(seed);
            Console.WriteLine("markstress, seed " + seed + " (pass it back to repeat this run)");
            Console.WriteLine();

            using (var form = new Form())
            {
                form.FormBorderStyle = FormBorderStyle.None;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.TopMost = true;
                form.BackColor = Color.Black;
                form.Location = new Point(40, 40);
                form.Size = new Size(1300, 60);
                form.Show();
                Pump(200);

                Console.WriteLine("Input: synthetic mouse over random rows");
                Input(form);
                Console.WriteLine();

                Console.WriteLine("Underline after marks and reorders change the row (read off the screen)");
                Underline(form);
                Console.WriteLine();

                Console.WriteLine("Look: rendering sheet");
                Sheet(form);
                Console.WriteLine();
            }

            Console.WriteLine("Shell: MarkSync on scratch desktops");
            Shell(api);
            Console.WriteLine();

            Console.WriteLine(_failures == 0
                ? "PASS - " + _checks + " checks"
                : "FAILED - " + _failures + " of " + _checks + " checks (seed " + seed + ")");
            return _failures == 0 ? 0 : 1;
        }

        // --- the test row ---------------------------------------------------------

        /// <summary>A strip in the test window with a row of fake desktops and marks of its own.</summary>
        sealed class Rig : IDisposable
        {
            public SwitcherStrip Strip;
            public List<Desktop> Row = new List<Desktop>();
            public Dictionary<Guid, DesktopMark> Marks = new Dictionary<Guid, DesktopMark>();
            public int ButtonWidth, MarkedWidth, PlusWidth, Height;

            public List<int[]> Reorders = new List<int[]>();
            public List<Guid> Switches = new List<Guid>();
            public int Creates;
            public List<KeyValuePair<int, Rectangle>> Menus = new List<KeyValuePair<int, Rectangle>>();
            public List<Guid> Removes = new List<Guid>();

            public Rig(IntPtr parent, int y, int count, int current, int buttonWidth, int markedWidth, int height,
                       Color background, Look look, double dpi)
            {
                ButtonWidth = buttonWidth;
                MarkedWidth = markedWidth;
                PlusWidth = (int)Math.Round(26 * dpi);
                Height = height;
                for (int i = 0; i < count; i++) Row.Add(new Desktop(Guid.NewGuid(), i, null, i == current));

                Strip = new SwitcherStrip(parent, new Rectangle(0, y, 1200, height),
                                          buttonWidth, markedWidth, PlusWidth, 0, Math.Max(2, (int)Math.Round(3 * dpi)),
                                          background, Color.FromArgb(0x4C, 0xC2, 0xFF), 400, 300, 80, dpi, look, look);
                Strip.MarkProvider = delegate(Guid id) { DesktopMark m; return Marks.TryGetValue(id, out m) ? m : null; };

                Strip.ReorderRequested += delegate(int from, int to)
                {
                    Reorders.Add(new[] { from, to });

                    // As the controller does: the new order comes back inside the call.
                    Desktop moving = Row[from];
                    Row.RemoveAt(from);
                    Row.Insert(to, moving);
                    Renumber();
                    Strip.SetDesktops(Row);
                };
                Strip.SwitchRequested += delegate(Guid id) { Switches.Add(id); };
                Strip.CreateRequested += delegate { Creates++; };
                Strip.RemoveRequested += delegate(Guid id) { Removes.Add(id); };
                Strip.ContextMenuRequested += delegate(int index, Rectangle anchor)
                {
                    Menus.Add(new KeyValuePair<int, Rectangle>(index, anchor));
                    Strip.MenuClosed();
                };
                Strip.SetDesktops(Row);
            }

            public void Renumber()
            {
                for (int i = 0; i < Row.Count; i++)
                    Row[i] = new Desktop(Row[i].Id, i, Row[i].Name, Row[i].IsCurrent);
            }

            public int[] Widths()
            {
                var w = new int[Row.Count];
                for (int i = 0; i < Row.Count; i++) w[i] = Marks.ContainsKey(Row[i].Id) ? MarkedWidth : ButtonWidth;
                return w;
            }

            public int[] Lefts()
            {
                int[] w = Widths();
                var l = new int[w.Length];
                int x = 0;
                for (int i = 0; i < w.Length; i++) { l[i] = x; x += w[i]; }
                return l;
            }

            public int Total()
            {
                int t = 0;
                foreach (int w in Widths()) t += w;
                return t;
            }

            public void ClearLog()
            {
                Reorders.Clear(); Switches.Clear(); Creates = 0; Menus.Clear(); Removes.Clear();
            }

            public void Dispose()
            {
                Strip.Dispose();
            }
        }

        static DesktopMark RandomMark()
        {
            switch (_rng.Next(3))
            {
                case 0:
                    string[] letters = { "WW", "MM", "DS", "CW", "A", "0", "88", "QJ" };
                    return DesktopMark.Letters(letters[_rng.Next(letters.Length)]);
                case 1:
                    return DesktopMark.Glyph(MarkPainter.Glyphs[_rng.Next(MarkPainter.Glyphs.Length)]);
                default:
                    return DesktopMark.Swatch(MarkPainter.Colors[_rng.Next(MarkPainter.Colors.Length)]);
            }
        }

        static void RandomMarks(Rig rig)
        {
            rig.Marks.Clear();
            int style = _rng.Next(4);   // none, all, half, random
            foreach (Desktop d in rig.Row)
            {
                bool marked = style == 1 || (style == 2 && d.Index % 2 == 0) || (style == 3 && _rng.Next(2) == 0);
                if (marked) rig.Marks[d.Id] = RandomMark();
            }
            rig.Strip.RefreshMarks();
        }

        // --- synthetic mouse --------------------------------------------------------

        static IntPtr Lp(int x, int y)
        {
            return new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        }

        static void Send(Rig rig, int msg, int keys, int x, int y)
        {
            SendMessage(rig.Strip.Handle, msg, new IntPtr(keys), Lp(x, y));
        }

        /// <summary>Press at (x0, y), move in steps to (x1, y1), release - no pumping between.</summary>
        static void Drag(Rig rig, int x0, int x1, int y1)
        {
            int y = rig.Height / 2;
            Send(rig, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, x0, y);
            const int steps = 7;
            for (int s = 1; s <= steps; s++)
            {
                int x = x0 + (x1 - x0) * s / steps;
                int yy = y + (y1 - y) * s / steps;
                Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, x, yy);
            }
            Send(rig, Native.WM_LBUTTONUP, 0, x1, y1);
        }

        static void Click(Rig rig, int x)
        {
            int y = rig.Height / 2;
            Send(rig, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, x, y);
            Send(rig, Native.WM_LBUTTONUP, 0, x, y);
        }

        /// <summary>
        /// Where a drag should land, worked out without the strip's helpers: try every slot,
        /// lay the row out with the button moved there, and take the slot whose left edge is
        /// nearest the dragged button's - the lower slot on a tie.
        /// </summary>
        static int Oracle(int[] widths, int from, float left)
        {
            int best = from;
            float bestD = float.MaxValue;
            for (int t = 0; t < widths.Length; t++)
            {
                var order = new List<int>();
                for (int i = 0; i < widths.Length; i++) if (i != from) order.Add(i);
                order.Insert(t, from);
                int x = 0;
                foreach (int i in order) { if (i == from) break; x += widths[i]; }
                float d = Math.Abs(left - x);
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        /// <summary>The row's geometry, read back through AnchorFor and Width, against the marks.</summary>
        static void CheckGeometry(Rig rig, string when)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(rig.Strip.Handle, out r)) { Check(false, when + ": no strip rect"); return; }

            int[] w = rig.Widths();
            int[] l = rig.Lefts();
            for (int i = 0; i < rig.Row.Count; i++)
            {
                Rectangle a = rig.Strip.AnchorFor(i);
                Check(a.X - r.Left == l[i] && a.Width == w[i],
                      when + ": button " + (i + 1) + " at " + (a.X - r.Left) + "+" + a.Width + ", expected " + l[i] + "+" + w[i]);
            }
            Check(rig.Strip.Width == rig.Total() + rig.PlusWidth,
                  when + ": Width " + rig.Strip.Width + ", expected " + (rig.Total() + rig.PlusWidth));
        }

        // --- input --------------------------------------------------------------------

        static void Input(Form form)
        {
            int threshold = Math.Max(SystemInformation.DragSize.Width, 8);
            int drags = 0, cancels = 0, clicks = 0, configs = 0;

            for (int config = 0; config < 40; config++)
            {
                double dpi = new[] { 1.0, 1.25, 1.5 }[_rng.Next(3)];
                int bw = (int)Math.Round(new[] { 34, 34, 24, 50 }[_rng.Next(4)] * dpi);
                int mw = (int)Math.Round(SwitcherStrip.MarkedWidthFor((int)Math.Round(bw / dpi)) * dpi);
                int count = 2 + _rng.Next(8);
                int h = (int)Math.Round(40 * dpi);

                using (var rig = new Rig(form.Handle, 0, count, _rng.Next(count), bw, mw, h,
                                         Color.FromArgb(16, 16, 16), _rng.Next(2) == 0 ? Look.Classic : Look.Modern, dpi))
                {
                    configs++;
                    RandomMarks(rig);
                    CheckGeometry(rig, "config " + config + " initial");

                    // Clicks on every button's first and last pixel, and '+' just past the end.
                    int[] l = rig.Lefts(), w = rig.Widths();
                    for (int i = 0; i < count; i++)
                    {
                        foreach (int x in new[] { l[i], l[i] + w[i] - 1 })
                        {
                            rig.ClearLog();
                            Click(rig, x);
                            clicks++;
                            Check(rig.Switches.Count == 1 && rig.Switches[0] == rig.Row[i].Id && rig.Reorders.Count == 0,
                                  "config " + config + ": click at x=" + x + " should switch to " + (i + 1) + ", got "
                                  + rig.Switches.Count + " switch(es)" + (rig.Switches.Count == 1 ? " to " + (rig.Row.FindIndex(d => d.Id == rig.Switches[0]) + 1) : ""));
                        }

                        // Right click: the menu for that button, anchored to its own width.
                        rig.ClearLog();
                        Send(rig, Native.WM_RBUTTONUP, 0, l[i] + w[i] / 2, h / 2);
                        Check(rig.Menus.Count == 1 && rig.Menus[0].Key == i && rig.Menus[0].Value.Width == w[i],
                              "config " + config + ": right click on " + (i + 1) + " gave " + rig.Menus.Count + " menu(s)"
                              + (rig.Menus.Count == 1 ? " for " + (rig.Menus[0].Key + 1) + " width " + rig.Menus[0].Value.Width : ""));
                    }
                    rig.ClearLog();
                    Click(rig, rig.Total());
                    Check(rig.Creates == 1 && rig.Switches.Count == 0, "config " + config + ": click just past the last button should hit '+'");

                    // Random drags.
                    for (int n = 0; n < 12; n++)
                    {
                        if (n % 4 == 3) { RandomMarks(rig); CheckGeometry(rig, "config " + config + " remarked"); }

                        w = rig.Widths(); l = rig.Lefts();
                        int from = _rng.Next(count);
                        int grab = _rng.Next(w[from]);
                        int x0 = l[from] + grab;

                        // Anywhere from well before the start to well past '+'.
                        int x1 = _rng.Next(-80, rig.Total() + rig.PlusWidth + 80);
                        if (Math.Abs(x1 - x0) < threshold + 1) x1 = x0 + (x1 >= x0 ? threshold + 1 : -(threshold + 1));

                        bool cancel = _rng.Next(6) == 0;
                        int y1 = cancel ? (_rng.Next(2) == 0 ? -h - 5 : 2 * h + 5) : _rng.Next(-h / 2, h + h / 2);

                        float left = Math.Max(0, Math.Min(rig.Total() - w[from], x1 - grab));
                        int expect = Oracle(w, from, left);
                        bool expectReorder = !cancel && expect != from;

                        Guid movingId = rig.Row[from].Id;
                        DesktopMark movingMark;
                        rig.Marks.TryGetValue(movingId, out movingMark);

                        rig.ClearLog();
                        Drag(rig, x0, x1, y1);
                        drags++;
                        if (cancel) cancels++;

                        string what = "config " + config + " drag " + (from + 1) + " grab " + grab + " to x=" + x1 + " y=" + y1
                                      + " widths [" + string.Join(",", Array.ConvertAll(w, v => v.ToString())) + "]";

                        Check(rig.Switches.Count == 0, what + ": a drag must never switch");
                        if (expectReorder)
                        {
                            Check(rig.Reorders.Count == 1 && rig.Reorders[0][0] == from && rig.Reorders[0][1] == expect,
                                  what + ": expected " + (from + 1) + "->" + (expect + 1) + ", got "
                                  + (rig.Reorders.Count == 0 ? "nothing" : (rig.Reorders[0][0] + 1) + "->" + (rig.Reorders[0][1] + 1)));
                        }
                        else
                        {
                            Check(rig.Reorders.Count == 0, what + ": expected no reorder" + (cancel ? " (cancelled)" : "") + ", got "
                                  + (rig.Reorders.Count == 0 ? "" : (rig.Reorders[0][0] + 1) + "->" + (rig.Reorders[0][1] + 1)));
                        }

                        // The mark went with its desktop.
                        int now = rig.Row.FindIndex(d => d.Id == movingId);
                        Check(now == (expectReorder ? expect : from), what + ": desktop ended at " + (now + 1));
                        CheckGeometry(rig, what);
                        Pump(5);
                    }

                    // A second button mid-drag is ignored; the drag still drops.
                    w = rig.Widths(); l = rig.Lefts();
                    if (count >= 3)
                    {
                        rig.ClearLog();
                        int y = h / 2;
                        Send(rig, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, l[0] + 2, y);
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, l[0] + 2 + threshold + 2, y);
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, l[count - 1] + 2, y);
                        Send(rig, Native.WM_RBUTTONUP, Native.MK_LBUTTON, l[count - 1] + 2, y);
                        Send(rig, Native.WM_MBUTTONUP, Native.MK_LBUTTON, l[count - 1] + 2, y);
                        Send(rig, Native.WM_LBUTTONUP, 0, l[count - 1] + 2, y);
                        Check(rig.Menus.Count == 0 && rig.Removes.Count == 0, "config " + config + ": right/middle click mid-drag acted");
                        Check(rig.Reorders.Count == 1 && rig.Reorders[0][0] == 0, "config " + config + ": drag with stray clicks did not drop");
                        CheckGeometry(rig, "config " + config + " after stray clicks");
                    }

                    // The marks change under a drag (as adoption on the startup tick could):
                    // the drag is let go, nothing reorders, and the row matches the new marks.
                    w = rig.Widths(); l = rig.Lefts();
                    {
                        rig.ClearLog();
                        int y = h / 2;
                        Send(rig, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, l[0] + 2, y);
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, l[0] + 2 + threshold + 4, y);
                        Desktop victim = rig.Row[count - 1];
                        if (rig.Marks.ContainsKey(victim.Id)) rig.Marks.Remove(victim.Id); else rig.Marks[victim.Id] = RandomMark();
                        rig.Strip.RefreshMarks();
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, rig.Total() - 2, y);
                        Send(rig, Native.WM_LBUTTONUP, 0, rig.Total() - 2, y);
                        Check(rig.Reorders.Count == 0, "config " + config + ": marks changed mid-drag, yet it reordered");
                        CheckGeometry(rig, "config " + config + " marks changed mid-drag");
                    }

                    // The row changes under a drag (a desktop added elsewhere).
                    w = rig.Widths(); l = rig.Lefts();
                    {
                        rig.ClearLog();
                        int y = h / 2;
                        Send(rig, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, l[0] + 2, y);
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, l[0] + 2 + threshold + 4, y);
                        rig.Row.Add(new Desktop(Guid.NewGuid(), rig.Row.Count, null, false));
                        rig.Strip.SetDesktops(rig.Row);
                        Send(rig, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, rig.Total() - 2, y);
                        Send(rig, Native.WM_LBUTTONUP, 0, rig.Total() - 2, y);
                        Check(rig.Reorders.Count == 0, "config " + config + ": row changed mid-drag, yet it reordered");
                        CheckGeometry(rig, "config " + config + " row changed mid-drag");
                    }
                }
                Pump(10);
            }

            Console.WriteLine("  " + configs + " rows, " + clicks + " clicks, " + drags + " drags (" + cancels + " cancelled)");
        }

        // --- underline, read off the screen --------------------------------------------

        static void Underline(Form form)
        {
            Color highlight = Color.FromArgb(0x4C, 0xC2, 0xFF);
            int checks = 0;

            using (var rig = new Rig(form.Handle, 0, 6, 2, 43, 55, 50, Color.FromArgb(16, 16, 16), Look.Classic, 1.25))
            {
                for (int n = 0; n < 16; n++)
                {
                    // Change the row: new marks, a reorder by drag, or a new current desktop.
                    int op = n % 3;
                    if (op == 0) RandomMarks(rig);
                    else if (op == 1)
                    {
                        int[] l = rig.Lefts(), w = rig.Widths();
                        int from = _rng.Next(rig.Row.Count);
                        Drag(rig, l[from] + w[from] / 2, _rng.Next(0, rig.Total()), rig.Height / 2);
                    }
                    else
                    {
                        int cur = _rng.Next(rig.Row.Count);
                        for (int i = 0; i < rig.Row.Count; i++)
                            rig.Row[i] = new Desktop(rig.Row[i].Id, i, null, i == cur);
                        rig.Strip.SetDesktops(rig.Row);
                    }

                    Pump(450);   // the bar's ease, well past animationMs

                    int current = rig.Row.FindIndex(d => d.IsCurrent);
                    int[] lefts = rig.Lefts(), widths = rig.Widths();

                    Native.RECT r;
                    Native.GetWindowRect(rig.Strip.Handle, out r);
                    using (var shot = new Bitmap(rig.Total(), 1))
                    using (var g = Graphics.FromImage(shot))
                    {
                        g.CopyFromScreen(r.Left, r.Top + rig.Height - 1, 0, 0, shot.Size);
                        int first = -1, last = -1;
                        for (int x = 0; x < shot.Width; x++)
                        {
                            Color c = shot.GetPixel(x, 0);
                            if (Math.Abs(c.R - highlight.R) + Math.Abs(c.G - highlight.G) + Math.Abs(c.B - highlight.B) < 30)
                            {
                                if (first < 0) first = x;
                                last = x;
                            }
                        }
                        checks++;
                        Check(first == lefts[current] && last == lefts[current] + widths[current] - 1,
                              "underline step " + n + ": drawn " + first + ".." + last + ", current button " + (current + 1)
                              + " is " + lefts[current] + ".." + (lefts[current] + widths[current] - 1));
                    }
                }
            }
            Console.WriteLine("  " + checks + " settled underlines read back");
        }

        // --- look -------------------------------------------------------------------------

        static void Sheet(Form form)
        {
            var rows = new List<Rig>();
            var labels = new List<string>();
            try
            {
                Color dark = Color.FromArgb(16, 16, 16), light = Color.FromArgb(0xDF, 0xE5, 0xEC);
                var specs = new[]
                {
                    new { Label = "classic dark 1.25",  Bg = dark,  Look = Look.Classic, Dpi = 1.25, Bw = 34 },
                    new { Label = "modern dark 1.25",   Bg = dark,  Look = Look.Modern,  Dpi = 1.25, Bw = 34 },
                    new { Label = "classic light 1.25", Bg = light, Look = Look.Classic, Dpi = 1.25, Bw = 34 },
                    new { Label = "modern light 1.25",  Bg = light, Look = Look.Modern,  Dpi = 1.25, Bw = 34 },
                    new { Label = "classic dark 1.0",   Bg = dark,  Look = Look.Classic, Dpi = 1.0,  Bw = 34 },
                    new { Label = "modern light 1.5",   Bg = light, Look = Look.Modern,  Dpi = 1.5,  Bw = 34 },
                    new { Label = "classic dark bw=24", Bg = dark,  Look = Look.Classic, Dpi = 1.25, Bw = 24 },
                    new { Label = "modern dark bw=16",  Bg = dark,  Look = Look.Modern,  Dpi = 1.25, Bw = 16 },
                };

                DesktopMark[] marks =
                {
                    DesktopMark.Letters("WW"), DesktopMark.Letters("MM"), DesktopMark.Glyph('\uE943'),
                    DesktopMark.Swatch(Color.FromArgb(0xFF, 0xB9, 0x00)), null, DesktopMark.Letters("0"),
                    DesktopMark.Swatch(Color.FromArgb(0x8A, 0x8A, 0x8A)), DesktopMark.Glyph('\uEBE8'), null,
                    DesktopMark.Letters("WW"), DesktopMark.Glyph('\uE8F2'), DesktopMark.Swatch(Color.FromArgb(0x00, 0x78, 0xD4)),
                };

                int y = 0;
                foreach (var spec in specs)
                {
                    int h = (int)Math.Round(40 * spec.Dpi);
                    int bw = (int)Math.Round(spec.Bw * spec.Dpi);
                    int mw = (int)Math.Round(SwitcherStrip.MarkedWidthFor(spec.Bw) * spec.Dpi);
                    var rig = new Rig(form.Handle, y, marks.Length, 9, bw, mw, h, spec.Bg, spec.Look, spec.Dpi);
                    for (int i = 0; i < marks.Length; i++) if (marks[i] != null) rig.Marks[rig.Row[i].Id] = marks[i];
                    rig.Strip.RefreshMarks();

                    // Occupancy on, desktops 2, 6 and 8 empty: their marks dim.
                    rig.Strip.OccupancyShown = true;
                    var occupied = new HashSet<Guid>();
                    foreach (Desktop d in rig.Row) if (d.Index != 1 && d.Index != 5 && d.Index != 7) occupied.Add(d.Id);
                    rig.Strip.SetOccupancy(occupied);

                    rows.Add(rig);
                    labels.Add(spec.Label);
                    y += h + 4;
                }

                form.Size = new Size(1300, y);
                Pump(600);

                using (var shot = new Bitmap(form.Width, form.Height))
                using (var g = Graphics.FromImage(shot))
                {
                    g.CopyFromScreen(form.Left, form.Top, 0, 0, shot.Size);

                    // Labels to the right of the strips, so the PNG explains itself.
                    using (var font = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
                    {
                        int ly = 0;
                        for (int i = 0; i < rows.Count; i++)
                        {
                            g.DrawString(labels[i], font, Brushes.White, 1000, ly + 4);
                            ly += rows[i].Height + 4;
                        }
                    }

                    string path = Path.Combine(Path.GetTempPath(), "markstress-sheet.png");
                    shot.Save(path, ImageFormat.Png);
                    Console.WriteLine("  saved " + path);
                    Console.WriteLine("  rows: buttons 1-12 marked WW MM code amber - 0 grey bug - WW chat blue; 10 current; 2, 6, 8 empty");
                }
            }
            finally
            {
                foreach (Rig r in rows) r.Dispose();
                form.Size = new Size(1300, 60);
            }
        }

        // --- shell ------------------------------------------------------------------------

        const string Prefix = "markstress ";

        static void Shell(VirtualDesktopApi api)
        {
            Guid home = api.GetCurrentId();
            int userCount = api.GetCount();
            var scratch = new List<Guid>();

            using (var form = new Form())
            {
                IntPtr handle = form.Handle;   // the marshaller needs a real window
                var service = new DesktopService(api, form);
                var sync = new MarkSync(new MarkStore());
                sync.Persist = false;
                sync.Start(0);   // past the startup window: removed means let go

                service.Recreated += sync.Recreated;
                service.DesktopsChanged += delegate { sync.Sync(service.Desktops); };

                try
                {
                    service.Start();
                    Pump(300);

                    var expected = new Dictionary<string, DesktopMark>();   // by name, since GUIDs change
                    DesktopMark gone = null;   // the mark of a desktop removed this step
                    int serial = 0;

                    for (int i = 0; i < 4; i++)
                    {
                        string name = Prefix + (char)('A' + serial++);
                        Guid id = api.Create();
                        api.SetName(id, name);
                        scratch.Add(id);
                        DesktopMark mark = DistinctMark(expected);
                        expected[name] = mark;
                    }
                    Pump(400);
                    sync.Sync(service.Desktops);
                    foreach (Desktop d in service.Desktops)
                        if (d.Name != null && expected.ContainsKey(d.Name)) sync.Set(d.Id, expected[d.Name], d.Index);

                    Verify(service, sync, expected, gone, "after marking");

                    int reorders = 0, removes = 0, adds = 0, bursts = 0;
                    for (int step = 0; step < 40; step++)
                    {
                        int count = service.Count;
                        int first = userCount;   // scratch desktops start here
                        int scratchCount = count - first;
                        int op = _rng.Next(10);
                        gone = null;

                        if (op < 6 && scratchCount >= 2)
                        {
                            int from = first + _rng.Next(scratchCount);
                            int to = first + _rng.Next(scratchCount);
                            ReorderLikeController(service, sync, from, to);
                            reorders++;
                        }
                        else if (op < 8 && scratchCount >= 2)
                        {
                            // Three back to back, nothing pumped between: the notifications of
                            // all three queue up and arrive after the last.
                            for (int k = 0; k < 3; k++)
                            {
                                int from = first + _rng.Next(scratchCount);
                                int to = first + _rng.Next(scratchCount);
                                ReorderLikeController(service, sync, from, to);
                                reorders++;
                            }
                            bursts++;
                        }
                        else if (op == 8 && scratchCount >= 3)
                        {
                            Desktop victim = service.Desktops[first + _rng.Next(scratchCount)];
                            if (victim.Name != null && expected.ContainsKey(victim.Name))
                            {
                                gone = expected[victim.Name];
                                expected.Remove(victim.Name);
                            }
                            service.Remove(victim.Id);
                            removes++;
                        }
                        else if (scratchCount < 6)
                        {
                            string name = Prefix + (char)('A' + serial++);
                            Guid id = api.Create();
                            api.SetName(id, name);
                            Pump(200);
                            if (_rng.Next(2) == 0)
                            {
                                DesktopMark mark = DistinctMark(expected);
                                expected[name] = mark;
                                Desktop d = service.Desktops[service.Count - 1];
                                sync.Set(d.Id, mark, d.Index);
                            }
                            adds++;
                        }

                        Pump(250);
                        Verify(service, sync, expected, gone, "step " + step);
                    }

                    Console.WriteLine("  " + reorders + " reorders (" + bursts + " bursts of three), " + removes + " removed, " + adds + " added");
                }
                finally
                {
                    // Every scratch desktop, by name - the GUIDs taken at creation are long gone.
                    try
                    {
                        if (api.GetCurrentId() != home) { try { api.SwitchTo(home); } catch { } }
                        foreach (Guid id in api.GetDesktopIds())
                        {
                            string name = VirtualDesktopApi.GetName(id);
                            if (name != null && name.StartsWith(Prefix)) api.Remove(id);
                        }
                        Pump(300);
                        if (api.GetCurrentId() != home) { try { api.SwitchTo(home); } catch { } }
                        Console.WriteLine("  cleaned up: " + api.GetCount() + " desktops remain (started with " + userCount + ")");
                        Check(api.GetCount() == userCount, "scratch desktops left behind");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  CLEANUP FAILED - " + ex.Message + "; remove desktops named \"" + Prefix + "...\" by hand");
                        _failures++;
                    }
                    service.Dispose();
                }
            }
        }

        static DesktopMark DistinctMark(Dictionary<string, DesktopMark> taken)
        {
            for (int tries = 0; tries < 100; tries++)
            {
                DesktopMark m = RandomMark();
                if (!taken.ContainsValue(m)) return m;
            }
            return DesktopMark.Letters("Z" + taken.Count);
        }

        /// <summary>Exactly what Controller.Reorder does around the service.</summary>
        static void ReorderLikeController(DesktopService service, MarkSync sync, int from, int to)
        {
            sync.BeginReorder();
            try { service.Reorder(from, to); }
            finally { sync.EndReorder(); }
            sync.Sync(service.Desktops);
        }

        static void Verify(DesktopService service, MarkSync sync, Dictionary<string, DesktopMark> expected,
                           DesktopMark gone, string when)
        {
            var seen = new HashSet<string>();
            foreach (Desktop d in service.Desktops)
            {
                DesktopMark has = sync.Store.Get(d.Id);
                DesktopMark want = null;
                if (d.Name != null && expected.TryGetValue(d.Name, out want)) seen.Add(d.Name);
                Check(DesktopMark.Same(has, want),
                      when + ": [" + d.Number + "] " + (d.Name ?? "Desktop " + d.Number) + " has "
                      + (has == null ? "no mark" : has.Format()) + ", expected " + (want == null ? "none" : want.Format()));
            }
            foreach (string name in expected.Keys)
                Check(seen.Contains(name), when + ": desktop \"" + name + "\" missing from the row");

            Check(sync.Store.Count == expected.Count,
                  when + ": store holds " + sync.Store.Count + " marks, expected " + expected.Count);

            // A desktop removed this step: its mark is let go, and first in Recent.
            if (gone != null)
            {
                IList<DesktopMark> recent = sync.Store.Recent;
                Check(recent.Count > 0 && recent[0].Equals(gone),
                      when + ": removed desktop's " + gone.Format() + " is not first in Recent");
            }
        }

        static void Pump(int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
        }
    }
}
