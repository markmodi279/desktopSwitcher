using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// A removed desktop's button folding away, instead of the row snapping shut.
    ///
    /// When exactly one desktop goes, the strip keeps a gap where its button was - the
    /// button's own width - fills it with the accent for a moment, then eases it shut. The
    /// strip is right-anchored, so the buttons right of the gap stay put on screen and the
    /// ones left of it slide right to close it; the strip is re-placed every frame the gap
    /// changes, the way the tray glide re-places it.
    ///
    /// Before this the row simply renumbered. Removing desktop 3 of 5 from its menu or a
    /// middle click, the button under the pointer instantly became what had been 4, with
    /// nothing on screen to say a desktop had gone rather than been switched away from.
    ///
    /// Only the arithmetic lives here, apart from SwitcherStrip, so --fold can step it
    /// headlessly the way --glide and --anim step theirs.
    /// </summary>
    sealed class StripFold
    {
        /// <summary>How long the gap shows the accent before it starts to close.</summary>
        public const int FlashMs = 250;

        /// <summary>
        /// The least time the gap takes to close. animationMs is tuned for the underline,
        /// which travels a few button widths and is watched; at 80 ms a gap closing after
        /// the flash read as a snap, which is the thing this exists to replace. Slower
        /// settings are used as they are. The same reasoning as the pin flash's own clock.
        /// </summary>
        public const int MinCloseMs = 200;

        /// <summary>Where the gap is: the index the button after it now has. -1 when none.</summary>
        public int Index = -1;

        /// <summary>The gap's width now, in pixels. Eases from the removed button's width to 0.</summary>
        public float Gap;

        /// <summary>The flash's strength, 1 to 0 over FlashMs. The gap holds its width meanwhile.</summary>
        public float Flash;

        public bool Active { get { return Index >= 0; } }

        /// <summary>The gap as the layout uses it: whole pixels.</summary>
        public int GapPixels { get { return Active ? (int)Math.Round(Gap) : 0; } }

        /// <summary>
        /// Opens a gap at <paramref name="index"/>. A fold already running is dropped where
        /// it stands: two removals in quick succession are two separate events, and a row
        /// with two gaps easing at once would have no single place to point at.
        /// </summary>
        public void Start(int index, float width)
        {
            Index = index;
            Gap = width;
            Flash = 1f;
        }

        /// <summary>
        /// One frame. True when anything changed, which keeps the strip's timer running.
        /// Linear for the flash, so it reads as one beat; Motion's ease for the closing,
        /// so it decelerates into place like every other move on the strip.
        /// </summary>
        public bool Step(int elapsedMs, int animationMs)
        {
            if (!Active) return false;
            if (elapsedMs <= 0) return false;

            if (animationMs <= 0)
            {
                Snap();
                return true;
            }

            if (Flash > 0f)
            {
                Flash = Math.Max(0f, Flash - (float)elapsedMs / FlashMs);
                return true;
            }

            int closeMs = Math.Max(animationMs, MinCloseMs);
            bool moved = Motion.Ease(ref Gap, 0f, Motion.Rate(elapsedMs, closeMs), Motion.PixelEpsilon);
            if (Gap == 0f) Index = -1;
            return moved;
        }

        /// <summary>Shut at once. Anything that changes the row mid-fold lands here.</summary>
        public void Snap()
        {
            Index = -1;
            Gap = 0f;
            Flash = 0f;
        }

        /// <summary>
        /// Which single desktop went, comparing the row before with the row after: its old
        /// index, or -1 when the change is anything other than exactly one removal with
        /// everything else in the same order. A reorder recreates desktops, so it is a
        /// removal and an addition at once and folds nothing.
        /// </summary>
        public static int RemovedIndex(Guid[] before, Guid[] after)
        {
            if (before == null || after == null || before.Length != after.Length + 1) return -1;

            int removed = -1;
            for (int i = 0, j = 0; i < before.Length; i++)
            {
                if (j < after.Length && before[i] == after[j]) { j++; continue; }
                if (removed >= 0) return -1;
                removed = i;
            }
            return removed;
        }
    }
}
