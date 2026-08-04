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

        // --- tooltips ---------------------------------------------------------
        public bool Tooltips = true;
        /// <summary>Hover delay before the panel appears. 400ms is the Windows default.</summary>
        public int TooltipDelayMs = 400;
        /// <summary>Window titles shown before collapsing the rest into "+N more".</summary>
        public int TooltipMaxWindows = 8;

        // --- appearance -------------------------------------------------------
        public Color HighlightColor = Color.FromArgb(0x00, 0x78, 0xD7);
        /// <summary>Color.Empty means "sample the live taskbar colour at runtime".</summary>
        public Color BackgroundColor = Color.Empty;

        readonly Dictionary<string, string> _unknown = new Dictionary<string, string>();

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
                case "tooltips":         Tooltips        = ParseBool(val, Tooltips);             break;
                case "tooltipdelayms":   TooltipDelayMs  = ParseInt(val, TooltipDelayMs, 0, 5000); break;
                case "tooltipmaxwindows": TooltipMaxWindows = ParseInt(val, TooltipMaxWindows, 1, 40); break;
                case "highlightcolor":   HighlightColor  = ParseColor(val, HighlightColor);      break;
                case "backgroundcolor":  BackgroundColor = ParseColor(val, Color.Empty);         break;
                default:                 _unknown[key]   = val;                                  break;
            }
        }

        public void Save()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                    System.IO.Directory.CreateDirectory(Directory);

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
                sb.AppendLine("tooltips = "        + (Tooltips ? "true" : "false"));
                sb.AppendLine("tooltipDelayMs = "  + TooltipDelayMs);
                sb.AppendLine("tooltipMaxWindows = " + TooltipMaxWindows);
                sb.AppendLine("highlightColor = "  + ToHex(HighlightColor));
                sb.AppendLine("backgroundColor = " + (BackgroundColor.IsEmpty ? "" : ToHex(BackgroundColor)));

                if (_unknown.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("# preserved from an earlier or hand-edited file");
                    foreach (var kv in _unknown)
                        sb.AppendLine(kv.Key + " = " + kv.Value);
                }

                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Write("config: save failed - " + ex.Message);
            }
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
