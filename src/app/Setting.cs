using System;
using System.Drawing;
using System.Globalization;

namespace DesktopSwitcher
{
    enum SettingKind
    {
        Int,      // a number clamped to Min..Max
        Bool,     // true / false
        Choice,   // one of Choices, held as an index
        Color,    // #RRGGBB, or blank for "automatic" when AllowAuto
    }

    /// <summary>
    /// Everything there is to know about one setting, in one place: its key in the file,
    /// where it sits in the Settings window, what to call it, what it does, what values it
    /// takes, and how to read and write it on a Config.
    ///
    /// Before this, the range of every number lived only inside a ParseInt call in
    /// Config.Apply and the explanation only in a doc comment nobody editing config.ini
    /// would ever see. The Settings window needs both, and so does the commented file, and
    /// three copies of "16 to 128" is exactly the kind of list that falls out of step. So
    /// Config.All is a table of these, and the parser, the file writer, the window and
    /// --config all read it.
    ///
    /// Plain public fields rather than a constructor with a dozen parameters: the table is
    /// written with object initialisers, which name every value at the point it is set.
    /// </summary>
    sealed class Setting
    {
        public string Key;        // as written in config.ini; matched case-insensitively
        public string Section;    // the Settings tab and the file's section banner
        public string Label;      // the Settings row's title
        public string Help;       // one or two plain sentences, shown in both places
        public string Unit = "";  // "px", "ms", "%" - Int only
        public SettingKind Kind;

        public int Min, Max;      // Int only, inclusive
        public string[] Choices;  // Choice only: the file's spellings, in index order
        public string[] Captions; // Choice only: what the window shows for each

        /// <summary>Color only: blank in the file means "sample the taskbar", held as Color.Empty.</summary>
        public bool AllowAuto;

        /// <summary>Written to the file but not offered in the window: state the app keeps, not a preference.</summary>
        public bool Hidden;

        /// <summary>
        /// Whether this setting does anything given the others - the hover panel's delay
        /// with the hover panel off does not, nor the bar sizes with the tile drawing a
        /// gauge. Null means always. The window greys the row out while it is false:
        /// disabled rather than hidden, so turning a parent off does not make the rows
        /// below it jump.
        /// </summary>
        public Func<Config, bool> Applies;

        // Accessors. Int and Choice share the int pair - a Choice is its index.
        public Func<Config, int> GetInt;
        public Action<Config, int> SetInt;
        public Func<Config, bool> GetBool;
        public Action<Config, bool> SetBool;
        public Func<Config, Color> GetColor;
        public Action<Config, Color> SetColor;

        /// <summary>Sets Applies and hands the setting back, so the table can say it inline.</summary>
        public Setting When(Func<Config, bool> applies)
        {
            Applies = applies;
            return this;
        }

        /// <summary>True when the row should be live: no condition, or the condition holds.</summary>
        public bool AppliesTo(Config c)
        {
            return Applies == null || Applies(c);
        }

        /// <summary>Copies this one setting's value from one Config to another.</summary>
        public void Copy(Config from, Config to)
        {
            switch (Kind)
            {
                case SettingKind.Int:
                case SettingKind.Choice: SetInt(to, GetInt(from)); break;
                case SettingKind.Bool:   SetBool(to, GetBool(from)); break;
                default:                 SetColor(to, GetColor(from)); break;
            }
        }

        /// <summary>The value as it is written to config.ini.</summary>
        public string Format(Config c)
        {
            switch (Kind)
            {
                case SettingKind.Int:    return GetInt(c).ToString(CultureInfo.InvariantCulture);
                case SettingKind.Bool:   return GetBool(c) ? "true" : "false";
                case SettingKind.Choice: return Choices[GetInt(c)];
                default:
                    Color color = GetColor(c);
                    return color.IsEmpty ? "" : ToHex(color);
            }
        }

        /// <summary>
        /// Reads a value from config.ini onto <paramref name="c"/>. A number out of range is
        /// pulled back into it; anything that cannot be read leaves the current value alone,
        /// which on a freshly constructed Config is the default. The one exception is a
        /// colour that allows "automatic": blank or unreadable means automatic, which is
        /// what it always meant.
        /// </summary>
        public void Parse(Config c, string val)
        {
            string v = val == null ? "" : val.Trim();

            switch (Kind)
            {
                case SettingKind.Int:
                {
                    int n;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        SetInt(c, Clamp(n));
                    break;
                }
                case SettingKind.Bool:
                    switch (v.ToLowerInvariant())
                    {
                        case "1": case "true":  case "yes": case "on":  SetBool(c, true);  break;
                        case "0": case "false": case "no":  case "off": SetBool(c, false); break;
                    }
                    break;

                case SettingKind.Choice:
                    for (int i = 0; i < Choices.Length; i++)
                        if (string.Equals(Choices[i], v, StringComparison.OrdinalIgnoreCase))
                            SetInt(c, i);
                    break;

                default:
                {
                    Color parsed;
                    if (TryParseHex(v, out parsed)) SetColor(c, parsed);
                    else if (AllowAuto) SetColor(c, Color.Empty);
                    break;
                }
            }
        }

        public int Clamp(int n)
        {
            if (n < Min) return Min;
            if (n > Max) return Max;
            return n;
        }

        /// <summary>
        /// The line under the help text in config.ini: what it accepts and what it starts
        /// at, e.g. "16 to 128 px, default 34" or "meters | gauge | chip, default meters".
        /// </summary>
        public string Describe(Config defaults)
        {
            string def = Format(defaults);

            switch (Kind)
            {
                case SettingKind.Int:
                    return Min + " to " + Max + (Unit.Length > 0 ? " " + Unit : "") + ", default " + def;
                case SettingKind.Bool:
                    return "true or false, default " + def;
                case SettingKind.Choice:
                    return string.Join(" | ", Choices) + ", default " + def;
                default:
                    return AllowAuto
                        ? "#RRGGBB, or blank to sample the taskbar (the default)"
                        : "#RRGGBB, default " + def;
            }
        }

        public static string ToHex(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public static bool TryParseHex(string s, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;

            string h = s.Trim();
            if (h.Length > 0 && h[0] == '#') h = h.Substring(1);
            if (h.Length != 6) return false;

            int rgb;
            if (!int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return false;

            color = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }
    }
}
