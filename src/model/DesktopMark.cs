using System;
using System.Drawing;
using System.Globalization;

namespace DesktopSwitcher
{
    /// <summary>
    /// The things a mark can be. A desktop has one kind or none: picking letters replaces a
    /// symbol, and so on, because two marks squeezed into one 44 px button would each be too
    /// small to do the job of either.
    ///
    /// Glyph is the old symbol kind, from before the picker's symbols became Lucide icons.
    /// Every glyph the picker offered converts to an icon as marks.ini is read, so a Glyph
    /// mark now only exists for a codepoint typed into the file by hand; it still draws, in
    /// Segoe MDL2 Assets as it always did.
    /// </summary>
    enum MarkKind
    {
        Letters,   // one or two of A-Z, 0-9
        Glyph,     // a Segoe MDL2 Assets codepoint - the old symbols; see above
        Color,     // a plain rounded square
        Icon,      // one of MarkIcons' icons, by key
    }

    /// <summary>
    /// A desktop's mark: what the user chose to recognise it by, drawn beside its number on
    /// the strip.
    ///
    /// It exists because the hover panel could not answer the question it was built for once
    /// every desktop held the same apps. With Chrome and VS Code on all four, the panel's
    /// rows read the same on every button, and the number on the button stops meaning
    /// anything the moment a drag renumbers the row. Parsing the project out of VS Code's
    /// title was considered and set aside in favour of this: a mark you chose yourself is
    /// recognised rather than read, works for a desktop that has no project in any title,
    /// and does not change under you when an editor switches folders.
    ///
    /// Immutable, and compared by value, so the strip can tell "same mark" from "changed"
    /// without caring which object it was handed.
    /// </summary>
    sealed class DesktopMark
    {
        /// <summary>Longest a Letters mark may be. Two fits beside a shrunk number in a 44 px button.</summary>
        public const int MaxLetters = 2;

        readonly MarkKind _kind;
        readonly string _text;    // the letters, the glyph as one char, or the icon's key; null for a colour
        readonly Color _color;    // opaque; empty unless Kind is Color

        DesktopMark(MarkKind kind, string text, Color color)
        {
            _kind = kind;
            _text = text;
            _color = color;
        }

        public MarkKind Kind { get { return _kind; } }

        /// <summary>The letters as drawn, the glyph's single character, or the icon's key. Null for a colour.</summary>
        public string Text { get { return _text; } }

        public Color Color { get { return _color; } }

        /// <summary>
        /// Letters, cleaned the way the picker's box cleans them: upper case, letters and
        /// digits only, at most two. Null when nothing survives, which means "no mark".
        /// </summary>
        public static DesktopMark Letters(string text)
        {
            string clean = CleanLetters(text);
            return clean.Length == 0 ? null : new DesktopMark(MarkKind.Letters, clean, Color.Empty);
        }

        /// <summary>A symbol from Segoe MDL2 Assets, by codepoint. Its private-use range only.</summary>
        public static DesktopMark Glyph(char codepoint)
        {
            if (codepoint < '\uE000' || codepoint > '\uF8FF') return null;
            return new DesktopMark(MarkKind.Glyph, codepoint.ToString(), Color.Empty);
        }

        /// <summary>One of MarkIcons' icons, by key. Null for a key this build has no icon for.</summary>
        public static DesktopMark Icon(string key)
        {
            if (!MarkIcons.Has(key)) return null;
            return new DesktopMark(MarkKind.Icon, key, Color.Empty);
        }

        /// <summary>A colour, alpha dropped: dimming an empty desktop is the strip's job, not the mark's.</summary>
        public static DesktopMark Swatch(Color color)
        {
            if (color.IsEmpty) return null;
            return new DesktopMark(MarkKind.Color, null, Color.FromArgb(color.R, color.G, color.B));
        }

        /// <summary>Upper case, A-Z and 0-9, cut to MaxLetters. Shared with the picker's box.</summary>
        public static string CleanLetters(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var chars = new char[MaxLetters];
            int n = 0;
            foreach (char raw in text.ToUpperInvariant())
            {
                if (n == MaxLetters) break;
                if ((raw >= 'A' && raw <= 'Z') || (raw >= '0' && raw <= '9')) chars[n++] = raw;
            }
            return new string(chars, 0, n);
        }

        // --- text form ----------------------------------------------------------

        /// <summary>
        /// The mark as marks.ini stores it: "letters:DS", "icon:rocket", "color:#16C60C",
        /// or for an old symbol "glyph:E943". The glyph is written as its hex codepoint rather
        /// than the character itself, since a private-use character in a text file is an
        /// invisible box to anyone editing it.
        /// </summary>
        public string Format()
        {
            switch (_kind)
            {
                case MarkKind.Letters: return "letters:" + _text;
                case MarkKind.Glyph:   return "glyph:" + ((int)_text[0]).ToString("X4");
                case MarkKind.Icon:    return "icon:" + _text;
                default:               return "color:#" + _color.R.ToString("X2") + _color.G.ToString("X2") + _color.B.ToString("X2");
            }
        }

        /// <summary>
        /// The reverse of Format. Null for anything it cannot read, never an exception.
        ///
        /// Not quite its inverse, on purpose: a "glyph:" whose codepoint was one of the old
        /// picker's symbols comes back as the icon that replaced it, so a mark chosen before
        /// the icons arrived keeps its meaning and gets their weight. It is written back as
        /// "icon:" the next time the store saves.
        /// </summary>
        public static DesktopMark Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int colon = text.IndexOf(':');
            if (colon <= 0) return null;

            string kind = text.Substring(0, colon).Trim().ToLowerInvariant();
            string value = text.Substring(colon + 1).Trim();

            int number;
            switch (kind)
            {
                case "letters":
                    return Letters(value);

                case "glyph":
                    if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)) return null;
                    if (number < 0xE000 || number > 0xF8FF) return null;
                    string converted = MarkIcons.FromGlyph((char)number);
                    return converted != null ? Icon(converted) : Glyph((char)number);

                case "icon":
                    return Icon(value.ToLowerInvariant());

                case "color":
                    if (value.StartsWith("#")) value = value.Substring(1);
                    if (value.Length != 6) return null;
                    if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)) return null;
                    return Swatch(Color.FromArgb((number >> 16) & 0xFF, (number >> 8) & 0xFF, number & 0xFF));

                default:
                    return null;
            }
        }

        // --- equality -------------------------------------------------------------

        public override bool Equals(object obj)
        {
            var other = obj as DesktopMark;
            if (other == null) return false;
            return _kind == other._kind && _text == other._text && _color.ToArgb() == other._color.ToArgb();
        }

        public override int GetHashCode()
        {
            return ((int)_kind * 397) ^ (_text != null ? _text.GetHashCode() : _color.ToArgb());
        }

        public static bool Same(DesktopMark a, DesktopMark b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }

        public override string ToString()
        {
            return Format();
        }
    }
}
