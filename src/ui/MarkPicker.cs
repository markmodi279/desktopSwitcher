using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// "Set mark..." from a desktop's menu: two letters, a symbol or a colour, with the marks
    /// recently used or let go of underneath, and Clear, Cancel and OK.
    ///
    /// Every edit is raised through Previewed as it happens, and the controller shows it on
    /// the strip at once - the button widens, the mark appears - so the choice is made by
    /// looking at the taskbar, not at a sample in here. Cancel puts the strip back; only OK
    /// keeps anything. Picking a kind replaces any other kind: a desktop has one mark.
    ///
    /// A modal Form like ConfirmDialog, in the Settings window's tones and controls -
    /// SettingsTheme and ThemedControl - so the three read as one family. It takes focus,
    /// which none of the strip's own surfaces do, because it has a text box.
    /// </summary>
    sealed class MarkPicker : Form
    {
        // Static so a colour mixed in the system dialog is still in its custom row the next
        // time, for as long as the app runs - the Settings window keeps its own the same way.
        static int[] _customColors;

        readonly SettingsTheme _theme;
        readonly Color _background;
        readonly Rectangle _anchor;
        readonly int _gap;

        readonly TextBox _letters;
        readonly MarkGrid _glyphs;
        readonly MarkGrid _colors;
        readonly MarkGrid _recent;   // null when there is nothing recent

        DesktopMark _draft;
        bool _syncing;
        bool _choosingColor;   // the system colour dialog is up - see OnDeactivate

        /// <summary>The mark as it stands - what OK commits. Null for none.</summary>
        public DesktopMark Mark { get { return _draft; } }

        /// <summary>Every change of the draft, null included, for the strip to show live.</summary>
        public event Action<DesktopMark> Previewed;

        public MarkPicker(string desktopName, DesktopMark current, IList<DesktopMark> recent,
                          Color background, Color accent, double scale, Rectangle anchor)
        {
            _theme = new SettingsTheme(scale, background, accent);
            _background = background;
            _anchor = anchor;
            _gap = _theme.S(4);
            _draft = current;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            BackColor = _theme.Surface;
            Text = "Set mark";

            int pad = _theme.S(16);
            int x = pad, y = _theme.S(14);

            // Eight symbols a row fixes the width; everything else is laid to it.
            int cellW = _theme.S(34), cellH = _theme.S(30), cellGap = _theme.S(4);
            int contentWidth = 8 * cellW + 7 * cellGap;

            var title = new Label();
            title.AutoSize = false;
            title.AutoEllipsis = true;
            title.UseCompatibleTextRendering = true;
            title.BackColor = Color.Transparent;
            title.ForeColor = _theme.Primary;
            title.Font = _theme.TitleFont;
            title.Text = "Mark for " + desktopName;
            title.Location = new Point(x, y);
            title.Size = new Size(contentWidth, _theme.S(20));
            Controls.Add(title);
            y = title.Bottom + _theme.S(10);

            // --- letters ---
            y = Section("Letters", x, y);
            _letters = new TextBox();
            _letters.BorderStyle = BorderStyle.FixedSingle;
            _letters.BackColor = _theme.Field;
            _letters.ForeColor = _theme.Primary;
            _letters.Font = new Font("Segoe UI Semibold", (float)(15 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            _letters.CharacterCasing = CharacterCasing.Upper;
            _letters.MaxLength = DesktopMark.MaxLetters;
            _letters.TextAlign = HorizontalAlignment.Center;
            _letters.Location = new Point(x, y);
            _letters.Width = _theme.S(64);
            _letters.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                // What DesktopMark keeps - A-Z and 0-9 - and nothing it would silently drop,
                // like an accented letter the box would show and the mark would not. Control
                // keys (backspace, Ctrl+A, Ctrl+V) pass; a paste is cleaned in TextChanged.
                if (!char.IsControl(e.KeyChar) && DesktopMark.CleanLetters(e.KeyChar.ToString()).Length == 0)
                    e.Handled = true;
            };
            _letters.TextChanged += delegate { OnLettersChanged(); };
            Controls.Add(_letters);

            var hint = new Label();
            hint.AutoSize = false;
            hint.UseCompatibleTextRendering = true;
            hint.BackColor = Color.Transparent;
            hint.ForeColor = _theme.Muted;
            hint.Font = _theme.SmallFont;
            hint.Text = "one or two, e.g. the project's initials";
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Location = new Point(_letters.Right + _theme.S(10), y);
            hint.Size = new Size(contentWidth - _letters.Width - _theme.S(10), _letters.Height);
            Controls.Add(hint);
            y = _letters.Bottom + _theme.S(12);

            // --- symbols ---
            y = Section("Symbol", x, y);
            var glyphItems = new List<DesktopMark>();
            foreach (char c in MarkPainter.Glyphs) glyphItems.Add(DesktopMark.Glyph(c));
            _glyphs = new MarkGrid(_theme, glyphItems, 8, cellW, cellH, cellGap);
            _glyphs.Location = new Point(x, y);
            _glyphs.Picked += delegate(int i) { SetDraft(_glyphs.Items[i]); };
            Controls.Add(_glyphs);
            y = _glyphs.Bottom + _theme.S(12);

            // --- colours: the ten, then a cell for any other ---
            y = Section("Colour", x, y);
            var colorItems = new List<DesktopMark>();
            foreach (Color c in MarkPainter.Colors) colorItems.Add(DesktopMark.Swatch(c));
            int swatchGap = _theme.S(3);
            int swatch = (contentWidth - 10 * swatchGap) / 11;
            _colors = new MarkGrid(_theme, colorItems, 11, swatch, swatch, swatchGap);
            _colors.HasCustomCell = true;
            _colors.Location = new Point(x, y);
            _colors.Picked += delegate(int i)
            {
                if (i < _colors.Items.Count) SetDraft(_colors.Items[i]);
                else PickCustom();
            };
            Controls.Add(_colors);
            y = _colors.Bottom + _theme.S(12);

            // --- recent ---
            if (recent != null && recent.Count > 0)
            {
                y = Section("Recent", x, y);
                _recent = new MarkGrid(_theme, new List<DesktopMark>(recent), 8, cellW, _theme.S(28), cellGap);
                _recent.Location = new Point(x, y);
                _recent.Picked += delegate(int i) { SetDraft(_recent.Items[i]); };
                Controls.Add(_recent);
                y = _recent.Bottom + _theme.S(12);
            }

            // --- buttons: Clear on its own at the left, Cancel and OK at the right ---
            y += _theme.S(4);
            int bw = _theme.S(80), bh = _theme.S(28), bgap = _theme.S(8);

            Button ok = MakeButton("OK", bw, bh);
            ok.Location = new Point(x + contentWidth - bw, y);
            ok.Click += delegate { DialogResult = DialogResult.OK; };

            Button cancel = MakeButton("Cancel", bw, bh);
            cancel.Location = new Point(ok.Left - bgap - bw, y);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Button clear = MakeButton("Clear", bw, bh);
            clear.Location = new Point(x, y);
            clear.Click += delegate { SetDraft(null); _letters.Focus(); };

            Controls.Add(clear);
            Controls.Add(cancel);
            Controls.Add(ok);

            // Enter commits from anywhere but a grid, which takes Enter to pick a cell; Escape
            // backs out from anywhere.
            AcceptButton = ok;
            CancelButton = cancel;

            ClientSize = new Size(pad + contentWidth + pad, y + bh + _theme.S(14));

            Sync();
            ActiveControl = _letters;
        }

        int Section(string text, int x, int y)
        {
            var label = new Label();
            label.AutoSize = true;
            label.UseCompatibleTextRendering = true;
            label.BackColor = Color.Transparent;
            label.ForeColor = _theme.Secondary;
            label.Font = _theme.SmallFont;
            label.Text = text;
            label.Location = new Point(x - _theme.S(1), y);
            Controls.Add(label);
            return y + label.PreferredHeight + _theme.S(4);
        }

        Button MakeButton(string text, int width, int height)
        {
            var button = new Button();
            button.Text = text;
            button.Size = new Size(width, height);
            button.Font = _theme.BodyFont;
            ConfirmDialog.StyleButton(button, _background);
            return button;
        }

        void OnLettersChanged()
        {
            if (_syncing) return;

            DesktopMark typed = DesktopMark.Letters(_letters.Text);
            if (typed != null) SetDraft(typed);

            // Emptying the box takes the letters away - and only letters: a symbol picked
            // after typing has already emptied it, and must not be undone by that.
            else if (_draft != null && _draft.Kind == MarkKind.Letters) SetDraft(null);
        }

        void SetDraft(DesktopMark mark)
        {
            if (DesktopMark.Same(mark, _draft)) return;
            _draft = mark;
            Sync();
            if (Previewed != null) Previewed(_draft);
        }

        /// <summary>Every control to the draft: one kind lit, the others cleared.</summary>
        void Sync()
        {
            _syncing = true;
            try
            {
                string letters = _draft != null && _draft.Kind == MarkKind.Letters ? _draft.Text : "";
                if (_letters.Text != letters)
                {
                    _letters.Text = letters;
                    _letters.SelectionStart = letters.Length;
                }

                _glyphs.Selected = _draft;
                _colors.Selected = _draft;
                if (_recent != null) _recent.Selected = _draft;

                // A colour that is not one of the ten shows in the custom cell, so the cell
                // says what the desktop has rather than only offering to change it.
                bool custom = _draft != null && _draft.Kind == MarkKind.Color && !_colors.Items.Contains(_draft);
                _colors.CustomColor = custom ? _draft.Color : Color.Empty;
            }
            finally
            {
                _syncing = false;
            }
        }

        void PickCustom()
        {
            using (var dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                if (_draft != null && _draft.Kind == MarkKind.Color) dialog.Color = _draft.Color;
                if (_customColors != null) dialog.CustomColors = _customColors;

                DialogResult result;
                _choosingColor = true;
                try { result = dialog.ShowDialog(this); }
                finally { _choosingColor = false; }
                if (result != DialogResult.OK) return;

                _customColors = dialog.CustomColors;
                SetDraft(DesktopMark.Swatch(dialog.Color));
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Over the button it marks, by the hover panel's placement rule - above the
            // taskbar, or below a taskbar at the top - clamped into the work area. Falls back
            // to the pointer when the strip had no rect to give, which is mid-teardown only.
            Rectangle anchor = _anchor.IsEmpty ? new Rectangle(Cursor.Position, Size.Empty) : _anchor;
            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            bool accentAtTop;
            Location = TooltipWindow.Place(Size, anchor, work, _gap, out accentAtTop);
        }

        /// <summary>
        /// A click anywhere else closes the picker, as Escape does: the preview comes off the
        /// strip and nothing is kept. It sits over the taskbar like the menu it was opened
        /// from, and a borderless panel that stays up after the pointer has gone elsewhere
        /// reads as stuck - it has no title bar to say it is a window that wants an answer.
        /// Cancel rather than OK, because a click away is not a choice: keeping a mark takes
        /// OK or Enter.
        ///
        /// Not while the colour dialog is up. It is modal over this form and takes the
        /// activation from it the moment it opens, which would otherwise read as a click away
        /// and close the picker under it. And not once a result is set: OK and Cancel
        /// deactivate the form on their way out too.
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_choosingColor || DialogResult != DialogResult.None) return;
            DialogResult = DialogResult.Cancel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // A borderless form has no edge of its own; the same one ConfirmDialog draws.
            using (var pen = new Pen(_theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_letters != null && _letters.Font != null) _letters.Font.Dispose();
                _theme.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A grid of marks to pick from - the picker's symbols, colours and Recent row. Each cell
    /// draws its mark through MarkPainter, so it looks as it will on the strip; the one equal
    /// to Selected is outlined in the accent. Click, or arrows and Enter or Space, picks.
    ///
    /// With HasCustomCell, one more cell after the items stands for "any other colour": a
    /// plus, or the custom colour itself once there is one. Picking it raises Picked with
    /// Items.Count, and the picker opens the system colour dialog.
    /// </summary>
    sealed class MarkGrid : ThemedControl
    {
        public readonly List<DesktopMark> Items;
        readonly int _columns, _cellW, _cellH, _gap;
        readonly Font _letters;
        readonly Font _glyph;
        readonly Font _plus;

        DesktopMark _selected;
        Color _customColor;
        int _hot = -1;
        int _focus;

        bool _hasCustom;

        public event Action<int> Picked;

        public bool HasCustomCell
        {
            get { return _hasCustom; }
            set { _hasCustom = value; SizeToCells(); Invalidate(); }
        }

        public MarkGrid(SettingsTheme theme, List<DesktopMark> items, int columns, int cellW, int cellH, int gap)
            : base(theme)
        {
            Items = items;
            _columns = columns;
            _cellW = cellW;
            _cellH = cellH;
            _gap = gap;
            _letters = new Font("Segoe UI Semibold", (float)(13 * theme.Scale), FontStyle.Regular, GraphicsUnit.Pixel);
            _glyph = MarkPainter.GlyphFont((float)(16 * theme.Scale));
            _plus = MarkPainter.GlyphFont((float)(11 * theme.Scale));
            Cursor = Cursors.Hand;
            SizeToCells();
        }

        int CellCount { get { return Items.Count + (_hasCustom ? 1 : 0); } }

        void SizeToCells()
        {
            int rows = (Math.Max(1, CellCount) + _columns - 1) / _columns;
            Size = new Size(_columns * _cellW + (_columns - 1) * _gap, rows * _cellH + (rows - 1) * _gap);
        }

        public DesktopMark Selected
        {
            get { return _selected; }
            set { if (DesktopMark.Same(_selected, value)) return; _selected = value; Invalidate(); }
        }

        public Color CustomColor
        {
            get { return _customColor; }
            set { if (_customColor == value) return; _customColor = value; Invalidate(); }
        }

        Rectangle Cell(int i)
        {
            int col = i % _columns, row = i / _columns;
            return new Rectangle(col * (_cellW + _gap), row * (_cellH + _gap), _cellW, _cellH);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < CellCount; i++)
                if (Cell(i).Contains(p)) return i;
            return -1;
        }

        void Pick(int i)
        {
            if (i < 0 || i >= CellCount) return;
            _focus = i;
            if (Picked != null) Picked(i);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hot = HitTest(e.Location);
            if (hot != _hot) { _hot = hot; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Pick(HitTest(e.Location));
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Enter: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int n = CellCount;
            int moved = _focus;
            switch (e.KeyCode)
            {
                case Keys.Left:  moved = Math.Max(0, _focus - 1); break;
                case Keys.Right: moved = Math.Min(n - 1, _focus + 1); break;
                case Keys.Up:    if (_focus - _columns >= 0) moved = _focus - _columns; break;
                case Keys.Down:  if (_focus + _columns < n) moved = _focus + _columns; break;
                case Keys.Enter:
                case Keys.Space: Pick(_focus); e.Handled = true; e.SuppressKeyPress = true; return;
                default: base.OnKeyDown(e); return;
            }
            e.Handled = true;
            if (moved != _focus) { _focus = moved; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float radius = Theme.S(4);

            for (int i = 0; i < CellCount; i++)
            {
                Rectangle r = Cell(i);
                var box = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
                bool custom = i >= Items.Count;
                DesktopMark mark = custom ? null : Items[i];
                bool selected = custom ? !_customColor.IsEmpty : DesktopMark.Same(mark, _selected);

                using (GraphicsPath path = SettingsTheme.Rounded(box, radius))
                {
                    using (var fill = new SolidBrush(i == _hot ? Theme.FieldHot : Theme.Field))
                        g.FillPath(fill, path);

                    if (selected)
                        using (var pen = new Pen(Theme.Accent, Theme.S(2)))
                            g.DrawPath(pen, path);
                    else if (ShowFocus && i == _focus)
                        using (var pen = new Pen(Theme.Primary, 1f))
                            g.DrawPath(pen, path);
                }

                if (!custom)
                {
                    // A colour cell is the colour, edge to edge less a margin, rather than the
                    // small square the strip draws - in here it is a swatch to aim at.
                    if (mark.Kind == MarkKind.Color)
                    {
                        float m = Theme.S(4);
                        var inner = new RectangleF(box.X + m, box.Y + m, box.Width - 2 * m, box.Height - 2 * m);
                        using (var brush = new SolidBrush(mark.Color))
                        using (GraphicsPath path = SettingsTheme.Rounded(inner, Theme.S(3)))
                            g.FillPath(brush, path);
                    }
                    else
                    {
                        MarkPainter.Draw(g, mark, box, Theme.Primary, _letters, _glyph, 1f);
                    }
                    continue;
                }

                // The custom cell: the custom colour once there is one, a plus either way.
                if (!_customColor.IsEmpty)
                {
                    float m = Theme.S(4);
                    var inner = new RectangleF(box.X + m, box.Y + m, box.Width - 2 * m, box.Height - 2 * m);
                    using (var brush = new SolidBrush(_customColor))
                    using (GraphicsPath path = SettingsTheme.Rounded(inner, Theme.S(3)))
                        g.FillPath(brush, path);
                }
                if (_plus != null)
                {
                    Color ink = _customColor.IsEmpty ? Theme.Secondary
                              : (Palette.IsLight(_customColor) ? Color.FromArgb(30, 30, 30) : Color.White);
                    using (var brush = new SolidBrush(ink))
                    using (var format = new StringFormat())
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        g.DrawString("\uE710", _plus, brush, box, format);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _letters.Dispose();
                if (_glyph != null) _glyph.Dispose();
                if (_plus != null) _plus.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
