using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// What a tooltip should say. Built by the controller, which is the only type holding
    /// the desktop model, the window inventory and the foreground tracker together; the
    /// strip supplies geometry and knows nothing about the text.
    /// </summary>
    sealed class TooltipContent
    {
        readonly string _title;
        readonly IList<string> _lines;

        public TooltipContent(string title, IList<string> lines)
        {
            _title = title;
            _lines = lines != null ? lines : new List<string>();
        }

        public string Title { get { return _title; } }

        /// <summary>Window titles, or a single "empty" marker.</summary>
        public IList<string> Lines { get { return _lines; } }
    }

    /// <summary>
    /// The hover panel.
    ///
    /// A top-level popup, NOT a child of the strip or the taskbar: a child is clipped to
    /// its parent, and the taskbar is exactly as tall as the strip, so a child tooltip
    /// would be invisible.
    ///
    /// It must never take activation. WS_EX_NOACTIVATE plus SWP_NOACTIVATE keep focus
    /// where it is - a tooltip that stole focus would break click handling and, worse,
    /// overwrite the window ForegroundTracker is holding, which is the whole basis of
    /// right-click-to-send. WS_EX_TRANSPARENT additionally makes it click-through, which
    /// is what lets the strip own a plain show/hide state machine with no hand-off.
    /// </summary>
    sealed class TooltipWindow : NativeWindow, IDisposable
    {
        /// <summary>One line of the panel. Measure and Render walk the same list, so they cannot disagree.</summary>
        struct Row
        {
            public string Text;
            public Font Font;
            public int Tone;        // grey level, 0-255
        }

        readonly Color _background;
        readonly Color _highlight;
        readonly double _scale;

        readonly Font _titleFont;
        readonly Font _bodyFont;

        readonly int _padX, _padY, _accent, _maxWidth;

        Bitmap _buffer;
        List<Row> _rows = new List<Row>();
        Rectangle _anchor;      // the hovered button, in screen coordinates
        bool _accentAtTop;      // panel sits below the anchor, so the accent flips
        bool _visible;
        bool _disposed;

        public TooltipWindow(Color background, Color highlight, double dpiScale)
        {
            _background = background;
            _highlight = highlight;
            _scale = dpiScale;

            _padX = Scale(12);
            _padY = Scale(9);
            _accent = Scale(3);
            // Wide enough that the app name leading each row is not paid for out of the
            // title's characters.
            _maxWidth = Scale(440);

            float body = (float)(12 * dpiScale);
            _titleFont = new Font("Segoe UI", body, FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", body, FontStyle.Regular, GraphicsUnit.Pixel);

            var cp = new CreateParams();
            cp.Caption = "DesktopSwitcherTooltip";
            cp.X = 0;
            cp.Y = 0;
            cp.Width = 10;
            cp.Height = 10;
            cp.Parent = IntPtr.Zero;
            // No WS_VISIBLE: it is shown explicitly, already sized and positioned, so it
            // never flashes at the wrong place first.
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
                       | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            CreateHandle(cp);
        }

        public bool IsVisible { get { return _visible; } }

        int Scale(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        // --- show / hide ------------------------------------------------------

        /// <summary>
        /// Sizes the panel to its content, places it clear of the anchor, and shows it
        /// without activating. Safe to call while already visible - that is how the
        /// panel follows the pointer between buttons without re-arming the delay.
        /// </summary>
        public void Show(TooltipContent content, Rectangle anchorScreenRect)
        {
            if (_disposed || content == null) return;

            _anchor = anchorScreenRect;

            Size size;
            try
            {
                _rows = BuildRows(content, out size);
            }
            catch (Exception ex)
            {
                Log.Write("tooltip: layout failed - " + ex.Message);
                return;
            }

            Point origin = Place(size, anchorScreenRect);

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST,
                origin.X, origin.Y, size.Width, size.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            _visible = true;
            Native.InvalidateRect(Handle, IntPtr.Zero, false);
        }

        public void Hide()
        {
            if (_disposed || !_visible) return;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE
                | Native.SWP_HIDEWINDOW);

            _visible = false;
        }

        /// <summary>
        /// Above the anchor when there is room, otherwise below, clamped into the
        /// monitor's work area.
        ///
        /// That one rule covers every taskbar edge without special-casing: the work area
        /// excludes the taskbar, so "above" lands on-screen for a bottom taskbar, fails
        /// the fit test for a top one and falls through to "below", and for a side-docked
        /// taskbar the horizontal clamp pushes the panel clear of the bar.
        /// </summary>
        Point Place(Size size, Rectangle anchor)
        {
            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            int gap = Scale(4);

            int y = anchor.Top - size.Height - gap;
            _accentAtTop = false;
            if (y < work.Top)
            {
                y = anchor.Bottom + gap;
                _accentAtTop = true;
            }
            if (y + size.Height > work.Bottom) y = work.Bottom - size.Height;
            if (y < work.Top) y = work.Top;

            // Centred on the button, so the accent bar lines up under it.
            int x = anchor.Left + anchor.Width / 2 - size.Width / 2;
            if (x + size.Width > work.Right) x = work.Right - size.Width;
            if (x < work.Left) x = work.Left;

            return new Point(x, y);
        }

        // --- layout -----------------------------------------------------------

        List<Row> BuildRows(TooltipContent content, out Size size)
        {
            var rows = new List<Row>();

            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Add(rows, g, content.Title, _titleFont, 255);

                foreach (string line in content.Lines)
                    Add(rows, g, line, _bodyFont, 205);

                size = Measure(g, rows);
            }

            return rows;
        }

        void Add(List<Row> rows, Graphics g, string text, Font font, int tone)
        {
            var row = new Row();
            row.Text = Fit(g, text, font, _maxWidth - _padX * 2);
            row.Font = font;
            row.Tone = tone;
            rows.Add(row);
        }

        Size Measure(Graphics g, List<Row> rows)
        {
            int width = 0;
            int height = _padY * 2 + _accent;

            for (int i = 0; i < rows.Count; i++)
            {
                SizeF s = g.MeasureString(rows[i].Text, rows[i].Font);
                int w = (int)Math.Ceiling(s.Width);
                if (w > width) width = w;
                height += LineHeight(rows[i].Font);
            }

            return new Size(width + _padX * 2, height);
        }

        int LineHeight(Font font)
        {
            return (int)Math.Ceiling(font.GetHeight()) + Scale(3);
        }

        /// <summary>Trims a title to fit, with an ellipsis. Window titles run long.</summary>
        static string Fit(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, font).Width <= maxWidth) return text;

            string s = text;
            while (s.Length > 4 && g.MeasureString(s + "...", font).Width > maxWidth)
                s = s.Substring(0, s.Length - 4);

            return s + "...";
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
                Log.Write("tooltip: paint failed - " + ex.Message);
            }
            finally
            {
                Native.EndPaint(Handle, ref ps);
            }
        }

        void Render(Graphics g, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Borderless, in the taskbar's own sampled colour, lifted just enough to
            // separate the panel from the bar it grows out of.
            using (var back = new SolidBrush(Lighten(_background, 0.06f)))
                g.FillRectangle(back, 0, 0, width, height);

            DrawAccent(g, width, height);

            int y = _padY + (_accentAtTop ? _accent : 0);

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];

                using (var brush = new SolidBrush(Color.FromArgb(row.Tone, row.Tone, row.Tone)))
                    g.DrawString(row.Text, row.Font, brush, _padX, y);

                y += LineHeight(row.Font);
            }
        }

        /// <summary>
        /// A short bar on the edge facing the strip, aligned with the hovered button, so
        /// the panel reads as belonging to that button rather than floating loose.
        /// </summary>
        void DrawAccent(Graphics g, int width, int height)
        {
            Native.RECT client;
            if (!Native.GetWindowRect(Handle, out client)) return;

            int barWidth = _anchor.Width > 0 ? _anchor.Width : Scale(24);
            int centre = _anchor.Left + _anchor.Width / 2 - client.Left;

            int x = centre - barWidth / 2;
            if (x < 0) x = 0;
            if (x + barWidth > width) x = width - barWidth;

            int y = _accentAtTop ? 0 : height - _accent;

            using (var brush = new SolidBrush(_highlight))
                g.FillRectangle(brush, x, y, barWidth, _accent);
        }

        static Color Lighten(Color color, float amount)
        {
            int r = color.R + (int)((255 - color.R) * amount);
            int g = color.G + (int)((255 - color.G) * amount);
            int b = color.B + (int)((255 - color.B) * amount);
            return Color.FromArgb(Clamp(r), Clamp(g), Clamp(b));
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        // --- window procedure --------------------------------------------------

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;

                case Native.WM_ERASEBKGND:
                    m.Result = new IntPtr(1);
                    return;
            }

            base.WndProc(ref m);
        }

        // --- lifetime ---------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;

            // Hidden before _disposed is set, or Hide() would bail out on its own guard
            // and leave a frame of this on screen as an orphaned topmost window - the
            // worst failure mode this class has, and the one an Explorer restart hits.
            Hide();
            _disposed = true;

            if (Handle != IntPtr.Zero) DestroyHandle();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            _titleFont.Dispose();
            _bodyFont.Dispose();
        }
    }
}
