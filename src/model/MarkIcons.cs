using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// The picker's symbols: icons from Lucide (lucide.dev), drawn from their own outlines
    /// with a thicker stroke than Lucide's, rather than characters from a font.
    ///
    /// They replaced 32 Segoe MDL2 Assets glyphs. Those were well chosen but thin: MDL2 is
    /// drawn for 16 px chrome with hairline strokes, and beside a full-size number on the
    /// strip a mark read as a faint outline. Four directions were mocked at strip size on a
    /// dark and a light taskbar before this one was picked: other Segoe faces (geometric
    /// shapes, objects, suits, Greek, dots, monochrome emoji - every one still a Segoe font,
    /// and none of them bolder), the same MDL2 glyphs fattened by widening their outlines
    /// (the busy ones - calendar, clipboard, bug - filled in), filled icon sets (Phosphor
    /// Fill, Material Symbols), and outline sets with a heavy stroke. Lucide at stroke 2.6
    /// of its 24-unit grid was the choice: as heavy as the numbers beside it, still outline
    /// in character like the glyphs it replaced, and every icon the same weight.
    ///
    /// No font ships with Lucide on Windows and nothing may be installed, so each icon is
    /// carried here as its SVG path data - plain ASCII, as the source files must be.
    /// Lucide's circles, lines, polylines and rects were turned into path data when the
    /// table was made, so SvgPath only needs the path commands. The grid is Lucide's:
    /// 24 x 24, stroke 2 with round caps and joins, which MarkPainter strokes heavier.
    ///
    /// It lives in model/ although it is mostly drawing data because DesktopMark needs it -
    /// to refuse a key with no icon, and to turn an old glyph into its icon as marks.ini is
    /// read - and model/ may not reach up into ui/. Parsing and drawing are in ui/.
    ///
    /// Keys are this app's own short names, not Lucide's file names, because they are what
    /// marks.ini stores ("icon:rocket") and Lucide renames icons now and then ("home" became
    /// "house"). A key must never change once released; drop an icon from Offered instead,
    /// and it keeps drawing for anyone who chose it.
    ///
    /// Lucide is ISC-licensed, and its terms ask for the notice to travel with the paths:
    ///
    ///   ISC License
    ///
    ///   Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
    ///   Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors
    ///   2022.
    ///
    ///   Permission to use, copy, modify, and/or distribute this software for any purpose
    ///   with or without fee is hereby granted, provided that the above copyright notice and
    ///   this permission notice appear in all copies.
    ///
    ///   THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD
    ///   TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN
    ///   NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
    ///   CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR
    ///   PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
    ///   ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
    ///
    /// Paths are from lucide-static 0.460.0.
    /// </summary>
    static class MarkIcons
    {
        /// <summary>
        /// What the picker offers, in its order: four rows of eight. The rows run roughly as
        /// the old glyphs did - home and people, then kinds of project, then other work, then
        /// the rest - with rocket, flame, leaf, bulb, target, flask, package, star and heart
        /// new, in place of the old symbols that said less at this size.
        /// </summary>
        public static readonly string[] Offered =
        {
            "home", "calendar", "chat", "mail", "people", "bot", "code", "terminal",
            "browser", "layers", "database", "bug", "wrench", "chart", "briefcase", "cloud",
            "palette", "bolt", "flag", "rocket", "flame", "leaf", "bulb", "target",
            "flask", "package", "music", "game", "star", "heart", "plane", "globe",
        };

        static readonly Dictionary<string, string> Data = new Dictionary<string, string>
        {
            { "home", "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8 M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" },
            { "calendar", "M8 2v4 M16 2v4 M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2Z M3 10h18" },
            { "chat", "M7.9 20A9 9 0 1 0 4 16.1L2 22Z" },
            { "mail", "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-12a2 2 0 0 1 2 -2Z M22 7l-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7" },
            { "people", "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M5 7A4 4 0 1 0 13 7A4 4 0 1 0 5 7Z M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75" },
            { "bot", "M12 8V4H8 M6 8h12a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2Z M2 14h2 M20 14h2 M15 13v2 M9 13v2" },
            { "code", "M16 18L22 12L16 6 M8 6L2 12L8 18" },
            { "terminal", "M7 11l2-2-2-2 M11 13h4 M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2Z" },
            { "browser", "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-12a2 2 0 0 1 2 -2Z M10 4v4 M2 8h20 M6 4v4" },
            { "layers", "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z M22 17.65l-9.17 4.16a2 2 0 0 1-1.66 0L2 17.65 M22 12.65l-9.17 4.16a2 2 0 0 1-1.66 0L2 12.65" },
            { "database", "M3 5A9 3 0 1 0 21 5A9 3 0 1 0 3 5Z M3 5V19A9 3 0 0 0 21 19V5 M3 12A9 3 0 0 0 21 12" },
            { "bug", "M8 2l1.88 1.88 M14.12 3.88 16 2 M9 7.13v-1a3.003 3.003 0 1 1 6 0v1 M12 20c-3.3 0-6-2.7-6-6v-3a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v3c0 3.3-2.7 6-6 6 M12 20v-9 M6.53 9C4.6 8.8 3 7.1 3 5 M6 13H2 M3 21c0-2.1 1.7-3.9 3.8-4 M20.97 5c0 2.1-1.6 3.8-3.5 4 M22 13h-4 M17.2 17c2.1.1 3.8 1.9 3.8 4" },
            { "wrench", "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z" },
            { "chart", "M21 12c.552 0 1.005-.449.95-.998a10 10 0 0 0-8.953-8.951c-.55-.055-.998.398-.998.95v8a1 1 0 0 0 1 1z M21.21 15.89A10 10 0 1 1 8 2.83" },
            { "briefcase", "M16 20V4a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16 M4 6h16a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-10a2 2 0 0 1 2 -2Z" },
            { "cloud", "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z" },
            { "palette", "M13 6.5A0.5 0.5 0 1 0 14 6.5A0.5 0.5 0 1 0 13 6.5Z M17 10.5A0.5 0.5 0 1 0 18 10.5A0.5 0.5 0 1 0 17 10.5Z M8 7.5A0.5 0.5 0 1 0 9 7.5A0.5 0.5 0 1 0 8 7.5Z M6 12.5A0.5 0.5 0 1 0 7 12.5A0.5 0.5 0 1 0 6 12.5Z M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10c.926 0 1.648-.746 1.648-1.688 0-.437-.18-.835-.437-1.125-.29-.289-.438-.652-.438-1.125a1.64 1.64 0 0 1 1.668-1.668h1.996c3.051 0 5.555-2.503 5.555-5.554C21.965 6.012 17.461 2 12 2z" },
            { "bolt", "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z" },
            { "flag", "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z M4 22L4 15" },
            { "rocket", "M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z M12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0 M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5" },
            { "flame", "M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z" },
            { "leaf", "M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10Z M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12" },
            { "bulb", "M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5 M9 18h6 M10 22h4" },
            { "target", "M2 12A10 10 0 1 0 22 12A10 10 0 1 0 2 12Z M6 12A6 6 0 1 0 18 12A6 6 0 1 0 6 12Z M10 12A2 2 0 1 0 14 12A2 2 0 1 0 10 12Z" },
            { "flask", "M10 2v7.527a2 2 0 0 1-.211.896L4.72 20.55a1 1 0 0 0 .9 1.45h12.76a1 1 0 0 0 .9-1.45l-5.069-10.127A2 2 0 0 1 14 9.527V2 M8.5 2h7 M7 16h10" },
            { "package", "M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z M12 22V12 M3.3 7l7.703 4.734a2 2 0 0 0 1.994 0L20.7 7 M7.5 4.27l9 5.15" },
            { "music", "M9 18V5l12-2v13 M3 18A3 3 0 1 0 9 18A3 3 0 1 0 3 18Z M15 16A3 3 0 1 0 21 16A3 3 0 1 0 15 16Z" },
            { "game", "M6 11L10 11 M8 9L8 13 M15 12L15.01 12 M18 10L18.01 10 M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z" },
            { "star", "M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z" },
            { "heart", "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" },
            { "plane", "M17.8 19.2 16 11l3.5-3.5C21 6 21.5 4 21 3c-1-.5-3 0-4.5 1.5L13 8 4.8 6.2c-.5-.1-.9.1-1.1.5l-.3.5c-.2.5-.1 1 .3 1.3L9 12l-2 3H4l-1 1 3 2 2 3 1-1v-3l3-2 3.5 5.3c.3.4.8.5 1.3.3l.5-.2c.4-.3.6-.7.5-1.2z" },
            { "globe", "M2 12A10 10 0 1 0 22 12A10 10 0 1 0 2 12Z M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20 M2 12h20" },

            // Not offered - here so a mark saved with the old Segoe symbol of the same
            // meaning converts to an icon rather than staying a glyph. See Legacy.
            { "video", "M16 13l5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5 M4 6h10a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2Z" },
            { "phone", "M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z" },
            { "headphones", "M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 18 0v7a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3" },
            { "network", "M17 16h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1v-4a1 1 0 0 1 1 -1Z M3 16h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1v-4a1 1 0 0 1 1 -1Z M10 2h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1v-4a1 1 0 0 1 1 -1Z M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3 M12 12V8" },
            { "clipboard", "M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1 -1 1h-6a1 1 0 0 1 -1 -1v-2a1 1 0 0 1 1 -1Z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" },
            { "school", "M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z M22 10v6 M6 12.5V16a6 3 0 0 0 12 0v-3.5" },
            { "cart", "M7 21A1 1 0 1 0 9 21A1 1 0 1 0 7 21Z M18 21A1 1 0 1 0 20 21A1 1 0 1 0 18 21Z M2.05 2.05h2l2.66 12.42a2 2 0 0 0 2 1.58h9.78a2 2 0 0 0 1.95-1.57l1.65-7.43H5.12" },
            { "brush", "M9.06 11.9l8.07-8.06a2.85 2.85 0 1 1 4.03 4.03l-8.06 8.08 M7.07 14.94c-1.66 0-3 1.35-3 3.02 0 1.33-2.5 1.52-2 2.02 1.08 1.1 2.49 2.02 4 2.02 2.2 0 4-1.8 4-4.04a3.01 3.01 0 0 0-3-3.02z" },
            { "image", "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2Z M7 9A2 2 0 1 0 11 9A2 2 0 1 0 7 9Z M21 15l-3.086-3.086a2 2 0 0 0-2.828 0L6 21" },
        };

        /// <summary>
        /// The Segoe MDL2 glyphs the picker used to offer, each to the icon that means the
        /// same. DesktopMark.Parse uses it, so a "glyph:E80F" in marks.ini comes back as the
        /// house and is written as "icon:home" at the next save - nobody's marks change
        /// meaning, they only get bolder. Every old glyph has a counterpart; a glyph mark
        /// survives only for a codepoint someone typed into the file by hand.
        /// </summary>
        static readonly Dictionary<char, string> Legacy = new Dictionary<char, string>
        {
            { '\uE787', "calendar" }, { '\uE8F2', "chat" },     { '\uE715', "mail" },      { '\uE714', "video" },
            { '\uE717', "phone" },    { '\uE716', "people" },   { '\uE7F6', "headphones" },{ '\uE99A', "bot" },
            { '\uE943', "code" },     { '\uE756', "terminal" }, { '\uE737', "browser" },   { '\uE81E', "layers" },
            { '\uEE94', "database" }, { '\uF22C', "network" },  { '\uEBE8', "bug" },       { '\uE90F', "wrench" },
            { '\uEB05', "chart" },    { '\uEADF', "clipboard" },{ '\uE821', "briefcase" }, { '\uE753', "cloud" },
            { '\uE790', "palette" },  { '\uE7BE', "school" },   { '\uE945', "bolt" },      { '\uE7C1', "flag" },
            { '\uE80F', "home" },     { '\uEC4F', "music" },    { '\uE7FC', "game" },      { '\uE7BF', "cart" },
            { '\uE709', "plane" },    { '\uE12B', "globe" },    { '\uE771', "brush" },     { '\uE914', "image" },
        };

        /// <summary>Whether <paramref name="key"/> names an icon this build can draw.</summary>
        public static bool Has(string key)
        {
            return key != null && Data.ContainsKey(key);
        }

        /// <summary>The icon an old Segoe glyph became, or null if it has none.</summary>
        public static string FromGlyph(char codepoint)
        {
            string key;
            return Legacy.TryGetValue(codepoint, out key) ? key : null;
        }

        /// <summary>Every key, offered or not - for the selftest to parse and round-trip.</summary>
        public static IEnumerable<string> All { get { return Data.Keys; } }

        /// <summary>Every key an old glyph converts to - for the selftest to check each is in the table.</summary>
        public static IEnumerable<string> Converted { get { return Legacy.Values; } }

        /// <summary>
        /// Icon <paramref name="key"/>'s SVG path data, in Lucide's 24 x 24 grid with its
        /// round-capped stroke in mind, or null. Drawing it is MarkPainter's job.
        /// </summary>
        public static string PathData(string key)
        {
            string data;
            return key != null && Data.TryGetValue(key, out data) ? data : null;
        }

    }
}
