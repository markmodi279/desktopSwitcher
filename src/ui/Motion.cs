using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// The easing every moving thing in this app is stepped by.
    ///
    /// This began inside SwitcherStrip, because for one release the strip was the only
    /// thing that moved: the button tones and the underline bar. When the hover panel
    /// learned to travel between buttons rather than teleport, a second animator appeared,
    /// and it could not simply call into the first - the strip *creates* the panel, so the
    /// panel reaching back for SwitcherStrip.Ease would have pointed the dependency
    /// backwards through the layer that owns it.
    ///
    /// So the arithmetic lives here and neither of them owns it. There are deliberately no
    /// forwarding constants left behind on SwitcherStrip: two names for one number is
    /// exactly the drift this codebase spends its comments avoiding.
    /// </summary>
    static class Motion
    {
        /// <summary>
        /// Frame interval. Deliberately 15 and not 16.
        ///
        /// The system timer ticks about every 15.6ms and a WM_TIMER only fires on a tick
        /// once its interval has elapsed. Ask for 16 and the first tick at 15.6 is one
        /// step too early, so the frame lands on the second at 31.2 instead - a logged
        /// trace of a real switch alternated 16ms and 31ms frames, running at nearer 40Hz
        /// than 60. Asking for 15 is satisfied by every tick.
        ///
        /// It only affects smoothness either way: each step is taken against measured
        /// elapsed time, not against this number.
        /// </summary>
        public const int FrameMs = 15;

        /// <summary>
        /// Below this, a 0..1 value can no longer change an 8-bit colour, so the ease
        /// finishes there rather than spending frames converging invisibly.
        /// </summary>
        public const float ToneEpsilon = 1f / 255f;

        /// <summary>
        /// The same idea for anything measured in pixels - the underline bar's rectangle,
        /// the hover panel's edges: half a pixel cannot be drawn.
        /// </summary>
        public const float PixelEpsilon = 0.5f;

        /// <summary>
        /// The fraction of the remaining distance to cross in a frame of
        /// <paramref name="elapsedMs"/>, for a settle time of
        /// <paramref name="animationMs"/>.
        ///
        /// Exponential smoothing rather than a fixed-duration tween, for one reason:
        /// retargeting mid-flight is free. Hold Win+Ctrl+Right down and the current
        /// desktop changes several times before anything settles; a tween has to re-base
        /// its start value and its start time on every one of those or the bar jumps, and
        /// this simply keeps heading somewhere new from wherever it had got to. It also
        /// decelerates into place by construction, which is what the motion should do.
        ///
        /// That property is what let the hover panel reuse this untouched. Sweeping the
        /// pointer along the strip retargets the panel at every button it crosses, which is
        /// the same problem the bar already had and the same answer.
        ///
        /// animationMs is the time to settle, not a time constant: ln(255) time constants
        /// is where the remaining distance falls under ToneEpsilon and the ease finishes.
        /// The snap itself lands on whichever frame comes after that, so --anim reports 120
        /// arriving at 144 on 16ms frames - frame quantisation, not drift.
        ///
        /// A frame that arrives very late gives a rate at or near 1 and lands the value on
        /// its target, so a busy machine loses frames rather than slowing the animation
        /// down - which is exactly what stepping by elapsed time is for. The UI thread also
        /// carries the reconcile tick, the watchdog and the window-inventory sweep, so late
        /// frames are the normal case, not the pathological one.
        /// </summary>
        public static float Rate(int elapsedMs, int animationMs)
        {
            if (animationMs <= 0 || elapsedMs <= 0) return 1f;

            const double Settles = 5.5413;   // Math.Log(255)

            double tau = animationMs / Settles;
            return (float)(1.0 - Math.Exp(-elapsedMs / tau));
        }

        /// <summary>
        /// One step of one value toward its target. True when the value moved, which is
        /// the caller's cue both to repaint and to keep the timer running.
        ///
        /// Within <paramref name="epsilon"/> the value is snapped onto the target and the
        /// snap still counts as a move, so the last frame drawn is the exact one; the call
        /// after that finds them equal, reports nothing, and that is what stops the timer.
        /// </summary>
        public static bool Ease(ref float value, float target, float rate, float epsilon)
        {
            if (value == target) return false;

            float distance = target - value;
            if (distance < 0) distance = -distance;

            if (distance < epsilon) { value = target; return true; }

            value += (target - value) * rate;
            return true;
        }
    }
}
