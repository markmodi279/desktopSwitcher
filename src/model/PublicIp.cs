using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// The machine's public IPv4 address, for the stats panel's footer and the tile's
    /// "Copy public IP" item. Off unless statsPublicIp is set, and the one thing in the
    /// app that talks to the internet - which is why it is opt-in.
    ///
    /// Nothing on the machine knows this. Behind a router every adapter holds a private
    /// address and the public one exists only on the far side of the NAT, so the only way
    /// to learn it is to ask a server what address the request arrived from.
    ///
    /// Cloudflare first, addressed as 1.1.1.1 rather than by name. The IPv4 literal is
    /// what makes the answer IPv4: a hostname with an AAAA record would, on a dual-stack
    /// connection, be reached over IPv6 and report the v6 address instead. It also skips
    /// DNS, which is one less thing to time out. ipify's api host is IPv4-only, so it is
    /// the fallback for a network that blocks 1.1.1.1 - some captive portals and
    /// corporate firewalls do - and gives the same guarantee by a different route.
    ///
    /// Asked lazily: when the panel or the tile's menu opens, and then not again for
    /// FreshMs unless the network changes. Never on the reconcile tick - that would be
    /// a request every two seconds for the life of the process, to somebody else's server,
    /// for a figure that changes perhaps once a day. A VPN connecting or Wi-Fi switching is
    /// exactly when it does change, and NetworkAddressChanged marks the cached one stale
    /// for that.
    ///
    /// The request runs on the thread pool and the answer is marshalled back, so every
    /// field here is only ever touched on the UI thread except _stale. A slow or dead
    /// network costs the UI nothing; the panel says "checking..." until it hears.
    /// </summary>
    sealed class PublicIp
    {
        /// <summary>How long an answer is trusted with no network change in between.</summary>
        public const int FreshMs = 10 * 60 * 1000;

        /// <summary>
        /// How soon to ask again after a failure. The panel calls Refresh once a second
        /// while it is open, and without this a dead network would be asked every second.
        /// </summary>
        public const int RetryMs = 30 * 1000;

        /// <summary>Per request, connect and read alike. Two requests at worst, so eight seconds.</summary>
        public const int TimeoutMs = 4000;

        public const string PrimaryUrl = "https://1.1.1.1/cdn-cgi/trace";
        public const string FallbackUrl = "https://api.ipify.org";

        readonly ISynchronizeInvoke _marshaller;

        string _address;        // the last good answer; kept through a later failure
        int _checkedAt;
        bool _checked;          // whether _checkedAt means anything yet
        bool _checking;
        bool _failed;           // the last attempt got no answer

        // Set from a thread-pool thread by NetworkChange, read on the UI thread - the same
        // arrangement as SystemStats._adaptersStale, for the same reason.
        volatile bool _stale;

        static PublicIp()
        {
            // The assembly carries no TargetFrameworkAttribute - csc on its own does not
            // emit one - so the runtime treats it as a legacy app and offers SSL3 and TLS
            // 1.0 only, which both services refuse. Added to, not replaced, so anything
            // the OS would have offered still is. Process-wide, but nothing else in the
            // process opens a connection.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                Log.Write("publicip: could not enable TLS 1.2 - " + ex.Message);
            }
        }

        public PublicIp(ISynchronizeInvoke marshaller)
        {
            _marshaller = marshaller;

            // A static event, so this instance lives as long as the process - which is
            // what the controller, its only owner, does anyway.
            try
            {
                NetworkChange.NetworkAddressChanged += delegate { _stale = true; };
            }
            catch (Exception ex)
            {
                // Without it a VPN coming up shows the old address until FreshMs runs out.
                Log.Write("publicip: no network change notifications - " + ex.Message);
            }
        }

        /// <summary>The last address heard, or null if none has been yet.</summary>
        public string Address { get { return _address; } }

        /// <summary>
        /// What the panel and the menu show for it: the address, or why there is none.
        /// A stale address is shown as it is rather than as "checking...": it is almost
        /// always still right, and is what someone reaching for it wants.
        /// </summary>
        public string Display
        {
            get
            {
                if (_address != null) return _address;
                if (_failed && !_checking) return "unavailable";
                return "checking...";
            }
        }

        /// <summary>
        /// Starts a lookup unless one is running or the last answer is still fresh. Cheap
        /// when there is nothing to do, so it can be called on every panel refresh. UI
        /// thread only.
        /// </summary>
        public void Refresh(bool force)
        {
            if (_checking) return;

            if (!force && _checked && !_stale)
            {
                int age = unchecked(Environment.TickCount - _checkedAt);
                int limit = _failed ? RetryMs : FreshMs;
                if (age >= 0 && age < limit) return;
            }

            // Cleared before asking, not after: a change landing mid-request may not be
            // reflected in its answer, and must leave the next call to ask again.
            _stale = false;
            _checking = true;

            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string source;
                    double ms;
                    string address = Lookup(out source, out ms);
                    Deliver(address, source, ms);
                });
            }
            catch (Exception ex)
            {
                _checking = false;
                Log.Write("publicip: could not queue the lookup - " + ex.Message);
            }
        }

        /// <summary>Hands a worker's answer to the UI thread. Called on the worker.</summary>
        void Deliver(string address, string source, double ms)
        {
            MethodInvoker apply = delegate
            {
                _checking = false;
                _checked = true;
                _checkedAt = Environment.TickCount;
                _failed = address == null;
                if (address != null) _address = address;
            };

            Log.Write(address != null
                ? string.Format("publicip: {0} from {1} in {2:0} ms", address, source, ms)
                : "publicip: no answer - " + source);

            try
            {
                _marshaller.BeginInvoke(apply, null);
            }
            catch (Exception ex)
            {
                // The controller's window is going away; there is no one left to tell.
                Log.Write("publicip: marshal failed - " + ex.Message);
            }
        }

        /// <summary>
        /// Asks the primary, then the fallback, synchronously. The address, or null with
        /// source saying what went wrong with each. Shared by the worker and --publicip.
        /// </summary>
        public static string Lookup(out string source, out double ms)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string error1, error2;
            string address = Ask(PrimaryUrl, out error1);
            if (address != null)
            {
                source = PrimaryUrl;
            }
            else
            {
                address = Ask(FallbackUrl, out error2);
                source = address != null
                    ? FallbackUrl + " (primary failed: " + error1 + ")"
                    : "primary: " + error1 + "; fallback: " + error2;
            }

            ms = watch.Elapsed.TotalMilliseconds;
            return address;
        }

        /// <summary>
        /// One request to one service. The address, or null with error saying why. Never
        /// throws. Public so --publicip can try each service on its own.
        /// </summary>
        public static string Ask(string url, out string error)
        {
            error = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.UserAgent = "DesktopSwitcher";

                // The system proxy, which is the default anyway; said so because a machine
                // that must go through one has no other way out.
                request.Proxy = WebRequest.DefaultWebProxy;

                string body;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    body = reader.ReadToEnd();

                string address = Parse(body);
                if (address == null) error = "no IPv4 address in the reply";
                return address;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// The IPv4 address in a reply, or null. Cloudflare's trace is key=value lines with
        /// the address on "ip="; ipify's is the bare address. Both are read here so either
        /// URL works with either format. Anything that does not parse as IPv4 is refused -
        /// a captive portal answering every request with its own login page must not end up
        /// on someone's clipboard as their address.
        /// </summary>
        public static string Parse(string body)
        {
            if (body == null) return null;

            string candidate = body.Trim();
            foreach (string raw in body.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("ip=", StringComparison.Ordinal))
                {
                    candidate = line.Substring(3);
                    break;
                }
            }

            IPAddress parsed;
            if (!IPAddress.TryParse(candidate, out parsed)) return null;
            if (parsed.AddressFamily != AddressFamily.InterNetwork) return null;

            // TryParse also accepts shorthand like "1" or "1.2" as IPv4, which no service
            // would ever send but a garbled reply might. The round-trip insists on the
            // dotted quad it would have sent.
            string normal = parsed.ToString();
            return normal == candidate ? normal : null;
        }
    }
}
