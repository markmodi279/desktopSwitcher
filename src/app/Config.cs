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

        readonly Dictionary<string, string> _unknown = new Dictionary<string, string>();

        /// <summary>Every key the file mentioned, recognised or not, lowercased.</summary>
        readonly HashSet<string> _present = new HashSet<string>();

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
            var cfg = new Config();
            string path = FilePath;
            if (!File.Exists(path)) return cfg;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    cfg.Apply(key, val);
                }
            }
            catch (Exception ex)
            {
                Log.Write("config: load failed, using defaults - " + ex.Message);
            }
            return cfg;
        }

        void Apply(string key, string val)
        {
            _present.Add(key.ToLowerInvariant());

            // Recognised keys match case-insensitively; unknown keys keep the caller's
            // original spelling so hand-edited files round-trip unchanged.
            switch (key.ToLowerInvariant())
            {
                case "lastcount":        LastCount       = ParseInt(val, LastCount, 1, 32);      break;
                case "buttonwidth":      ButtonWidth     = ParseInt(val, ButtonWidth, 16, 128);  break;
                case "pluswidth":        PlusWidth       = ParseInt(val, PlusWidth, 12, 128);    break;
                case "margin":           Margin          = ParseInt(val, Margin, 0, 200);        break;
                case "reconcilems":      ReconcileMs     = ParseInt(val, ReconcileMs, 250, 60000); break;
                case "diagnostics":      Diagnostics     = ParseBool(val, Diagnostics);          break;
                case "contextmenu":      ContextMenu     = ParseBool(val, ContextMenu);          break;
                case "tooltips":         Tooltips        = ParseBool(val, Tooltips);             break;
                case "tooltipdelayms":   TooltipDelayMs  = ParseInt(val, TooltipDelayMs, 0, 5000); break;
                case "tooltipmaxwindows": TooltipMaxWindows = ParseInt(val, TooltipMaxWindows, 1, 40); break;
                case "tooltipwidth":     TooltipWidth    = ParseInt(val, TooltipWidth, 160, 1200); break;
                case "animationms":      AnimationMs     = ParseInt(val, AnimationMs, 0, 2000);   break;
                case "highlightcolor":   HighlightColor  = ParseColor(val, HighlightColor);      break;
                case "backgroundcolor":  BackgroundColor = ParseColor(val, Color.Empty);         break;
                default:                 _unknown[key]   = val;                                  break;
            }
        }

        /// <summary>
        /// True when the file on disk says nothing about some setting this build has -
        /// normally an upgrade, where the file was written before the key existed. Such a
        /// setting is silently on its default with no line to edit, which is exactly the
        /// state the first-run write exists to avoid, so the caller rewrites the file.
        ///
        /// The expected keys are read back out of what Save writes, so there is no second
        /// list to fall out of step with the one above.
        /// </summary>
        public bool Incomplete
        {
            get
            {
                foreach (string key in KeysOf(Render()))
                    if (!_present.Contains(key)) return true;

                return false;
            }
        }

        static IEnumerable<string> KeysOf(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq > 0) yield return line.Substring(0, eq).Trim().ToLowerInvariant();
            }
        }

        public void Save()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                    System.IO.Directory.CreateDirectory(Directory);

                File.WriteAllText(FilePath, Render());
            }
            catch (Exception ex)
            {
                Log.Write("config: save failed - " + ex.Message);
            }
        }

        /// <summary>
        /// The complete file: every setting this build has, whether or not the file on
        /// disk mentioned it, followed by anything it did that this build does not know.
        /// </summary>
        string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DesktopSwitcher configuration");
            sb.AppendLine("# Colours are #RRGGBB. Leave backgroundColor blank to sample the taskbar.");
            sb.AppendLine();
            sb.AppendLine("lastCount = "       + LastCount);
            sb.AppendLine("buttonWidth = "     + ButtonWidth);
            sb.AppendLine("plusWidth = "       + PlusWidth);
            sb.AppendLine("margin = "          + Margin);
            sb.AppendLine("reconcileMs = "     + ReconcileMs);
            sb.AppendLine("diagnostics = "     + (Diagnostics ? "true" : "false"));
            sb.AppendLine("contextMenu = "     + (ContextMenu ? "true" : "false"));
            sb.AppendLine("tooltips = "        + (Tooltips ? "true" : "false"));
            sb.AppendLine("tooltipDelayMs = "  + TooltipDelayMs);
            sb.AppendLine("tooltipMaxWindows = " + TooltipMaxWindows);
            sb.AppendLine("tooltipWidth = "    + TooltipWidth);
            sb.AppendLine("animationMs = "     + AnimationMs);
            sb.AppendLine("highlightColor = "  + ToHex(HighlightColor));
            sb.AppendLine("backgroundColor = " + (BackgroundColor.IsEmpty ? "" : ToHex(BackgroundColor)));

            if (_unknown.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("# preserved from an earlier or hand-edited file");
                foreach (var kv in _unknown)
                    sb.AppendLine(kv.Key + " = " + kv.Value);
            }

            return sb.ToString();
        }

        // --- parsing helpers ---------------------------------------------------

        static int ParseInt(string s, int fallback, int min, int max)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return fallback;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        static bool ParseBool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "1": case "true":  case "yes": case "on":  return true;
                case "0": case "false": case "no":  case "off": return false;
                default: return fallback;
            }
        }

        static Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            string h = s.Trim();
            if (h.Length > 0 && h[0] == '#') h = h.Substring(1);
            if (h.Length != 6) return fallback;

            int rgb;
            if (!int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return fallback;

            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        static string ToHex(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }
}
