using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace DesktopSwitcher
{
    /// <summary>
    /// CPU, memory and network throughput, for the stats tile's panel.
    ///
    /// CPU and network are rates, and a rate needs two readings: what the OS hands back is
    /// a cumulative count since boot, and a single one of those averages over the whole
    /// uptime, which says nothing about now. So this keeps the previous reading and reports
    /// the difference. That is also why something has to sample *before* the panel opens -
    /// the controller does it on the reconcile tick - or the first panel would have nothing
    /// to subtract from and could only say "measuring".
    ///
    /// A reading costs well under a millisecond, and that took deliberate work: enumerating
    /// the network adapters is not cheap. NetworkInterface.GetAllNetworkInterfaces measured
    /// ~70ms a call on the development machine - on the UI thread, every reconcile tick,
    /// which is four dropped animation frames every two seconds for the life of the process
    /// - while reading the byte counters off adapters already in hand measured 0.1ms. So the
    /// adapter list is cached and rebuilt only when Windows says the network changed, plus a
    /// slow fallback in case it ever does not say. See Adapters.
    ///
    /// MinIntervalMs keeps two callers landing close together - the reconcile tick and the
    /// panel's own refresh - from producing a rate over a 20ms window, which for CPU is a
    /// coin toss between 0% and 100%.
    ///
    /// Values read -1 until they are known. The caller decides how to say so.
    /// </summary>
    /// <summary>One reading in SystemStats.History. Rates are -1 where not yet known.</summary>
    sealed class StatsPoint
    {
        public readonly int At;           // Environment.TickCount when it was taken
        public readonly float Cpu;
        public readonly float Memory;     // memory load, 0..100
        public readonly double Down, Up;

        public StatsPoint(int at, float cpu, float memory, double down, double up)
        {
            At = at;
            Cpu = cpu;
            Memory = memory;
            Down = down;
            Up = up;
        }
    }

    sealed class SystemStats
    {
        /// <summary>A sample closer than this to the last one is skipped, not taken.</summary>
        public const int MinIntervalMs = 500;

        /// <summary>
        /// How far back History reaches: the span the panel's charts draw. A minute is
        /// long enough to show a spike that has already passed - the thing a single figure
        /// refreshed once a second cannot - and short enough that one is not flattened into
        /// a hairline by the rest of the window.
        /// </summary>
        public const int HistoryMs = 60 * 1000;

        /// <summary>
        /// The longest the cached adapter list is trusted without a change notification.
        /// One 70ms enumeration every five minutes is noise; it is there for a change the
        /// notification missed, not as the way changes are normally found.
        /// </summary>
        public const int AdapterRefreshMs = 5 * 60 * 1000;

        NetworkInterface[] _adapters;   // the counted ones only, as of _adaptersAt
        int _adaptersAt;

        // Set from a thread-pool thread by NetworkChange, read on the UI thread. A bool is
        // written atomically, and volatile is what stops the read being hoisted out of
        // existence; nothing else crosses threads here.
        volatile bool _adaptersStale = true;

        bool _primed;
        int _at;                        // Environment.TickCount of the last reading

        bool _cpuOk;
        long _idle, _kernel, _user;

        // By NetworkInterface.Id, { received, sent }. Per interface rather than a running
        // total, because the set of interfaces changes - a VPN connecting, Wi-Fi coming up -
        // and an adapter that appears between two readings would otherwise add its entire
        // since-boot count to one delta and report gigabytes a second for a frame.
        Dictionary<string, long[]> _net;

        float _cpu = -1f;
        double _down = -1, _up = -1;

        bool _memOk;
        uint _memLoad;
        ulong _memTotal, _memAvail;

        // Oldest first. Fed by every reading that is actually taken, so it fills from the
        // reconcile tick while the panel is closed - two seconds apart - and once a second
        // while it is open. Uneven spacing is fine because each point carries its own time
        // and the sparkline places it by age, not by index.
        readonly List<StatsPoint> _history = new List<StatsPoint>();

        /// <summary>
        /// Busy share of all processors, 0..100, between the last two readings.
        ///
        /// This is "% Processor Time". Task Manager on Windows 10 shows "% Processor
        /// Utility" instead, which scales by the clock against base frequency, so on a
        /// turbo-boosting machine under load Task Manager can read noticeably higher. Both
        /// are correct answers to slightly different questions.
        /// </summary>
        public float CpuPercent { get { return _cpu; } }

        public double DownBytesPerSecond { get { return _down; } }
        public double UpBytesPerSecond { get { return _up; } }

        public bool HasMemory { get { return _memOk; } }

        /// <summary>The kernel's own "memory load", 0..100. Same figure Task Manager rounds.</summary>
        public uint MemoryLoad { get { return _memLoad; } }
        public ulong MemoryTotal { get { return _memTotal; } }

        /// <summary>
        /// Total minus available. Available counts standby pages, so this is "in use" plus
        /// modified - within a few percent of Task Manager's "In use", never wildly apart.
        /// </summary>
        public ulong MemoryUsed { get { return _memTotal > _memAvail ? _memTotal - _memAvail : 0; } }

        /// <summary>
        /// The last HistoryMs of readings, oldest first, plus at most one older point so a
        /// line drawn from them enters at the window's left edge rather than starting
        /// partway in. Read-only; the list itself is this object's.
        /// </summary>
        public IList<StatsPoint> History { get { return _history.AsReadOnly(); } }

        public SystemStats()
        {
            // Any address coming or going - Wi-Fi joining, a VPN connecting, an adapter
            // disabled - means the cached list may be wrong. The handler only marks it; the
            // next reading, on the UI thread, does the enumerating.
            //
            // A static event, so this instance is kept alive by it for the life of the
            // process. That is the lifetime it has anyway: the controller owns one from
            // start to exit.
            try
            {
                NetworkChange.NetworkAddressChanged += delegate { _adaptersStale = true; };
            }
            catch (Exception ex)
            {
                // Without it the five-minute fallback is the only refresh, which is late
                // but not wrong.
                Log.Write("stats: no network change notifications - " + ex.Message);
            }
        }

        /// <summary>
        /// Takes a reading, unless the last was under MinIntervalMs ago. Never throws: a
        /// figure that cannot be read keeps its last value, and the panel goes on saying
        /// whatever it last knew rather than going blank.
        /// </summary>
        public void Sample()
        {
            int now = Environment.TickCount;

            // Unchecked for TickCount's wrap at ~24.9 days, as everywhere else it is read.
            int elapsed = unchecked(now - _at);
            if (_primed && elapsed >= 0 && elapsed < MinIntervalMs) return;

            SampleCpu();
            SampleMemory();
            SampleNetwork(elapsed);

            _at = now;
            _primed = true;

            Record(now);
        }

        /// <summary>
        /// Appends the reading just taken and drops what has aged out. Nothing is recorded
        /// until CPU has a rate: the first reading has nothing to measure from, and a point
        /// that says "unknown" would only be skipped when drawn.
        /// </summary>
        void Record(int now)
        {
            if (_cpu < 0) return;

            _history.Add(new StatsPoint(now, _cpu, _memOk ? (float)_memLoad : -1f, _down, _up));

            // Keeps the newest point that is past the window - the one the line enters from
            // - and drops everything older. A negative age is TickCount having wrapped
            // between two points, which is treated as aged out like anything else ancient.
            while (_history.Count > 1)
            {
                int age = unchecked(now - _history[1].At);
                if (age >= 0 && age < HistoryMs) break;
                _history.RemoveAt(0);
            }
        }

        void SampleCpu()
        {
            long idle, kernel, user;
            bool ok = Native.GetSystemTimes(out idle, out kernel, out user);

            if (ok && _cpuOk && _primed)
            {
                // Kernel time includes idle time, so the total is kernel + user and busy is
                // what is left of it once idle comes out.
                long total = (kernel - _kernel) + (user - _user);
                long busy = total - (idle - _idle);

                if (total > 0)
                    _cpu = Math.Max(0f, Math.Min(100f, 100f * busy / total));
            }

            _cpuOk = ok;
            if (!ok) return;

            _idle = idle;
            _kernel = kernel;
            _user = user;
        }

        void SampleMemory()
        {
            var status = new Native.MEMORYSTATUSEX();
            status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));

            if (!Native.GlobalMemoryStatusEx(ref status)) return;

            _memOk = true;
            _memLoad = status.dwMemoryLoad;
            _memTotal = status.ullTotalPhys;
            _memAvail = status.ullAvailPhys;
        }

        void SampleNetwork(int elapsed)
        {
            Dictionary<string, long[]> net = ReadNetwork();
            if (net == null) return;   // keep the last reading to measure from next time

            if (_net != null && _primed && elapsed > 0)
            {
                long received = 0, sent = 0;

                foreach (KeyValuePair<string, long[]> kv in net)
                {
                    long[] before;
                    if (!_net.TryGetValue(kv.Key, out before)) continue;

                    // A counter that went backwards was reset - the adapter was disabled and
                    // re-enabled, or the driver reloaded. Nothing it says this time is a
                    // delta, so it contributes nothing rather than a huge negative.
                    long r = kv.Value[0] - before[0];
                    long s = kv.Value[1] - before[1];
                    if (r > 0) received += r;
                    if (s > 0) sent += s;
                }

                _down = received * 1000.0 / elapsed;
                _up = sent * 1000.0 / elapsed;
            }

            _net = net;
        }

        /// <summary>
        /// Byte counters for every interface that counts, by interface id. Null when there
        /// is no adapter list to read from, which is not the same thing as no traffic.
        /// </summary>
        Dictionary<string, long[]> ReadNetwork()
        {
            NetworkInterface[] adapters = Adapters();
            if (adapters == null) return null;

            var result = new Dictionary<string, long[]>();
            List<NetworkInterface> readable = null;

            for (int i = 0; i < adapters.Length; i++)
            {
                NetworkInterface ni = adapters[i];
                try
                {
                    // Live counters, read fresh on every call - GetIfEntry2 underneath. Only
                    // the list of adapters is cached, never what they have carried.
                    IPInterfaceStatistics stats = ni.GetIPStatistics();
                    result[ni.Id] = new long[] { stats.BytesReceived, stats.BytesSent };
                    if (readable != null) readable.Add(ni);
                }
                catch (Exception ex)
                {
                    // Usually an adapter that went away before the notification saying so
                    // landed. Dropped from the cached list, not answered by re-enumerating:
                    // an adapter that fails every read would otherwise buy a 70ms
                    // enumeration on every tick, which is the cost the cache is for.
                    Log.Write("stats: adapter " + ni.Name + " unreadable, dropped - " + ex.Message);

                    if (readable == null)
                    {
                        readable = new List<NetworkInterface>();
                        for (int j = 0; j < i; j++) readable.Add(adapters[j]);
                    }
                }
            }

            if (readable != null) _adapters = readable.ToArray();
            return result;
        }

        /// <summary>
        /// The counted adapters, re-enumerated only when marked stale or past
        /// AdapterRefreshMs. OperationalStatus is a snapshot taken at enumeration, not a
        /// live read, so an adapter going down is noticed through the same notification as
        /// one appearing - which is the case it covers, since losing its link drops its
        /// addresses.
        ///
        /// A failed enumeration keeps the previous list: the adapters it names may still be
        /// there, and readings off them are better than none.
        /// </summary>
        NetworkInterface[] Adapters()
        {
            int age = unchecked(Environment.TickCount - _adaptersAt);
            bool expired = age < 0 || age > AdapterRefreshMs;

            // No null check on the list: _adaptersStale starts true, which is what forces the
            // first enumeration, and a failed one should wait out the interval like any
            // other rather than retrying at 70ms a go on every reading.
            if (!_adaptersStale && !expired) return _adapters;

            // Cleared before enumerating, not after: a change landing mid-enumeration may not
            // be in what comes back, and must leave the list marked for next time.
            _adaptersStale = false;
            _adaptersAt = Environment.TickCount;

            try
            {
                var counted = new List<NetworkInterface>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (SkipReason(ni) == null) counted.Add(ni);

                _adapters = counted.ToArray();
            }
            catch (Exception ex)
            {
                Log.Write("stats: network enumeration failed - " + ex.Message);
            }

            return _adapters;
        }

        /// <summary>
        /// Why an interface is left out of the network total, or null when it is counted.
        /// Public so --stats can print the verdict for every adapter on the machine.
        ///
        /// The point is to count each byte that crosses the machine's edge once. Loopback
        /// never leaves the box. Virtual switches are the trap: WSL and Docker traffic
        /// crosses vEthernet (WSL) and is then NATed out through the physical adapter, so
        /// counting both reports every download twice - on the machine this is developed
        /// on, from WSL, that is the normal case and not an edge one. The same goes for
        /// VMware and VirtualBox host adapters, and for tunnels, whose bytes are carried
        /// again, encapsulated, by the adapter underneath.
        ///
        /// A heuristic, matched on names Windows and the hypervisors actually use. A VPN
        /// that presents itself as a plain Ethernet adapter still double counts.
        /// </summary>
        public static string SkipReason(NetworkInterface ni)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) return "not up";

            switch (ni.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Loopback: return "loopback";
                case NetworkInterfaceType.Tunnel:   return "tunnel";
            }

            string name = ni.Name ?? "";
            string description = ni.Description ?? "";

            if (name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                || description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hyper-V virtual switch";

            if (description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                return "virtual adapter";

            return null;
        }
    }
}
