using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// The one confirmation the app ever asks for: removing a desktop that still has
    /// windows on it. Themed off the taskbar colour like the strip, the hover panel and
    /// the context menu - a stock MessageBox would be the one thing on screen that looks
    /// borrowed from another program.
    ///
    /// A real Form, not a NativeWindow like TooltipWindow. Everything else here is
    /// deliberately non-activating so it cannot interfere with the strip or steal the
    /// window ForegroundTracker is holding; this is the one surface that is supposed to
    /// take focus and block input, which is exactly what ShowDialog gives for free.
    /// </summary>
    sealed class ConfirmDialog : Form
    {
        readonly Color _background;
        readonly Rectangle _anchor;
        readonly int _gap;
        Button _remove;
        Button _cancel;

        public ConfirmDialog(string desktopName, int windowCount, Color background,
                             double dpiScale, Rectangle anchor)
        {
            _background = background;
            _anchor = anchor;
            _gap = Scale(4, dpiScale);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            // Same surface tone MenuTheme gives a ContextMenuStrip over this background,
            // so the dialog, the menu and the hover panel all read as one family.
            BackColor = Palette.Lift(background, 0.06f);

            int scale4 = Scale(4, dpiScale);
            Padding = new Padding(Scale(16, dpiScale), Scale(14, dpiScale),
                                  Scale(16, dpiScale), Scale(14, dpiScale));

            Font titleFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Bold,
                                      GraphicsUnit.Pixel);
            Font bodyFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Regular,
                                     GraphicsUnit.Pixel);

            int buttonWidth = Scale(88, dpiScale);
            int buttonHeight = Scale(28, dpiScale);
            int buttonGap = Scale(8, dpiScale);

            string titleText = "Remove \"" + desktopName + "\"?";
            string bodyText = windowCount == 1
                ? "1 window will move to the next desktop."
                : windowCount + " windows will move to the next desktop.";

            // Measured, not guessed: Label's own AutoSize does not reliably grow its
            // Height to match wrapped text, which is what let the second line paint over
            // the title above it. TooltipWindow.BuildRows already solves this the same
            // way - Graphics.MeasureString against a scratch bitmap - so this reuses it
            // rather than trusting WinForms layout for text it has already shown it gets
            // wrong here.
            //
            // Sized to whichever line is naturally longest, clamped between "wide enough
            // for two buttons" and a ceiling that keeps a long custom desktop name from
            // making the dialog enormous - only past that ceiling does the sentence
            // actually wrap, rather than wrapping on every ordinary two-or-three-digit
            // window count the way a fixed narrow width would.
            int minWidth = buttonWidth * 2 + buttonGap;
            int maxWidth = Scale(360, dpiScale);
            int contentWidth;
            Size titleSize, bodySize;

            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                SizeF titleNatural = g.MeasureString(titleText, titleFont);
                SizeF bodyNatural = g.MeasureString(bodyText, bodyFont);

                int natural = (int)Math.Ceiling(Math.Max(titleNatural.Width, bodyNatural.Width));
                contentWidth = Math.Max(minWidth, Math.Min(natural, maxWidth));

                titleSize = Ceiling(g.MeasureString(titleText, titleFont, contentWidth));
                bodySize = Ceiling(g.MeasureString(bodyText, bodyFont, contentWidth));
            }

            var title = new Label();
            title.AutoSize = false;
            title.UseCompatibleTextRendering = true;   // paint with the same GDI+ engine just measured with
            title.BackColor = Color.Transparent;
            title.ForeColor = Palette.PrimaryText(background);
            title.Font = titleFont;
            title.Text = titleText;
            title.Location = new Point(Padding.Left, Padding.Top);
            title.Size = titleSize;

            var body = new Label();
            body.AutoSize = false;
            body.UseCompatibleTextRendering = true;
            body.BackColor = Color.Transparent;
            body.ForeColor = Palette.BodyText(background);
            body.Font = bodyFont;
            body.Text = bodyText;
            body.Location = new Point(Padding.Left, title.Bottom + scale4);
            body.Size = bodySize;

            int buttonsTop = body.Bottom + Scale(16, dpiScale);

            _remove = MakeButton("Remove", DialogResult.OK, background, buttonWidth, buttonHeight, bodyFont);
            _cancel = MakeButton("Cancel", DialogResult.Cancel, background, buttonWidth, buttonHeight, bodyFont);

            int clientWidth = Padding.Left + contentWidth + Padding.Right;

            _cancel.Location = new Point(clientWidth - Padding.Right - buttonWidth, buttonsTop);
            _remove.Location = new Point(_cancel.Left - buttonGap - buttonWidth, buttonsTop);

            int clientHeight = buttonsTop + buttonHeight + Padding.Bottom;

            ClientSize = new Size(clientWidth, clientHeight);

            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(_remove);
            Controls.Add(_cancel);

            // Cancel is the safer default: a focused Button fires on Enter whether or
            // not it is the form's AcceptButton, so this is what a stray Enter reaches.
            // Remove is one Tab or a click away, never a single keystroke away.
            CancelButton = _cancel;
            ActiveControl = _cancel;
        }

        Button MakeButton(string text, DialogResult result, Color background,
                          int width, int height, Font font)
        {
            var button = new Button();
            button.Text = text;
            button.DialogResult = result;
            button.Size = new Size(width, height);
            button.Font = font;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Palette.Lift(background, 0.24f);
            button.FlatAppearance.MouseOverBackColor = Palette.Lift(background, 0.14f);
            button.FlatAppearance.MouseDownBackColor = Palette.Lift(background, 0.20f);
            button.BackColor = Palette.Lift(background, 0.06f);
            button.ForeColor = Palette.BodyText(background);
            button.UseVisualStyleBackColor = false;
            return button;
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

            // The same placement rule TooltipWindow.Place uses for the hover panel: above
            // the strip when there is room, below it otherwise, clamped into the work
            // area, centered on the anchor's width - which here is the whole strip, not
            // one button, since a modal dialog has no pointer to follow along it.
            //
            // Falls back to the pointer's own position if the strip had no window rect to
            // anchor to, which only happens mid-teardown (Explorer restarting under a
            // click). Done in OnLoad rather than the constructor: Size is not final until
            // layout has run.
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

            // The one piece of chrome a borderless form has none of otherwise: without
            // it the dialog has no edge at all against whatever is behind it. Derived
            // from the original taskbar colour, not the already-lifted surface, so this
            // is the same border tone MenuTheme gives a ContextMenuStrip.
            using (var pen = new Pen(Palette.Lift(_background, 0.24f)))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
}
