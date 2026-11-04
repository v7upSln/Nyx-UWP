using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Windows.Storage;

namespace Nyx.Browser
{
    /// <summary>
    /// Lifetime protection statistics for the new tab page: totals, the last seven days and the most blocked hosts.
    /// Bandwidth and time are estimates based on typical sizes of the blocked resource types.
    /// </summary>
    public static class Stats
    {
        private const double SecondsPerRequest = 0.04;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> Daily = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> Hosts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static long _blocked, _bytes;
        private static bool _loaded, _dirty;

        private static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    var v = ApplicationData.Current.LocalSettings.Values;
                    if (v.TryGetValue("stats.blocked", out var b) && b is long lb) _blocked = lb;
                    if (v.TryGetValue("stats.bytes", out var y) && y is long ly) _bytes = ly;
                    ParseInto(v.TryGetValue("stats.daily", out var d) ? d as string : null, Daily);
                    ParseInto(v.TryGetValue("stats.hosts", out var h) ? h as string : null, Hosts);
                }
                catch { }
            }
        }

        private static void ParseInto(string text, Dictionary<string, long> into)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var part in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.LastIndexOf(':');
                if (c > 0 && long.TryParse(part.Substring(c + 1), out var n)) into[part.Substring(0, c)] = n;
            }
        }

        private static long Estimate(string type)
        {
            switch (type)
            {
                case "script": return 38 * 1024;
                case "image": return 52 * 1024;
                case "stylesheet": return 14 * 1024;
                case "font": return 28 * 1024;
                case "media": return 320 * 1024;
                case "sub_frame": return 110 * 1024;
                case "xmlhttprequest": return 4 * 1024;
                case "ping": return 1024;
                case "websocket": return 2 * 1024;
                default: return 12 * 1024;
            }
        }

        /// <summary>The site a tracker belongs to: example.co.uk, not cdn.ads.example.co.uk.</summary>
        private static string Registrable(string host)
        {
            var labels = (host ?? "").ToLowerInvariant().Split('.');
            if (labels.Length <= 2) return string.Join(".", labels);
            var sld = labels[labels.Length - 2];
            bool twoPart = labels[labels.Length - 1].Length == 2 && (sld == "co" || sld == "com" || sld == "org" || sld == "net" || sld == "gov" || sld == "ac");
            return string.Join(".", labels.Skip(labels.Length - (twoPart ? 3 : 2)));
        }

        public static void RecordBlock(string type, string url)
        {
            Load();
            string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Registrable(u.Host) : "";
            lock (Gate)
            {
                _blocked++;
                _bytes += Estimate(type);
                Daily[day] = (Daily.TryGetValue(day, out var d) ? d : 0) + 1;
                if (host.Length > 0) Hosts[host] = (Hosts.TryGetValue(host, out var h) ? h : 0) + 1;
                _dirty = true;
            }
        }

        public static void FlushIfDirty()
        {
            string daily, hosts; long blocked, bytes;
            lock (Gate)
            {
                if (!_dirty) return;
                _dirty = false;
                // keep 30 days and the 150 busiest hosts
                var cutoff = DateTime.Now.AddDays(-30).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                foreach (var k in Daily.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) Daily.Remove(k);
                if (Hosts.Count > 200)
                    foreach (var k in Hosts.OrderBy(p => p.Value).Take(Hosts.Count - 150).Select(p => p.Key).ToList()) Hosts.Remove(k);
                daily = string.Join(";", Daily.Select(p => p.Key + ":" + p.Value));
                hosts = string.Join(";", Hosts.Select(p => p.Key + ":" + p.Value));
                blocked = _blocked; bytes = _bytes;
            }
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                v["stats.blocked"] = blocked; v["stats.bytes"] = bytes;
                v["stats.daily"] = daily; v["stats.hosts"] = hosts;
            }
            catch { }
        }

        public static void Reset()
        {
            Load();
            lock (Gate) { _blocked = 0; _bytes = 0; Daily.Clear(); Hosts.Clear(); _dirty = true; }
            FlushIfDirty();
        }

        public static long Blocked { get { Load(); lock (Gate) return _blocked; } }
        public static string BlockedText => Blocked.ToString("N0", CultureInfo.InvariantCulture);

        public static (string Value, string Unit) Bandwidth()
        {
            Load();
            double b; lock (Gate) b = _bytes;
            if (b >= 1024.0 * 1024 * 1024) return ((b / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture), "GB");
            if (b >= 1024.0 * 1024) return ((b / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture), "MB");
            return (Math.Round(b / 1024.0).ToString("0", CultureInfo.InvariantCulture), "KB");
        }

        public static (string Value, string Unit) TimeSaved()
        {
            double s = Blocked * SecondsPerRequest;
            if (s >= 3600) return ((s / 3600).ToString("0.#", CultureInfo.InvariantCulture), "Hours");
            if (s >= 60) return (Math.Round(s / 60).ToString("0", CultureInfo.InvariantCulture), "Min");
            return (Math.Round(s).ToString("0", CultureInfo.InvariantCulture), "Sec");
        }

        /// <summary>The last seven days, oldest first, today last.</summary>
        public static List<KeyValuePair<DateTime, long>> Last7Days()
        {
            Load();
            var list = new List<KeyValuePair<DateTime, long>>();
            lock (Gate)
                for (int i = 6; i >= 0; i--)
                {
                    var day = DateTime.Now.Date.AddDays(-i);
                    Daily.TryGetValue(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture), out var n);
                    list.Add(new KeyValuePair<DateTime, long>(day, n));
                }
            return list;
        }

        public static List<KeyValuePair<string, long>> TopHosts(int count)
        {
            Load();
            lock (Gate) return Hosts.OrderByDescending(p => p.Value).Take(count).ToList();
        }
    }
}
