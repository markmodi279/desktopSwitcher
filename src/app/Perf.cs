using System;
using System.Runtime.InteropServices;

namespace DesktopSwitcher
{
    /// <summary>
    /// One line of what this process is costing right now: CPU time used so far, working
    /// set, private bytes, and the GDI and USER objects it holds. Written into the log at
    /// the edges of anything new that animates or opens windows - a close-all's start and
    /// end, its window closing, a strip fold - so a "did that make it heavier" question has
    /// numbers before and after in the same file, and a leak shows as a GDI or USER count
    /// that does not come back down.
    ///
    /// Only with diagnostics on. Reading these opens the process's counters, which costs
    /// next to nothing, but it is still nothing a run without a log should pay for.
    /// </summary>
    static class Perf
    {
        /// <summary>
        /// Through GetProcessTimes and GetProcessMemoryInfo, on this process only. Not
        /// System.Diagnostics.Process: its memory and thread properties are filled from a
        /// snapshot of every process on the machine, which cost enough that the first perf
        /// lines were measurably adding to the close-all CPU they were there to measure.
        /// </summary>
        public static string Snapshot()
        {
            try
            {
                IntPtr self = GetCurrentProcess();   // a pseudo-handle: nothing to close

                long creation, exit, kernel, user;
                double cpu = GetProcessTimes(self, out creation, out exit, out kernel, out user)
                    ? (kernel + user) / 10000.0 : -1;

                var memory = new PROCESS_MEMORY_COUNTERS_EX();
                memory.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
                bool haveMemory = K32GetProcessMemoryInfo(self, out memory, memory.cb);

                return string.Format("cpu {0:0} ms, ws {1:0.0} MB, private {2:0.0} MB, gdi {3}, user {4}",
                    cpu,
                    haveMemory ? memory.WorkingSetSize.ToUInt64() / 1048576.0 : -1,
                    haveMemory ? memory.PrivateUsage.ToUInt64() / 1048576.0 : -1,
                    Native.GetGuiResources(self, Native.GR_GDIOBJECTS),
                    Native.GetGuiResources(self, Native.GR_USEROBJECTS));
            }
            catch (Exception ex)
            {
                return "unreadable - " + ex.Message;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        static extern bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

        /// <summary>Logs a snapshot under a label, and only reads one when the log is on.</summary>
        public static void Log(string what)
        {
            if (!DesktopSwitcher.Log.Enabled) return;
            DesktopSwitcher.Log.Write("perf: " + what + " - " + Snapshot());
        }
    }
}
