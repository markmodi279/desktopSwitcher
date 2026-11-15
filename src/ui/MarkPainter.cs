using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DesktopSwitcher
{
    /// <summary>
    /// Draws a DesktopMark, and holds the picker's colours. One place for both, so the
    /// strip, the picker's grid and its Recent row cannot come to draw the same mark three
    /// slightly different ways.
    /// </summary>
    static class MarkPainter
    {
        // The picker's symbols are MarkIcons.Offered - Lucide icons drawn from their own
        // outlines. They used to be 32 Segoe MDL2 Assets glyphs listed here; MarkIcons says
        // why they went, and DesktopMark.Parse turns a saved one into its icon.

        /// <summary>
        /// The picker's ten colours, Windows' own accent swatches, which were chosen to read
        /// on both a dark and a light taskbar - red, orange, amber, green, teal, blue, indigo,
        /// purple, pink, grey. Custom... covers anything else.
        /// </summary>
        public static readonly Color[] Colors =
        {
            Color.FromArgb(0xE7, 0x48, 0x56), Color.FromArgb(0xF7, 0x63, 0x0C), Color.FromArgb(0xFF, 0xB9, 0x00),
            Color.FromArgb(0x16, 0xC6, 0x0C), Color.FromArgb(0x00, 0xB7, 0xC3), Color.FromArgb(0x00, 0x78, 0xD4),
            Color.FromArgb(0x5C, 0x5F, 0xD6), Color.FromArgb(0x8E, 0x44, 0xAD), Color.FromArgb(0xE3, 0x00, 0x8C),
            Color.FromArgb(0x8A, 0x8A, 0x8A),
        };

        /// <summary>
        /// Opens Segoe MDL2 Assets at <paramref name="size"/> pixels, or null if the face is
        /// not there. Only an old Glyph mark is drawn in it now, and the picker's plus. GDI+ quietly substitutes a missing face, and a private-use codepoint in
        /// any other font is a box - so a glyph mark with no font is drawn as nothing rather
        /// than as a box. Every Windows 10 has the font; this is the belt to that braces.
        /// </summary>
        public static Font GlyphFont(float size)
        {
            var font = new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Pixel);
            if (font.Name == "Segoe MDL2 Assets") return font;
            font.Dispose();
            return null;
        }

        /// <summary>
        /// How wide the mark draws, for laying it out beside the number. A colour is a square
        /// as tall as the letters' cap height would be, an icon a square of IconSize; letters
        /// and glyphs are measured.
        /// </summary>
        public static float Measure(Graphics g, DesktopMark mark, Font letters, Font glyph)
        {
            if (mark == null) return 0f;
            if (mark.Kind == MarkKind.Color) return SwatchSize(letters);
            if (mark.Kind == MarkKind.Icon) return IconSize(letters);

            Font font = mark.Kind == MarkKind.Glyph ? glyph : letters;
            if (font == null) return 0f;
            return g.MeasureString(mark.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        /// <summary>A colour mark's side: about a capital letter's height, so it sits level with letters.</summary>
        public static float SwatchSize(Font letters)
        {
            return (float)Math.Round(letters.Size * 0.85f);
        }

        /// <summary>
        /// An icon's side: a sixth larger than the letters' em, which is 14 px beside the
        /// 12 px number font on a 40 px taskbar - the size the icons were mocked and chosen
        /// at. Sized off the letters so it follows the taskbar and DPI the way they do, and
        /// in the picker comes out a little larger with its larger letters.
        /// </summary>
        public static float IconSize(Font letters)
        {
            return (float)Math.Round(letters.Size * 7f / 6f);
        }

        /// <summary>
        /// Draws <paramref name="mark"/> centred in <paramref name="box"/>. Letters, icons and
        /// glyphs take <paramref name="ink"/>, the strip's text tone, since a mark's only colour is a
        /// colour mark's; <paramref name="alpha"/> dims the whole mark, which is how the strip
        /// says the desktop has nothing on it.
        /// </summary>
        public static void Draw(Graphics g, DesktopMark mark, RectangleF box, Color ink,
                                Font letters, Font glyph, float alpha)
        {
            if (mark == null || alpha <= 0.001f) return;
            int a = (int)Math.Round(255 * Math.Min(1f, alpha));

            if (mark.Kind == MarkKind.Color)
            {
                float side = SwatchSize(letters);
                var square = new RectangleF((float)Math.Round(box.X + (box.Width - side) / 2f),
                                            (float)Math.Round(box.Y + (box.Height - side) / 2f), side, side);
                SmoothingMode was = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(a, mark.Color)))
                using (GraphicsPath path = Rounded(square, Math.Max(2f, side * 0.25f)))
                    g.FillPath(brush, path);
                g.SmoothingMode = was;
                return;
            }

            if (mark.Kind == MarkKind.Icon)
            {
                float side = IconSize(letters);
                var origin = new PointF(box.X + (box.Width - side) / 2f, box.Y + (box.Height - side) / 2f);
                DrawIcon(g, mark.Text, origin, side, Color.FromArgb(a * ink.A / 255, ink));
                return;
            }

            Font font = mark.Kind == MarkKind.Glyph ? glyph : letters;
            if (font == null) return;

            using (var brush = new SolidBrush(Color.FromArgb(a * ink.A / 255, ink)))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                g.DrawString(mark.Text, font, brush, box, format);
            }
        }

        // --- icons ---------------------------------------------------------------

        /// <summary>MarkIcons' grid, Lucide's: every path is in a 24 x 24 box.</summary>
        const float Grid = 24f;

        /// <summary>
        /// Stroke width in grid units. Lucide's own is 2; 2.6 is what was mocked and chosen -
        /// at 14 px on a 40 px taskbar it is a pixel and a half, about the weight of the
        /// Segoe UI number beside it. Much heavier and the small enclosed shapes (the bug's
        /// body, the bot's eyes) close up.
        /// </summary>
        const float Stroke = 2.6f;

        // Parsed once, in grid units; DrawIcon scales a copy. A null is an icon that did not parse.
        static readonly Dictionary<string, GraphicsPath> Parsed = new Dictionary<string, GraphicsPath>();

        /// <summary>
        /// Strokes icon <paramref name="key"/> into a <paramref name="side"/>-pixel square at
        /// <paramref name="origin"/>. The origin is rounded to whole pixels, so an icon lands
        /// on the same pixel grid wherever its button is and does not shimmer by half a pixel
        /// from one button to the next.
        /// </summary>
        public static void DrawIcon(Graphics g, string key, PointF origin, float side, Color ink)
        {
            GraphicsPath path = IconPath(key);
            if (path == null || side <= 0f) return;

            float scale = side / Grid;
            using (var copy = (GraphicsPath)path.Clone())
            using (var m = new Matrix())
            using (var pen = new Pen(ink, Stroke * scale))
            {
                m.Translate((float)Math.Round(origin.X), (float)Math.Round(origin.Y));
                m.Scale(scale, scale);
                copy.Transform(m);

                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                SmoothingMode was = g.SmoothingMode;
                PixelOffsetMode wasOffset = g.PixelOffsetMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawPath(pen, copy);
                g.SmoothingMode = was;
                g.PixelOffsetMode = wasOffset;
            }
        }

        static GraphicsPath IconPath(string key)
        {
            GraphicsPath path;
            if (Parsed.TryGetValue(key ?? "", out path)) return path;

            string data;
            data = MarkIcons.PathData(key);
            if (data == null) return null;

            path = new GraphicsPath(FillMode.Winding);
            try
            {
                SvgPath.Append(data, path);
            }
            catch (Exception e)
            {
                // A typo in the table is a build-time mistake, and --marks parses every icon
                // to catch it; at runtime it costs that one icon, drawn as nothing.
                Log.Write("mark icon " + key + " did not parse: " + e.Message);
                path.Dispose();
                path = null;
            }
            Parsed[key] = path;
            return path;
        }

        /// <summary>Parses every icon; the selftest's check that the table is sound. Returns the keys that failed.</summary>
        public static List<string> VerifyIcons()
        {
            var failed = new List<string>();
            foreach (string key in MarkIcons.All)
            {
                try
                {
                    using (var path = new GraphicsPath(FillMode.Winding))
                    {
                        SvgPath.Append(MarkIcons.PathData(key), path);
                        RectangleF b = path.GetBounds();
                        // Lucide keeps a unit of margin inside the 24 grid; anything well
                        // outside it is a mistranslated command, not a design choice.
                        if (b.Width <= 0 || b.Height <= 0 || b.Left < -1 || b.Top < -1 || b.Right > Grid + 1 || b.Bottom > Grid + 1)
                            failed.Add(key + " (bounds " + b + ")");
                    }
                }
                catch (Exception e)
                {
                    failed.Add(key + " (" + e.Message + ")");
                }
            }
            foreach (string key in MarkIcons.Offered)
                if (!MarkIcons.Has(key)) failed.Add(key + " (offered but not in the table)");
            foreach (string key in MarkIcons.Converted)
                if (!MarkIcons.Has(key)) failed.Add(key + " (a glyph converts to it but it is not in the table)");
            return failed;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
