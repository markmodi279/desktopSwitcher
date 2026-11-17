using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>What a CloseSelectDialog was closed with.</summary>
    enum CloseChoice
    {
        Cancel,
        CloseSelected,
        CloseAndRemove
    }

    /// <summary>
    /// The close-all confirmation with a checklist: every window on the desktop, every one
    /// ticked, so the usual "close all" is still one click, and "all but the editor" is one
    /// more instead of moving the editor somewhere else first.
    ///
    /// Apart from ConfirmDialog rather than a mode of it. Every other confirmation is a
    /// title, a sentence and two buttons, and folding a list, a scroll and buttons that come
    /// and go into that class would make all of them harder to read for the sake of one.
    /// The family look is shared instead: ConfirmDialog.StyleButton, the same surface and
    /// border tones, the same placement over the strip.
    ///
    /// Only for two windows or more. With one, a checklist is a single box that unticked
    /// leaves nothing to do, and the controller shows the plain dialog it always has.
    ///
    /// Remove is only offered with every row ticked. Removing a desktop makes the shell
    /// carry whatever is left on it to a neighbour, so "keep these" plus "remove" would
    /// really mean "move these to whichever desktop the shell picks" - which the dialog
    /// would then have to say, for something nobody asked for. With a row unticked the
    /// remove button greys out, a line says why, and "Close N" appears beside it, which
    /// closes the ticked ones and keeps the desktop.
    /// </summary>
    sealed class CloseSelectDialog : Form
    {
        readonly Color _background;
        readonly Rectangle _anchor;
        readonly int _gap;
        readonly bool _remove;
        readonly CheckList _list;
        readonly Label _hint;
        readonly Button _close;
        readonly Button _removeButton;
        readonly Button _cancel;
        readonly Font _titleFont;
        readonly Font _bodyFont;
        readonly int _closeCount;
        CloseChoice _choice = CloseChoice.Cancel;

        /// <summary>
        /// Cancel unless one of the two close buttons was pressed - Escape, Alt+F4 and the
        /// Cancel button all leave it there.
        /// </summary>
        public CloseChoice Choice
        {
            get { return _choice; }
        }

        /// <summary>The handles of the rows still ticked when the dialog closed.</summary>
        public HashSet<IntPtr> Ticked
        {
            get { return _list.TickedHandles(); }
        }

        /// <param name="windows">The closeable windows, each a ticked row.</param>
        /// <param name="pinned">
        /// Windows pinned to every desktop. Shown greyed after the rest and never ticked:
        /// close-all leaves them alone, and without a row for them a window the user can see
        /// on this desktop would be missing from the list with nothing to say why.
        /// </param>
        /// <param name="background">The taskbar colour, which every tone here derives from.</param>
        /// <param name="accent">highlightColor, for the ticked boxes - the strip's own accent.</param>
        public CloseSelectDialog(string titleText, string bodyText, List<WindowEntry> windows,
                                 IList<WindowEntry> pinned, bool remove, Color background,
                                 Color accent, double dpiScale, Rectangle anchor)
        {
            _background = background;
            _anchor = anchor;
            _remove = remove;
            _gap = Scale(4, dpiScale);
            _closeCount = windows.Count;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = false;
            BackColor = Palette.Lift(background, 0.06f);

            Padding = new Padding(Scale(16, dpiScale), Scale(14, dpiScale),
                                  Scale(16, dpiScale), Scale(14, dpiScale));

            _titleFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);

            int buttonMinWidth = Scale(88, dpiScale);
            int buttonPad = Scale(24, dpiScale);
            int buttonHeight = Scale(28, dpiScale);
            int buttonGap = Scale(8, dpiScale);

            // Fixed, not sized to the text the way ConfirmDialog is. The list's rows are as
            // wide as the dialog, and a dialog that resized to its longest window title
            // would be as wide as a browser tab's page title - or, clamped, would wrap the
            // body differently from one desktop to the next for no reason anyone could see.
            int contentWidth = Scale(420, dpiScale);

            const string removeText = "Close and remove";
            const string hintText = "Unticked windows stay, so the desktop can't be removed.";

            Size titleSize, bodySize, hintSize;
            int closeWidth, removeWidth, cancelWidth;
            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                // The close button is as wide as the widest label it can carry, so it
                // never changes width under the pointer as rows are ticked and unticked.
                closeWidth = Math.Max(Measure(g, "Close all"), Measure(g, "Close " + _closeCount));
                closeWidth = Math.Max(buttonMinWidth, closeWidth + buttonPad);
                removeWidth = Math.Max(buttonMinWidth, Measure(g, removeText) + buttonPad);
                cancelWidth = Math.Max(buttonMinWidth, Measure(g, "Cancel") + buttonPad);

                int buttons = closeWidth + cancelWidth + buttonGap
                            + (remove ? removeWidth + buttonGap : 0);
                contentWidth = Math.Max(contentWidth, buttons);

                titleSize = Ceiling(g.MeasureString(titleText, _titleFont, contentWidth));
                bodySize = Ceiling(g.MeasureString(bodyText, _bodyFont, contentWidth));
                hintSize = Ceiling(g.MeasureString(hintText, _bodyFont, contentWidth));
            }

            var title = MakeLabel(titleText, _titleFont, Palette.PrimaryText(background));
            title.Location = new Point(Padding.Left, Padding.Top);
            title.Size = titleSize;

            var body = MakeLabel(bodyText, _bodyFont, Palette.BodyText(background));
            body.Location = new Point(Padding.Left, title.Bottom + Scale(4, dpiScale));
            body.Size = bodySize;

            _list = new CheckList(windows, pinned, background, accent, dpiScale, _bodyFont);
            _list.Location = new Point(Padding.Left, body.Bottom + Scale(12, dpiScale));
            _list.Width = contentWidth;
            _list.TabIndex = 0;
            _list.Changed += delegate { UpdateState(); };

            // Only remove has anything to explain. Its room is kept whether or not the
            // line is showing, so the buttons do not jump down the first time a row is
            // unticked and back up when it is ticked again.
            int below = _list.Bottom;
            _hint = MakeLabel(remove ? hintText : string.Empty, _bodyFont, Palette.MutedText(background));
            if (remove)
            {
                _hint.Location = new Point(Padding.Left, _list.Bottom + Scale(8, dpiScale));
                _hint.Size = hintSize;
                below = _hint.Bottom;
            }

            int buttonsTop = below + Scale(16, dpiScale);
            int clientWidth = Padding.Left + contentWidth + Padding.Right;
            ClientSize = new Size(clientWidth, buttonsTop + buttonHeight + Padding.Bottom);

            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(_list);
            if (remove) Controls.Add(_hint);

            // Laid out once, from the right, in their final places. Close sits leftmost in
            // both modes, so when it appears in remove mode nothing beside it moves: the
            // remove button stays where the pointer last saw it, only greyed.
            int right = clientWidth - Padding.Right;
            _cancel = MakeButton("Cancel", cancelWidth, buttonHeight, right - cancelWidth, buttonsTop);
            right = _cancel.Left - buttonGap;
            if (remove)
            {
                _removeButton = MakeButton(removeText, removeWidth, buttonHeight, right - removeWidth, buttonsTop);
                right = _removeButton.Left - buttonGap;
            }
            _close = MakeButton("Close all", closeWidth, buttonHeight, right - closeWidth, buttonsTop);

            _close.TabIndex = 1;
            if (_removeButton != null) _removeButton.TabIndex = 2;
            _cancel.TabIndex = 3;

            _close.Click += delegate { Finish(CloseChoice.CloseSelected); };
            if (_removeButton != null) _removeButton.Click += delegate { Finish(CloseChoice.CloseAndRemove); };
            _cancel.Click += delegate { Finish(CloseChoice.Cancel); };

            Controls.Add(_close);
            if (_removeButton != null) Controls.Add(_removeButton);
            Controls.Add(_cancel);

            // Cancel has the focus, as in ConfirmDialog: a focused Button fires on Enter, so
            // a stray Enter and Escape both reach the safe choice. Closing is a click, or a
            // Tab and a key, never one keystroke from the moment the dialog appears.
            CancelButton = _cancel;
            ActiveControl = _cancel;

            UpdateState();
        }

        int Measure(Graphics g, string text)
        {
            return (int)Math.Ceiling(g.MeasureString(text, _bodyFont).Width);
        }

        Label MakeLabel(string text, Font font, Color ink)
        {
            var label = new Label();
            label.AutoSize = false;
            label.UseCompatibleTextRendering = true;   // paint with the GDI+ engine just measured with
            label.BackColor = Color.Transparent;
            label.ForeColor = ink;
            label.Font = font;
            label.Text = text;
            return label;
        }

        Button MakeButton(string text, int width, int height, int left, int top)
        {
            var button = new Button();
            button.Text = text;
            button.Size = new Size(width, height);
            button.Location = new Point(left, top);
            button.Font = _bodyFont;
            ConfirmDialog.StyleButton(button, _background);
            return button;
        }

        void Finish(CloseChoice choice)
        {
            _choice = choice;
            DialogResult = DialogResult.OK;
        }

        /// <summary>
        /// The buttons and the hint, from how many rows are ticked. Run on every toggle.
        /// </summary>
        void UpdateState()
        {
            int ticked = _list.TickedCount;
            bool all = ticked == _closeCount;

            _close.Text = all ? "Close all" : "Close " + ticked;
            SetEnabled(_close, ticked > 0);

            if (_remove)
            {
                // All ticked, remove mode is the dialog it always was: Close and remove,
                // Cancel. "Close all" beside it would be the menu's other item, offered
                // again here for no reason.
                _close.Visible = !all;
                SetEnabled(_removeButton, all);
                _hint.Visible = !all;
            }
        }

        /// <summary>
        /// A flat button disabled keeps the border it was styled with, so it still looked
        /// pressable beside an enabled one; the border fades with the text.
        /// </summary>
        void SetEnabled(Button button, bool enabled)
        {
            if (button.Enabled == enabled) return;

            // Disabling the focused button would leave the keyboard nowhere; give it to the
            // list, which is what was being used to change the ticks.
            if (!enabled && button.Focused) _list.Focus();

            button.Enabled = enabled;
            button.FlatAppearance.BorderColor = enabled
                ? Palette.Lift(_background, 0.24f)
                : Palette.Lift(_background, 0.14f);
        }

        static int Scale(int value, double dpiScale)
        {
            return (int)Math.Round(value * dpiScale);
        }

        static Size Ceiling(SizeF size)
        {
            return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // ConfirmDialog's placement exactly: over the strip, centred on it, clamped
            // into the work area. See there for the fallback to the pointer.
            Rectangle anchor = _anchor.IsEmpty
                ? new Rectangle(Cursor.Position, Size.Empty)
                : _anchor;

            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            bool accentAtTop;
            Location = TooltipWindow.Place(Size, anchor, work, _gap, out accentAtTop);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // ConfirmDialog's border, the one edge a borderless form has.
            using (var pen = new Pen(Palette.Lift(_background, 0.24f)))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _titleFont.Dispose();
                _bodyFont.Dispose();
            }
        }

        // --- the list ---------------------------------------------------------------

        /// <summary>
        /// The checklist: an "All" row, then one row per window, then the pinned ones.
        ///
        /// Owner-drawn rather than a CheckedListBox or a ListView with checkboxes. Those
        /// paint their boxes, selection and scrollbar in the system's light chrome whatever
        /// BackColor says - the reason SettingsControls exists - and a white scrollbar in a
        /// dialog themed off a black taskbar would be the one thing on screen that looks
        /// borrowed.
        ///
        /// Row-granular scrolling, past eight rows: the header stays put above it, so a
        /// list of twenty can be cleared with one click on All and then the one or two
        /// keepers ticked, which is the quick way to say "close everything but these".
        ///
        /// The focus ring only shows once the keyboard has been used in the list. A click
        /// also takes the focus, and a ring around the row just clicked says nothing the
        /// box changing has not already said.
        /// </summary>
        sealed class CheckList : Control
        {
            const int HeaderIndex = -1;
            const int NoRow = -2;
            const int MaxVisible = 8;

            readonly List<WindowEntry> _rows = new List<WindowEntry>();
            readonly bool[] _ticked;
            readonly int _closeable;
            readonly Color _background;
            readonly Color _surface;
            readonly Color _accent;
            readonly Color _onAccent;
            readonly double _scale;
            readonly Font _font;
            readonly Font _smallFont;
            readonly int _rowHeight;
            readonly int _visible;

            int _top;              // first row in view
            int _focus;            // HeaderIndex or a closeable row
            int _hot = NoRow;
            bool _keyboard;
            bool _dragging;
            int _dragOffset;

            public event EventHandler Changed;

            public CheckList(List<WindowEntry> windows, IList<WindowEntry> pinned, Color background,
                             Color accent, double scale, Font font)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);
                TabStop = true;

                _rows.AddRange(windows);
                _closeable = windows.Count;
                if (pinned != null) _rows.AddRange(pinned);

                _ticked = new bool[_closeable];
                for (int i = 0; i < _ticked.Length; i++) _ticked[i] = true;

                _background = background;
                _surface = Palette.Lift(background, 0.06f);
                _accent = accent;
                _onAccent = Palette.IsLight(accent) ? Color.FromArgb(20, 20, 20) : Color.White;
                _scale = scale;
                _font = font;
                _smallFont = new Font("Segoe UI", (float)(11 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
                _rowHeight = S(26);
                _visible = Math.Min(MaxVisible, _rows.Count);
                _focus = HeaderIndex;

                BackColor = _surface;
                // A rule above, under the header and below, around the header and rows.
                Height = 1 + _rowHeight + 1 + _visible * _rowHeight + 1;
            }

            int S(int value)
            {
                return (int)Math.Round(value * _scale);
            }

            public int TickedCount
            {
                get
                {
                    int n = 0;
                    for (int i = 0; i < _ticked.Length; i++) if (_ticked[i]) n++;
                    return n;
                }
            }

            public HashSet<IntPtr> TickedHandles()
            {
                var set = new HashSet<IntPtr>();
                for (int i = 0; i < _ticked.Length; i++)
                    if (_ticked[i]) set.Add(_rows[i].Handle);
                return set;
            }

            bool Scrolls
            {
                get { return _rows.Count > _visible; }
            }

            int RowsTop
            {
                get { return 1 + _rowHeight + 1; }
            }

            Rectangle HeaderRect
            {
                get { return new Rectangle(0, 1, Width, _rowHeight); }
            }

            /// <summary>Row i's rectangle at the current scroll, whether or not it is in view.</summary>
            Rectangle RowRect(int i)
            {
                return new Rectangle(0, RowsTop + (i - _top) * _rowHeight, Width - (Scrolls ? S(10) : 0), _rowHeight);
            }

            Rectangle TrackRect
            {
                get { return new Rectangle(Width - S(8), RowsTop + S(2), S(4), _visible * _rowHeight - S(4)); }
            }

            Rectangle ThumbRect
            {
                get
                {
                    Rectangle track = TrackRect;
                    int h = Math.Max(S(16), track.Height * _visible / _rows.Count);
                    int range = _rows.Count - _visible;
                    int y = track.Top + (range == 0 ? 0 : (track.Height - h) * _top / range);
                    return new Rectangle(track.Left, y, track.Width, h);
                }
            }

            /// <summary>HeaderIndex, a row index, or NoRow for the rules and the scrollbar.</summary>
            int HitTest(Point p)
            {
                if (HeaderRect.Contains(p)) return HeaderIndex;
                if (p.Y < RowsTop || p.Y >= RowsTop + _visible * _rowHeight) return NoRow;
                if (Scrolls && p.X >= Width - S(10)) return NoRow;
                int i = _top + (p.Y - RowsTop) / _rowHeight;
                return i < _rows.Count ? i : NoRow;
            }

            void Toggle(int index)
            {
                if (index == HeaderIndex)
                {
                    // Anything unticked ticks everything; only a full list clears. The
                    // same rule Explorer's own select-all box follows, and the one that
                    // makes a stray click on a partial list the harmless direction.
                    bool all = TickedCount == _closeable;
                    for (int i = 0; i < _ticked.Length; i++) _ticked[i] = !all;
                }
                else if (index >= 0 && index < _closeable)
                {
                    _ticked[index] = !_ticked[index];
                }
                else return;

                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }

            void ScrollTo(int top)
            {
                int max = Math.Max(0, _rows.Count - _visible);
                top = Math.Max(0, Math.Min(max, top));
                if (top == _top) return;
                _top = top;
                Invalidate();
            }

            void EnsureVisible(int index)
            {
                if (index < 0) return;
                if (index < _top) ScrollTo(index);
                else if (index >= _top + _visible) ScrollTo(index - _visible + 1);
            }

            // --- input --------------------------------------------------------------

            protected override bool IsInputKey(Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Up:
                    case Keys.Down:
                    case Keys.Home:
                    case Keys.End:
                    case Keys.PageUp:
                    case Keys.PageDown:
                        return true;
                }
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                int target = _focus;
                switch (e.KeyCode)
                {
                    case Keys.Space:
                        _keyboard = true;
                        Toggle(_focus);
                        e.Handled = true;
                        return;
                    case Keys.Up:       target = _focus - 1; break;
                    case Keys.Down:     target = _focus + 1; break;
                    case Keys.PageUp:   target = _focus - _visible; break;
                    case Keys.PageDown: target = _focus + _visible; break;
                    case Keys.Home:     target = HeaderIndex; break;
                    case Keys.End:      target = _closeable - 1; break;
                    default:
                        base.OnKeyDown(e);
                        return;
                }

                // Pinned rows cannot be ticked, so the keyboard stops at the last one that can.
                _focus = Math.Max(HeaderIndex, Math.Min(_closeable - 1, target));
                _keyboard = true;
                EnsureVisible(_focus);
                Invalidate();
                e.Handled = true;
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                _keyboard = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                Focus();
                _keyboard = false;

                if (Scrolls && e.X >= Width - S(10))
                {
                    Rectangle thumb = ThumbRect;
                    if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
                    {
                        _dragging = true;
                        _dragOffset = e.Y - thumb.Top;
                    }
                    else
                    {
                        // A click on the track pages toward it, as a system scrollbar does.
                        ScrollTo(_top + (e.Y < thumb.Top ? -_visible : _visible));
                    }
                    return;
                }

                int hit = HitTest(e.Location);
                if (hit == HeaderIndex || (hit >= 0 && hit < _closeable))
                {
                    _focus = hit;
                    Toggle(hit);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);

                if (_dragging)
                {
                    Rectangle track = TrackRect;
                    Rectangle thumb = ThumbRect;
                    int room = track.Height - thumb.Height;
                    int range = _rows.Count - _visible;
                    if (room > 0)
                    {
                        int y = e.Y - _dragOffset - track.Top;
                        ScrollTo((int)Math.Round((double)y * range / room));
                    }
                    return;
                }

                int hit = HitTest(e.Location);
                if (hit >= _closeable) hit = NoRow;   // pinned rows do not light up: nothing to click
                if (hit != _hot)
                {
                    _hot = hit;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragging = false;
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hot != NoRow)
                {
                    _hot = NoRow;
                    Invalidate();
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (!Scrolls) return;
                ScrollTo(_top - e.Delta / 120 * 3);
                // The row under the pointer is a different one now.
                OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, e.X, e.Y, 0));
            }

            // --- paint --------------------------------------------------------------

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(_surface);

                Color rule = Palette.Lift(_background, 0.14f);
                using (var pen = new Pen(rule))
                {
                    g.DrawLine(pen, 0, 0, Width - 1, 0);
                    g.DrawLine(pen, 0, RowsTop - 1, Width - 1, RowsTop - 1);
                    g.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
                }

                PaintHeader(g);

                Region clip = g.Clip;
                g.SetClip(new Rectangle(0, RowsTop, Width, _visible * _rowHeight));
                for (int i = _top; i < Math.Min(_rows.Count, _top + _visible); i++)
                    PaintRow(g, i);
                g.Clip = clip;

                if (Scrolls)
                {
                    using (var brush = new SolidBrush(Palette.Lift(_background, _dragging ? 0.34f : 0.24f)))
                        g.FillRectangle(brush, ThumbRect);
                }
            }

            void PaintHeader(Graphics g)
            {
                Rectangle r = HeaderRect;
                if (_hot == HeaderIndex) Fill(g, r, Palette.Lift(_background, 0.12f));

                int ticked = TickedCount;
                int pad = S(6);
                Rectangle box = BoxAt(r, pad);
                if (ticked == _closeable) DrawTicked(g, box);
                else if (ticked == 0) DrawEmpty(g, box, false);
                else DrawMixed(g, box);

                int x = box.Right + S(8);
                string count = ticked + " of " + _closeable;
                int countWidth = (int)Math.Ceiling(g.MeasureString(count, _font).Width);

                Write(g, "All", _font, Palette.SecondaryText(_background),
                     new Rectangle(x, r.Top, r.Right - x - countWidth - pad, r.Height), false);
                Write(g, count, _font, Palette.MutedText(_background),
                     new Rectangle(r.Right - pad - countWidth, r.Top, countWidth, r.Height), false);

                if (_keyboard && Focused && _focus == HeaderIndex) Ring(g, r);
            }

            void PaintRow(Graphics g, int i)
            {
                Rectangle r = RowRect(i);
                WindowEntry w = _rows[i];
                bool pinned = i >= _closeable;
                bool on = !pinned && _ticked[i];

                if (_hot == i) Fill(g, r, Palette.Lift(_background, 0.12f));

                int pad = S(6);
                Rectangle box = BoxAt(r, pad);
                if (pinned) DrawEmpty(g, box, true);
                else if (on) DrawTicked(g, box);
                else DrawEmpty(g, box, false);

                int icon = S(16);
                var iconRect = new Rectangle(box.Right + S(8), r.Top + (r.Height - icon) / 2, icon, icon);
                Bitmap bitmap = AppIcons.Get(w, icon);   // the cache's; never disposed here
                if (bitmap != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bitmap, iconRect);
                }
                else DrawLetterTile(g, iconRect, w.App);

                // Faded, not struck through: the box already says which way it is, and a
                // line through every kept title made the rows the user chose to keep the
                // hardest ones in the list to read.
                Color appInk = on ? Palette.BodyText(_background) : Palette.MutedText(_background);
                Color titleInk = on ? Palette.SecondaryText(_background) : Palette.MutedText(_background);

                int x = iconRect.Right + S(8);
                int right = r.Right - pad;

                if (pinned)
                {
                    const string tag = "pinned, stays";
                    int tagWidth = (int)Math.Ceiling(g.MeasureString(tag, _smallFont).Width);
                    Write(g, tag, _smallFont, Palette.MutedText(_background),
                         new Rectangle(right - tagWidth, r.Top, tagWidth, r.Height), false);
                    right -= tagWidth + S(8);
                }

                string app = w.App ?? string.Empty;
                string title = w.Title ?? string.Empty;
                if (app.Length == 0) { app = title; title = string.Empty; }
                else if (title == app) title = string.Empty;

                // The app name gets what it needs up to half the room, the title the rest.
                // Names are short and say most; a title is often a long path whose tail
                // matters least, and the ellipsis takes that.
                int room = Math.Max(0, right - x);
                int appWidth = Math.Min(room / 2, (int)Math.Ceiling(g.MeasureString(app, _font).Width));
                if (title.Length == 0) appWidth = room;
                Write(g, app, _font, appInk, new Rectangle(x, r.Top, appWidth, r.Height), true);

                if (title.Length > 0)
                {
                    int tx = x + appWidth + S(10);
                    Write(g, title, _font, titleInk, new Rectangle(tx, r.Top, Math.Max(0, right - tx), r.Height), true);
                }

                if (_keyboard && Focused && _focus == i) Ring(g, r);
            }

            Rectangle BoxAt(Rectangle row, int pad)
            {
                int size = S(14);
                return new Rectangle(row.Left + pad, row.Top + (row.Height - size) / 2, size, size);
            }

            void DrawTicked(Graphics g, Rectangle box)
            {
                Fill(g, box, _accent);
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(_onAccent, Math.Max(1.5f, (float)(1.8 * _scale))))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    float u = box.Width / 14f;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.Left + 3.5f * u, box.Top + 7.2f * u),
                        new PointF(box.Left + 6.0f * u, box.Top + 9.6f * u),
                        new PointF(box.Left + 10.6f * u, box.Top + 4.6f * u)
                    });
                }
                g.SmoothingMode = old;
            }

            void DrawMixed(Graphics g, Rectangle box)
            {
                Fill(g, box, _accent);
                int bar = Math.Max(2, S(2));
                int inset = S(3);
                Fill(g, new Rectangle(box.Left + inset, box.Top + (box.Height - bar) / 2, box.Width - 2 * inset, bar), _onAccent);
            }

            /// <summary>An empty box; dotted for a pinned row, which cannot be ticked at all.</summary>
            void DrawEmpty(Graphics g, Rectangle box, bool locked)
            {
                using (var pen = new Pen(Palette.Lift(_background, locked ? 0.20f : 0.32f)))
                {
                    if (locked) pen.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height - 1);
                }
            }

            /// <summary>
            /// TooltipWindow's stand-in for an icon that could not be had: the app's first
            /// letter on a soft square, so the column of icons stays a column.
            /// </summary>
            void DrawLetterTile(Graphics g, Rectangle r, string app)
            {
                Fill(g, r, Palette.Lift(_surface, 0.16f));
                string letter = string.IsNullOrEmpty(app) ? "?" : app.Substring(0, 1).ToUpperInvariant();
                using (var ink = new SolidBrush(Palette.SecondaryText(_surface)))
                using (var format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(letter, _smallFont, ink, r, format);
                }
            }

            void Ring(Graphics g, Rectangle r)
            {
                using (var pen = new Pen(Palette.SecondaryText(_background)))
                    g.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);
            }

            static void Fill(Graphics g, Rectangle r, Color c)
            {
                using (var brush = new SolidBrush(c)) g.FillRectangle(brush, r);
            }

            static void Write(Graphics g, string text, Font font, Color ink, Rectangle r, bool ellipsis)
            {
                if (r.Width <= 0 || string.IsNullOrEmpty(text)) return;
                using (var brush = new SolidBrush(ink))
                using (var format = new StringFormat(StringFormatFlags.NoWrap))
                {
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
                    g.DrawString(text, font, brush, r, format);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _smallFont.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
