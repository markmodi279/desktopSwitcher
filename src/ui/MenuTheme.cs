using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// Dresses a ContextMenuStrip to match the strip and the hover panel.
    ///
    /// WinForms draws menus in its own light "Professional" palette, which would look
    /// borrowed from another program hanging off a dark taskbar. Everything here is derived
    /// from the taskbar colour the strip already sampled, so the menu, the strip and the
    /// panel are lit from the same source whatever the user's taskbar is set to.
    ///
    /// An instance, not a static helper, because it owns a font: the menu is rebuilt on
    /// every right click - its contents depend on the model - and creating a font per click
    /// would leak one per click.
    /// </summary>
    sealed class MenuTheme : IDisposable
    {
        /// <summary>
        /// Row text, and what a row that cannot be clicked fades to.
        ///
        /// Instance state rather than the two constants these used to be: the tones depend
        /// on the taskbar colour, which is not known until a theme is constructed. A menu
        /// over a light taskbar needs dark rows, and the constants only ever went lighter.
        /// </summary>
        readonly Color _text;
        readonly Color _disabledText;

        readonly Font _font;
        readonly Color _surface;
        readonly Color _field;
        readonly double _scale;
        readonly ToolStripRenderer _renderer;

        public MenuTheme(Color background, double dpiScale)
        {
            _scale = dpiScale;

            _text = Palette.BodyText(background);
            _disabledText = Palette.MutedText(background);

            // The panel's own lift, so a menu and a hover panel over the same taskbar are
            // the same colour rather than two guesses at it.
            _surface = Palette.Lift(background, 0.06f);

            // Enough clear of the surface to read as a well you can type into, and less
            // than the selection colour, so a highlighted row still wins over the box
            // inside it.
            _field = Palette.Lift(background, 0.14f);

            _renderer = new Renderer(new Colors(background, _surface), _text, _disabledText);

            // Sized in pixels off the DPI scale, exactly as the hover panel does it:
            // the process is per-monitor DPI aware, so WinForms will not scale a menu font
            // for us and a default one comes out tiny on a scaled display.
            _font = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Regular,
                             GraphicsUnit.Pixel);
        }

        public void Apply(ContextMenuStrip menu)
        {
            menu.RenderMode = ToolStripRenderMode.Professional;
            menu.Renderer = _renderer;
            menu.BackColor = _surface;
            menu.ForeColor = _text;
            menu.Font = _font;

            // Nothing here has an icon, and the margin would just be a lighter gutter down
            // the left edge of an otherwise flat panel.
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;

            // After the menu-wide colours, never before: setting those propagates to items
            // and would undo anything a hosted control had already been given.
            foreach (ToolStripItem item in menu.Items)
            {
                var box = item as ToolStripTextBox;
                if (box != null) ApplyIdle(box);
            }
        }

        /// <summary>
        /// An editable row at rest, reading as a menu row rather than a form field: the
        /// name is there to be seen, and only worth framing once it is being typed into.
        ///
        /// A hosted control paints itself, which puts it outside everything the renderer
        /// and the colour table can reach - left alone it comes out as a white box in a
        /// dark menu, the one thing in the whole strip that looks borrowed.
        /// </summary>
        public void ApplyIdle(ToolStripTextBox box)
        {
            box.ForeColor = _text;
            box.Font = _font;

            // Never toggled once the box is live. Changing BorderStyle recreates the
            // control's window, and a recreate in the middle of taking focus is how you
            // lose the caret you just asked for - so the two states differ by fill alone.
            box.BorderStyle = BorderStyle.None;

            // Menus size themselves to their captions, and a hosted control is not a
            // caption: without a width of its own the box comes out at the WinForms
            // default, which is neither the menu's width nor scaled for the display.
            // AutoSize first, or the layout recomputes the width straight back.
            box.AutoSize = false;
            box.Width = (int)Math.Round(180 * _scale);

            box.BackColor = _surface;
        }

        /// <summary>The same row once it has the caret: a well, lit enough to look typed into.</summary>
        public void ApplyEditing(ToolStripTextBox box)
        {
            box.BackColor = _field;
        }

        public void Dispose()
        {
            _font.Dispose();
        }

        // --- palette ----------------------------------------------------------

        /// <summary>
        /// Every colour the professional renderer asks for, answered from the taskbar
        /// colour. The ones left to the base class are for toolbars and menu bars, which a
        /// context menu never draws.
        /// </summary>
        sealed class Colors : ProfessionalColorTable
        {
            readonly Color _surface;
            readonly Color _selected;
            readonly Color _border;
            readonly Color _separator;

            public Colors(Color background, Color surface)
            {
                _surface = surface;

                // Far enough clear of the surface to be unmistakable while the pointer moves
                // down the list - a menu selection has to be readable at a glance, unlike
                // the strip's hover, which only ever confirms where the pointer already is.
                _selected = Palette.Lift(background, 0.20f);
                _border = Palette.Lift(background, 0.24f);
                _separator = Palette.Lift(background, 0.14f);
            }

            public override Color ToolStripDropDownBackground { get { return _surface; } }
            public override Color MenuBorder { get { return _border; } }

            public override Color MenuItemSelected { get { return _selected; } }
            public override Color MenuItemSelectedGradientBegin { get { return _selected; } }
            public override Color MenuItemSelectedGradientEnd { get { return _selected; } }
            public override Color MenuItemBorder { get { return _selected; } }
            public override Color MenuItemPressedGradientBegin { get { return _selected; } }
            public override Color MenuItemPressedGradientMiddle { get { return _selected; } }
            public override Color MenuItemPressedGradientEnd { get { return _selected; } }

            // The image margin is off, but these still paint if a menu ever turns it back on.
            public override Color ImageMarginGradientBegin { get { return _surface; } }
            public override Color ImageMarginGradientMiddle { get { return _surface; } }
            public override Color ImageMarginGradientEnd { get { return _surface; } }

            public override Color SeparatorDark { get { return _separator; } }
            public override Color SeparatorLight { get { return _separator; } }
        }

        /// <summary>
        /// The palette handles every fill. Text is the one thing the colour table cannot
        /// reach, and it is what carries the disabled state: a greyed row here means the
        /// action is impossible right now - nothing to send, or nowhere to send it - which
        /// is information, so it has to stay legible rather than fade out.
        /// </summary>
        sealed class Renderer : ToolStripProfessionalRenderer
        {
            // Handed in rather than read off the enclosing instance: this is a nested type,
            // not an inner one, so it has no enclosing instance to read.
            readonly Color _enabled;
            readonly Color _disabled;

            public Renderer(ProfessionalColorTable colors, Color enabled, Color disabled)
                : base(colors)
            {
                _enabled = enabled;
                _disabled = disabled;

                // Square, like the strip and the panel.
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? _enabled : _disabled;
                base.OnRenderItemText(e);
            }
        }
    }
}
