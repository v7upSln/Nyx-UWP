using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Storage;

namespace Nyx.Browser
{
    public sealed class HistoryEntry
    {
        public long Ticks;
        public string Url, Title;
    }

    /// <summary>Browsing history kept in a tab-separated file (one line per visit), newest last.</summary>
    public static class HistoryStore
    {
        private const int MaxEntries = 5000;
        private static readonly object Gate = new object();
        private static List<HistoryEntry> _items;

        private static string FilePath
        {
            get
            {
                var d = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "history.tsv");
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static List<HistoryEntry> Items()
        {
            if (_items != null) return _items;
            _items = new List<HistoryEntry>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var p = line.Split('\t');
                        if (p.Length >= 2 && long.TryParse(p[0], out var t))
                            _items.Add(new HistoryEntry { Ticks = t, Url = p[1], Title = p.Length > 2 ? p[2] : "" });
                    }
            }
            catch { }
            return _items;
        }

        public static void Add(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://"))) return;
            lock (Gate)
            {
                var items = Items();
                var now = DateTime.UtcNow.Ticks;
                var last = items.Count > 0 ? items[items.Count - 1] : null;
                if (last != null && last.Url == url && now - last.Ticks < TimeSpan.FromSeconds(5).Ticks) return;
                var e = new HistoryEntry { Ticks = now, Url = url, Title = Clean(title) };
                items.Add(e);
                try { File.AppendAllText(FilePath, $"{e.Ticks}\t{e.Url}\t{e.Title}\n"); } catch { }
                if (items.Count > MaxEntries + 500) { items.RemoveRange(0, items.Count - MaxEntries); Rewrite(items); }
            }
        }

        /// <summary>Fills in the title of the most recent visit to this address.</summary>
        public static void UpdateTitle(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            lock (Gate)
            {
                var items = Items();
                for (int i = items.Count - 1; i >= 0 && i > items.Count - 8; i--)
                    if (items[i].Url == url)
                    {
                        if (items[i].Title != Clean(title)) { items[i].Title = Clean(title); Rewrite(items); }
                        return;
                    }
            }
        }

        public static List<HistoryEntry> Search(string query, int max)
        {
            lock (Gate)
            {
                IEnumerable<HistoryEntry> q = Items().AsEnumerable().Reverse();
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var t = query.Trim();
                    q = q.Where(e => e.Url.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     e.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return q.Take(max).ToList();
            }
        }


        /// <summary>Most visited sites (grouped by host), as a tile-ready title and root address.</summary>
        public static List<HistoryEntry> TopSites(int max)
        {
            lock (Gate)
            {
                var items = Items();
                var recent = items.Skip(Math.Max(0, items.Count - 2000));
                return recent
                    .Select(e => new { e, host = Uri.TryCreate(e.Url, UriKind.Absolute, out var u) ? u.Host : "" })
                    .Where(x => x.host.Length > 0)
                    .GroupBy(x => x.host)
                    .OrderByDescending(g => g.Count())
                    .Take(max)
                    .Select(g => new HistoryEntry { Url = "https://" + g.Key + "/", Title = g.Key.StartsWith("www.") ? g.Key.Substring(4) : g.Key, Ticks = g.Max(x => x.e.Ticks) })
                    .ToList();
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Items().Clear();
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
        }

        private static void Rewrite(List<HistoryEntry> items)
        {
            try { File.WriteAllLines(FilePath, items.Select(e => $"{e.Ticks}\t{e.Url}\t{e.Title}")); } catch { }
        }
    }
}
