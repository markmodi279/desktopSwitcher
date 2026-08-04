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
        /// <summary>Row text, and what a row that cannot be clicked fades to.</summary>
        static readonly Color EnabledText = Grey(225);
        static readonly Color DisabledText = Grey(130);

        readonly Font _font;
        readonly Color _surface;
        readonly ToolStripRenderer _renderer;

        public MenuTheme(Color background, double dpiScale)
        {
            // The panel's own lift, so a menu and a hover panel over the same taskbar are
            // the same colour rather than two guesses at it.
            _surface = Lighten(background, 0.06f);

            _renderer = new Renderer(new Colors(background, _surface));

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
            menu.ForeColor = EnabledText;
            menu.Font = _font;

            // Nothing here has an icon, and the margin would just be a lighter gutter down
            // the left edge of an otherwise flat panel.
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
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

                // Far enough above the surface to be unmistakable while the pointer moves
                // down the list - a menu selection has to be readable at a glance, unlike
                // the strip's hover, which only ever confirms where the pointer already is.
                _selected = Lighten(background, 0.20f);
                _border = Lighten(background, 0.24f);
                _separator = Lighten(background, 0.14f);
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
            public Renderer(ProfessionalColorTable colors)
                : base(colors)
            {
                // Square, like the strip and the panel.
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? EnabledText : DisabledText;
                base.OnRenderItemText(e);
            }
        }

        // --- colour arithmetic -------------------------------------------------

        static Color Grey(int tone)
        {
            return Color.FromArgb(tone, tone, tone);
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
    }
}
