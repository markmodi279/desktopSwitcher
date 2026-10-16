using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// What the stats tile draws in its cell - statsTileStyle. Here rather than in Config
    /// because the strip is what draws it; Config names it, as it names a Color.
    /// </summary>
    enum StatsTileStyle
    {
        Meters,   // two bars, CPU and memory
        Gauge,    // a half-ring gauge, needle on CPU
        Chip,     // a static chip glyph, tinted by the heavier of CPU and memory
        Rings,    // two concentric rings, CPU outside and memory inside
        Monitor,  // a static monitor glyph, tinted like the chip
        Fan,      // a static fan glyph, tinted like the chip
    }

    /// <summary>
    /// A click on the stats tile that does something other than toggle the panel -
    /// statsCopyGesture and statsKeepGesture. Here rather than in Config because the strip
    /// is what recognises it. Off is also what the controller hands over for a copy while
    /// statsPublicIp is off, so the click falls back to whatever it did before.
    ///
    /// Shift is the only modifier a right click can carry: a plain or Ctrl right click
    /// opens the menu before the tile gets a look at it. Shift+right is free on the tile
    /// because the menu already steps aside for it - on a desktop button it means "send
    /// the active window here", which the tile has no use for.
    ///
    /// Appended to, never reordered: a Choice setting is stored by name but held by index.
    /// </summary>
    enum TileGesture
    {
        Off,
        CtrlClick,
        ShiftClick,
        AltClick,
        MiddleClick,
        ShiftRightClick,
    }

    /// <summary>
    /// How the strip or the hover panel is drawn - stripStyle and panelStyle, one each.
    ///
    /// It was one switch for both at first, on the reasoning that the two are meant to read
    /// as one thing. Two won out: someone who wants the new panel may well want to keep the
    /// strip the taskbar has always had, and the strip is the part on screen all day. Every
    /// mix is meant to work - a classic panel keeps its stub over a modern strip, and a
    /// modern panel needs none over a classic one, where the lit cell still says which
    /// button it describes.
    ///
    /// Classic is the original: square cells, a full-width underline, and a flat panel
    /// flush over the strip with an accent stub. Modern rounds the cells, marks the current
    /// desktop with a short pill, and makes the panel a layered window - rounded, shadowed,
    /// an icon on every window row. The panel's frame is the one structural difference; the
    /// rest is the same drawing code reading different numbers.
    /// </summary>
    enum Look
    {
        Classic,
        Modern,
    }

    /// <summary>
    /// The numbered button strip that lives in the taskbar.
    ///
    /// A NativeWindow rather than a Form, because a Form cannot become a child of
    /// Shell_TrayWnd. It owns no desktop state: it renders whatever list it is given
    /// and reports intent through events, leaving DesktopService to act.
    ///
    /// ANIMATION: per-button visual state lives in ButtonVisual as floats in 0..1,
    /// separate from the Desktop model, each with the target it is heading for beside
    /// it. SyncVisuals sets targets; a frame timer eases the values toward them and
    /// stops the moment they all arrive. Render stays pure - it reads visual state and
    /// draws, mutating nothing - which is what lets the animator be a timer and a
    /// handful of eases rather than a rewrite.
    ///
    /// Two independent per-button floats can only ever cross-fade, though, so the
    /// underline bar is not one of them: its rectangle is strip-level state, eased as
    /// geometry and drawn once outside the per-button loop, which is what makes it
    /// travel from the old current button to the new one. The M6 note here promised
    /// Render would need no change to animate; that held for the buttons, and did not
    /// anticipate the bar.
    ///
    /// The frame timer drives the hover panel as well, which is a window of its own and
    /// repaints itself. The two motions always begin together - the WM_MOUSEMOVE that
    /// lifts a button's hover tone is the same one that aims the panel at it - so a second
    /// timer would duplicate the elapsed-time bookkeeping to run in lockstep with this one.
    /// OnFrame therefore asks the strip and the panel separately whether they moved, so a
    /// slide happening entirely above the taskbar does not repaint the taskbar for it.
    ///
    /// STATS TILE: optional, after '+', at index _desktops.Count + 1. It has no desktop and
    /// raises no intent; its whole job is the panel, which shows CPU, memory and network and
    /// refreshes once a second while it is up. Hover shows only an ordinary tooltip naming
    /// the tile; the figures come from a click, which pins the panel, and a pinned panel is
    /// where the panel *rests*: it still follows the pointer
    /// to describe other buttons, and wherever it would otherwise have hidden - the pointer
    /// leaving, a click, a desktop change - it returns to the tile instead. The content is
    /// the controller's, through StatsProvider, for the same reason tooltip text is.
    ///
    /// Pinned comes in two strengths. Opened by a click on the tile, it is a popup: a press
    /// anywhere but the tile closes it - see ClickWatcher. Opened by "Keep open" from the
    /// tile's menu, it is kept: clicks elsewhere leave it alone, and only the tile or the
    /// menu again close it. Either way, while the taskbar is not on
    /// screen - auto-hidden, or under a fullscreen app - the panel is put away without
    /// being unpinned, and comes back with the taskbar. See StatsCovered.
    /// </summary>
    sealed class SwitcherStrip : NativeWindow, IDisposable
    {
        /// <summary>Continuous visual state, deliberately decoupled from the model.</summary>
        struct ButtonVisual
        {
            public float Highlight;   // 0 = inactive, 1 = current desktop
            public float Hover;       // 0 = idle,     1 = pointer over
            public float Dot;         // 0 = empty,    1 = has windows

            public float HighlightTarget;
            public float HoverTarget;
            public float DotTarget;
        }

        /// <summary>
        /// How long an already-open panel waits before changing what it says.
        ///
        /// Not a second hover delay - the full delay is paid once, to open the first panel,
        /// and re-charging it per button would wreck the one thing the panel is for, which
        /// is reading along the strip.
        ///
        /// It gates the text and nothing else. For one release it gated everything, and that
        /// was worse than the strobe it fixed: for 80ms the panel sat still, attached to the
        /// button the pointer had left, describing a desktop that was no longer the one
        /// being asked about. A panel that is wrong and motionless reads as broken in a way
        /// a panel that is merely busy does not.
        ///
        /// Splitting it is what makes the delay invisible. The panel starts travelling on
        /// the same mouse message, so something answers the pointer immediately; the text
        /// lands 80ms later, which - animationMs being 80 as well - is about when the glide
        /// finishes. The two arrive together and the wait has nowhere to show itself.
        ///
        /// The cost per crossing is now MoveTo only: no BuildRows, so no MeasureString per
        /// row on the thread that is also running the frame timer. (An earlier note here
        /// claimed a window-inventory query per crossing too. That was wrong - WindowInventory
        /// caches for a second, so a sweep never re-enumerates.)
        /// </summary>
        public const int ReshowMs = 80;

        /// <summary>
        /// How often an open stats panel re-reads its figures. A second is what Task Manager
        /// uses at its normal speed, and it is slow enough that a figure can be read before
        /// it changes. The timer behind it runs only while the stats panel is on screen.
        /// </summary>
        public const int StatsRefreshMs = 1000;

        /// <summary>
        /// How often a pinned panel checks that the taskbar it rests on is still on screen.
        /// Faster than the refresh because the cases it catches are visible: an auto-hide
        /// taskbar slides away a moment after the pointer leaves, and a panel lingering a
        /// full second after it reads as left behind. The check is two cheap calls.
        /// </summary>
        public const int StatsGuardMs = 250;

        /// <summary>
        /// Posted by the ClickWatcher for every mouse press on the system, with the screen
        /// point in lParam. Private to this window class, so WM_APP's range is ours.
        /// </summary>
        const int WM_CLICK_ANYWHERE = Native.WM_APP + 1;

        readonly int _buttonWidth;
        readonly int _plusWidth;
        readonly int _statsWidth;      // 0 means no stats tile at all
        readonly int _barHeight;
        readonly uint _hoverDelay;
        readonly int _tooltipWidth;
        readonly int _animationMs;
        readonly double _dpiScale;
        readonly Look _look;           // stripStyle: this strip's own cells and bar
        readonly Look _panelLook;      // panelStyle: handed to the hover panel it creates

        List<Desktop> _desktops = new List<Desktop>();
        ButtonVisual[] _visuals = new ButtonVisual[0];

        // Which desktops have windows, by Guid - keyed the same way _desktops is read in
        // SyncVisuals, so a set change and an occupancy refresh landing in either order
        // still end up pointing at the right button. Empty until the first refresh, which
        // reads as every button empty rather than every button occupied - the safer of
        // the two wrong answers for the one frame before real data arrives.
        ICollection<Guid> _occupied = new HashSet<Guid>();

        // The underline bar, in strip coordinates. Strip-level rather than per-button
        // because a bar that travels is one object moving, not two fading.
        float _barX, _barWidth, _barLevel;
        float _barXTarget, _barWidthTarget, _barLevelTarget;

        // The stats tile's two meters, CPU and memory, 0..1. Strip-level rather than in
        // ButtonVisual because they belong to one button only, and eased like everything
        // else on the strip so a reading arriving does not snap the bar to its new height.
        // Zero until the first push, which draws an empty track - "not measured yet" - rather
        // than a full one.
        float _cpuLevel, _memLevel;
        float _cpuLevelTarget, _memLevelTarget;

        // Meter geometry in physical pixels, from SetStatsMeter. Zero until it is called,
        // which falls back to sizes derived from the font - see DrawStatsButton.
        int _meterWidth, _meterGap, _meterHeight;
        int _iconSize;                 // any icon style's height, physical; 0 falls back to _meterHeight
        bool _meterLabels = true;
        bool _meterRounded;
        StatsTileStyle _tileStyle;                        // the bars or one of the icons
        float _meterWarn = 0.75f, _meterAlert = 0.90f;   // levels the fill turns amber, then red
        Font _labelFont;                                  // the "C" and "M" under the meters

        Timer _frameTimer;            // runs only while something is actually moving

        // statsFanSpin: the fan's rotor angle, in degrees within one blade's 120, and its own
        // timer - see UpdateSpin for why it is not the frame timer.
        bool _fanSpin;
        float _fanSpeed = 1f;         // statsFanSpeed, as a multiplier on the two speeds below
        float _fanAngle;
        int _fanLast;
        Timer _spinTimer;
        int _lastFrame;                // Environment.TickCount at the last stepped frame

        int _hoverIndex = -1;          // == _desktops.Count means '+', one past it the stats tile
        int _menuIndex = -1;           // button whose context menu is open, by the same index
        bool _trackingMouse;
        bool _disposed;

        bool _statsPinned;             // the panel rests on the stats tile instead of hiding
        bool _statsKept;               // pinned from "Keep open": a click elsewhere does not close it
        bool _showingStats;            // what the open panel says is the stats, not a button's
        bool _statsDismissed;          // unpinned by a click; no hover panel until the pointer leaves the tile
        bool _statsSuppressed;         // pinned, but put away because the taskbar is not on screen
        int _statsShownAt;             // Environment.TickCount the figures were last read
        Timer _statsTimer;             // runs only while the stats panel is up, or suppressed
        ClickWatcher _clicks;          // exists only while pinned

        Color _background;
        Color _highlight;
        Bitmap _buffer;
        Font _font;
        Font _currentFont;             // modern: the current desktop's number, semibold
        Font _glyphFont;               // modern: Segoe MDL2 Assets for the '+'; null if missing

        TooltipWindow _tooltip;
        Timer _reshowTimer;            // runs only between a button change and the follow

        public SwitcherStrip(IntPtr parent, Rectangle bounds,
                             int buttonWidth, int plusWidth, int statsWidth, int barHeight,
                             Color background, Color highlight,
                             uint hoverDelay, int tooltipWidth, int animationMs,
                             double dpiScale, Look look, Look panelLook)
        {
            _buttonWidth = buttonWidth;
            _plusWidth = plusWidth;
            _statsWidth = Math.Max(0, statsWidth);
            _barHeight = barHeight;
            _hoverDelay = hoverDelay;
            _tooltipWidth = tooltipWidth;
            _animationMs = animationMs;
            _dpiScale = dpiScale;
            _look = look;
            _panelLook = panelLook;
            _background = background;
            _highlight = highlight;

            var cp = new CreateParams();
            cp.Caption = "DesktopSwitcherStrip";
            cp.X = bounds.X;
            cp.Y = bounds.Y;
            cp.Width = bounds.Width;
            cp.Height = bounds.Height;
            cp.Parent = parent;
            cp.Style = Native.WS_CHILD | Native.WS_VISIBLE;
            cp.ExStyle = Native.WS_EX_NOACTIVATE;
            CreateHandle(cp);
        }

        /// <summary>Left click on a desktop button.</summary>
        public event Action<Guid> SwitchRequested;

        /// <summary>Middle click on a desktop button.</summary>
        public event Action<Guid> RemoveRequested;

        /// <summary>Shift + right click on a desktop button - send the active window there.</summary>
        public event Action<Guid> MoveWindowRequested;

        /// <summary>Left click on the '+' button.</summary>
        public event EventHandler CreateRequested;

        /// <summary>
        /// Right click on any button: the index HitTest returned, and that button in screen
        /// coordinates for the menu to anchor to.
        ///
        /// The handler owes this class a MenuClosed() call on every path, including
        /// declining to show a menu at all - the button stays lit while its menu is up, and
        /// that is the only thing that puts it out.
        /// </summary>
        public event Action<int, Rectangle> ContextMenuRequested;

        /// <summary>
        /// Supplies the text for a button's tooltip, by the same index HitTest returns -
        /// so _desktops.Count means the '+' button. Returning null suppresses the tooltip.
        /// The stats tile comes here too, for its hover hint - StatsIndex - while it is not
        /// pinned; its figures come from StatsProvider.
        ///
        /// The strip contributes geometry only. Content needs the desktop model, the
        /// window inventory and the foreground tracker, none of which belong here.
        /// </summary>
        public Func<int, TooltipContent> TooltipProvider { get; set; }

        /// <summary>
        /// Supplies the stats panel's text, told whether the panel is kept open so the title
        /// can say so - a kept panel ignoring clicks elsewhere needs to explain itself, a
        /// popup does not. Called once when the panel opens and again every StatsRefreshMs
        /// while it stays up. Returning null suppresses the panel.
        /// </summary>
        public Func<bool, TooltipContent> StatsProvider { get; set; }

        /// <summary>
        /// The stats panel was pinned, kept or closed - (pinned, kept) - from a click on the
        /// tile, a click elsewhere, or SetStatsPinned. The strip is rebuilt from scratch after
        /// an Explorer restart, a theme change or an unlock, and the state would die with it;
        /// the controller keeps it from here and hands it back to the new strip.
        /// </summary>
        public event Action<bool, bool> StatsPinChanged;

        /// <summary>The stats tile was clicked with StatsCopyGesture: copy the public IP.</summary>
        public event EventHandler StatsCopyRequested;

        /// <summary>Which click on the stats tile raises StatsCopyRequested. Off for none.</summary>
        public TileGesture StatsCopyGesture { get; set; }

        /// <summary>
        /// Which click on the stats tile toggles the panel kept open - what the menu's
        /// "Keep open" does, without the menu. Off for none. Handled here, not raised: the
        /// strip owns the pin state and the controller only mirrors it.
        /// </summary>
        public TileGesture StatsKeepGesture { get; set; }

        /// <summary>The stats tile was clicked with StatsTaskManagerGesture.</summary>
        public event EventHandler StatsTaskManagerRequested;

        /// <summary>Which click on the stats tile raises StatsTaskManagerRequested. Off for none.</summary>
        public TileGesture StatsTaskManagerGesture { get; set; }

        // --- layout -----------------------------------------------------------

        public static int MeasureWidth(int desktopCount, int buttonWidth, int plusWidth, int statsWidth)
        {
            return desktopCount * buttonWidth + plusWidth + Math.Max(0, statsWidth);
        }

        public int Width { get { return MeasureWidth(_desktops.Count, _buttonWidth, _plusWidth, _statsWidth); } }

        /// <summary>
        /// The stats tile's index, by the same numbering HitTest uses, or -1 when there is no
        /// tile. Public so the controller can tell its context menu apart from '+'.
        /// </summary>
        public int StatsIndex { get { return _statsWidth > 0 ? _desktops.Count + 1 : -1; } }

        /// <summary>Every button, '+' and the stats tile included: what _visuals is sized to.</summary>
        int SlotCount { get { return _desktops.Count + (_statsWidth > 0 ? 2 : 1); } }

        Rectangle ButtonBounds(int index, int height)
        {
            if (index < _desktops.Count)
                return new Rectangle(index * _buttonWidth, 0, _buttonWidth, height);

            int plusX = _desktops.Count * _buttonWidth;
            if (index == _desktops.Count || _statsWidth <= 0)
                return new Rectangle(plusX, 0, _plusWidth, height);

            return new Rectangle(plusX + _plusWidth, 0, _statsWidth, height);
        }

        int HitTest(int x, int y)
        {
            if (x < 0) return -1;

            int buttonsWidth = _desktops.Count * _buttonWidth;
            if (x < buttonsWidth) return x / _buttonWidth;
            if (x < buttonsWidth + _plusWidth) return _desktops.Count;
            if (x < buttonsWidth + _plusWidth + _statsWidth) return _desktops.Count + 1;
            return -1;
        }

        /// <summary>
        /// A button in screen coordinates, spanning the full height of the strip. What the
        /// hover panel anchors to and what the context menu opens against, so the two come
        /// out of the same edge of the same rectangle.
        ///
        /// Empty when the strip has no window rect, which means it is being torn down.
        /// </summary>
        Rectangle AnchorFor(int index)
        {
            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return Rectangle.Empty;

            Rectangle button = ButtonBounds(index, strip.Height);
            return new Rectangle(strip.Left + button.X, strip.Top, button.Width, strip.Height);
        }

        /// <summary>
        /// The whole strip in screen coordinates. What the removal confirmation anchors
        /// to - unlike a button anchor, it does not travel with whichever button was
        /// clicked, since the dialog is modal and has no pointer to follow.
        ///
        /// Empty when the strip has no window rect, which means it is being torn down.
        /// </summary>
        public Rectangle ScreenBounds()
        {
            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return Rectangle.Empty;

            return new Rectangle(strip.Left, strip.Top, strip.Width, strip.Height);
        }

        // --- model ------------------------------------------------------------

        /// <summary>
        /// Replaces the rendered list. Visual state is rebuilt to match, preserving
        /// nothing across a set change since indices may refer to different desktops.
        /// </summary>
        public void SetDesktops(IList<Desktop> desktops)
        {
            // Whether these are the same desktops in the same order, decided before the
            // list is replaced. It is the difference between animating and snapping: the
            // bar sliding from button 3 to button 2 means something when they are the
            // desktops they were, and means nothing when a removal has just renumbered
            // everything after the one that went. Same for the first list a freshly built
            // strip is handed, after login or an Explorer restart - it must come up
            // already correct rather than sliding in from the left edge.
            bool sameSet = SameSet(_desktops, desktops);

            _desktops = new List<Desktop>(desktops);

            if (_visuals.Length != SlotCount)
                _visuals = new ButtonVisual[SlotCount];

            if (_hoverIndex >= SlotCount) _hoverIndex = -1;
            if (_menuIndex >= SlotCount) _menuIndex = -1;

            // Anything the panel was saying about desktop N may now describe a different
            // desktop, since a removal renumbers everything after it. The stats panel
            // describes no desktop and is left exactly where it is: this runs on every
            // desktop switch, and a pinned panel that blinked out and back each time would
            // be pinned in name only. Not even re-anchored, because it does not need to be -
            // the strip is right-anchored and the tile is its last button, so the tile's
            // place on screen does not move when the count does. (Re-anchoring here would be
            // actively wrong: the controller resizes the strip only after this returns, so
            // the window rect AnchorFor reads is still the old one.)
            if (!ShowingStats) RestOrHide();

            SyncVisuals();
            if (!sameSet) Settle();

            Invalidate();
        }

        /// <summary>Same desktops, same order. Identity is the Guid, never the index.</summary>
        static bool SameSet(IList<Desktop> a, IList<Desktop> b)
        {
            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++)
                if (a[i].Id != b[i].Id) return false;

            return true;
        }

        /// <summary>
        /// Points visual state at the model. The only place that decides what "current"
        /// and "hovered" look like, which is why it sets targets rather than values -
        /// every caller that changes what the strip should look like already comes
        /// through here, so every caller animates for free.
        /// </summary>
        void SyncVisuals()
        {
            int current = -1;

            for (int i = 0; i < _visuals.Length; i++)
            {
                bool isCurrent = i < _desktops.Count && _desktops[i].IsCurrent;
                if (isCurrent) current = i;

                _visuals[i].HighlightTarget = isCurrent ? 1f : 0f;

                // A button whose menu is open reads as hovered whatever the pointer is
                // doing, so it stays visibly the one the menu belongs to. A pinned stats
                // tile likewise, so it is visibly the thing holding the panel up - and the
                // thing to click to put it away.
                bool pinned = _statsPinned && i == StatsIndex;
                _visuals[i].HoverTarget = (i == _hoverIndex || i == _menuIndex || pinned) ? 1f : 0f;

                // '+' and the stats tile have no desktop and so are never in _occupied - i
                // at or beyond _desktops.Count reads false here the same way isCurrent does.
                bool occupied = i < _desktops.Count && _occupied.Contains(_desktops[i].Id);
                _visuals[i].DotTarget = occupied ? 1f : 0f;
            }

            if (current >= 0)
            {
                Rectangle bounds = ButtonBounds(current, 0);
                _barXTarget = bounds.X;
                _barWidthTarget = bounds.Width;
                _barLevelTarget = 1f;
            }
            else
            {
                // No current desktop - mid-reconcile, or the shell went away. Fade the bar
                // out where it stands: sliding it to x=0 would point it at a button that is
                // not current either, which is worse than pointing at nothing.
                _barLevelTarget = 0f;
            }

            Animate();
        }

        public void SetBackground(Color color)
        {
            if (_background == color) return;
            _background = color;
            Invalidate();
        }

        /// <summary>
        /// Which desktops have windows, refreshed independently of the desktop set - a
        /// window opening or closing does not change which buttons exist. Null holds
        /// whatever is already on screen: the shell being unavailable mid-refresh is not
        /// the same thing as every desktop actually being empty.
        ///
        /// Goes through SyncVisuals like every other change of intent, so the dot fades
        /// rather than snaps, and Invalidate is still needed alongside it for the
        /// animationMs = 0 case - see the same pairing in OnMouseMove.
        /// </summary>
        public void SetOccupancy(ICollection<Guid> occupied)
        {
            if (occupied == null) return;
            _occupied = occupied;
            SyncVisuals();
            Invalidate();
        }

        /// <summary>
        /// The readings the stats tile's meters show, in percent; negative means not known
        /// yet and draws an empty track. Pushed by the controller each time it samples - the
        /// reconcile tick, or once a second while the panel is open - so the tile is never
        /// further behind than the panel would be.
        ///
        /// Retargets rather than sets, and the frame timer carries the bar there, so a jump
        /// from 10% to 60% reads as the bar rising rather than a cell flickering. Invalidate
        /// alongside Animate for the animationMs = 0 case, as in SetOccupancy.
        /// </summary>
        public void SetStatsLevels(float cpuPercent, float memoryPercent)
        {
            if (StatsIndex < 0) return;

            float cpu = Level(cpuPercent);
            float memory = Level(memoryPercent);
            if (cpu == _cpuLevelTarget && memory == _memLevelTarget) return;

            _cpuLevelTarget = cpu;
            _memLevelTarget = memory;
            Animate();
            Invalidate();
        }

        /// <summary>
        /// The meters' size in physical pixels - already DPI-scaled, the way every width the
        /// constructor takes is. A setter rather than more constructor arguments because it
        /// is appearance only and the strip draws sensibly without it; the controller sets
        /// it straight after construction, from statsBarWidth, statsBarGap, statsBarHeight,
        /// statsBarLabels, statsBarRounded, the two thresholds, which are percent, and
        /// statsTileStyle as <paramref name="style"/>, with statsIconSize as
        /// <paramref name="iconSize"/> for the styles that are not bars, and statsFanSpin and
        /// statsFanSpeed as <paramref name="spin"/> and <paramref name="spinPercent"/>, which
        /// only the fan reads.
        /// </summary>
        public void SetStatsMeter(int barWidth, int gap, int height, bool labels, bool rounded,
                                  int warnPercent, int alertPercent, StatsTileStyle style, int iconSize,
                                  bool spin, int spinPercent)
        {
            _fanSpin = spin;
            _fanSpeed = Math.Max(1, spinPercent) / 100f;
            _tileStyle = style;
            _iconSize = Math.Max(1, iconSize);
            _meterWidth = Math.Max(1, barWidth);
            _meterGap = Math.Max(0, gap);
            _meterHeight = Math.Max(1, height);
            _meterLabels = labels;
            _meterRounded = rounded;

            // An alert set below the warning would make amber unreachable; it is read as
            // "straight to red" instead, which is what the setting most plausibly meant.
            _meterWarn = Math.Max(1, Math.Min(100, warnPercent)) / 100f;
            _meterAlert = Math.Max(_meterWarn, Math.Min(100, alertPercent) / 100f);
            UpdateSpin();
            Invalidate();
        }

        // Degrees a second at idle and the extra at flat out, at statsFanSpeed = 100.
        // Scaled together by that setting, so a faster or slower fan keeps the same gap
        // between idle and busy. An eighth of a turn a second
        // idle reads as running without drawing the eye - a third was tried first, and at
        // idle it was the busiest thing on the taskbar. One turn a second at 100% is still
        // plainly faster, so load is visible, without blurring into a disc at 30 frames.
        const float FanIdleSpeed = 45f;
        const float FanLoadSpeed = 315f;
        const int SpinFrameMs = 33;

        /// <summary>
        /// Starts or stops the fan's spin to match the settings. Its own timer rather than
        /// the frame timer, because the frame timer's whole contract is that it stops when
        /// everything has arrived - and a spinning fan never arrives, so folding it in would
        /// keep every ease on the strip stepping at 60Hz forever. This one ticks at about
        /// 30, which a rotor this size cannot be told apart from, touches nothing but the
        /// angle, and repaints only the tile.
        ///
        /// Off with animationMs = 0, the same "nothing moves" that setting means to
        /// everything else on the strip.
        /// </summary>
        void UpdateSpin()
        {
            bool want = !_disposed && _fanSpin && _tileStyle == StatsTileStyle.Fan
                        && _animationMs > 0 && StatsIndex >= 0;

            if (!want)
            {
                if (_spinTimer != null) _spinTimer.Stop();
                return;
            }

            if (_spinTimer == null)
            {
                _spinTimer = new Timer();
                _spinTimer.Interval = SpinFrameMs;
                _spinTimer.Tick += OnSpin;
            }

            if (!_spinTimer.Enabled)
            {
                _fanLast = Environment.TickCount;
                _spinTimer.Start();
            }
        }

        void OnSpin(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            int elapsed = unchecked(now - _fanLast);   // across the TickCount wrap, as OnFrame
            if (elapsed <= 0) return;
            _fanLast = now;

            // Speed off the eased CPU level, so a spike spins it up smoothly instead of
            // jumping a gear. Wrapped at 120 because the rotor has three blades: the angle
            // never grows without bound, and 120 looks exactly like 0.
            float speed = _fanSpeed * (FanIdleSpeed + FanLoadSpeed * Math.Max(0f, Math.Min(1f, _cpuLevel)));
            _fanAngle = (_fanAngle + speed * elapsed / 1000f) % 120f;

            // Hidden with the taskbar - an Explorer restart, a fullscreen game on some
            // setups - means nothing to repaint. The angle still advances, so it picks up
            // where it would have been rather than where it stopped.
            if (Handle == IntPtr.Zero || !Native.IsWindowVisible(Handle)) return;

            Native.RECT client;
            int index = StatsIndex;
            if (index < 0 || !Native.GetWindowRect(Handle, out client)) return;

            Rectangle tile = ButtonBounds(index, client.Height);
            var dirty = new Native.RECT { Left = tile.Left, Top = tile.Top, Right = tile.Right, Bottom = tile.Bottom };
            Native.InvalidateRect(Handle, ref dirty, false);
        }

        static float Level(float percent)
        {
            if (percent <= 0f || float.IsNaN(percent)) return 0f;
            return Math.Min(1f, percent / 100f);
        }

        // --- animation --------------------------------------------------------

        /// <summary>
        /// Starts the frame timer, unless there is nothing to animate. Called from
        /// SyncVisuals, so every change of intent gets one of these and nothing else
        /// has to remember to.
        /// </summary>
        void Animate()
        {
            if (_disposed) return;

            // animationMs = 0 means off: apply the targets where they stand and never
            // create a timer at all. The panel is included, so the one setting means the
            // same thing to everything that moves.
            if (_animationMs <= 0) { Settle(); return; }

            if (Settled()) return;

            if (_frameTimer == null)
            {
                _frameTimer = new Timer();
                _frameTimer.Interval = Motion.FrameMs;
                _frameTimer.Tick += OnFrame;
            }

            if (!_frameTimer.Enabled)
            {
                // Only when starting from stopped. Re-basing on every retarget would throw
                // away the time since the last frame, so a moving pointer - which retargets
                // on every WM_MOUSEMOVE - would keep resetting the clock and stall the
                // animation it is supposed to be driving.
                _lastFrame = Environment.TickCount;
                _frameTimer.Start();
            }
        }

        void OnFrame(object sender, EventArgs e)
        {
            int now = Environment.TickCount;

            // Unchecked because TickCount wraps to int.MinValue after ~24.9 days of
            // uptime, and this subtraction is still the right elapsed time across the
            // wrap. This process runs from login to logout, so it will see one.
            int elapsed = unchecked(now - _lastFrame);
            if (elapsed <= 0) return;

            _lastFrame = now;
            float rate = Motion.Rate(elapsed, _animationMs);

            // Stepped separately and asked separately. The panel is a window of its own and
            // repaints itself as it moves, so a strip that repainted whenever *anything*
            // moved would redraw the taskbar sixty times a second for a slide happening
            // entirely above it.
            bool strip = Step(rate);
            bool panel = _tooltip != null && _tooltip.Step(rate);

            if (strip) Invalidate();
            if (strip || panel) return;

            // Everything is on its target. A permanently ticking 60Hz timer that repaints
            // nothing is not something a taskbar utility gets to do.
            _frameTimer.Stop();
        }

        /// <summary>Steps every animated value. True when any of them moved.</summary>
        bool Step(float rate)
        {
            bool moved = false;

            // |= and not ||=, deliberately: every value must be stepped, not just the ones
            // before the first that moved.
            for (int i = 0; i < _visuals.Length; i++)
            {
                moved |= Motion.Ease(ref _visuals[i].Highlight, _visuals[i].HighlightTarget, rate, Motion.ToneEpsilon);
                moved |= Motion.Ease(ref _visuals[i].Hover, _visuals[i].HoverTarget, rate, Motion.ToneEpsilon);
                moved |= Motion.Ease(ref _visuals[i].Dot, _visuals[i].DotTarget, rate, Motion.ToneEpsilon);
            }

            moved |= Motion.Ease(ref _barX, _barXTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _barWidth, _barWidthTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _barLevel, _barLevelTarget, rate, Motion.ToneEpsilon);

            // Levels, not tones, but on the same 0..1 scale - and a meter a few pixels tall
            // has no visible step finer than ToneEpsilon either.
            moved |= Motion.Ease(ref _cpuLevel, _cpuLevelTarget, rate, Motion.ToneEpsilon);
            moved |= Motion.Ease(ref _memLevel, _memLevelTarget, rate, Motion.ToneEpsilon);

            return moved;
        }

        bool Settled()
        {
            for (int i = 0; i < _visuals.Length; i++)
            {
                if (_visuals[i].Highlight != _visuals[i].HighlightTarget) return false;
                if (_visuals[i].Hover != _visuals[i].HoverTarget) return false;
                if (_visuals[i].Dot != _visuals[i].DotTarget) return false;
            }

            if (_tooltip != null && !_tooltip.Settled) return false;

            return _barX == _barXTarget && _barWidth == _barWidthTarget
                && _barLevel == _barLevelTarget
                && _cpuLevel == _cpuLevelTarget && _memLevel == _memLevelTarget;
        }

        /// <summary>
        /// Arrive now, without animating. What a desktop set change wants, and what
        /// animation being switched off means.
        /// </summary>
        void Settle()
        {
            Step(1f);
            if (_tooltip != null) _tooltip.Settle();
            if (_frameTimer != null) _frameTimer.Stop();
        }

        public void Invalidate()
        {
            if (Handle != IntPtr.Zero)
                Native.InvalidateRect(Handle, IntPtr.Zero, false);
        }

        // --- rendering --------------------------------------------------------

        void Paint()
        {
            Native.PAINTSTRUCT ps;
            IntPtr hdc = Native.BeginPaint(Handle, out ps);
            if (hdc == IntPtr.Zero) return;

            try
            {
                Native.RECT client;
                if (!Native.GetWindowRect(Handle, out client)) return;

                int w = client.Width, h = client.Height;
                if (w <= 0 || h <= 0) return;

                if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
                {
                    if (_buffer != null) _buffer.Dispose();
                    _buffer = new Bitmap(w, h);
                }

                using (var g = Graphics.FromImage(_buffer))
                {
                    Render(g, w, h);
                }

                using (var target = Graphics.FromHdc(hdc))
                {
                    target.DrawImageUnscaled(_buffer, 0, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Write("strip: paint failed - " + ex.Message);
            }
            finally
            {
                Native.EndPaint(Handle, ref ps);
            }
        }

        /// <summary>
        /// Pure: reads model and visual state, draws, mutates nothing. Everything that
        /// moves is a float the animator has already stepped, which is what keeps the
        /// animator to a timer and a handful of eases.
        /// </summary>
        void Render(Graphics g, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var back = new SolidBrush(_background))
                g.FillRectangle(back, 0, 0, width, height);

            EnsureFont(height);

            for (int i = 0; i < _desktops.Count; i++)
                DrawButton(g, ButtonBounds(i, height), _desktops[i].Number.ToString(), _visuals[i]);

            int plusIndex = _desktops.Count;
            if (plusIndex < _visuals.Length)
                DrawButton(g, ButtonBounds(plusIndex, height), "+", _visuals[plusIndex]);

            int statsIndex = StatsIndex;
            if (statsIndex >= 0 && statsIndex < _visuals.Length)
                DrawStatsButton(g, ButtonBounds(statsIndex, height), _visuals[statsIndex]);

            DrawBar(g, height);
        }

        /// <summary>
        /// The underline under the current desktop, drawn once for the whole strip rather
        /// than per button - it is one bar that travels, and a bar mid-travel belongs to
        /// no button. After the cells, so a neighbour's fill cannot paint over it as it
        /// crosses.
        /// </summary>
        void DrawBar(Graphics g, int height)
        {
            if (_barLevel <= 0.001f || _barWidth < 1f) return;

            int barHeight = Math.Max(2, (int)Math.Round(_barHeight * _barLevel));

            if (_look == Look.Modern)
            {
                // A short pill centred under the number, inside the rounded cell rather than
                // along the taskbar's edge - Windows 11's mark, where classic keeps Windows
                // 10's full-width line. Centred on the travelling bar's own centre, so it
                // slides between buttons exactly as the line does.
                float pw = Math.Min(_barWidth, Scale(16));
                float ph = Math.Max(2f, barHeight - 1);
                float cx = _barX + _barWidth / 2f;
                float bottom = height - CellInsetY() - Scale(2);

                using (var brush = new SolidBrush(_highlight))
                using (var path = RoundedRect(new RectangleF(cx - pw / 2f, bottom - ph, pw, ph), ph / 2f))
                    g.FillPath(brush, path);
                return;
            }

            var bar = new Rectangle((int)Math.Round(_barX), height - barHeight,
                                    (int)Math.Round(_barWidth), barHeight);

            using (var brush = new SolidBrush(_highlight))
                g.FillRectangle(brush, bar);
        }

        int Scale(int value)
        {
            return (int)Math.Round(value * _dpiScale);
        }

        /// <summary>
        /// How far a modern cell stands in from the taskbar's top and bottom edges. Inset so
        /// it reads as a soft tile on the bar rather than a slice of it.
        /// </summary>
        int CellInsetY()
        {
            return Scale(4);
        }

        void DrawButton(Graphics g, Rectangle bounds, string caption, ButtonVisual visual)
        {
            DrawCell(g, bounds, visual);

            using (var brush = new SolidBrush(Palette.RampText(_background, Emphasis(visual))))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;

                // Nudge up so the glyph looks centred in the space above the bar.
                var textBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - _barHeight);

                // Modern: the current desktop's number in semibold, so it reads in weight as
                // well as brightness, and '+' as the Add glyph from the icon font Windows 10's
                // own interface uses. The weight switches halfway through the highlight's
                // ease, where the brightness is already most of the way there.
                Font font = _font;
                string text = caption;
                if (_look == Look.Modern)
                {
                    if (caption == "+" && _glyphFont != null) { font = _glyphFont; text = "\uE710"; }
                    else if (visual.Highlight >= 0.5f && _currentFont != null) font = _currentFont;
                }

                g.DrawString(text, font, brush, textBounds, format);

                DrawDot(g, textBounds, caption, font, visual.Dot);
            }
        }

        /// <summary>The cell behind a button's glyph, lifted by hover and current.</summary>
        void DrawCell(Graphics g, Rectangle bounds, ButtonVisual visual)
        {
            // Hover and current both separate the cell from the bar; current adds the
            // underline. Which way that separation goes is Palette's problem - on a light
            // taskbar the cell darkens instead of lightening.
            float lift = Math.Max(visual.Hover * 0.55f, visual.Highlight * 0.85f);
            if (lift > 0.001f)
            {
                using (var fill = new SolidBrush(Palette.Lift(_background, lift * 0.10f)))
                {
                    if (_look == Look.Modern)
                    {
                        var inner = new RectangleF(bounds.X + Scale(2), bounds.Y + CellInsetY(),
                                                   bounds.Width - Scale(4), bounds.Height - CellInsetY() * 2);
                        using (var path = RoundedRect(inner, Scale(4)))
                            g.FillPath(fill, path);
                    }
                    else
                    {
                        g.FillRectangle(fill, bounds);
                    }
                }
            }

            // The underline is not drawn here. See DrawBar: it travels between buttons,
            // and a bar drawn inside this loop can only fade out of one cell and into the
            // next, which reads as two bars rather than one moving.
        }

        /// <summary>How far up the text ramp a glyph sits, 0 resting to 1 full.</summary>
        static float Emphasis(ButtonVisual visual)
        {
            // Current desktop reads at full strength; hover carries an inactive one part of
            // the way there. HoverShare is the ratio the two tone steps used to stand in
            // before Palette owned the ramp - 30 of the 55 between resting and full - kept
            // exactly so a dark taskbar renders the same tones it always did.
            const float HoverShare = 30f / 55f;
            return Math.Min(1f, visual.Highlight + visual.Hover * HoverShare);
        }

        /// <summary>
        /// The stats tile: two small upright meters, CPU on the left and memory on the
        /// right, each a dim rounded track with a fill rising from the bottom, and a small
        /// "C" and "M" under them.
        ///
        /// The second version of the meters. The first were plain rectangles, unlabelled
        /// and always the accent colour: they read as decoration as much as data, and a
        /// machine pinned at 100% looked no different at a distance from one at 60%. Now
        /// the ends can be rounded to match the panel's ring gauges - statsBarRounded, off by
        /// default, since square was preferred once both had been seen - the letters say which bar
        /// is which without a hover, and the fill turns amber and then red past the
        /// statsWarnPercent and statsAlertPercent thresholds - see Palette.Load - so a heavy
        /// load is visible from across the room, which is the one thing a glance is for.
        ///
        /// It used to be a mark - a dot between two pairs of arcs, "(( . ))" - which said
        /// nothing until clicked, and read as a wireless or broadcast icon besides, so the
        /// tile looked like a second network indicator next to the one in the tray. The
        /// meters are the answer to both: the tile is now a reading in its own right, and
        /// "is the machine busy" no longer costs a click. The panel is still where the
        /// figures are; the meters are only the glance.
        ///
        /// Two and not three: CPU and memory are percentages and fill a bar honestly.
        /// Network has no top - a bar for it would need a scale picked out of the air, and
        /// whatever was picked would sit pinned full on a fast link or flat on a slow one.
        ///
        /// In the accent colour rather than the text ramp - the one thing on the strip that
        /// is not a number gets the one colour that is not grey. The fill is muted at rest
        /// and full strength when hovered or pinned, the same "this one is live" cue the ramp
        /// gives the numbers; full accent at rest competed with the current-desktop
        /// underline, the only other thing on the strip in that colour. Alpha rather than a
        /// blend toward the background, so it keeps its hue on a light taskbar as on a dark
        /// one.
        ///
        /// Sized by config - statsBarWidth, statsBarGap, statsBarHeight - and centred in
        /// textBounds, the same nudged-up rectangle the numbers centre in, so the meters sit
        /// on the digits' line. The first version sized them off the font, digit-tall and
        /// three pixels wide, and they read as too slight beside the numbers; how heavy
        /// they should be is taste, so it is a setting. Track edges are snapped to whole
        /// pixels so the meters stay crisp; only the fill's top is fractional, which is what
        /// lets an eased level glide instead of stepping a pixel at a time.
        /// </summary>
        void DrawStatsButton(Graphics g, Rectangle bounds, ButtonVisual visual)
        {
            DrawCell(g, bounds, visual);

            var textBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - _barHeight);

            // Font-derived sizes when SetStatsMeter was never called, so a strip built
            // without one still draws a sensible pair.
            int height = _meterHeight > 0 ? _meterHeight : (int)Math.Round(Math.Max(8f, _font.Size * 0.85f));

            float strength = 0.65f + 0.35f * Math.Min(1f, visual.Hover);

            // Faint enough to read as the empty part of the bar, strong enough that an idle
            // machine - a CPU meter near zero - still shows two meters, not a blank cell.
            Color trackInk = Color.FromArgb(70, _highlight);

            if (_tileStyle == StatsTileStyle.Gauge)
            {
                DrawGauge(g, textBounds, _iconSize > 0 ? _iconSize : height, trackInk, strength);
                return;
            }

            int icon = _iconSize > 0 ? _iconSize : height;
            switch (_tileStyle)
            {
                case StatsTileStyle.Chip:    DrawChip(g, textBounds, icon, visual, strength); return;
                case StatsTileStyle.Rings:   DrawRings(g, textBounds, icon, trackInk, strength); return;
                case StatsTileStyle.Monitor: DrawMonitor(g, textBounds, icon, visual, strength); return;
                case StatsTileStyle.Fan:     DrawFan(g, textBounds, icon, visual, strength); return;
            }

            int width = _meterWidth > 0 ? _meterWidth : Math.Max(3, (int)Math.Round(3 * _dpiScale));
            int gap = _meterWidth > 0 ? _meterGap : Math.Max(2, (int)Math.Round(2 * _dpiScale));

            // The letters' line: the label font's size rather than its line height, since
            // a capital set with the typographic format has no leading to account for, and
            // two pixels between it and the bars. Zero without labels.
            int labelGap = Math.Max(1, (int)Math.Round(2 * _dpiScale));
            int labelHeight = _meterLabels ? (int)Math.Ceiling(_labelFont.Size) : 0;
            int under = _meterLabels ? labelGap + labelHeight : 0;

            // Fitted to the cell: a pixel of breathing room above and below, and at least
            // two either side, so no setting can draw into '+' or onto the underline. The
            // labels keep their room and the bars give it up. The gap shrinks first, but
            // never to nothing - a gapless pair reads as one bar - and the bars narrow after.
            height = Math.Min(height, Math.Max(1, textBounds.Height - 2 - under));
            int room = Math.Max(3, textBounds.Width - 4);
            if (width * 2 + gap > room) gap = Math.Min(gap, Math.Max(1, room / 5));
            if (width * 2 + gap > room) width = Math.Max(1, (room - gap) / 2);

            // Bars and letters centred as one group, so the bars sit a little above the
            // digits' line with labels on and exactly on it with them off.
            int left = textBounds.X + (textBounds.Width - (width * 2 + gap)) / 2;
            int top = textBounds.Y + (textBounds.Height - (height + under)) / 2;

            var cpu = new Rectangle(left, top, width, height);
            var memory = new Rectangle(left + width + gap, top, width, height);

            using (var track = new SolidBrush(trackInk))
            {
                DrawMeter(g, track, cpu, _cpuLevel, strength);
                DrawMeter(g, track, memory, _memLevel, strength);
            }

            if (!_meterLabels) return;

            // Muted at rest, brought up with the bars on hover - the same "this one is live"
            // cue, in the text ramp because they are text.
            Color ink = Palette.Blend(Palette.MutedText(_background),
                                      Palette.SecondaryText(_background), Math.Min(1f, visual.Hover));
            int labelTop = top + height + labelGap;
            DrawMeterLabel(g, "C", ink, cpu, labelTop, labelHeight);
            DrawMeterLabel(g, "M", ink, memory, labelTop, labelHeight);
        }

        /// <summary>
        /// One meter: a track - square, or with round ends under statsBarRounded - and the
        /// fill rising inside it in the load's colour. The fill is clipped to the track's own
        /// shape, so a rounded foot follows the track's and the top stays flat either way -
        /// the flat edge is the reading, and rounding it would make 50% look like 47%.
        /// </summary>
        void DrawMeter(Graphics g, Brush track, Rectangle meter, float level, float strength)
        {
            using (var shape = Capsule(meter))
            {
                g.FillPath(track, shape);

                if (level <= 0.001f) return;

                Color ink = Palette.Load(_highlight, level, _meterWarn, _meterAlert);
                using (var fill = new SolidBrush(Color.FromArgb((int)(255 * strength), ink)))
                {
                    Region previous = g.Clip;
                    g.SetClip(shape, CombineMode.Intersect);
                    try
                    {
                        // At least a pixel once there is any reading at all, so 1% is
                        // distinguishable from "not measured yet", which draws the track alone.
                        float filled = Math.Max(1f, meter.Height * level);
                        g.FillRectangle(fill, meter.X, meter.Bottom - filled, meter.Width, filled);
                    }
                    finally
                    {
                        g.Clip = previous;
                        previous.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// statsTileStyle = gauge: a half-ring track and a needle from a hub at its centre,
        /// pointing at the CPU level - left is idle, straight up is half, right is flat out.
        /// CPU alone because it is the reading that moves; memory sits still for minutes and
        /// a needle that never moves says nothing. The panel still has both.
        ///
        /// The needle rides _cpuLevel, which the frame timer already eases, so it sweeps to a
        /// new reading rather than jumping - the one thing that makes it read as a gauge
        /// rather than a clock hand. Only the needle and hub take the load colour; the track
        /// stays the faint highlight, the same split as the meters' fill and track.
        ///
        /// Sized from statsIconSize, then fitted to the cell as the meters are: a pixel of
        /// air top and bottom, two either side. The hub sits below the arc's centre line by
        /// its own radius, so that is counted in the height too, or the dot would land on
        /// the underline.
        /// </summary>
        void DrawGauge(Graphics g, Rectangle textBounds, int height, Color trackInk, float strength)
        {
            float stroke = Math.Max(2f, (float)Math.Round(2 * _dpiScale));
            float hub = stroke;   // radius of the dot the needle turns on

            float tall = Math.Min(height, Math.Max(4, textBounds.Height - 2));
            float room = Math.Max(4, textBounds.Width - 4);
            float radius = Math.Max(3f, Math.Min(tall - stroke / 2 - hub, (room - stroke) / 2));

            float cx = textBounds.X + textBounds.Width / 2f;
            float group = stroke / 2 + radius + hub;
            float cy = textBounds.Y + (textBounds.Height - group) / 2f + stroke / 2 + radius;

            float level = Math.Max(0f, Math.Min(1f, _cpuLevel));

            // Round caps on the track, the needle and nothing else - square ends on an arc
            // this small read as notches.
            using (var track = new Pen(trackInk, stroke))
            {
                track.StartCap = LineCap.Round;
                track.EndCap = LineCap.Round;
                g.DrawArc(track, cx - radius, cy - radius, radius * 2, radius * 2, 180, 180);
            }

            // No needle before the first reading, as the meters draw no fill: the hub alone,
            // in track ink, is "not measured yet". A reading of exactly zero looks the same,
            // which the meters share too - Level cannot tell them apart - and a busy-enough
            // machine to run this never sits at a true 0% for long.
            if (level <= 0.001f)
            {
                using (var dot = new SolidBrush(trackInk))
                    g.FillEllipse(dot, cx - hub, cy - hub, hub * 2, hub * 2);
                return;
            }

            Color ink = Color.FromArgb((int)(255 * strength),
                                       Palette.Load(_highlight, level, _meterWarn, _meterAlert));

            // GDI+ angles run clockwise from +x with y down, so 180 is left, 270 is up and 360
            // is right - exactly the arc DrawArc drew. The tip stops a stroke short of the
            // track's inner edge so the two never merge into one blob at a glance.
            double angle = Math.PI * (1.0 + level);
            float length = Math.Max(1f, radius - stroke);
            float tipX = cx + (float)Math.Cos(angle) * length;
            float tipY = cy + (float)Math.Sin(angle) * length;

            using (var needle = new Pen(ink, Math.Max(1f, stroke * 0.75f)))
            using (var dot = new SolidBrush(ink))
            {
                needle.StartCap = LineCap.Round;
                needle.EndCap = LineCap.Round;
                g.DrawLine(needle, cx, cy, tipX, tipY);
                g.FillEllipse(dot, cx - hub, cy - hub, hub * 2, hub * 2);
            }
        }

        /// <summary>
        /// statsTileStyle = chip: a processor chip - a rounded body, pins along all four
        /// sides, a die in the middle. A mark rather than a reading, for a tile that should
        /// read as "system" and nothing more until something is wrong.
        ///
        /// At rest the body and pins are in the text ramp, muted and brought up on hover the
        /// way the meters' letters are, and only the die carries the accent - an accent-ink
        /// outline beside white digits read as a second highlighted button. Under load the
        /// whole glyph goes amber and then red through Palette.Load, whose short blend at
        /// each threshold keeps it from flicking as an eased level sits on the line.
        ///
        /// Tinted by the heavier of CPU and memory, not CPU alone as the gauge is: a chip
        /// stands for the machine, and a machine out of memory is in as much trouble as one
        /// out of processor. The cost is that a box that lives above statsWarnPercent memory
        /// shows amber at rest - raise the threshold rather than lose the warning.
        ///
        /// Square, sized from statsIconSize and fitted to the cell like the other two styles.
        /// Two pins a side below about 12px of body, where three merge into a fringe.
        /// </summary>
        void DrawChip(Graphics g, Rectangle textBounds, int height, ButtonVisual visual, float strength)
        {
            float stroke = Math.Max(1f, (float)Math.Round(1.5 * _dpiScale));
            float pin = Math.Max(2f, (float)Math.Round(2.5 * _dpiScale));

            float side = Math.Min(height, Math.Min(textBounds.Height - 2, textBounds.Width - 4));
            side = Math.Max(8f, side);

            // The body's outline is a pen centred on the rectangle, so the rectangle is the
            // outer square less the pins and half a stroke each side.
            float x0 = textBounds.X + (textBounds.Width - side) / 2f;
            float y0 = textBounds.Y + (textBounds.Height - side) / 2f;
            var body = new RectangleF(x0 + pin + stroke / 2, y0 + pin + stroke / 2,
                                      side - 2 * pin - stroke, side - 2 * pin - stroke);

            Color bodyInk, dieInk;
            IconInks(visual, strength, out bodyInk, out dieInk);

            int pins = body.Width >= 12 ? 3 : 2;

            using (var pen = new Pen(bodyInk, stroke))
            using (var path = RoundedRect(body, Math.Max(1f, stroke * 1.5f)))
            {
                g.DrawPath(pen, path);

                // Flat caps so a pin ends where it is measured to, not half a stroke past -
                // past would put the top row on the cell's edge on a short taskbar.
                for (int i = 1; i <= pins; i++)
                {
                    float along = (float)i / (pins + 1);
                    float px = body.X + body.Width * along;
                    float py = body.Y + body.Height * along;
                    float outer = stroke / 2;

                    g.DrawLine(pen, px, body.Y - outer, px, body.Y - outer - pin);
                    g.DrawLine(pen, px, body.Bottom + outer, px, body.Bottom + outer + pin);
                    g.DrawLine(pen, body.X - outer, py, body.X - outer - pin, py);
                    g.DrawLine(pen, body.Right + outer, py, body.Right + outer + pin, py);
                }
            }

            // The die: a third of the body, centred, at least two pixels so it survives the
            // smallest taskbar.
            float die = Math.Max(2f, body.Width / 3f);
            using (var fill = new SolidBrush(dieInk))
            {
                g.FillRectangle(fill, body.X + (body.Width - die) / 2f,
                                body.Y + (body.Height - die) / 2f, die, die);
            }
        }

        /// <summary>
        /// The two inks every static icon - chip, monitor, fan - is drawn in: the body in the
        /// text ramp, muted at rest and brought up on hover, and the one accent detail at the
        /// hover strength, both pushed to amber and red by the heavier of CPU and memory. One
        /// place, so the three marks cannot drift apart in how they warn.
        /// </summary>
        void IconInks(ButtonVisual visual, float strength, out Color body, out Color accent)
        {
            float level = Math.Max(_cpuLevel, _memLevel);
            level = Math.Max(0f, Math.Min(1f, level));

            Color text = Palette.Blend(Palette.MutedText(_background),
                                       Palette.SecondaryText(_background), Math.Min(1f, visual.Hover));
            body = Palette.Load(text, level, _meterWarn, _meterAlert);
            accent = Color.FromArgb((int)(255 * strength),
                                    Palette.Load(_highlight, level, _meterWarn, _meterAlert));
        }

        /// <summary>
        /// statsTileStyle = rings: two concentric rings, CPU on the outside and memory inside,
        /// each filled clockwise from twelve o'clock - the panel's ring gauges in miniature,
        /// so the tile and the panel it opens speak the same shape. Both readings in a square,
        /// where the meters need the width of two bars and a gap.
        ///
        /// Tracks and fills split as the meters' do: the faint highlight for the empty ring,
        /// the load colour at hover strength for the filled part, and each ring takes its own
        /// colour, so amber on the inside says memory without a hover. The fills ride the
        /// eased levels, so they sweep round rather than jump.
        ///
        /// Sized from statsIconSize and fitted to the cell like the others. Two and a half
        /// pixels between the rings at 96 DPI: one was tried first, and on the taskbar the
        /// two tracks ran together into a single thick ring. Below the size where the inner ring
        /// would have no hole, it is dropped and the outer ring alone shows CPU - one honest
        /// reading over two that blur into a disc.
        /// </summary>
        void DrawRings(Graphics g, Rectangle textBounds, int height, Color trackInk, float strength)
        {
            float stroke = Math.Max(2f, (float)Math.Round(2 * _dpiScale));
            float gap = Math.Max(2f, (float)Math.Round(2.5 * _dpiScale));

            float side = Math.Min(height, Math.Min(textBounds.Height - 2, textBounds.Width - 4));
            side = Math.Max(8f, side);

            float cx = textBounds.X + textBounds.Width / 2f;
            float cy = textBounds.Y + textBounds.Height / 2f;

            // Radii to the pen's centre line, since a pen straddles the path it strokes.
            float outer = side / 2 - stroke / 2;
            float inner = outer - stroke - gap;

            DrawRing(g, cx, cy, outer, stroke, trackInk, _cpuLevel, strength);
            if (inner >= stroke) DrawRing(g, cx, cy, inner, stroke, trackInk, _memLevel, strength);
        }

        void DrawRing(Graphics g, float cx, float cy, float radius, float stroke,
                      Color trackInk, float level, float strength)
        {
            var box = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var track = new Pen(trackInk, stroke))
                g.DrawEllipse(track, box);

            // Nothing past the track before the first reading, as the meters draw no fill.
            level = Math.Max(0f, Math.Min(1f, level));
            if (level <= 0.001f) return;

            Color ink = Color.FromArgb((int)(255 * strength),
                                       Palette.Load(_highlight, level, _meterWarn, _meterAlert));

            // Flat caps: round ones would push a 50% fill visibly past six o'clock on a ring
            // this small, and the arc's end is the reading. At least a few degrees, so 1% is
            // distinguishable from "not measured yet".
            using (var fill = new Pen(ink, stroke))
                g.DrawArc(fill, box, -90f, Math.Max(6f, 360f * level));
        }

        /// <summary>
        /// statsTileStyle = monitor: a screen on a stand, the generic mark for "this PC" - the
        /// one most likely to read as "system" to someone who has never seen the tile before.
        /// Static and tinted as the chip is, through IconInks: outline and stand in the text
        /// ramp, and the accent as a faint glow on the screen with a chart line across it - the
        /// chip's die, but spread over the panel and held back in alpha, since a solid accent
        /// screen is the size of a highlighted button and read as one.
        ///
        /// Six by five overall, the screen taking the top seven tenths and the stand the
        /// rest. Four by three was tried first and read as a wide-screen bar beside the
        /// numbers. Height from statsIconSize; the width is what meets the cell's cap first on a
        /// narrow tile, and the height comes down with it so the proportions hold.
        /// </summary>
        void DrawMonitor(Graphics g, Rectangle textBounds, int height, ButtonVisual visual, float strength)
        {
            float stroke = Math.Max(1f, (float)Math.Round(1.5 * _dpiScale));

            float tall = Math.Min(height, textBounds.Height - 2);
            tall = Math.Min(tall, (textBounds.Width - 4) / 1.2f);
            tall = Math.Max(8f, tall);
            float wide = tall * 1.2f;

            float x0 = textBounds.X + (textBounds.Width - wide) / 2f;
            float y0 = textBounds.Y + (textBounds.Height - tall) / 2f;

            float screenTall = (float)Math.Round(tall * 0.7f);
            var screen = new RectangleF(x0 + stroke / 2, y0 + stroke / 2, wide - stroke, screenTall - stroke);

            Color bodyInk, glowInk;
            IconInks(visual, strength, out bodyInk, out glowInk);

            // The glow inside the outline, a stroke clear of it, so the frame stays a frame.
            var glass = RectangleF.Inflate(screen, -stroke, -stroke);
            if (glass.Width > 0 && glass.Height > 0)
                using (var fill = new SolidBrush(Color.FromArgb(glowInk.A * 2 / 5, glowInk)))
                    g.FillRectangle(fill, glass);

            // A faint chart line across the glass, so the screen says "monitoring" rather than
            // "a screen". Drawn, not data: the shape is fixed - the colour is what carries the
            // load, as for the rest of the glyph - and a live trace in a few pixels of height
            // would only ever read as noise. Skipped on a screen too short to hold a line with
            // air above and below it.
            var plot = RectangleF.Inflate(glass, -stroke, -stroke);
            if (plot.Width >= 6 && plot.Height >= 4)
            {
                float[] xs = { 0f, 0.22f, 0.42f, 0.6f, 0.8f, 1f };
                float[] ys = { 0.75f, 0.45f, 0.62f, 0.2f, 0.5f, 0.35f };
                var line = new PointF[xs.Length];
                for (int i = 0; i < xs.Length; i++)
                    line[i] = new PointF(plot.X + plot.Width * xs[i], plot.Y + plot.Height * ys[i]);

                using (var trace = new Pen(Color.FromArgb(glowInk.A * 3 / 4, glowInk), Math.Max(1f, stroke * 0.75f)))
                {
                    trace.LineJoin = LineJoin.Round;
                    trace.StartCap = LineCap.Round;
                    trace.EndCap = LineCap.Round;
                    g.DrawLines(trace, line);
                }
            }

            using (var pen = new Pen(bodyInk, stroke))
            using (var path = RoundedRect(screen, Math.Max(1f, stroke)))
            {
                g.DrawPath(pen, path);

                // Neck down the middle, foot across the bottom: flat caps, so the foot stops
                // where it is measured and the neck does not poke through the frame.
                float mid = x0 + wide / 2f;
                float foot = y0 + tall - stroke / 2;
                float half = Math.Max(stroke, wide * 0.22f);
                g.DrawLine(pen, mid, screen.Bottom + stroke / 2, mid, foot);
                g.DrawLine(pen, mid - half, foot, mid + half, foot);
            }
        }

        /// <summary>
        /// statsTileStyle = fan: a three-blade rotor in a ring - "hardware" with a hint of
        /// "heat", which is what a machine under load makes. Static and tinted as the chip is,
        /// through IconInks: ring and blades in the text ramp, the hub in the accent, the
        /// chip's die again.
        ///
        /// Still by default. statsFanSpin turns it, faster as CPU rises, on a timer of its
        /// own - see UpdateSpin. Off by default because the strip otherwise goes quiet the
        /// moment nothing moves, and a system monitor that costs CPU to sit idle is the wrong
        /// way round; the spin is cheap, but it is not free.
        ///
        /// Each blade is an ellipse set off the radial line, so the three read as swept rather
        /// than as a propeller drawn with a ruler. Square, sized and fitted like the chip.
        /// </summary>
        void DrawFan(Graphics g, Rectangle textBounds, int height, ButtonVisual visual, float strength)
        {
            float stroke = Math.Max(1f, (float)Math.Round(1.5 * _dpiScale));

            float side = Math.Min(height, Math.Min(textBounds.Height - 2, textBounds.Width - 4));
            side = Math.Max(8f, side);

            float cx = textBounds.X + textBounds.Width / 2f;
            float cy = textBounds.Y + textBounds.Height / 2f;
            float ring = side / 2 - stroke / 2;

            // Inside the ring with a stroke of air, or the blade tips touch it and the whole
            // thing reads as a pie cut in three.
            float reach = ring - stroke * 1.5f;
            float hub = Math.Max(1f, reach * 0.28f);
            float bladeLong = Math.Max(1f, reach - hub * 0.5f);
            float bladeWide = Math.Max(1f, reach * 0.55f);

            Color bodyInk, hubInk;
            IconInks(visual, strength, out bodyInk, out hubInk);

            using (var pen = new Pen(bodyInk, stroke))
                g.DrawEllipse(pen, cx - ring, cy - ring, ring * 2, ring * 2);

            GraphicsState saved = g.Save();
            try
            {
                g.TranslateTransform(cx, cy);
                g.RotateTransform(_fanAngle);
                using (var blade = new SolidBrush(bodyInk))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        // Pointing up, nudged right of the centre line by a fifth of its own
                        // width - the sweep - and starting half a hub out, so it grows from
                        // under the hub rather than from the centre point.
                        g.FillEllipse(blade, -bladeWide * 0.3f, -hub * 0.5f - bladeLong,
                                      bladeWide, bladeLong);
                        g.RotateTransform(120f);
                    }
                }
            }
            finally
            {
                g.Restore(saved);
            }

            // The hub last, over the blades' roots, so the three meet in one clean disc
            // instead of overlapping ellipse ends.
            using (var dot = new SolidBrush(hubInk))
                g.FillEllipse(dot, cx - hub, cy - hub, hub * 2, hub * 2);
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
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

        void DrawMeterLabel(Graphics g, string text, Color ink, Rectangle meter, int top, int height)
        {
            using (var brush = new SolidBrush(ink))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                // Centred on the bar, not confined to it: a bar a few pixels wide is narrower
                // than an "M", and a box that narrow would clip the letter.
                format.Alignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                float centre = meter.X + meter.Width / 2f;
                g.DrawString(text, _labelFont, brush,
                             new RectangleF(centre - height * 2, top, height * 4, height), format);
            }
        }

        /// <summary>
        /// A meter's outline: an upright rectangle, or with statsBarRounded a capsule whose
        /// top and bottom are semicircles as wide as it is. A path either way, so the fill
        /// is clipped to it the same way whichever shape is set.
        /// </summary>
        GraphicsPath Capsule(Rectangle r)
        {
            var path = new GraphicsPath();
            int d = Math.Min(r.Width, r.Height);
            if (!_meterRounded || d < 2)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180, 180);
            path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// A muted dot beside the number, faded in by visual.Dot: dim when the desktop has
        /// windows, invisible when it does not.
        ///
        /// Beside the glyph rather than under it. Under was tried first and cost two
        /// rounds of fighting the same problem: the button is barely 30px tall, so any
        /// vertical space taken for the dot comes straight out of the number's centring,
        /// and the dot ended up either visibly nudging the glyph up or sitting jammed
        /// against the underline. The button has width to spare that it does not have
        /// height for, so the dot goes there instead and the glyph's position is
        /// untouched - textBounds is exactly what it was before this feature existed.
        ///
        /// Measures the caption to place the dot off its actual right edge rather than a
        /// fixed offset, because Number can be two digits - up to 32 desktops - and a
        /// fixed offset tuned for one digit would sit on top of the second.
        ///
        /// A fixed tone rather than one that rides the hover/current ramp: it answers "is
        /// this desktop occupied", a fact about the desktop and not about the pointer, and
        /// riding the ramp would read as a second state mark competing with the underline.
        /// </summary>
        void DrawDot(Graphics g, Rectangle textBounds, string caption, Font font, float amount)
        {
            if (amount <= 0.001f) return;

            int diameter = Math.Max(2, _barHeight);
            int gap = Math.Max(2, _barHeight);

            float halfGlyph = g.MeasureString(caption, font).Width / 2f;
            int x = textBounds.X + textBounds.Width / 2 + (int)Math.Ceiling(halfGlyph) + gap;

            // Clamped to the button's own right edge so a two-digit number on a narrow
            // button cannot push the dot into the next button over.
            int rightLimit = textBounds.Right - diameter;
            if (x > rightLimit) x = rightLimit;

            int y = textBounds.Y + textBounds.Height / 2 - diameter / 2;

            Color muted = Palette.MutedText(_background);
            using (var brush = new SolidBrush(Color.FromArgb((int)(255 * amount), muted)))
                g.FillEllipse(brush, x, y, diameter, diameter);
        }

        void EnsureFont(int height)
        {
            // Sized in pixels from the taskbar height, so it tracks DPI without
            // depending on the device context's notion of points.
            float size = Math.Max(9f, height * 0.30f);
            if (_font != null && Math.Abs(_font.Size - size) < 0.5f) return;

            if (_font != null) _font.Dispose();
            _font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);

            // The meters' letters, made and replaced with the main font so the two always
            // agree on the DPI they were sized for. Small enough to sit under a 20px bar in a
            // 40px taskbar; semibold so a letter that small still has some weight to it.
            if (_labelFont != null) _labelFont.Dispose();
            _labelFont = new Font("Segoe UI Semibold", Math.Max(7f, size * 0.70f), FontStyle.Regular, GraphicsUnit.Pixel);

            if (_currentFont != null) { _currentFont.Dispose(); _currentFont = null; }
            if (_glyphFont != null) { _glyphFont.Dispose(); _glyphFont = null; }
            if (_look != Look.Modern) return;

            _currentFont = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);

            // GDI+ quietly substitutes a missing face, and E710 in any other font is a box.
            // Every Windows 10 has MDL2 Assets, but a font that is not there falls back to
            // the plain '+' rather than drawing that box.
            _glyphFont = new Font("Segoe MDL2 Assets", size * 0.93f, FontStyle.Regular, GraphicsUnit.Pixel);
            if (_glyphFont.Name != "Segoe MDL2 Assets") { _glyphFont.Dispose(); _glyphFont = null; }
        }

        // --- input ------------------------------------------------------------

        /// <summary>
        /// Asks for WM_MOUSELEAVE and one WM_MOUSEHOVER after the delay.
        ///
        /// Both are one-shot, and TME_HOVER only fires while the pointer stays inside a
        /// small system-defined rect. Moving from one button to the next can easily stay
        /// inside that rect, so this must be re-armed whenever the hovered button
        /// changes or the hover simply never fires again.
        /// </summary>
        void ArmTracking()
        {
            var tme = new Native.TRACKMOUSEEVENT();
            tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
            tme.dwFlags = Native.TME_LEAVE | Native.TME_HOVER;
            tme.hwndTrack = Handle;
            tme.dwHoverTime = _hoverDelay;
            Native.TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        void OnMouseMove(int x, int y)
        {
            if (!_trackingMouse) ArmTracking();

            int index = HitTest(x, y);
            if (index == _hoverIndex) return;

            _hoverIndex = index;
            _statsDismissed = false;   // off the tile, so the click that dismissed it is done
            SyncVisuals();
            Invalidate();

            // Restart the delay against the button now under the pointer.
            ArmTracking();

            if (index < 0) { RestOrHide(); return; }

            // An open panel follows the pointer in two speeds. The geometry goes now, so it
            // is already travelling before the pointer has settled; the text waits out
            // ReshowMs, so a sweep along the strip is one continuous glide carrying one
            // change of text rather than a flip-book of them.
            MoveTooltip();
            QueueReshow();
        }

        void OnMouseLeave()
        {
            _trackingMouse = false;

            // Cleared before RestOrHide, not after: a pinned panel's refresh decides whether
            // to keep going by where the pointer is, and it has to see the pointer as gone.
            bool wasHovering = _hoverIndex != -1;
            _hoverIndex = -1;
            _statsDismissed = false;

            RestOrHide();

            if (!wasHovering) return;

            // SyncVisuals keeps a pinned button lit, so opening a menu - which takes the
            // pointer off the strip and fires this - does not darken the button under it.
            SyncVisuals();
            Invalidate();
        }

        void OnClick(int x, int y, MouseButtons button, bool shift)
        {
            // Whatever the click does, the panel describing it is now stale.
            HideTooltip();
            ArmTracking();

            int index = HitTest(x, y);
            if (index < 0) { RestOrHide(); return; }

            // The menu is the touchpad's only way to the actions the other buttons carry,
            // so it answers on every button, '+' included. With nothing listening - the
            // menu turned off in config - the click falls through to what right click did
            // before the menu existed.
            if (button == MouseButtons.Right && !shift && OpenMenu(index)) return;

            // Before the '+' check, which would otherwise claim it: the tile is also an
            // index at or past _desktops.Count.
            if (index == StatsIndex)
            {
                // Ahead of the pin toggle, which a modified left click would otherwise also
                // be. The panel stays as it was: a copy is not a request to open or close it.
                if (StatsCopyRequested != null && IsGesture(StatsCopyGesture, button))
                {
                    Raise(StatsCopyRequested);
                    RestOrHide();
                    return;
                }

                // The panel is left as it was, like a copy: Task Manager opens over it, and
                // a kept panel is still there when it closes.
                if (StatsTaskManagerRequested != null && IsGesture(StatsTaskManagerGesture, button))
                {
                    Raise(StatsTaskManagerRequested);
                    RestOrHide();
                    return;
                }

                // Checked last, so a gesture set the same as copy or Task Manager does that
                // rather than both. Unkeeping closes, as the menu item's unticking does - and, as with
                // a left click putting the panel away, the hover is held off until the
                // pointer leaves, or it would bring the panel straight back.
                if (IsGesture(StatsKeepGesture, button))
                {
                    if (_statsKept)
                    {
                        SetStatsPinned(false);
                        _statsDismissed = true;
                    }
                    else SetStatsPinned(true, true);
                    return;
                }

                // Only left click means anything here. Pinning shows the panel at once,
                // without the hover delay - the click is the request.
                if (button == MouseButtons.Left)
                {
                    bool pin = !_statsPinned;
                    SetStatsPinned(pin);

                    // Unpinning is a request to put the panel away, but the pointer is still
                    // on the tile and tracking was re-armed above, so the hover would put it
                    // straight back 400ms later and the click would look like it did nothing.
                    // Held off until the pointer leaves the tile.
                    if (!pin) _statsDismissed = true;
                }
                else RestOrHide();
                return;
            }

            if (index >= _desktops.Count)
            {
                if (button == MouseButtons.Left) Raise(CreateRequested);
            }
            else
            {
                Guid id = _desktops[index].Id;
                switch (button)
                {
                    case MouseButtons.Left:   Raise(SwitchRequested, id); break;
                    case MouseButtons.Right:  Raise(MoveWindowRequested, id); break;   // shift held
                    case MouseButtons.Middle: Raise(RemoveRequested, id); break;
                }
            }

            // The click hid whatever was open. Pinned, the panel goes back to the tile -
            // a click that changes nothing, like the current desktop's own button, would
            // otherwise leave it dark until the pointer next left the strip.
            RestOrHide();
        }

        // --- context menu -----------------------------------------------------

        /// <summary>False when there is no menu to open, leaving the click to its old meaning.</summary>
        bool OpenMenu(int index)
        {
            if (ContextMenuRequested == null) return false;

            Rectangle anchor = AnchorFor(index);
            if (anchor.Width == 0) return false;

            // Pinned before the handler runs, because the handler shows the menu and the
            // button must already be lit underneath it.
            _menuIndex = index;
            SyncVisuals();
            Invalidate();

            try
            {
                ContextMenuRequested(index, anchor);
            }
            catch (Exception ex)
            {
                // Nothing is going to close a menu that was never shown, so let the button go.
                Log.Write("strip: menu handler threw - " + ex.Message);
                MenuClosed();
            }

            return true;
        }

        /// <summary>
        /// The menu raised by ContextMenuRequested has gone. Idempotent, and required on
        /// every path out of the handler - including one that showed nothing.
        /// </summary>
        public void MenuClosed()
        {
            if (_menuIndex == -1) return;

            _menuIndex = -1;

            // Tracking lapsed while the menu held the pointer, and no mouse message is owed
            // to us just because the menu closed. Re-read where the pointer actually is, or
            // the strip sits dead - unlit, no panel - until it leaves and comes back.
            _hoverIndex = HitTestScreen(Cursor.Position);
            _trackingMouse = false;
            if (_hoverIndex >= 0) ArmTracking();

            SyncVisuals();
            Invalidate();

            // The panel declined to show while the menu was up. A pinned one comes back now
            // rather than waiting for the pointer to leave.
            if (_statsPinned) ShowStats();
        }

        int HitTestScreen(Point screen)
        {
            var point = new Native.POINT();
            point.X = screen.X;
            point.Y = screen.Y;
            if (!Native.ScreenToClient(Handle, ref point)) return -1;

            Native.RECT client;
            if (!Native.GetWindowRect(Handle, out client)) return -1;
            if (point.Y < 0 || point.Y >= client.Height) return -1;

            return HitTest(point.X, point.Y);
        }

        // --- tooltip ----------------------------------------------------------

        /// <summary>
        /// Schedules the open panel onto the button now under the pointer, ReshowMs from
        /// now, restarting the wait if the pointer crosses another button first - so a
        /// sweep along the strip resolves to a single panel. See ReshowMs.
        ///
        /// Does nothing when no panel is open: the first one is WM_MOUSEHOVER's to show,
        /// after the full delay, and this must not become a second route to opening one.
        ///
        /// Only OnMouseMove calls this, and only when the hovered button actually changed,
        /// so the wait is measured from entering a button rather than from the last mouse
        /// message. A pointer drifting about inside one button cannot defer the panel
        /// indefinitely.
        /// </summary>
        void QueueReshow()
        {
            if (_disposed || _tooltip == null || !_tooltip.IsVisible) return;

            if (_reshowTimer == null)
            {
                _reshowTimer = new Timer();
                _reshowTimer.Interval = ReshowMs;
                _reshowTimer.Tick += OnReshow;
            }

            // Stop before Start, always: assigning Enabled = true while it is already true
            // is a no-op and would leave the original countdown running, which is the exact
            // opposite of the restart this needs.
            _reshowTimer.Stop();
            _reshowTimer.Start();
        }

        void OnReshow(object sender, EventArgs e)
        {
            // One-shot. ShowTooltip stops it too, but not on the path where it bails out.
            _reshowTimer.Stop();
            ShowTooltip();
        }

        void CancelReshow()
        {
            if (_reshowTimer != null) _reshowTimer.Stop();
        }

        void ShowTooltip()
        {
            // Whatever was queued, this is it happening - by hover delay, by re-show timer
            // or by a caller that got there first.
            CancelReshow();

            if (_disposed || _hoverIndex < 0) return;

            // Hovering the tile gets an ordinary tooltip saying what it is, from
            // TooltipProvider like any other button; the figures are only ever shown by a
            // click. A live readout appearing on hover turned out to be more than anyone
            // passing the pointer along the strip asked for. Pinned, the panel is already the
            // stats and stays the stats - swapping it for the hint would read as the click
            // having been undone. And just after a click put it away, nothing at all: the
            // hint popping up in its place would look like the panel refusing to close.
            if (_hoverIndex == StatsIndex)
            {
                if (_statsPinned) { ShowStats(); return; }
                if (_statsDismissed) return;
            }

            if (TooltipProvider == null) return;

            // The menu already says what this button can do, and the panel would only be in
            // front of it or behind it.
            if (_menuIndex >= 0) return;

            TooltipContent content;
            try
            {
                content = TooltipProvider(_hoverIndex);
            }
            catch (Exception ex)
            {
                Log.Write("strip: tooltip provider threw - " + ex.Message);
                return;
            }

            if (content == null) { HideTooltip(); return; }

            EnsureTooltip();

            // The button in screen coordinates: the panel anchors to it, and the accent
            // stub lines up under it.
            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);

            // It says something about a button now, so the stats refresh has nothing left
            // to refresh and must not drag the panel back to the tile a second from now.
            _showingStats = false;
            StopStatsRefresh();

            // Show only retargets - a panel already open now has somewhere new to be, and
            // this is what puts a frame timer behind it. SyncVisuals does the same job for
            // every other kind of change; the panel is the one that does not go through it.
            Animate();
        }

        /// <summary>
        /// Moves an open panel onto the button now under the pointer, without disturbing
        /// what it says. The immediate half of the pair; QueueReshow is the other.
        /// </summary>
        void MoveTooltip()
        {
            if (_disposed || _tooltip == null || !_tooltip.IsVisible) return;

            // Same reason ShowTooltip declines: the menu already answers for this button, and
            // a panel sliding out from under it is noise.
            if (_menuIndex >= 0) return;

            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.MoveTo(anchor);
            Animate();
        }

        /// <summary>
        /// The single hide path, which makes it the single place a queued re-show has to be
        /// dropped. Every caller that invalidates the panel - leaving the strip, clicking,
        /// a desktop set change - already comes through here, so none of them has to know
        /// the timer exists.
        /// </summary>
        void HideTooltip()
        {
            CancelReshow();
            StopStatsRefresh();
            _showingStats = false;
            _statsSuppressed = false;   // with the timer stopped, nothing would bring it back
            if (_tooltip != null) _tooltip.Hide();
        }

        void EnsureTooltip()
        {
            if (_tooltip == null)
                _tooltip = new TooltipWindow(_background, _highlight, _tooltipWidth, _dpiScale, _panelLook);
        }

        // --- stats panel ------------------------------------------------------

        /// <summary>The open panel is showing the stats, as opposed to a button or nothing.</summary>
        bool ShowingStats
        {
            get { return _showingStats && _tooltip != null && _tooltip.IsVisible; }
        }

        /// <summary>
        /// Whether the stats panel is on screen and taking its own readings once a second.
        /// The controller skips its reconcile-tick reading while this is true: two callers
        /// sampling on unrelated clocks, with MinIntervalMs settling the collisions, made
        /// the window each rate was measured over wander between 0.6s and 1.4s, and the CPU
        /// figure jumped about with it. One sampler keeps the window a steady second.
        /// </summary>
        public bool StatsShowing { get { return ShowingStats; } }

        /// <summary>
        /// Where the panel goes when nothing is asking for it: back to the tile when pinned,
        /// away otherwise. Every place that used to hide the panel unconditionally - leaving
        /// the strip, a click, a desktop set change - comes through here instead, so none of
        /// them has to know a pin exists.
        /// </summary>
        void RestOrHide()
        {
            if (_statsPinned && StatsIndex >= 0) ShowStats();
            else HideTooltip();
        }

        /// <summary>
        /// Pins the stats panel as a popup, or unpins it. Pinning shows it now, unpinning puts
        /// it away now. A no-op when there is no tile.
        /// </summary>
        public void SetStatsPinned(bool pinned)
        {
            SetStatsPinned(pinned, false);
        }

        /// <summary>
        /// Pins the stats panel, kept or as a popup, or unpins it. A kept panel has no click
        /// watch behind it at all, rather than one that ignores what it sees: the hook is a
        /// system-wide cost, and a panel that will not act on clicks has no reason to pay it.
        /// Switching a popup to kept, or back, leaves the panel exactly where it is.
        /// </summary>
        public void SetStatsPinned(bool pinned, bool kept)
        {
            if (_disposed || StatsIndex < 0) pinned = false;
            if (!pinned) kept = false;
            if (pinned == _statsPinned && kept == _statsKept) return;

            bool wasPinned = _statsPinned;
            _statsPinned = pinned;
            _statsKept = kept;
            _statsDismissed = false;
            SyncVisuals();
            Invalidate();

            WatchClicks(pinned && !kept);

            // Re-shown on a change of strength too, not only on opening, so the title picks
            // up or drops "(kept open)" now rather than at the next refresh.
            if (pinned) ShowStats();
            else if (wasPinned && (ShowingStats || _statsSuppressed)) HideTooltip();

            if (StatsPinChanged != null)
            {
                try { StatsPinChanged(pinned, kept); }
                catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
            }
        }

        /// <summary>Whether the panel is kept open, as opposed to open as a popup or closed.</summary>
        public bool StatsKept { get { return _statsKept; } }

        /// <summary>
        /// Puts the stats on the panel, anchored to the tile, and keeps them current. Safe
        /// to call on a panel already showing them - that is exactly what each refresh is.
        /// </summary>
        void ShowStats()
        {
            CancelReshow();

            int index = StatsIndex;
            if (_disposed || index < 0 || StatsProvider == null) return;

            // Same as ShowTooltip: the menu is up and answering for its button. MenuClosed
            // brings a pinned panel back.
            if (_menuIndex >= 0) return;

            // Checked here rather than only on the guard tick, because this is also how a
            // desktop change, a closed menu and a rebuild bring the panel back - and a
            // Win+Ctrl+Arrow from inside a fullscreen game must not put it over the game.
            if (StatsCovered()) { SuppressStats(); return; }

            TooltipContent content;
            try
            {
                content = StatsProvider(_statsKept);
            }
            catch (Exception ex)
            {
                Log.Write("strip: stats provider threw - " + ex.Message);
                return;
            }

            if (content == null) { HideTooltip(); return; }

            EnsureTooltip();

            Rectangle anchor = AnchorFor(index);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);
            _showingStats = true;
            _statsSuppressed = false;
            _statsShownAt = Environment.TickCount;
            StartStatsRefresh();

            // A panel arriving from another button slides here, and the frame timer is
            // what carries it - see the same call at the end of ShowTooltip.
            Animate();
        }

        void StartStatsRefresh()
        {
            if (_disposed) return;

            if (_statsTimer == null)
            {
                _statsTimer = new Timer();
                _statsTimer.Interval = StatsGuardMs;
                _statsTimer.Tick += OnStatsRefresh;
            }

            // Not restarted if already running: a refresh is itself a call to ShowStats, and
            // restarting from inside the tick it is handling would be harmless, but a hover
            // re-show landing mid-interval would push the next refresh back each time.
            if (!_statsTimer.Enabled) _statsTimer.Start();
        }

        void StopStatsRefresh()
        {
            if (_statsTimer != null) _statsTimer.Stop();
        }

        /// <summary>
        /// Every StatsGuardMs: is the taskbar still there, and is a second up since the
        /// figures were last read. The figures are re-read only on the second, so the
        /// faster tick costs the check and nothing else.
        /// </summary>
        void OnStatsRefresh(object sender, EventArgs e)
        {
            // Pinned, with the pointer on the tile or off the strip. With the pointer on
            // another button the panel is on its way there, and a refresh would yank it back.
            bool wanted = _statsPinned && (_hoverIndex == StatsIndex || _hoverIndex < 0);

            if (_statsSuppressed)
            {
                if (!_statsPinned) { HideTooltip(); return; }
                if (StatsCovered()) return;   // still away; keep watching for the taskbar

                // The taskbar is back. If the pointer is already on another button, that
                // button's hover owns the panel now, and leaving the strip will bring the
                // stats back through RestOrHide like any other time.
                _statsSuppressed = false;
                if (wanted) ShowStats();
                else StopStatsRefresh();
                return;
            }

            if (!ShowingStats || !wanted)
            {
                StopStatsRefresh();
                return;
            }

            if (StatsCovered()) { SuppressStats(); return; }

            // Half a guard tick of slack: a WinForms timer lands a few ms either side of its
            // interval, and a strict 1000 would miss on the fourth tick at 998 and wait for
            // the fifth, turning a one-second refresh into one of 1.0 or 1.25 at random.
            int age = unchecked(Environment.TickCount - _statsShownAt);
            if (age < 0 || age >= StatsRefreshMs - StatsGuardMs / 2) ShowStats();
        }

        /// <summary>
        /// The taskbar the panel rests on is not on screen, so the panel should not be
        /// either. Three ways that happens:
        ///
        /// The strip is hidden - it is the taskbar's child, so it is hidden whenever the
        /// taskbar is. An auto-hide taskbar is different again: it is not hidden but slid
        /// off the monitor edge with a two-pixel sliver left showing, so the strip is
        /// "visible" and simply not on the monitor. Hence the containment test rather than
        /// IsWindowVisible alone.
        ///
        /// And a fullscreen app, which covers the taskbar without moving it. That one the
        /// shell has to tell us; see Native.SHQueryUserNotificationState.
        /// </summary>
        bool StatsCovered()
        {
            if (!Native.IsWindowVisible(Handle)) return true;

            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return true;

            var info = new Native.MONITORINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
            IntPtr monitor = Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info))
            {
                Native.RECT m = info.rcMonitor;
                if (strip.Left < m.Left || strip.Top < m.Top
                    || strip.Right > m.Right || strip.Bottom > m.Bottom)
                    return true;
            }

            int state;
            try
            {
                if (Native.SHQueryUserNotificationState(out state) != 0) return false;
            }
            catch (Exception)
            {
                // Missing export or a shell that will not answer: assume nothing is in the
                // way. Wrong only over a fullscreen app, which is how it behaved before.
                return false;
            }

            return state == Native.QUNS_BUSY
                || state == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                || state == Native.QUNS_PRESENTATION_MODE
                || state == Native.QUNS_NOT_PRESENT;
        }

        /// <summary>
        /// Puts the panel away without unpinning it, and keeps the guard tick running so it
        /// comes back when the taskbar does. Not HideTooltip, which stops that tick.
        /// </summary>
        void SuppressStats()
        {
            CancelReshow();
            _showingStats = false;
            _statsSuppressed = true;
            if (_tooltip != null && _tooltip.IsVisible) _tooltip.Hide();
            StartStatsRefresh();
        }

        /// <summary>
        /// Installs or removes the system-wide click watch that closes a pinned panel on a
        /// click elsewhere. Only ever present while pinned.
        /// </summary>
        void WatchClicks(bool watch)
        {
            if (watch && _clicks == null && !_disposed)
            {
                try
                {
                    _clicks = new ClickWatcher(Handle, WM_CLICK_ANYWHERE);
                }
                catch (Exception ex)
                {
                    // Survivable: the panel still closes from its own tile, as it always did.
                    Log.Write("strip: no click watch - " + ex.Message);
                }
            }
            else if (!watch && _clicks != null)
            {
                _clicks.Dispose();
                _clicks = null;
            }
        }

        /// <summary>
        /// A mouse press somewhere on the system, from the ClickWatcher. Anywhere but the
        /// tile unpins the panel - the tile's own click is OnClick's, which toggles it, and
        /// answering it here as well would close the panel on the press and reopen it on
        /// the release.
        ///
        /// Ignored while a context menu is up. The press is then usually on the menu, and
        /// one of its items is "Keep open"; unpinning on the press would close the popup the
        /// item was about to keep. A press outside the menu closes the
        /// menu, and MenuClosed brings the panel back - a second click closes it.
        ///
        /// A press on another of the strip's buttons counts as outside. That is the popup
        /// convention, and it is what a panel opened by a click is: open until you click
        /// elsewhere. A switch by keyboard still leaves it open - see SetDesktops. A kept
        /// panel never gets here, since it has no watch installed; the check is for a press
        /// already posted when "Keep open" was chosen.
        /// </summary>
        void OnClickAnywhere(Point screen)
        {
            if (!_statsPinned || _statsKept || _menuIndex >= 0) return;
            if (HitTestScreen(screen) == StatsIndex) return;

            SetStatsPinned(false);
        }

        void Raise(Action<Guid> handler, Guid id)
        {
            if (handler == null) return;
            try { handler(id); }
            catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
        }

        void Raise(EventHandler handler)
        {
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
        }

        // --- window procedure --------------------------------------------------

        /// <summary>Modifier state as the mouse message itself reported it.</summary>
        /// <summary>
        /// Whether this click is the given gesture. The modifiers come from ModifierKeys -
        /// the key state as of the message being handled - rather than the message's own
        /// MK_ flags, because those carry Ctrl and Shift but not Alt, and one source for all
        /// three keeps "Ctrl+Shift+click" from counting as both or neither by accident.
        /// Exactly the named key: Ctrl+Shift+click is not Ctrl+click.
        /// </summary>
        static bool IsGesture(TileGesture gesture, MouseButtons button)
        {
            Keys mods = Control.ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt);
            switch (gesture)
            {
                case TileGesture.CtrlClick:   return button == MouseButtons.Left && mods == Keys.Control;
                case TileGesture.ShiftClick:  return button == MouseButtons.Left && mods == Keys.Shift;
                case TileGesture.AltClick:    return button == MouseButtons.Left && mods == Keys.Alt;
                case TileGesture.MiddleClick: return button == MouseButtons.Middle;
                case TileGesture.ShiftRightClick: return button == MouseButtons.Right && mods == Keys.Shift;
                default:                      return false;
            }
        }

        static bool Shift(IntPtr wParam)
        {
            return ((int)(long)wParam & Native.MK_SHIFT) != 0;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;

                case Native.WM_ERASEBKGND:
                    // Everything is painted in WM_PAINT from a back buffer; letting the
                    // system erase first would just flicker.
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_SETCURSOR:
                    // A plain NativeWindow gets no default cursor handling (that's a
                    // Control-only behaviour), so without this the strip just keeps
                    // showing whatever cursor was active before the pointer arrived -
                    // a taskbar resize cursor, a stray wait cursor, etc.
                    Cursor.Current = Cursors.Default;
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_MOUSEMOVE:
                    OnMouseMove(Native.LoWord(m.LParam), Native.HiWord(m.LParam));
                    return;

                case Native.WM_MOUSEHOVER:
                    ShowTooltip();
                    return;

                case Native.WM_MOUSELEAVE:
                    OnMouseLeave();
                    return;

                case Native.WM_LBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Left, Shift(m.WParam));
                    return;

                case Native.WM_RBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Right, Shift(m.WParam));
                    return;

                case Native.WM_MBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Middle, Shift(m.WParam));
                    return;

                case WM_CLICK_ANYWHERE:
                    OnClickAnywhere(new Point(Native.LoWord(m.LParam), Native.HiWord(m.LParam)));
                    return;
            }

            base.WndProc(ref m);
        }

        // --- lifetime ---------------------------------------------------------

        public void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Before the handle goes, or a frame already queued lands on a destroyed
            // window and invalidates a rectangle that is no longer anybody's.
            if (_frameTimer != null) { _frameTimer.Stop(); _frameTimer.Dispose(); _frameTimer = null; }
            if (_spinTimer != null) { _spinTimer.Stop(); _spinTimer.Dispose(); _spinTimer = null; }

            // Same reason, and one worse: a re-show landing after the tooltip is gone would
            // call ShowTooltip on a disposed panel.
            if (_reshowTimer != null) { _reshowTimer.Stop(); _reshowTimer.Dispose(); _reshowTimer = null; }
            if (_statsTimer != null) { _statsTimer.Stop(); _statsTimer.Dispose(); _statsTimer = null; }

            // A system-wide hook must not outlive the window it posts to. The controller
            // hands the pin to the next strip, which installs its own.
            if (_clicks != null) { _clicks.Dispose(); _clicks = null; }

            // Before the strip goes, so an Explorer restart cannot strand the panel on
            // screen: the tooltip is top-level and would otherwise outlive its parent.
            if (_tooltip != null) { _tooltip.Dispose(); _tooltip = null; }

            Destroy();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            if (_font != null) { _font.Dispose(); _font = null; }
            if (_currentFont != null) { _currentFont.Dispose(); _currentFont = null; }
            if (_glyphFont != null) { _glyphFont.Dispose(); _glyphFont = null; }
            if (_labelFont != null) { _labelFont.Dispose(); _labelFont = null; }
        }
    }
}
