using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace DesktopSwitcher
{
    /// <summary>
    /// What a save prompt is asking, read off the prompt itself, so a close-all row can say
    /// "Do you want to save changes to Untitled?" instead of only that something is asking.
    ///
    /// The caption is no use on its own. Notepad's, Office's and most modern prompts are
    /// TaskDialogs whose caption is just the app's name - "Notepad" - and whose question is
    /// drawn inside a DirectUIHWND, which has no Win32 text to read at all. UI Automation is
    /// the one way in from outside the process, and it is the reason this class exists:
    /// three steps, cheapest first, each tried only when the one before found nothing.
    ///
    ///   1. Win32: the first Static child with text. A classic MessageBox's message is
    ///      one, so is a WinForms label, and reading it needs nothing loaded.
    ///   2. UI Automation: the dialog's first Text element that is not its caption - a
    ///      TaskDialog's main instruction, a WinForms label.
    ///   3. The caption, if it says more than the app's name.
    ///
    /// Statics first, not UIA first, for what it costs: the first UIA read loads two
    /// assemblies and starts four threads, about 6 MB measured in --closeall, and a
    /// MessageBox gives the same answer either way.
    ///
    /// Null when none of them has anything; the row then says the window has a dialog open.
    ///
    /// Off the UI thread, always. A UIA call is a cross-process round trip, and a prompt's
    /// process is by definition busy with something; on the UI thread a slow one would
    /// freeze the strip and the progress window with it. Run on a pool thread, which is MTA,
    /// which is what UIA client calls want. The answer is handed to the callback on that
    /// thread; the caller marshals it back and checks it is still about the prompt it asked.
    ///
    /// UIAutomationClient.dll and UIAutomationTypes.dll ship with .NET 4.8, so nothing is
    /// installed, but they are not loaded until the first prompt is read: every UIA type is
    /// behind ByAutomation, which is never inlined, so the JIT only pulls the assemblies in
    /// when that method first runs. A session that never meets a save prompt never pays the
    /// few MB they cost.
    /// </summary>
    static class DialogText
    {
        /// <summary>Longer than this is cut with an ellipsis. Two lines of the row at most.</summary>
        const int MaxChars = 140;

        static int _automationReads;

        /// <summary>
        /// Reads <paramref name="dialog"/> on a pool thread and calls <paramref name="done"/>
        /// there with the text, or null, and whether it came from inside the dialog - false
        /// for the caption or nothing, which the caller may try once more, since a dialog
        /// read the moment it appeared can still be building its contents. Never throws,
        /// and always calls back.
        /// </summary>
        public static void Read(IntPtr dialog, string app, Action<string, bool> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null;
                var clock = Stopwatch.StartNew();
                string how = "nothing";
                bool first = false;

                try
                {
                    string caption = Native.GetText(dialog);

                    text = Clean(ByStatics(dialog));
                    if (text != null) how = "static";

                    if (text == null)
                    {
                        first = Interlocked.Increment(ref _automationReads) == 1;
                        text = Clean(ByAutomation(dialog, caption));
                        if (text != null) how = "automation";
                    }

                    if (text == null && caption.Length > 0
                        && !string.Equals(caption, app, StringComparison.OrdinalIgnoreCase))
                    {
                        text = Clean(caption);
                        if (text != null) how = "caption";
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("dialog text: " + ex.GetType().Name + " - " + ex.Message);
                }

                long ms = clock.ElapsedMilliseconds;
                Log.Write(delegate
                {
                    return "dialog text: 0x" + dialog.ToString("X") + " by " + how + " in " + ms + " ms"
                           + (first ? " (first read, UIA loaded)" : "");
                });

                bool inside = how == "static" || how == "automation";
                try { done(text, inside); }
                catch (Exception ex) { Log.Write("dialog text: callback threw - " + ex.Message); }
            });
        }

        /// <summary>
        /// The UIA step, kept in a method of its own and never inlined, so that the two UIA
        /// assemblies load only when it first runs - see the class comment.
        ///
        /// Descendants, not children: a TaskDialog nests its text a few panes down. Small
        /// trees only - a prompt is a handful of elements - so the walk is cheap once the
        /// connection to the process is made, which is most of the cost of a first read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ByAutomation(IntPtr dialog, string caption)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(dialog);
                if (root == null) return null;

                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
                AutomationElementCollection texts = root.FindAll(TreeScope.Descendants, condition);

                foreach (AutomationElement element in texts)
                {
                    string name = element.Current.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name.Trim(), caption, StringComparison.Ordinal)) continue;
                    return name;
                }

                // Nothing: say what was there, for the "why only the caption" question.
                int count = texts.Count;
                int children = root.FindAll(TreeScope.Children, Condition.TrueCondition).Count;
                Log.Write(delegate
                {
                    return "dialog text: automation found " + count + " text elements, "
                           + children + " children, none but the caption";
                });
            }
            catch (Exception ex)
            {
                // ElementNotAvailable when the prompt closes mid-read, which is the user
                // answering it - the commonest way to get here and nothing to worry about.
                Log.Write("dialog text: automation - " + ex.GetType().Name);
            }
            return null;
        }

        /// <summary>
        /// A classic MessageBox's text lives in a Static child; a WinForms label is one too,
        /// under WinForms' own class name. The first with any text.
        ///
        /// The WinForms case is not hypothetical: UI Automation, run against --closeall's
        /// WinForms test prompt on its own, found no Text element in it at all, every time -
        /// the label was there as a window and nowhere in the UIA tree - and the row fell back
        /// to the caption. Read as a window it is there every time, and costs nothing.
        /// </summary>
        static string ByStatics(IntPtr dialog)
        {
            string found = null;
            Native.EnumChildWindows(dialog, delegate(IntPtr child, IntPtr param)
            {
                string cls = Native.GetClass(child);
                if (cls != "Static" && !cls.StartsWith("WindowsForms10.STATIC.", StringComparison.OrdinalIgnoreCase))
                    return true;
                string text = Native.GetText(child);
                if (text.Trim().Length == 0) return true;   // an icon Static, or a spacer
                found = text;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// One line, mnemonics taken out, cut to MaxChars. Null for nothing left.
        /// A MessageBox's text is often several lines, and only the first sentence or two
        /// fit a row anyway; collapsing keeps those rather than a first line that may be a
        /// bare file path.
        /// </summary>
        static string Clean(string text)
        {
            if (text == null) return null;

            var sb = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '&')
                {
                    // "&&" is a literal ampersand; a lone one marks the mnemonic letter.
                    if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; space = false; }
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }
                sb.Append(c);
                space = false;
            }

            string result = sb.ToString().Trim();
            if (result.Length == 0) return null;
            if (result.Length > MaxChars)
                result = result.Substring(0, MaxChars - 1).TrimEnd(' ', '.', ',') + "\u2026";
            return result;
        }
    }
}
