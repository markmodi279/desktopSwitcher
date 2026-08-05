using System.Drawing;

namespace DesktopSwitcher
{
    /// <summary>
    /// The one place that knows which way "away from the background" is.
    ///
    /// Everything drawn here derives from the sampled taskbar colour, which was the right
    /// instinct badly carried out: three files independently assumed that colour was dark.
    /// The strip, the hover panel and the menu each lightened the background for their
    /// surfaces and painted near-white text on top. Set a light taskbar - which is the
    /// default on a fresh Windows 10 in light mode - and every one of them produced
    /// near-white on near-white. The app was not merely ugly, it was invisible, and the
    /// tray icon was the only evidence it was running.
    ///
    /// So the two operations are stated once, here, in terms of the background rather than
    /// in terms of "lighter": Lift moves away from it whichever way that is, and text tones
    /// come from whichever pole the background is not sitting on. Callers keep the amounts
    /// they were already passing, so a dark taskbar looks exactly as it did.
    /// </summary>
    static class Palette
    {
        /// <summary>
        /// True when the background is nearer white than black.
        ///
        /// Rec. 601 luma rather than a plain channel average: green carries most of
        /// perceived brightness and blue almost none, so a saturated blue accent taskbar
        /// reads as dark - which it is, to the eye - where an average would call it
        /// middling and leave the text tone on a coin toss.
        /// </summary>
        public static bool IsLight(Color background)
        {
            return (299 * background.R + 587 * background.G + 114 * background.B) / 1000 >= 128;
        }

        /// <summary>
        /// Moves a colour away from the background by <paramref name="amount"/> of the
        /// distance to the far end - lightening a dark background, darkening a light one.
        ///
        /// This is the old Lighten() with the direction taken out of it. Every amount in
        /// the codebase was tuned against a dark taskbar and is unchanged, so the same
        /// number now buys the same separation on a light one instead of washing out.
        /// </summary>
        public static Color Lift(Color background, float amount)
        {
            int target = IsLight(background) ? 0 : 255;

            return Color.FromArgb(Mix(background.R, target, amount),
                                  Mix(background.G, target, amount),
                                  Mix(background.B, target, amount));
        }

        // Text tones, one column per pole. The dark-background column is exactly what the
        // three files hardcoded before this existed, so nothing moves on a dark taskbar.
        //
        // The light column is not a straight mirror. Pure black on a near-white bar is
        // harsher than near-white is on a dark one - dark-on-light already has the higher
        // contrast at equal distance from the background - so each tone stops short of the
        // end, and the muted tone barely moves, because it is already mid-grey.
        //
        //                                        on dark   on light
        static readonly int[] Primary   = { 255, 20 };   // titles, and the current button at full
        static readonly int[] Body      = { 225, 45 };   // menu rows
        static readonly int[] Secondary = { 205, 75 };   // hover panel window rows
        static readonly int[] Muted     = { 130, 120 };  // a row that cannot be clicked
        static readonly int[] Resting   = { 200, 95 };   // an inactive, unhovered button

        /// <summary>Highest-contrast text. Panel titles, and the number on the current button.</summary>
        public static Color PrimaryText(Color background) { return Tone(background, Primary); }

        /// <summary>Ordinary readable text. Menu rows.</summary>
        public static Color BodyText(Color background) { return Tone(background, Body); }

        /// <summary>Text that supports something above it. The window rows under a panel title.</summary>
        public static Color SecondaryText(Color background) { return Tone(background, Secondary); }

        /// <summary>
        /// A disabled row. Faded, deliberately not faded out: a greyed item here means the
        /// action is impossible right now, which is information worth reading.
        /// </summary>
        public static Color MutedText(Color background) { return Tone(background, Muted); }

        /// <summary>
        /// The strip's ramp. A button at rest sits back from the primary tone, and emphasis
        /// - being current, being hovered - carries it the rest of the way there.
        ///
        /// On a light taskbar this runs downward, toward black. The caller never has to know
        /// that, which is the entire point of the file.
        /// </summary>
        public static Color RampText(Color background, float emphasis)
        {
            int index = IsLight(background) ? 1 : 0;
            int rest = Resting[index];
            int full = Primary[index];

            return Grey(Clamp(rest + (int)((full - rest) * emphasis)));
        }

        static Color Tone(Color background, int[] pair)
        {
            return Grey(pair[IsLight(background) ? 1 : 0]);
        }

        static Color Grey(int tone)
        {
            return Color.FromArgb(tone, tone, tone);
        }

        static int Mix(int from, int to, float amount)
        {
            return Clamp(from + (int)((to - from) * amount));
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }
    }
}
