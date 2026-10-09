using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopSwitcher
{
    /// <summary>
    /// Flat key=value settings file. Unknown keys are preserved on save so hand-edits
    /// and future keys survive a round trip. Missing or malformed values fall back to
    /// defaults rather than failing.
    ///
    /// The fields below are what the rest of the app reads. What each one is called in the
    /// file, what it accepts and what to tell a person about it is in the All table further
    /// down, which is the only list of settings there is.
    /// </summary>
    sealed class Config
    {
        // --- persisted state -------------------------------------------------
        /// <summary>Desktop count to restore at login; Windows forgets it on reboot.</summary>
        public int LastCount = 2;

        // --- layout ----------------------------------------------------------
        public int ButtonWidth = 34;
        public int PlusWidth = 26;
        public int Margin = 6;

        // --- behaviour --------------------------------------------------------
        /// <summary>Reconcile watchdog interval. Notifications are the primary path.</summary>
        public int ReconcileMs = 2000;
        public bool Diagnostics = false;

        // --- menu -------------------------------------------------------------
        /// <summary>
        /// Right click a button for a menu of what it can do. Off means right click sends
        /// the active window there, as it did before the menu existed; Shift + right click
        /// does that either way.
        /// </summary>
        public bool ContextMenu = true;

        // --- occupancy ----------------------------------------------------------
        /// <summary>
        /// A dim dot beside a button's number when that desktop has windows on it, nothing
        /// beside one that does not - which desktop is safe to remove, without hovering to
        /// find out. Off means the reconcile tick never sweeps for it, so it costs nothing.
        /// </summary>
        public bool OccupancyDots = true;

        // --- system stats -------------------------------------------------------
        /// <summary>
        /// A tile after '+' whose panel shows CPU, memory and network throughput. Click to
        /// open it, click again to close it; hovering only names the tile. Off removes the
        /// tile and the sampling behind it, so it costs nothing.
        /// </summary>
        public bool StatsTile = true;
        public int StatsWidth = 38;

        /// <summary>
        /// Which CPU figure the tile and panel show. "utility" - % Processor Utility, what
        /// Task Manager shows - scales busy time by the clock speed against base, so it reads
        /// lower than "time" at idle on a laptop and higher under boost. "time" - % Processor
        /// Time - is the plain share of time not idle. Utility falls back to time if the
        /// counter cannot be opened.
        /// </summary>
        public bool StatsCpuUtility = true;

        /// <summary>
        /// What the tile itself draws. "meters" - the default - is the two bars, CPU and
        /// memory. "gauge" is a single half-ring speedometer whose needle follows CPU alone:
        /// one glyph rather than two readings, for a tile that should read as an icon.
        /// "chip" is an icon outright - a processor chip that only says something when it
        /// turns amber or red. The statsBar* settings below shape the meters only; the gauge
        /// and the chip are sized by statsIconSize instead.
        /// </summary>
        public StatsTileStyle StatsStyle = StatsTileStyle.Meters;

        /// <summary>
        /// How tall the gauge or the chip stands, at 96 DPI - its own key rather than
        /// statsBarHeight, which the icons borrowed at first, so sizing the icon does not
        /// mean editing a setting named for bars that are not on screen. Capped at draw time
        /// to the cell like the meters: the taskbar's height, less the underline, and the
        /// tile's width - so past a point a bigger icon needs statsWidth raised too. The
        /// gauge is twice as wide as it is tall and meets the width cap first.
        /// </summary>
        public int StatsIconSize = 20;

        /// <summary>
        /// The tile's two meters, CPU and memory, at 96 DPI like every other size here: how
        /// wide each bar is, the space between them, and how tall they stand. Clamped at draw
        /// time to fit the tile and the taskbar, so an oversized value shrinks to fit rather
        /// than spilling into '+' or under the underline.
        /// </summary>
        public int StatsBarWidth = 6;
        public int StatsBarGap = 3;
        public int StatsBarHeight = 20;

        /// <summary>A small "C" and "M" under the bars, naming them. Off gives the bars the height back.</summary>
        public bool StatsBarLabels = true;

        /// <summary>
        /// Round ends on the bars, echoing the panel's ring gauges. Off - the default - draws
        /// plain rectangles, which were tried rounded and preferred square.
        /// </summary>
        public bool StatsBarRounded = false;

        /// <summary>
        /// Load, in percent, at which a bar turns amber and then red - the same for CPU and
        /// memory. Below the first it is the highlight colour.
        /// </summary>
        public int StatsWarnPercent = 75;
        public int StatsAlertPercent = 90;

        // --- tooltips ---------------------------------------------------------
        public bool Tooltips = true;
        /// <summary>Hover delay before the panel appears. 400ms is the Windows default.</summary>
        public int TooltipDelayMs = 400;
        /// <summary>Window titles shown before collapsing the rest into "+N more".</summary>
        public int TooltipMaxWindows = 8;
        /// <summary>
        /// Panel width at 96 DPI. Every panel is this wide whatever it holds, so the width
        /// does not jump as the pointer moves along the strip; longer rows are trimmed to
        /// fit. Widen it if your titles deserve more room.
        /// </summary>
        public int TooltipWidth = 440;

        // --- animation --------------------------------------------------------
        /// <summary>
        /// How long the strip takes to settle after the highlight or the hover changes:
        /// the underline slides to the new button and the cell tones ease rather than
        /// snapping. This is the whole of it, not a time constant - nothing is still
        /// moving afterwards. 0 turns animation off and the strip snaps as it used to,
        /// which also means no frame timer is ever created.
        ///
        /// 80 rather than the 120 this started at, because the ease is not the only delay
        /// in the chain. A logged Win+Ctrl+Right measured 36ms before Explorer says a word
        /// and 20ms more to the first moving pixel, so the animation lands on top of ~56ms
        /// that is already spent. At 120 the bar arrived 185ms after the key, late enough
        /// that the desktop had visibly changed first and the strip looked like it was
        /// catching up. At 80 it arrives around 145ms and reads as a response.
        /// </summary>
        public int AnimationMs = 80;

        // --- appearance -------------------------------------------------------
        public Color HighlightColor = Color.FromArgb(0x00, 0x78, 0xD7);
        /// <summary>Color.Empty means "sample the live taskbar colour at runtime".</summary>
        public Color BackgroundColor = Color.Empty;

        // --- the settings table ----------------------------------------------

        // Section names. They are the Settings window's tabs and the file's banners, in
        // the order they first appear in All.
        public const string StripSection = "Strip";
        public const string PanelSection = "Hover panel";
        public const string StatsSection = "Stats";
        public const string AdvancedSection = "Advanced";
        const string StateSection = "Kept by the app";

        /// <summary>
        /// Every setting, in the order the file lists them and the window shows them. The
        /// one list: Apply looks keys up in it, Render writes it out, the Settings window
        /// builds a row from each entry and --config checks it round-trips. A setting added
        /// here appears in all four without being mentioned anywhere else.
        ///
        /// The help text is for the person changing the value, so it says what they will
        /// see rather than why the code is shaped the way it is - that stays on the fields
        /// above. Kept ASCII, like every source file here.
        /// </summary>
        public static readonly Setting[] All =
        {
            // --- strip
            Int(StripSection, "buttonWidth", "Button width", 16, 128, "px",
                c => c.ButtonWidth, (c, v) => c.ButtonWidth = v,
                "Width of each numbered desktop button."),
            Int(StripSection, "plusWidth", "'+' button width", 12, 128, "px",
                c => c.PlusWidth, (c, v) => c.PlusWidth = v,
                "Width of the '+' button that adds a desktop."),
            Int(StripSection, "margin", "Gap to tray", 0, 200, "px",
                c => c.Margin, (c, v) => c.Margin = v,
                "Space between the strip and the notification area on its right."),
            Int(StripSection, "animationMs", "Animation", 0, 2000, "ms",
                c => c.AnimationMs, (c, v) => c.AnimationMs = v,
                "How long the underline takes to slide to a new desktop. 0 turns animation off."),
            Colour(StripSection, "highlightColor", "Highlight colour", false,
                c => c.HighlightColor, (c, v) => c.HighlightColor = v,
                "The underline under the current desktop. The tray icon and the meters use it too."),
            Colour(StripSection, "backgroundColor", "Background colour", true,
                c => c.BackgroundColor, (c, v) => c.BackgroundColor = v,
                "Behind the buttons, and the tone every panel and menu is drawn from. "
                + "Automatic samples the taskbar's own colour and follows it when it changes."),
            Bool(StripSection, "occupancyDots", "Occupancy dots",
                c => c.OccupancyDots, (c, v) => c.OccupancyDots = v,
                "A dim dot beside a desktop's number when it has windows on it, so the empty "
                + "ones - the ones safe to remove - show at a glance."),
            Bool(StripSection, "contextMenu", "Right-click menu",
                c => c.ContextMenu, (c, v) => c.ContextMenu = v,
                "Right click a button for a menu of what it can do. Off makes right click send "
                + "the active window to that desktop; Shift + right click does that either way."),

            // --- hover panel
            Bool(PanelSection, "tooltips", "Hover panel",
                c => c.Tooltips, (c, v) => c.Tooltips = v,
                "Hovering a button lists the windows on that desktop."),
            Int(PanelSection, "tooltipDelayMs", "Delay", 0, 5000, "ms",
                c => c.TooltipDelayMs, (c, v) => c.TooltipDelayMs = v,
                "How long to hover before the panel appears. 400 is the Windows default.")
                .When(c => c.Tooltips),
            Int(PanelSection, "tooltipMaxWindows", "Windows listed", 1, 40, "",
                c => c.TooltipMaxWindows, (c, v) => c.TooltipMaxWindows = v,
                "Window titles shown before the rest collapse into \"+N more\".")
                .When(c => c.Tooltips),
            Int(PanelSection, "tooltipWidth", "Panel width", 160, 1200, "px",
                c => c.TooltipWidth, (c, v) => c.TooltipWidth = v,
                "Every panel is this wide, so it does not jump about as the pointer moves "
                + "along the strip. Longer titles are trimmed to fit.")
                .When(c => c.Tooltips),

            // --- stats
            Bool(StatsSection, "systemStats", "Stats tile",
                c => c.StatsTile, (c, v) => c.StatsTile = v,
                "A tile after '+' showing CPU, memory and network. Click it to open the "
                + "panel, click again to close it. Off removes the tile and the sampling behind it."),
            Int(StatsSection, "statsWidth", "Tile width", 12, 128, "px",
                c => c.StatsWidth, (c, v) => c.StatsWidth = v,
                "Width of the stats tile.")
                .When(c => c.StatsTile),
            Choice(StatsSection, "statsCpu", "CPU figure",
                new[] { "utility", "time" }, new[] { "Utility", "Time" },
                c => c.StatsCpuUtility ? 0 : 1, (c, v) => c.StatsCpuUtility = v == 0,
                "Utility is what Task Manager shows: busy time scaled by clock speed. Time is "
                + "the plain share of time not idle, and reads higher at idle on a laptop.")
                .When(c => c.StatsTile),
            Choice(StatsSection, "statsTileStyle", "Tile style",
                new[] { "meters", "gauge", "chip" }, new[] { "Meters", "Gauge", "Chip" },
                c => (int)c.StatsStyle, (c, v) => c.StatsStyle = (StatsTileStyle)v,
                "Meters is two bars, CPU and memory. Gauge is a needle following CPU. Chip is "
                + "an icon that only changes colour under load.")
                .When(c => c.StatsTile),
            Int(StatsSection, "statsIconSize", "Icon size", 8, 64, "px",
                c => c.StatsIconSize, (c, v) => c.StatsIconSize = v,
                "Height of the gauge or the chip. Shrunk to fit the tile and the taskbar.")
                .When(c => c.StatsTile && c.StatsStyle != StatsTileStyle.Meters),
            Int(StatsSection, "statsBarWidth", "Bar width", 1, 32, "px",
                c => c.StatsBarWidth, (c, v) => c.StatsBarWidth = v,
                "Width of each meter bar.")
                .When(c => c.StatsTile && c.StatsStyle == StatsTileStyle.Meters),
            Int(StatsSection, "statsBarGap", "Bar gap", 0, 32, "px",
                c => c.StatsBarGap, (c, v) => c.StatsBarGap = v,
                "Space between the two meter bars.")
                .When(c => c.StatsTile && c.StatsStyle == StatsTileStyle.Meters),
            Int(StatsSection, "statsBarHeight", "Bar height", 4, 64, "px",
                c => c.StatsBarHeight, (c, v) => c.StatsBarHeight = v,
                "Height of the meter bars. Shrunk to fit the taskbar if it is taller.")
                .When(c => c.StatsTile && c.StatsStyle == StatsTileStyle.Meters),
            Bool(StatsSection, "statsBarLabels", "Bar labels",
                c => c.StatsBarLabels, (c, v) => c.StatsBarLabels = v,
                "A small C and M under the bars, naming them. Off gives the bars the height back.")
                .When(c => c.StatsTile && c.StatsStyle == StatsTileStyle.Meters),
            Bool(StatsSection, "statsBarRounded", "Rounded bars",
                c => c.StatsBarRounded, (c, v) => c.StatsBarRounded = v,
                "Round ends on the meter bars rather than square ones.")
                .When(c => c.StatsTile && c.StatsStyle == StatsTileStyle.Meters),
            Int(StatsSection, "statsWarnPercent", "Amber at", 1, 100, "%",
                c => c.StatsWarnPercent, (c, v) => c.StatsWarnPercent = v,
                "Load at which the tile turns amber, for CPU and memory alike.")
                .When(c => c.StatsTile),
            Int(StatsSection, "statsAlertPercent", "Red at", 1, 100, "%",
                c => c.StatsAlertPercent, (c, v) => c.StatsAlertPercent = v,
                "Load at which it turns red. Never below the amber level.")
                .When(c => c.StatsTile),

            // --- advanced
            Int(AdvancedSection, "reconcileMs", "Check interval", 250, 60000, "ms",
                c => c.ReconcileMs, (c, v) => c.ReconcileMs = v,
                "How often the desktop list is double-checked and the occupancy dots and stats "
                + "refreshed. Shell notifications do the real work; this is the safety net."),
            Bool(AdvancedSection, "diagnostics", "Diagnostics log",
                c => c.Diagnostics, (c, v) => c.Diagnostics = v,
                "Write what the app is doing to log.txt beside this file. For tracking down a problem."),

            // --- state
            Hide(Int(StateSection, "lastCount", "Desktops at login", 1, 32, "",
                c => c.LastCount, (c, v) => c.LastCount = v,
                "How many desktops to put back at login, since Windows forgets them on a "
                + "reboot. The app keeps this up to date as desktops come and go.")),
        };

        static readonly Dictionary<string, Setting> ByKey = Index(All);

        /// <summary>Every value at its default, for the file's "default N" lines.</summary>
        static readonly Config Factory = new Config();

        static Dictionary<string, Setting> Index(Setting[] all)
        {
            var map = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
            foreach (Setting s in all) map.Add(s.Key, s);
            return map;
        }

        static Setting Int(string section, string key, string label, int min, int max, string unit,
                           Func<Config, int> get, Action<Config, int> set, string help)
        {
            return new Setting { Section = section, Key = key, Label = label, Help = help,
                                 Kind = SettingKind.Int, Min = min, Max = max, Unit = unit,
                                 GetInt = get, SetInt = set };
        }

        static Setting Bool(string section, string key, string label,
                            Func<Config, bool> get, Action<Config, bool> set, string help)
        {
            return new Setting { Section = section, Key = key, Label = label, Help = help,
                                 Kind = SettingKind.Bool, GetBool = get, SetBool = set };
        }

        static Setting Choice(string section, string key, string label, string[] choices, string[] captions,
                              Func<Config, int> get, Action<Config, int> set, string help)
        {
            return new Setting { Section = section, Key = key, Label = label, Help = help,
                                 Kind = SettingKind.Choice, Choices = choices, Captions = captions,
                                 Min = 0, Max = choices.Length - 1, GetInt = get, SetInt = set };
        }

        static Setting Colour(string section, string key, string label, bool allowAuto,
                              Func<Config, Color> get, Action<Config, Color> set, string help)
        {
            return new Setting { Section = section, Key = key, Label = label, Help = help,
                                 Kind = SettingKind.Color, AllowAuto = allowAuto,
                                 GetColor = get, SetColor = set };
        }

        static Setting Hide(Setting s)
        {
            s.Hidden = true;
            return s;
        }

        public static Setting Find(string key)
        {
            Setting s;
            return ByKey.TryGetValue(key, out s) ? s : null;
        }

        // --- file ---------------------------------------------------------------

        /// <summary>
        /// The second line of every file this build writes. A file without it predates the
        /// commented layout, and gets rewritten into it at startup the same way a file
        /// missing a key does - see Outdated.
        /// </summary>
        const string FormatMarker = "# format 4";

        readonly Dictionary<string, string> _unknown = new Dictionary<string, string>();

        /// <summary>Every key the file mentioned, recognised or not, lowercased.</summary>
        readonly HashSet<string> _present = new HashSet<string>();

        bool _formatCurrent;

        /// <summary>
        /// The text of config.ini that the live settings came from: what this process last
        /// wrote, or the hand edit it last applied. ConfigWatcher compares the file against
        /// it, which is how the app's own saves - a desktop added, a slider in the Settings
        /// window - are told apart from a person saving in Notepad, and do not bounce back
        /// as a reload of what was just applied.
        ///
        /// It has to follow hand edits too, not only writes. It used to be only what this
        /// process wrote, and then an edit that put a value back - 100 to 101, applied, then
        /// back to 100 - produced exactly the text last written, was taken for the app's own
        /// save and ignored, and left the strip on 101 with the file saying 100.
        /// </summary>
        public static string LastWritten { get; private set; }

        /// <summary>Records that the live settings now match <paramref name="text"/>, which this process did not write.</summary>
        public static void Adopt(string text)
        {
            LastWritten = text;
        }

        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DesktopSwitcher");
            }
        }

        public static string FilePath { get { return Path.Combine(Directory, "config.ini"); } }
        public static string LogPath { get { return Path.Combine(Directory, "log.txt"); } }

        public static Config Load()
        {
            string path = FilePath;
            if (!File.Exists(path)) return new Config();

            try
            {
                return FromText(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Write("config: load failed, using defaults - " + ex.Message);
                return new Config();
            }
        }

        public static Config FromText(string text)
        {
            var cfg = new Config();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line == FormatMarker) cfg._formatCurrent = true;
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                cfg.Apply(key, val);
            }

            cfg.Normalize(null);
            return cfg;
        }

        void Apply(string key, string val)
        {
            _present.Add(key.ToLowerInvariant());

            // Recognised keys match case-insensitively; unknown keys keep the caller's
            // original spelling so hand-edited files round-trip unchanged.
            Setting s = Find(key);
            if (s != null) s.Parse(this, val);
            else _unknown[key] = val;
        }

        /// <summary>
        /// The one rule that spans two settings: red cannot start below amber, or a bar would
        /// skip amber altogether. Whichever was just changed wins and the other moves to meet
        /// it, so dragging either slider past the other pushes it along rather than stopping
        /// dead. From the file there is no "just changed", and red is the one that moves.
        /// </summary>
        public void Normalize(string changedKey)
        {
            if (StatsAlertPercent >= StatsWarnPercent) return;

            if (string.Equals(changedKey, "statsAlertPercent", StringComparison.OrdinalIgnoreCase))
                StatsWarnPercent = StatsAlertPercent;
            else
                StatsAlertPercent = StatsWarnPercent;
        }

        /// <summary>
        /// True when the file on disk says nothing about some setting this build has -
        /// normally an upgrade, where the file was written before the key existed. Such a
        /// setting is silently on its default with no line to edit, which is exactly the
        /// state the first-run write exists to avoid, so the caller rewrites the file.
        /// </summary>
        public bool Incomplete
        {
            get
            {
                foreach (Setting s in All)
                    if (!_present.Contains(s.Key.ToLowerInvariant())) return true;

                return false;
            }
        }

        /// <summary>
        /// Incomplete, or written before the file carried its own explanations. Either way
        /// the caller rewrites it; every value survives, having just been read from it.
        /// </summary>
        public bool Outdated { get { return Incomplete || !_formatCurrent; } }

        public void Save()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                    System.IO.Directory.CreateDirectory(Directory);

                string text = Render();
                File.WriteAllText(FilePath, text);
                LastWritten = text;
            }
            catch (Exception ex)
            {
                Log.Write("config: save failed - " + ex.Message);
            }
        }

        /// <summary>
        /// The complete file: every setting this build has, whether or not the file on
        /// disk mentioned it, each under a comment saying what it does and what it takes,
        /// followed by anything the file had that this build does not know.
        ///
        /// The comments are regenerated, never read back. That is the cost of a file the
        /// app also writes: a person's own comments do not survive the next save, and the
        /// header says so rather than letting them find out.
        /// </summary>
        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DesktopSwitcher settings");
            sb.AppendLine(FormatMarker);
            sb.AppendLine("#");
            sb.AppendLine("# Saving this file applies it straight away - no restart, no reload.");
            sb.AppendLine("# The same settings are under Settings on the tray icon's menu, which");
            sb.AppendLine("# shows each change on the strip as you make it.");
            sb.AppendLine("#");
            sb.AppendLine("# Sizes are in pixels at 100% display scaling and grow with it.");
            sb.AppendLine("# A number out of range is pulled back into it; a value that cannot be");
            sb.AppendLine("# read keeps its default. The app rewrites this file whenever a setting");
            sb.AppendLine("# changes, so comments of your own are not kept.");

            // The lines a person actually edits stand out from the explanation around them:
            // the comments are indented and the settings sit hard against the left edge, as
            // a compact key=value with no spaces. A plain text file has no bold, and this is
            // the nearest thing to it that survives Notepad. The parser trims around '=', so
            // a hand edit that adds spaces back still reads.
            string section = null;
            foreach (Setting s in All)
            {
                if (s.Section != section)
                {
                    section = s.Section;
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("# " + new string('=', 76));
                    sb.AppendLine("#   " + section);
                    sb.AppendLine("# " + new string('=', 76));
                }

                sb.AppendLine();
                foreach (string line in Wrap(s.Help, 72))
                    sb.AppendLine(CommentIndent + "# " + line);
                sb.AppendLine(CommentIndent + "# [" + s.Describe(Factory) + "]");

                string value = s.Format(this);
                sb.AppendLine(s.Key + "=" + value);
            }

            if (_unknown.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("# " + new string('=', 76));
                sb.AppendLine("#   Not recognised by this version - kept as they were");
                sb.AppendLine("# " + new string('=', 76));
                sb.AppendLine();
                foreach (var kv in _unknown)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
            }

            return sb.ToString();
        }

        const string CommentIndent = "    ";

        static IEnumerable<string> Wrap(string text, int width)
        {
            var line = new StringBuilder();
            foreach (string word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Length = 0;
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
        }

        // --- copies ---------------------------------------------------------------

        /// <summary>
        /// A separate Config with the same values, the same unrecognised keys and the same
        /// record of what the file mentioned. The Settings window edits one of these and the
        /// live config is replaced wholesale, never written through.
        /// </summary>
        public Config Clone()
        {
            var copy = new Config();
            foreach (Setting s in All) s.Copy(this, copy);
            foreach (var kv in _unknown) copy._unknown[kv.Key] = kv.Value;
            foreach (string key in _present) copy._present.Add(key);
            copy._formatCurrent = _formatCurrent;
            return copy;
        }

        /// <summary>
        /// Every preference back at its default, keeping what is not a preference: the
        /// desktop count and any keys this build does not recognise.
        /// </summary>
        public Config WithDefaults()
        {
            var fresh = Clone();
            foreach (Setting s in All)
                if (!s.Hidden) s.Copy(Factory, fresh);
            return fresh;
        }

        /// <summary>Same value for every setting and the same unrecognised keys.</summary>
        public bool SameAs(Config other)
        {
            if (other == null) return false;

            foreach (Setting s in All)
                if (s.Format(this) != s.Format(other)) return false;

            if (_unknown.Count != other._unknown.Count) return false;
            foreach (var kv in _unknown)
            {
                string v;
                if (!other._unknown.TryGetValue(kv.Key, out v) || v != kv.Value) return false;
            }
            return true;
        }
    }
}
