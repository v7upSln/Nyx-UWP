using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Storage;

namespace Nyx.Browser
{
    public sealed class Bookmark
    {
        public long Ticks;
        public string Url, Title;
    }

    /// <summary>Bookmarks in a tab-separated file (one line per bookmark).</summary>
    public static class BookmarkStore
    {
        private static readonly object Gate = new object();
        private static List<Bookmark> _items;
        public static event Action Changed;

        private static string FilePath
        {
            get
            {
                var d = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "bookmarks.tsv");
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        public static string Key(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int h = url.IndexOf('#');
            if (h >= 0) url = url.Substring(0, h);
            return url.TrimEnd('/').ToLowerInvariant();
        }

        private static List<Bookmark> Items()
        {
            if (_items != null) return _items;
            _items = new List<Bookmark>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var p = line.Split('\t');
                        if (p.Length >= 2 && long.TryParse(p[0], out var t))
                            _items.Add(new Bookmark { Ticks = t, Url = p[1], Title = p.Length > 2 ? p[2] : "" });
                    }
            }
            catch { }
            return _items;
        }

        private static void Save()
        {
            try { File.WriteAllLines(FilePath, Items().Select(b => $"{b.Ticks}\t{b.Url}\t{b.Title}")); } catch { }
        }

        public static bool IsBookmarked(string url)
        {
            var k = Key(url);
            lock (Gate) return k.Length > 0 && Items().Any(b => Key(b.Url) == k);
        }

        /// <summary>Adds or removes. Returns true when the page is bookmarked afterwards.</summary>
        public static bool Toggle(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://"))) return false;
            bool now;
            lock (Gate)
            {
                var items = Items();
                var k = Key(url);
                var existing = items.FirstOrDefault(b => Key(b.Url) == k);
                if (existing != null) { items.Remove(existing); now = false; }
                else { items.Add(new Bookmark { Ticks = DateTime.UtcNow.Ticks, Url = url, Title = Clean(title) }); now = true; }
                Save();
            }
            Changed?.Invoke();
            return now;
        }

        public static void Remove(string url)
        {
            lock (Gate)
            {
                var k = Key(url);
                Items().RemoveAll(b => Key(b.Url) == k);
                Save();
            }
            Changed?.Invoke();
        }

        public static List<Bookmark> All()
        {
            lock (Gate) return Items().AsEnumerable().Reverse().ToList();
        }

        public static List<Bookmark> Search(string query, int max)
        {
            lock (Gate)
            {
                IEnumerable<Bookmark> q = Items().AsEnumerable().Reverse();
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var t = query.Trim();
                    q = q.Where(b => b.Url.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || b.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return q.Take(max).ToList();
            }
        }
    }
}
