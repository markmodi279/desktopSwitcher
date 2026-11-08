using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DesktopSwitcher
{
    /// <summary>
    /// Draws a DesktopMark, and holds what the picker offers. One place for both, so the
    /// strip, the picker's grid and its Recent row cannot come to draw the same mark three
    /// slightly different ways.
    /// </summary>
    static class MarkPainter
    {
        /// <summary>
        /// The picker's symbols: codepoints in Segoe MDL2 Assets, the icon font every
        /// Windows 10 has and its own interface is drawn in. Chosen from all 1,724 of its
        /// glyphs, drawn at strip size on a dark and a light taskbar, for three things: told
        /// apart at a glance, meaning something without a label, and fitting what a desktop
        /// is kept for day to day. The rows run by that: meetings and people, then the kinds
        /// of project (API is the braces or the hub, web the browser window, fullstack the
        /// layers), then other work, then the rest of life. A plain document and a folder
        /// were tried and dropped - at 16 px one is a blank rectangle and the other could be
        /// a book. Thirty-two fills four rows of eight; more means a taller picker.
        ///
        /// Written as escapes, not the characters: the in-box compiler reads a BOM-less file
        /// in the system codepage, and a private-use character is an invisible box in an
        /// editor anyway. A glyph mark already saved keeps drawing if its symbol leaves this
        /// list - DesktopMark takes any private-use codepoint; this is only what is offered.
        /// </summary>
        public static readonly char[] Glyphs =
        {
            '\uE787', '\uE8F2', '\uE715', '\uE714', '\uE717', '\uE716', '\uE7F6', '\uE99A',   // calendar, chat, mail, video call, phone, people, headset, bot
            '\uE943', '\uE756', '\uE737', '\uE81E', '\uEE94', '\uF22C', '\uEBE8', '\uE90F',   // code, terminal, browser window, layers, database, network hub, bug, wrench
            '\uEB05', '\uEADF', '\uE821', '\uE753', '\uE790', '\uE7BE', '\uE945', '\uE7C1',   // pie chart, clipboard, briefcase, cloud, palette, education, bolt, flag
            '\uE80F', '\uEC4F', '\uE7FC', '\uE7BF', '\uE709', '\uE12B', '\uE771', '\uE914',   // home, music, game, cart, plane, globe, brush, picture
        };

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
        /// not there. GDI+ quietly substitutes a missing face, and a private-use codepoint in
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
        /// as tall as the letters' cap height would be; letters and glyphs are measured.
        /// </summary>
        public static float Measure(Graphics g, DesktopMark mark, Font letters, Font glyph)
        {
            if (mark == null) return 0f;
            if (mark.Kind == MarkKind.Color) return SwatchSize(letters);

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
        /// Draws <paramref name="mark"/> centred in <paramref name="box"/>. Letters and glyphs
        /// take <paramref name="ink"/>, the strip's text tone, since a mark's only colour is a
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
