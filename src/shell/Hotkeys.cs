using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DesktopSwitcher
{
    /// <summary>
    /// The modifiers the desktop shortcuts are held under - jumpShortcut for the digits,
    /// stepShortcut for the arrows, one each.
    ///
    /// A fixed list rather than free text, because a free-text binding is a parser, an
    /// editor control and a list of the ways it can be spelt wrong, and every combination
    /// worth having fits in five. Ctrl+Win alone is missing on purpose: Ctrl+Win+arrows is
    /// the very shortcut this exists to replace, Explorer holds it, and RegisterHotKey
    /// would only ever fail on it.
    ///
    /// Appended to, never reordered: a Choice setting is stored by name but held by index.
    /// </summary>
    enum HotkeyModifiers
    {
        Off,
        CtrlAlt,
        AltShift,
        CtrlShift,
        CtrlWinAlt,
    }

    /// <summary>
    /// Global shortcuts that switch desktops, claimed with RegisterHotKey on the
    /// controller's hidden window: Ctrl+Alt+1..9 jumps to a desktop, Ctrl+Alt+Left and
    /// Right step through them.
    ///
    /// The point is the switch rather than the keys. Windows' own Ctrl+Win+arrows slides
    /// the whole screen across, and that slide belongs to Explorer's keyboard path, not to
    /// the switch: the strip's own click goes through IVirtualDesktopManagerInternal's
    /// SwitchDesktop and lands at once. A hotkey that ends in the same call gets the same
    /// instant switch.
    ///
    /// RegisterHotKey rather than a low-level keyboard hook, which would have let the app
    /// take Ctrl+Win+arrows itself. A hook sees every keystroke on the system, and Windows
    /// silently removes one whose thread is slow to answer - the UI thread here makes COM
    /// calls into the shell, so it would be, and the slide would come back unannounced. A
    /// registered hotkey costs nothing between presses, needs no thread of its own, and
    /// belongs to our window rather than to Explorer's, so an Explorer restart does not
    /// take it away.
    ///
    /// MOD_NOREPEAT on every key, so holding Ctrl+Alt+Right moves one desktop rather than
    /// racing to the end at the keyboard's repeat rate with a COM call per step.
    ///
    /// The price of RegisterHotKey is that it is first come, first served. A combination
    /// another app already holds cannot be had, and nothing says so unless we do - Register
    /// hands back what failed, for the controller to tell the person.
    /// </summary>
    sealed class Hotkeys : IDisposable
    {
        // Ids are ours to pick, per window. 1..9 are the digits, so a jump's id is its
        // desktop number.
        const int FirstJumpId = 1;
        const int JumpCount = 9;
        const int PreviousId = 10;
        const int NextId = 11;

        const uint VK_LEFT = 0x25;
        const uint VK_RIGHT = 0x27;
        const uint VK_1 = 0x31;

        const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        readonly IntPtr _window;
        readonly List<int> _held = new List<int>();

        public Hotkeys(IntPtr window)
        {
            _window = window;
        }

        /// <summary>
        /// Swaps whatever is held for the combinations given, and returns the names of the
        /// ones that could not be had - empty when everything registered. Each key stands or
        /// falls alone: one taken digit does not cost the other eight.
        /// </summary>
        public List<string> Register(HotkeyModifiers jump, HotkeyModifiers step)
        {
            Unregister();

            var failed = new List<string>();
            foreach (Binding b in Bindings(jump, step))
            {
                if (Native.RegisterHotKey(_window, b.Id, b.Modifiers | Native.MOD_NOREPEAT, b.Key))
                {
                    _held.Add(b.Id);
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    failed.Add(b.Name);
                    Log.Write("hotkeys: " + b.Name + " not registered - " + Describe(error));
                }
            }

            Log.Write("hotkeys: holding " + _held.Count + ", " + failed.Count + " unavailable");
            return failed;
        }

        public void Unregister()
        {
            for (int i = 0; i < _held.Count; i++)
                Native.UnregisterHotKey(_window, _held[i]);
            _held.Clear();
        }

        public void Dispose()
        {
            Unregister();
        }

        /// <summary>
        /// What a WM_HOTKEY's id asks for: a desktop by its zero-based position
        /// (<paramref name="jumpIndex"/>, with <paramref name="step"/> 0), or a step of -1 or
        /// +1 (with <paramref name="jumpIndex"/> -1). False for an id that is not ours.
        /// </summary>
        public static bool TryDecode(int id, out int jumpIndex, out int step)
        {
            jumpIndex = -1;
            step = 0;

            if (id >= FirstJumpId && id < FirstJumpId + JumpCount)
            {
                jumpIndex = id - FirstJumpId;
                return true;
            }
            if (id == PreviousId) { step = -1; return true; }
            if (id == NextId) { step = 1; return true; }
            return false;
        }

        /// <summary>
        /// Tries every configured combination on the calling thread and lets it go again at
        /// once, for --hotkeys: one line per key, "free" or why not. A null window ties the
        /// registration to the thread, so no window is needed to ask.
        /// </summary>
        public static List<string> Probe(HotkeyModifiers jump, HotkeyModifiers step)
        {
            var lines = new List<string>();
            foreach (Binding b in Bindings(jump, step))
            {
                if (Native.RegisterHotKey(IntPtr.Zero, b.Id, b.Modifiers | Native.MOD_NOREPEAT, b.Key))
                {
                    Native.UnregisterHotKey(IntPtr.Zero, b.Id);
                    lines.Add(b.Name.PadRight(22) + "free");
                }
                else
                {
                    lines.Add(b.Name.PadRight(22) + Describe(Marshal.GetLastWin32Error()));
                }
            }
            return lines;
        }

        /// <summary>"Ctrl+Alt" and the like, for the log and the tray's complaint. Empty for Off.</summary>
        public static string ModifierName(HotkeyModifiers m)
        {
            switch (m)
            {
                case HotkeyModifiers.CtrlAlt:    return "Ctrl+Alt";
                case HotkeyModifiers.AltShift:   return "Alt+Shift";
                case HotkeyModifiers.CtrlShift:  return "Ctrl+Shift";
                case HotkeyModifiers.CtrlWinAlt: return "Ctrl+Win+Alt";
                default:                         return "";
            }
        }

        static uint ModifierFlags(HotkeyModifiers m)
        {
            switch (m)
            {
                case HotkeyModifiers.CtrlAlt:    return Native.MOD_CONTROL | Native.MOD_ALT;
                case HotkeyModifiers.AltShift:   return Native.MOD_ALT | Native.MOD_SHIFT;
                case HotkeyModifiers.CtrlShift:  return Native.MOD_CONTROL | Native.MOD_SHIFT;
                case HotkeyModifiers.CtrlWinAlt: return Native.MOD_CONTROL | Native.MOD_WIN | Native.MOD_ALT;
                default:                         return 0;
            }
        }

        static string Describe(int error)
        {
            return error == ERROR_HOTKEY_ALREADY_REGISTERED
                ? "taken by another app"
                : "refused (error " + error + ")";
        }

        struct Binding
        {
            public int Id;
            public uint Modifiers;
            public uint Key;
            public string Name;
        }

        static List<Binding> Bindings(HotkeyModifiers jump, HotkeyModifiers step)
        {
            var list = new List<Binding>();

            if (jump != HotkeyModifiers.Off)
            {
                for (int i = 0; i < JumpCount; i++)
                    list.Add(new Binding { Id = FirstJumpId + i, Modifiers = ModifierFlags(jump),
                                           Key = VK_1 + (uint)i,
                                           Name = ModifierName(jump) + "+" + (i + 1) });
            }

            if (step != HotkeyModifiers.Off)
            {
                list.Add(new Binding { Id = PreviousId, Modifiers = ModifierFlags(step),
                                       Key = VK_LEFT, Name = ModifierName(step) + "+Left" });
                list.Add(new Binding { Id = NextId, Modifiers = ModifierFlags(step),
                                       Key = VK_RIGHT, Name = ModifierName(step) + "+Right" });
            }

            return list;
        }
    }
}
