using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Nyx.Browser
{
    /// <summary>
    /// Site icons. WebView2 only hands out the small favicon, which looks soft on tabs. Nyx instead asks the page for its
    /// best icon (apple-touch-icon or the largest icon), keeps it on disk and shows it downscaled, so it stays sharp.
    /// </summary>
    public static class Favicons
    {
        private static readonly Dictionary<string, BitmapImage> Mem = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        public static event Action<string> Updated;

        /// <summary>Drops in-memory icons under memory pressure; they are reloaded from disk when next drawn.</summary>
        public static void TrimCache()
        {
            lock (Mem) Mem.Clear();
        }

        static Favicons()
        {
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Nyx");
        }

        private static string Dir
        {
            get
            {
                var d = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx", "favicons");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string FileName(string host) => new string(host.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray()) + ".img";
        private static string PathOf(string host) => Path.Combine(Dir, FileName(host));

        private static string Bare(string host)
        {
            host = (host ?? "").ToLowerInvariant();
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        /// <summary>Cached icon for a host, or null. Call on the UI thread.</summary>
        public static BitmapImage Get(string host)
        {
            host = Bare(host);
            if (host.Length == 0) return null;
            lock (Mem) { if (Mem.TryGetValue(host, out var cached)) return cached; }
            try
            {
                if (!File.Exists(PathOf(host))) return null;
                var bmp = new BitmapImage { DecodePixelWidth = 40 };
                bmp.UriSource = new Uri("ms-appdata:///local/nyx/favicons/" + FileName(host));
                lock (Mem)
                {
                    if (Mem.Count > 192) Mem.Clear();   // keep the cache bounded on a low-memory console
                    Mem[host] = bmp;
                }
                return bmp;
            }
            catch { return null; }
        }

        public static bool IsStale(string host)
        {
            try
            {
                var p = PathOf(Bare(host));
                return !File.Exists(p) || DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > TimeSpan.FromDays(7);
            }
            catch { return true; }
        }

        private const string FindIcons =
            "(function(){var o=[];document.querySelectorAll('link[rel~=icon i],link[rel~=apple-touch-icon i],link[rel=\"shortcut icon\" i]')" +
            ".forEach(function(l){if(l.href)o.push({h:l.href,s:(l.sizes&&l.sizes.value)||'',r:l.rel||'',t:l.type||''});});return JSON.stringify(o);})()";

        private static int Score(string rel, string sizes, string href, string type)
        {
            if (href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || type.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0) return -1;
            int best = rel.IndexOf("apple-touch-icon", StringComparison.OrdinalIgnoreCase) >= 0 ? 150 : 16;
            foreach (var part in sizes.Split(' '))
            {
                var wh = part.ToLowerInvariant().Split('x');
                if (wh.Length == 2 && int.TryParse(wh[0], out var w) && w > 0) best = Math.Max(best, Math.Min(w, 192));
            }
            return best;
        }

        /// <summary>Finds, downloads and stores the page's best icon. Returns the image, or null.</summary>
        public static async Task<BitmapImage> EnsureAsync(CoreWebView2 core, string pageUrl)
        {
            try
            {
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return null;
                var host = Bare(page.Host);

                var candidates = new List<Tuple<int, string>>();
                try
                {
                    var raw = await core.ExecuteScriptAsync(FindIcons);
                    var inner = JsonValue.Parse(raw).GetString();
                    if (JsonArray.TryParse(inner, out var arr))
                        foreach (var v in arr)
                        {
                            var o = v.GetObject();
                            var href = o.GetNamedString("h", "");
                            int score = Score(o.GetNamedString("r", ""), o.GetNamedString("s", ""), href, o.GetNamedString("t", ""));
                            if (score > 0 && href.StartsWith("http")) candidates.Add(Tuple.Create(score, href));
                        }
                }
                catch { }

                candidates = candidates.OrderByDescending(c => c.Item1).ToList();
                candidates.Add(Tuple.Create(0, new Uri(page, "/favicon.ico").ToString()));

                foreach (var c in candidates.Take(3))
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(c.Item2);
                        if (bytes.Length < 60 || bytes.Length > 700 * 1024) continue;
                        File.WriteAllBytes(PathOf(host), bytes);
                        lock (Mem) Mem.Remove(host);
                        var bmp = Get(host);
                        if (bmp != null) { Updated?.Invoke(host); return bmp; }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- icons for sites you have not opened yet

        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);

        /// <summary>Fetches a site's own icon in the background (its home page and /favicon.ico), so suggestions show the real one.</summary>
        public static void Prefetch(string host)
        {
            host = Bare(host);
            if (host.Length == 0 || host.IndexOf('.') < 0 || File.Exists(PathOf(host))) return;
            lock (InFlight)
            {
                if (Failed.Contains(host) || !InFlight.Add(host)) return;
            }
            _ = Task.Run(async () =>
            {
                bool ok = false;
                await Gate.WaitAsync();
                try { ok = await FetchHostAsync(host); }
                catch { }
                finally
                {
                    Gate.Release();
                    lock (InFlight) { InFlight.Remove(host); if (!ok) Failed.Add(host); }
                }
            });
        }

        private static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
        }

        private static async Task<bool> FetchHostAsync(string host)
        {
            foreach (var root in new[] { "https://" + host + "/", "https://www." + host + "/" })
            {
                try
                {
                    string html;
                    using (var cts = new CancellationTokenSource(6000))
                    using (var resp = await Http.GetAsync(root, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!resp.IsSuccessStatusCode) continue;
                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        {
                            var buf = new byte[150 * 1024]; int total = 0, n;
                            while (total < buf.Length && (n = await stream.ReadAsync(buf, total, buf.Length - total, cts.Token)) > 0) total += n;
                            html = Encoding.UTF8.GetString(buf, 0, total);
                        }
                    }

                    var baseUri = new Uri(root);
                    var found = new List<Tuple<int, string>>();
                    foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
                    {
                        var rel = Attr(tag.Value, "rel");
                        if (rel.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var href = Attr(tag.Value, "href");
                        if (href.Length == 0) continue;
                        int score = Score(rel, Attr(tag.Value, "sizes"), href, Attr(tag.Value, "type"));
                        if (score > 0 && Uri.TryCreate(baseUri, href, out var abs) && abs.Scheme.StartsWith("http")) found.Add(Tuple.Create(score, abs.ToString()));
                    }
                    found = found.OrderByDescending(f => f.Item1).Take(3).ToList();
                    found.Add(Tuple.Create(0, new Uri(baseUri, "/favicon.ico").ToString()));

                    foreach (var f in found)
                    {
                        try
                        {
                            var bytes = await Http.GetByteArrayAsync(f.Item2);
                            if (bytes.Length < 60 || bytes.Length > 700 * 1024) continue;
                            File.WriteAllBytes(PathOf(host), bytes);
                            lock (Mem) Mem.Remove(host);
                            Updated?.Invoke(host);
                            return true;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>Like Create, but starts with a letter and swaps in the real icon as soon as it has been fetched.</summary>
        public static FrameworkElement CreateLive(string host, double size)
        {
            var holder = new ContentControl
            {
                Width = size, Height = size, Padding = new Thickness(0), IsTabStop = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = Create(host, size)
            };
            var key = Bare(host);
            if (key.Length > 0 && Get(key) == null)
            {
                Action<string> handler = null;
                handler = updated =>
                {
                    if (!string.Equals(updated, key, StringComparison.OrdinalIgnoreCase)) return;
                    Updated -= handler;
                    var _ = holder.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => holder.Content = Create(host, size));
                };
                Updated += handler;
                Prefetch(key);
            }
            return holder;
        }

        private static Color Hue(string s)
        {
            int hash = 0;
            foreach (var ch in s) hash = (hash * 31 + ch) & 0xFFFF;
            double hue = hash % 360, sat = 0.42, lig = 0.42;
            double c = (1 - Math.Abs(2 * lig - 1)) * sat, x = c * (1 - Math.Abs((hue / 60) % 2 - 1)), m = lig - c / 2;
            double r = 0, g = 0, b = 0;
            if (hue < 60) { r = c; g = x; } else if (hue < 120) { r = x; g = c; } else if (hue < 180) { g = c; b = x; }
            else if (hue < 240) { g = x; b = c; } else if (hue < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        /// <summary>A site icon of the given size: the real icon when known, otherwise a colored letter.</summary>
        public static FrameworkElement Create(string host, double size)
        {
            var bmp = Get(host);
            if (bmp != null)
                return new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };

            var name = Bare(host);
            var letter = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
            var grid = new Grid { Width = size, Height = size };
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(Hue(name)) });
            grid.Children.Add(new TextBlock
            {
                Text = letter, FontSize = size * 0.5, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return grid;
        }
    }
}
