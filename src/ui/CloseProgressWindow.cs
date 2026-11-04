using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// The live panel behind "Close all and remove": every window on the desktop, ticked
    /// off as it goes, with whatever is holding things up named as it happens.
    ///
    /// Replaces a fixed wait followed by a report. There is nothing worth waiting a set
    /// time for - a window that closes is gone in a fraction of a second, and one with a
    /// save prompt shows it about as fast - so the panel opens at once and the buttons work
    /// from the first frame. The desktop goes the moment the list is empty; this window
    /// only shows that happening. CloseAllRun decides everything.
    ///
    /// Modeless, unlike ConfirmDialog, because the user has to be free to answer the save
    /// prompts while it is up. TopMost so a prompt does not bury it; anchored to the strip,
    /// at the screen's edge, so it does not sit on the prompt either - those open centred.
    ///
    /// On every desktop at once, as a tool window. An ordinary window belongs to the
    /// desktop it opened on, and the run's whole business is with another one: Go there
    /// switches away from it, and an app that grabs focus to ask its question can switch
    /// the user without asking. Either way the panel used to stay behind, and the run
    /// carried on out of sight.
    /// </summary>
    sealed class CloseProgressWindow : Form
    {
        /// <summary>Rows shown before the rest collapse into "+N more".</summary>
        const int MaxRows = 10;

        // Status marks, escaped to keep the source ASCII - see Controller.Bullet.
        const string MarkClosed = "\u2713";     // check mark
        const string MarkPrompting = "!";
        const string MarkClosing = "\u2026";    // ellipsis
        const string MarkRefused = "\u2715";    // multiplication x

        // The panel's only colours not derived from the taskbar: state, which has to read
        // the same over a light or a dark bar. The amber and red are the stats panel's
        // warning tones in spirit; the green is the one this app had no use for before.
        static readonly Color Good = Color.FromArgb(0x5F, 0xC2, 0x6B);
        static readonly Color Waiting = Color.FromArgb(0xE8, 0xA8, 0x2E);
        static readonly Color Bad = Color.FromArgb(0xE0, 0x5A, 0x5A);

        public event EventHandler GoRequested;
        public event EventHandler ForceRequested;
        public event EventHandler CancelRequested;

        readonly Color _background;
        readonly Rectangle _anchor;
        readonly double _scale;
        readonly string _title;

        readonly Font _titleFont;
        readonly Font _bodyFont;
        readonly Button _go;
        readonly Button _force;
        readonly Button _cancel;

        readonly int _pad, _rowHeight, _buttonHeight, _gap, _width;

        IList<string> _texts = new string[0];
        IList<CloseState> _states = new CloseState[0];
        string _status = "";
        string _hint = "";

        /// <summary>Set by Finish, so a close the run asked for is not reported back to it
        /// as the user cancelling.</summary>
        bool _finishing;

        /// <summary>Set once the window is on its way out by any route, so a Finish
        /// arriving from inside that close does not start a second one.</summary>
        bool _closing;

        public CloseProgressWindow(string title, Color background, double dpiScale, Rectangle anchor)
        {
            _title = title;
            _background = background;
            _anchor = anchor;
            _scale = dpiScale;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Palette.Lift(background, 0.06f);
            Text = title;

            _pad = Scale(16);
            _gap = Scale(8);
            _rowHeight = Scale(22);
            _buttonHeight = Scale(28);
            _width = Scale(400);

            _titleFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);

            _go = MakeButton("Go there");
            _force = MakeButton("Force close");
            _cancel = MakeButton("Cancel");

            _go.Click += delegate { Raise(GoRequested); };
            _force.Click += delegate { Raise(ForceRequested); };
            _cancel.Click += delegate { Raise(CancelRequested); };

            // Escape is Cancel, which stops the run and leaves everything as it stands -
            // the one answer here that does nothing irreversible.
            CancelButton = _cancel;
            ActiveControl = _cancel;

            Arrange();
        }

        Button MakeButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Font = _bodyFont;
            // A scratch surface, not CreateGraphics: that would create the window's handle
            // from inside the constructor, before it has a size or a place.
            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                int textWidth = TextRenderer.MeasureText(g, text, _bodyFont).Width;
                button.Size = new Size(Math.Max(Scale(88), textWidth + Scale(24)), Scale(28));
            }
            ConfirmDialog.StyleButton(button, _background);
            Controls.Add(button);
            return button;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry, and - the reason it
                // is here - no virtual desktop of its own, so it shows on every one.
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        int Scale(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        /// <summary>
        /// Everything the panel shows, replaced whole on every tick of the run. Rows keep
        /// their order - each window stays on its own line and changes in place - so the
        /// eye can follow one down the list rather than chasing it as others close.
        /// </summary>
        public void SetRows(IList<string> texts, IList<CloseState> states, string status,
                            string hint, bool canGo, bool canForce)
        {
            bool grew = VisibleRows(texts.Count) != VisibleRows(_texts.Count);

            _texts = texts;
            _states = states;
            _status = status;
            _hint = hint;

            _go.Enabled = canGo;
            _force.Enabled = canForce;

            if (grew) Arrange();
            Invalidate();
        }

        static int VisibleRows(int count)
        {
            // Past the cap, the last line becomes "+N more" rather than another window.
            return Math.Min(count, MaxRows + 1);
        }

        /// <summary>Title, status, the rows, the hint, the buttons - top to bottom.</summary>
        void Arrange()
        {
            int rows = VisibleRows(_texts.Count);
            int buttonsTop = RowsTop + rows * _rowHeight + _gap + _rowHeight + _gap;

            int right = _width - _pad;
            Button[] order = { _cancel, _force, _go };
            foreach (Button b in order)
            {
                b.Location = new Point(right - b.Width, buttonsTop);
                right = b.Left - _gap;
            }

            ClientSize = new Size(_width, buttonsTop + _buttonHeight + _pad);
            if (IsHandleCreated) Place();
        }

        int TitleTop { get { return Scale(14); } }
        int StatusTop { get { return TitleTop + Scale(20); } }
        int RowsTop { get { return StatusTop + Scale(26); } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Place();
        }

        /// <summary>ConfirmDialog's placement: by the strip, inside the work area.</summary>
        void Place()
        {
            Rectangle anchor = _anchor.IsEmpty
                ? new Rectangle(Cursor.Position, Size.Empty)
                : _anchor;

            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            bool accentAtTop;
            Location = TooltipWindow.Place(Size, anchor, work, Scale(4), out accentAtTop);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            using (var pen = new Pen(Palette.Lift(_background, 0.24f)))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            int inner = _width - _pad * 2;
            const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                                         | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

            TextRenderer.DrawText(g, _title, _titleFont,
                new Rectangle(_pad, TitleTop, inner, Scale(20)), Palette.PrimaryText(_background), Line);
            TextRenderer.DrawText(g, _status, _bodyFont,
                new Rectangle(_pad, StatusTop, inner, Scale(20)), Palette.BodyText(_background), Line);

            int markWidth = Scale(20);
            int labelWidth = Scale(110);
            int y = RowsTop;
            int shown = Math.Min(_texts.Count, MaxRows);

            for (int i = 0; i < shown; i++)
            {
                CloseState state = _states[i];

                TextRenderer.DrawText(g, Mark(state), _bodyFont,
                    new Rectangle(_pad, y, markWidth, _rowHeight), MarkColor(state), Line);

                // A closed row steps back, so what is left stands out without moving.
                Color text = state == CloseState.Closed
                    ? Palette.MutedText(_background)
                    : Palette.BodyText(_background);
                TextRenderer.DrawText(g, _texts[i], _bodyFont,
                    new Rectangle(_pad + markWidth, y, inner - markWidth - labelWidth, _rowHeight), text, Line);

                string label = Label(state);
                if (label.Length > 0)
                    TextRenderer.DrawText(g, label, _bodyFont,
                        new Rectangle(_width - _pad - labelWidth, y, labelWidth, _rowHeight),
                        state == CloseState.Closing ? Palette.SecondaryText(_background) : MarkColor(state),
                        Line | TextFormatFlags.Right);

                y += _rowHeight;
            }

            if (_texts.Count > MaxRows)
            {
                int open = 0;
                for (int i = MaxRows; i < _states.Count; i++)
                    if (_states[i] != CloseState.Closed) open++;

                string more = "+" + (_texts.Count - MaxRows) + " more"
                              + (open > 0 ? ", " + open + " still open" : ", all closed");
                TextRenderer.DrawText(g, more, _bodyFont,
                    new Rectangle(_pad + markWidth, y, inner - markWidth, _rowHeight),
                    Palette.SecondaryText(_background), Line);
                y += _rowHeight;
            }

            TextRenderer.DrawText(g, _hint, _bodyFont,
                new Rectangle(_pad, y + _gap, inner, _rowHeight), Palette.MutedText(_background), Line);
        }

        static string Mark(CloseState state)
        {
            switch (state)
            {
                case CloseState.Closed: return MarkClosed;
                case CloseState.Prompting:
                case CloseState.StillOpen:
                case CloseState.KeptOpen: return MarkPrompting;
                case CloseState.Refused: return MarkRefused;
                default: return MarkClosing;
            }
        }

        Color MarkColor(CloseState state)
        {
            switch (state)
            {
                case CloseState.Closed: return Good;
                case CloseState.Prompting:
                case CloseState.StillOpen:
                case CloseState.KeptOpen: return Waiting;
                case CloseState.Refused: return Bad;
                default: return Palette.SecondaryText(_background);
            }
        }

        static string Label(CloseState state)
        {
            switch (state)
            {
                case CloseState.Closing: return "closing";
                case CloseState.Prompting: return "asking to save";
                case CloseState.StillOpen: return "still open";
                case CloseState.KeptOpen: return "kept open";
                case CloseState.Refused: return "can't close";
                default: return "";
            }
        }

        /// <summary>Closes the panel on the run's behalf, without it counting as Cancel.</summary>
        public void Finish()
        {
            _finishing = true;
            if (!_closing && !IsDisposed) Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;

            // Alt+F4 means what Cancel means.
            if (!_finishing)
            {
                _finishing = true;
                Raise(CancelRequested);
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _bodyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
