using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    // The Settings window's building blocks. Every one is owner-drawn from a shared
    // SettingsTheme, for the same reason ConfirmDialog and MenuTheme exist: a stock
    // CheckBox, TrackBar or ComboBox paints itself in the system's light chrome whatever
    // BackColor says, and would be the one thing on screen that looks borrowed. There is no
    // designer and no third-party control library to lean on, so each of these is as small
    // as it can be and still take the keyboard, show focus, and grey out when disabled.

    /// <summary>
    /// The colours and fonts the Settings window is drawn with, shared by reference with
    /// every control in it.
    ///
    /// Shared and mutable on purpose. When the taskbar colour changes under an open window
    /// - a theme switch, a custom background just applied - SetColors updates this one
    /// object and the window repaints; nothing is rebuilt, so a slider mid-drag keeps its
    /// capture. Only a DPI change, which changes every size and font, builds a new theme
    /// and new controls with it.
    ///
    /// Tones come from Palette off the taskbar colour, exactly as ConfirmDialog takes them:
    /// the surface is Lift 0.06 and the text is measured against the taskbar colour, not
    /// the surface, so the dialog, the menu and this window are one family.
    /// </summary>
    sealed class SettingsTheme : IDisposable
    {
        public readonly double Scale;
        public readonly Font TitleFont;   // a row's name
        public readonly Font BodyFont;    // values, buttons, tabs
        public readonly Font SmallFont;   // a row's help text

        public Color Background;   // the taskbar colour everything is derived from
        public Color Surface;      // the window's body
        public Color Field;        // tracks, boxes, unselected segments
        public Color FieldHot;     // the same under the pointer
        public Color Border;
        public Color Divider;      // between rows: quieter than Border
        public Color Primary, Body, Secondary, Muted;
        public Color Accent;       // highlightColor
        public Color OnAccent;     // text and knobs drawn on the accent

        public SettingsTheme(double scale, Color background, Color accent)
        {
            Scale = scale;
            TitleFont = new Font("Segoe UI Semibold", Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            BodyFont = new Font("Segoe UI", Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            SmallFont = new Font("Segoe UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            SetColors(background, accent);
        }

        public void SetColors(Color background, Color accent)
        {
            Background = background;
            Surface = Palette.Lift(background, 0.06f);
            Field = Palette.Lift(background, 0.13f);
            FieldHot = Palette.Lift(background, 0.19f);
            Border = Palette.Lift(background, 0.24f);
            Divider = Palette.Lift(background, 0.11f);
            Primary = Palette.PrimaryText(background);
            Body = Palette.BodyText(background);
            Secondary = Palette.SecondaryText(background);
            Muted = Palette.MutedText(background);
            Accent = accent;
            OnAccent = Palette.IsLight(accent) ? Color.FromArgb(20, 20, 20) : Color.White;
        }

        public bool Dark { get { return !Palette.IsLight(Background); } }

        public int S(int value) { return (int)Math.Round(value * Scale); }

        float Px(int value) { return (float)(value * Scale); }

        /// <summary>A rectangle with fully rounded ends - the toggle's track, the slider's.</summary>
        public static GraphicsPath Pill(RectangleF r)
        {
            return Rounded(r, Math.Min(r.Width, r.Height) / 2f);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            TitleFont.Dispose();
            BodyFont.Dispose();
            SmallFont.Dispose();
        }
    }

    /// <summary>
    /// What every control below shares: double-buffered owner draw on the theme's surface,
    /// a hover flag, and a repaint on every state change that alters the look.
    /// </summary>
    abstract class ThemedControl : Control
    {
        protected readonly SettingsTheme Theme;
        bool _hot;

        protected ThemedControl(SettingsTheme theme)
        {
            Theme = theme;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected bool Hot { get { return _hot; } }

        /// <summary>Focused, and the keyboard has been used - the system's own rule for drawing focus.</summary>
        protected bool ShowFocus { get { return Focused && ShowFocusCues; } }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Surface);
        }

        protected void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color,
                                TextFormatFlags align)
        {
            TextRenderer.DrawText(g, text, font, bounds, color,
                                  align | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                                  TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>An on/off switch. Click, Space or Enter flips it.</summary>
    sealed class Toggle : ThemedControl
    {
        bool _checked;

        public event EventHandler CheckedChanged;

        public Toggle(SettingsTheme theme) : base(theme)
        {
            Size = new Size(theme.S(44), theme.S(22));
        }

        /// <summary>Setting this from code does not raise CheckedChanged; only the user flipping it does.</summary>
        public bool Checked
        {
            get { return _checked; }
            set { if (_checked == value) return; _checked = value; Invalidate(); }
        }

        void Flip()
        {
            _checked = !_checked;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Flip();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Flip(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var track = new RectangleF(1, 1, Width - 3, Height - 3);
            float inset = Theme.S(4);
            float knob = track.Height - inset * 2;

            Color fill, ring, dot;
            if (Enabled)
            {
                fill = _checked ? Theme.Accent : (Hot ? Theme.FieldHot : Theme.Field);
                ring = _checked ? Theme.Accent : Theme.Secondary;
                dot = _checked ? Theme.OnAccent : Theme.Body;
            }
            else
            {
                fill = _checked ? Theme.Border : Theme.Surface;
                ring = Theme.Border;
                dot = _checked ? Theme.Surface : Theme.Border;
            }

            using (GraphicsPath path = SettingsTheme.Pill(track))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(ShowFocus ? Theme.Primary : ring, ShowFocus ? 2f : 1f))
                    g.DrawPath(pen, path);
            }

            float x = _checked ? track.Right - inset - knob : track.X + inset;
            using (var brush = new SolidBrush(dot))
                g.FillEllipse(brush, x, track.Y + inset, knob, knob);
        }
    }

    /// <summary>
    /// A horizontal slider over an integer range. Drag or click to set it; arrows step by
    /// Step, Shift+arrows and Page keys by ten steps, Home and End to the ends.
    ///
    /// The wheel only moves it once it has focus. Unfocused, the wheel falls through to the
    /// scrolling page underneath: a column of sliders that each grab the wheel as the
    /// pointer passes over them turns scrolling the page into changing settings at random.
    /// </summary>
    sealed class Slider : ThemedControl
    {
        int _min, _max = 100, _value;
        bool _dragging;

        public event EventHandler ValueChanged;

        public Slider(SettingsTheme theme) : base(theme)
        {
            Size = new Size(theme.S(150), theme.S(24));
        }

        public void SetRange(int min, int max)
        {
            _min = min;
            _max = Math.Max(min, max);
            _value = Clamp(_value);
            Invalidate();
        }

        /// <summary>Setting this from code does not raise ValueChanged.</summary>
        public int Value
        {
            get { return _value; }
            set { int v = Clamp(value); if (v == _value) return; _value = v; Invalidate(); }
        }

        /// <summary>
        /// A step that suits the range: 1 for a width in pixels, 10 for a delay of a few
        /// seconds, 100 for the minute-long check interval. A whole power of ten, so where
        /// a drag lands is a round number rather than whatever one pixel happened to map to.
        /// </summary>
        public int Step
        {
            get
            {
                int step = 1;
                while (step * 100 <= (_max - _min) / 10) step *= 10;
                return step;
            }
        }

        int Clamp(int v) { return v < _min ? _min : (v > _max ? _max : v); }

        void SetFromUser(int v)
        {
            v = Clamp(v);
            if (v == _value) return;
            _value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        float ThumbRadius { get { return Theme.S(Hot || _dragging ? 8 : 7); } }
        float TrackLeft { get { return Theme.S(9); } }
        float TrackRight { get { return Width - Theme.S(9) - 1; } }

        float XFor(int v)
        {
            if (_max == _min) return TrackLeft;
            return TrackLeft + (TrackRight - TrackLeft) * (v - _min) / (float)(_max - _min);
        }

        int ValueAt(int x)
        {
            float t = (x - TrackLeft) / Math.Max(1f, TrackRight - TrackLeft);
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double raw = _min + t * (_max - _min);
            int step = Step;
            int snapped = (int)(Math.Round(raw / step) * step);
            return t >= 1 ? _max : (t <= 0 ? _min : snapped);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _dragging = true;
                Capture = true;
                SetFromUser(ValueAt(e.X));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromUser(ValueAt(e.X));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            _dragging = false;
            Invalidate();
            base.OnMouseCaptureChanged(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = Step * (e.Shift ? 10 : 1);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down:  SetFromUser(_value - step); e.Handled = true; break;
                case Keys.Right: case Keys.Up:   SetFromUser(_value + step); e.Handled = true; break;
                case Keys.PageDown:              SetFromUser(_value - Step * 10); e.Handled = true; break;
                case Keys.PageUp:                SetFromUser(_value + Step * 10); e.Handled = true; break;
                case Keys.Home:                  SetFromUser(_min); e.Handled = true; break;
                case Keys.End:                   SetFromUser(_max); e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Focused) { base.OnMouseWheel(e); return; }

            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;

            int step = Step * ((ModifierKeys & Keys.Shift) != 0 ? 10 : 1);
            SetFromUser(_value + Math.Sign(e.Delta) * step);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float mid = Height / 2f;
            float thick = Theme.S(4);
            float x = XFor(_value);

            Color filled = Enabled ? Theme.Accent : Theme.Border;
            Color rest = Enabled ? Theme.Border : Theme.Field;

            using (GraphicsPath path = SettingsTheme.Pill(new RectangleF(TrackLeft, mid - thick / 2, TrackRight - TrackLeft, thick)))
            using (var brush = new SolidBrush(rest))
                g.FillPath(brush, path);

            if (x > TrackLeft)
            {
                using (GraphicsPath path = SettingsTheme.Pill(new RectangleF(TrackLeft, mid - thick / 2, x - TrackLeft, thick)))
                using (var brush = new SolidBrush(filled))
                    g.FillPath(brush, path);
            }

            float r = ThumbRadius;
            using (var brush = new SolidBrush(Enabled ? Theme.Accent : Theme.Border))
                g.FillEllipse(brush, x - r, mid - r, r * 2, r * 2);

            // A ring in the surface colour, so the thumb reads as sitting on the track
            // rather than as a bulge in it.
            using (var pen = new Pen(Theme.Surface, Theme.S(2)))
                g.DrawEllipse(pen, x - r, mid - r, r * 2, r * 2);

            if (ShowFocus)
            {
                float f = r + Theme.S(3);
                using (var pen = new Pen(Theme.Primary, 1.5f))
                    g.DrawEllipse(pen, x - f, mid - f, f * 2, f * 2);
            }
        }
    }

    /// <summary>
    /// A typed-in number beside a slider, for the exact value a drag cannot land on. The
    /// unit sits inside the box, after the number.
    ///
    /// The edit is a real TextBox - caret, selection, clipboard and IME are not things to
    /// reimplement - with its border turned off and this control drawing the field around
    /// it. A value is only taken on Enter, on leaving the box, or on Up and Down: taking it
    /// as typed would apply "1" on the way to typing "128" and rebuild the strip at the
    /// minimum width in between.
    /// </summary>
    sealed class NumberBox : ThemedControl
    {
        readonly TextBox _box;
        readonly string _unit;
        int _min, _max, _value;

        public event EventHandler ValueCommitted;

        public NumberBox(SettingsTheme theme, string unit) : base(theme)
        {
            _unit = unit ?? "";
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.TextAlign = HorizontalAlignment.Right;
            _box.Font = theme.BodyFont;
            _box.KeyDown += OnBoxKeyDown;
            _box.Leave += delegate { Commit(); };
            _box.GotFocus += delegate { Invalidate(); };
            _box.LostFocus += delegate { Invalidate(); };
            Controls.Add(_box);

            Size = new Size(theme.S(78), theme.S(26));
        }

        public void SetRange(int min, int max) { _min = min; _max = max; }

        /// <summary>Setting this from code does not raise ValueCommitted.</summary>
        public int Value
        {
            get { return _value; }
            set
            {
                _value = value;
                string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_box.Text != text) _box.Text = text;
            }
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            int step = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    Commit();
                    _box.SelectAll();
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Up:
                    Commit();
                    Set(_value + step);
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Down:
                    Commit();
                    Set(_value - step);
                    e.SuppressKeyPress = true;
                    break;
            }
        }

        void Commit()
        {
            int typed;
            if (!int.TryParse(_box.Text.Trim(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out typed))
            {
                Value = _value;   // put back what it was; nothing to apply
                return;
            }
            Set(typed);
        }

        void Set(int v)
        {
            if (v < _min) v = _min;
            if (v > _max) v = _max;
            bool changed = v != _value;
            Value = v;
            if (changed && ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
        }

        int UnitWidth
        {
            get
            {
                if (_unit.Length == 0) return 0;
                return TextRenderer.MeasureText(_unit, Theme.BodyFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.S(7);
            int unit = UnitWidth;
            int right = Width - pad - (unit > 0 ? unit + Theme.S(4) : 0);
            int h = _box.PreferredHeight;
            _box.SetBounds(pad, (Height - h) / 2, Math.Max(Theme.S(16), right - pad), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The TextBox paints itself in the system colours unless told otherwise, and
            // a retheme only repaints - so its colours are brought up to date here, where
            // every retheme is guaranteed to pass through.
            Color field = Enabled ? Theme.Field : Theme.Surface;
            if (_box.BackColor != field) _box.BackColor = field;
            Color ink = Enabled ? Theme.Primary : Theme.Muted;
            if (_box.ForeColor != ink) _box.ForeColor = ink;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            using (GraphicsPath path = SettingsTheme.Rounded(r, Theme.S(4)))
            {
                using (var brush = new SolidBrush(field)) g.FillPath(brush, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }

            // The Windows 10 text box's tell: an accent line along the bottom while typing.
            if (_box.Focused)
            {
                g.SmoothingMode = SmoothingMode.None;
                using (var brush = new SolidBrush(Theme.Accent))
                    g.FillRectangle(brush, Theme.S(3), Height - 1 - Theme.S(2), Width - Theme.S(6) - 1, Theme.S(2));
            }

            if (_unit.Length > 0)
            {
                int unit = UnitWidth;
                var box = new Rectangle(Width - Theme.S(7) - unit, 0, unit, Height - 1);
                DrawText(g, _unit, Theme.BodyFont, box, Enabled ? Theme.Secondary : Theme.Muted, TextFormatFlags.Left);
            }
        }
    }

    /// <summary>
    /// A few mutually exclusive options side by side, the selected one filled with the
    /// accent. Click one, or Left and Right with focus.
    ///
    /// Segments are narrower past three: statsTileStyle has six, and at the two-or-three
    /// width they made a control wider than the help text beside it, which then wrapped
    /// into a column a word wide. Six-letter captions still fit the narrower segment.
    /// </summary>
    sealed class Segmented : ThemedControl
    {
        readonly string[] _items;
        int _index;
        int _hotIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public Segmented(SettingsTheme theme, string[] items) : base(theme)
        {
            _items = items;
            Size = new Size(theme.S(items.Length > 3 ? 58 : 76) * items.Length, theme.S(26));
        }

        /// <summary>Setting this from code does not raise SelectedIndexChanged.</summary>
        public int SelectedIndex
        {
            get { return _index; }
            set { if (value == _index || value < 0 || value >= _items.Length) return; _index = value; Invalidate(); }
        }

        void Pick(int i)
        {
            if (i < 0 || i >= _items.Length || i == _index) return;
            _index = i;
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        int IndexAt(int x)
        {
            int i = x * _items.Length / Math.Max(1, Width);
            return Math.Max(0, Math.Min(_items.Length - 1, i));
        }

        Rectangle Segment(int i)
        {
            int left = i * Width / _items.Length;
            int right = (i + 1) * Width / _items.Length;
            return new Rectangle(left, 0, right - left, Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); Pick(IndexAt(e.X)); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != _hotIndex) { _hotIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hotIndex = -1;
            base.OnMouseLeave(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { Pick(_index - 1); e.Handled = true; }
            if (e.KeyCode == Keys.Right) { Pick(_index + 1); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var outer = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            float radius = Theme.S(4);

            using (GraphicsPath path = SettingsTheme.Rounded(outer, radius))
            {
                using (var brush = new SolidBrush(Enabled ? Theme.Field : Theme.Surface)) g.FillPath(brush, path);

                // The selected segment is clipped to the outer shape, so the end segments
                // take its rounded corners and the middle one stays square.
                Region clip = g.Clip;
                g.SetClip(path);
                for (int i = 0; i < _items.Length; i++)
                {
                    Rectangle seg = Segment(i);
                    Color fill = Color.Empty;
                    if (i == _index) fill = Enabled ? Theme.Accent : Theme.Border;
                    else if (Enabled && i == _hotIndex) fill = Theme.FieldHot;

                    if (!fill.IsEmpty)
                        using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, seg);
                }
                g.Clip = clip;

                using (var pen = new Pen(ShowFocus ? Theme.Primary : Theme.Border, ShowFocus ? 2f : 1f))
                    g.DrawPath(pen, path);
            }

            for (int i = 0; i < _items.Length; i++)
            {
                Color ink;
                if (!Enabled) ink = i == _index ? Theme.Surface : Theme.Muted;
                else ink = i == _index ? Theme.OnAccent : Theme.Body;
                DrawText(g, _items[i], Theme.BodyFont, Segment(i), ink, TextFormatFlags.HorizontalCenter);
            }
        }
    }

    /// <summary>
    /// A colour: a chip of it and its hex value. Clicking - or Space or Enter - asks for a
    /// new one through PickRequested; the window owns the dialog, since it owns the modal.
    /// </summary>
    sealed class Swatch : ThemedControl
    {
        Color _color;

        public event EventHandler PickRequested;

        public Swatch(SettingsTheme theme) : base(theme)
        {
            Size = new Size(theme.S(110), theme.S(26));
            Cursor = Cursors.Hand;
        }

        public Color Color
        {
            get { return _color; }
            set { if (_color == value) return; _color = value; Invalidate(); }
        }

        void Ask()
        {
            if (PickRequested != null) PickRequested(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) { Focus(); Ask(); }
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Ask(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var outer = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            Color field = !Enabled ? Theme.Surface : (Hot ? Theme.FieldHot : Theme.Field);

            using (GraphicsPath path = SettingsTheme.Rounded(outer, Theme.S(4)))
            {
                using (var brush = new SolidBrush(field)) g.FillPath(brush, path);
                using (var pen = new Pen(ShowFocus ? Theme.Primary : Theme.Border, ShowFocus ? 2f : 1f))
                    g.DrawPath(pen, path);
            }

            int chip = Height - Theme.S(10);
            var chipRect = new RectangleF(Theme.S(5), (Height - 1 - chip) / 2f, chip, chip);
            Color shown = _color.IsEmpty ? Theme.Background : _color;
            if (!Enabled) shown = Palette.Blend(shown, Theme.Surface, 0.6f);

            using (GraphicsPath path = SettingsTheme.Rounded(chipRect, Theme.S(3)))
            {
                using (var brush = new SolidBrush(shown)) g.FillPath(brush, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }

            int textLeft = (int)chipRect.Right + Theme.S(8);
            var textRect = new Rectangle(textLeft, 0, Width - textLeft - Theme.S(4), Height - 1);
            DrawText(g, Setting.ToHex(shown), Theme.BodyFont, textRect,
                     Enabled ? Theme.Body : Theme.Muted, TextFormatFlags.Left);
        }
    }

    /// <summary>
    /// The window's tabs, one per settings section, with the selected one underlined in the
    /// accent. The underline slides to a newly selected tab on Motion's easing - the same
    /// curve and frame interval as the strip's own bar, so the window moves like the thing
    /// it configures.
    /// </summary>
    sealed class TabRow : ThemedControl
    {
        readonly string[] _tabs;
        Rectangle[] _bounds = new Rectangle[0];
        int _index;
        int _hotIndex = -1;

        float _barX, _barW;
        bool _barPlaced;
        readonly Timer _frame;
        readonly Stopwatch _clock = new Stopwatch();

        const int SlideMs = 160;

        public event EventHandler SelectedIndexChanged;

        public TabRow(SettingsTheme theme, string[] tabs) : base(theme)
        {
            _tabs = tabs;
            Height = theme.S(44);

            _frame = new Timer();
            _frame.Interval = Motion.FrameMs;
            _frame.Tick += OnFrame;
        }

        public int Count { get { return _tabs.Length; } }

        public int SelectedIndex
        {
            get { return _index; }
            set { Choose(value, false); }
        }

        void Choose(int i, bool raise)
        {
            if (_tabs.Length == 0) return;
            i = ((i % _tabs.Length) + _tabs.Length) % _tabs.Length;
            if (i == _index) return;

            _index = i;
            _clock.Restart();
            _frame.Start();
            Invalidate();
            if (raise && SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        /// <summary>Selects the next or previous tab, wrapping - Ctrl+Tab and Ctrl+Shift+Tab.</summary>
        public void Cycle(int delta) { Choose(_index + delta, true); }

        void OnFrame(object sender, EventArgs e)
        {
            if (_bounds.Length == 0) { _frame.Stop(); return; }

            int elapsed = (int)_clock.ElapsedMilliseconds;
            _clock.Restart();
            float rate = Motion.Rate(elapsed, SlideMs);

            Rectangle target = Underline(_index);
            bool moving = Motion.Ease(ref _barX, target.X, rate, Motion.PixelEpsilon);
            moving |= Motion.Ease(ref _barW, target.Width, rate, Motion.PixelEpsilon);

            Invalidate();
            if (!moving) _frame.Stop();
        }

        Rectangle Underline(int i)
        {
            Rectangle b = _bounds[i];
            int inset = Theme.S(12);
            return new Rectangle(b.X + inset, 0, b.Width - inset * 2, 0);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            _bounds = new Rectangle[_tabs.Length];
            int x = Theme.S(8);
            for (int i = 0; i < _tabs.Length; i++)
            {
                int w = TextRenderer.MeasureText(_tabs[i], Theme.TitleFont, Size.Empty, TextFormatFlags.NoPadding).Width
                        + Theme.S(24);
                _bounds[i] = new Rectangle(x, 0, w, Height);
                x += w;
            }

            // The first layout places the bar outright: there is nowhere for it to slide from.
            if (!_barPlaced && _bounds.Length > 0)
            {
                Rectangle u = Underline(_index);
                _barX = u.X;
                _barW = u.Width;
                _barPlaced = true;
            }
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < _bounds.Length; i++)
                if (_bounds[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) { Focus(); Choose(i, true); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != _hotIndex) { _hotIndex = i; Invalidate(); }
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hotIndex = -1;
            base.OnMouseLeave(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { Choose(_index - 1, true); e.Handled = true; }
            if (e.KeyCode == Keys.Right) { Choose(_index + 1, true); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int barH = Theme.S(3);

            using (var brush = new SolidBrush(Theme.Border))
                g.FillRectangle(brush, 0, Height - 1, Width, 1);

            for (int i = 0; i < _bounds.Length; i++)
            {
                Color ink = i == _index ? Theme.Primary : (i == _hotIndex ? Theme.Body : Theme.Secondary);
                var text = new Rectangle(_bounds[i].X, 0, _bounds[i].Width, Height - barH);
                DrawText(g, _tabs[i], Theme.TitleFont, text, ink, TextFormatFlags.HorizontalCenter);

                if (i == _index && ShowFocus)
                {
                    Rectangle f = Rectangle.Inflate(text, -Theme.S(4), -Theme.S(8));
                    ControlPaint.DrawFocusRectangle(g, f, Theme.Primary, Theme.Surface);
                }
            }

            if (_bounds.Length > 0)
            {
                using (var brush = new SolidBrush(Theme.Accent))
                    g.FillRectangle(brush, _barX, Height - barH, _barW, barH);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _frame.Stop(); _frame.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One setting: its name and help text on the left, its editor on the right, a hairline
    /// under it. Not focusable itself - Tab goes straight to the editor.
    ///
    /// Arrange is called with the width the row may take and answers the height it needs,
    /// since the help text wraps and only the row knows how it measures. Text is measured
    /// and drawn with the same TextRenderer flags, so what was measured is what is drawn -
    /// the mismatch ConfirmDialog's comments describe does not get a chance to happen.
    /// </summary>
    sealed class SettingRow : Control
    {
        public readonly Setting Setting;
        readonly SettingsTheme _theme;
        readonly Control _editor;
        Rectangle _titleRect, _helpRect;

        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
                                     TextFormatFlags.TextBoxControl | TextFormatFlags.Left;

        public SettingRow(SettingsTheme theme, Setting setting, Control editor)
        {
            _theme = theme;
            Setting = setting;
            _editor = editor;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Controls.Add(editor);
        }

        public int Arrange(int width)
        {
            int padX = _theme.S(20);
            int padY = _theme.S(12);
            int gap = _theme.S(24);

            int textWidth = Math.Max(_theme.S(120), width - padX * 2 - gap - _editor.Width);

            Size title = TextRenderer.MeasureText(Setting.Label, _theme.TitleFont,
                                                  new Size(textWidth, int.MaxValue), Wrap);
            Size help = TextRenderer.MeasureText(Setting.Help, _theme.SmallFont,
                                                 new Size(textWidth, int.MaxValue), Wrap);

            _titleRect = new Rectangle(padX, padY, textWidth, title.Height);
            _helpRect = new Rectangle(padX, _titleRect.Bottom + _theme.S(3), textWidth, help.Height);

            int height = Math.Max(_helpRect.Bottom, padY + _editor.Height) + padY;
            Size = new Size(width, height);

            // Level with the title rather than centred on the row: with three lines of help
            // a centred control floats beside the middle of the explanation, away from the
            // name it belongs to.
            int editorTop = padY + Math.Max(0, (title.Height - _editor.Height) / 2);
            _editor.Location = new Point(width - padX - _editor.Width, editorTop);

            Invalidate();
            return height;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Surface);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            TextRenderer.DrawText(g, Setting.Label, _theme.TitleFont, _titleRect,
                                  Enabled ? _theme.Primary : _theme.Muted, Wrap);
            TextRenderer.DrawText(g, Setting.Help, _theme.SmallFont, _helpRect,
                                  Enabled ? _theme.Secondary : _theme.Muted, Wrap);

            using (var brush = new SolidBrush(_theme.Divider))
                g.FillRectangle(brush, _theme.S(20), Height - 1, Width - _theme.S(40), 1);
        }
    }

    /// <summary>
    /// A few controls side by side, right-aligned, as one editor - a slider and its number
    /// box, or the automatic/custom choice and its swatch. Only a container: it draws the
    /// surface and nothing else, and is never focused.
    /// </summary>
    sealed class EditorGroup : Control
    {
        readonly SettingsTheme _theme;

        public EditorGroup(SettingsTheme theme, params Control[] parts)
        {
            _theme = theme;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            int gap = theme.S(8);
            int x = 0, height = 0;
            foreach (Control part in parts) height = Math.Max(height, part.Height);
            foreach (Control part in parts)
            {
                part.Location = new Point(x, (height - part.Height) / 2);
                Controls.Add(part);
                x += part.Width + gap;
            }
            Size = new Size(Math.Max(0, x - gap), height);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Surface);
        }
    }
}
