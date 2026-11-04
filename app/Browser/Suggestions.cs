using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Nyx.Browser
{
    public enum SuggestKind { Go, Search, DidYouMean, Bookmark, History, Remote }

    public sealed class Suggestion
    {
        public SuggestKind Kind;
        public string Text;   // main line
        public string Sub;    // second line
        public string Url;    // target for Go / DidYouMean / Bookmark / History
    }

    public static class Suggestions
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Nyx");
            return c;
        }

        // ---------------------------------------------------------------- address detection

        public static bool LooksLikeAddress(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Contains(" ")) return false;
            if (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(input.Split(':')[0], out _)) return true;
            return input.Contains(".") && Uri.TryCreate("https://" + input, UriKind.Absolute, out var u) && u.Host.Contains(".");
        }

        /// <summary>
        /// The address bar can hand over a search URL whose query is itself a search URL — the previous results
        /// page was still in the box when the new query was typed, so it got searched for as if it were the query.
        /// Unwrap that (only when the inner value really is a search URL), so Google does not appear twice in the URL.
        /// </summary>
        public static string Normalize(string input)
        {
            input = (input ?? "").Trim();
            for (int depth = 0; depth < 3; depth++)
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var url)) return input;
                if (url.Scheme != "http" && url.Scheme != "https") return input;

                string inner = SearchQueryValue(url);
                if (inner == null) return input;
                inner = inner.Trim();
                if (!inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return input;
                if (!IsSearchUrl(inner)) return input;
                input = inner;
            }
            return input;
        }

        /// <summary>The value of the search parameter, if this URL is one of the known engines' results pages.</summary>
        private static string SearchQueryValue(Uri url)
        {
            foreach (var engine in SearchEngines.All)
            {
                if (!MatchesAction(url, engine.Action)) continue;
                foreach (var pair in url.Query.TrimStart('?').Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!Uri.UnescapeDataString(pair.Substring(0, eq)).Equals(engine.Param, StringComparison.OrdinalIgnoreCase)) continue;
                    return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
                }
            }
            return null;
        }

        private static bool IsSearchUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var u) && SearchEngines.All.Any(e => MatchesAction(u, e.Action));

        private static bool MatchesAction(Uri url, string action)
        {
            // "https://duckduckgo.com/" and "https://duckduckgo.com" are the same page.
            var s = url.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return s.Equals(action.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Turns typed text into a URL: an address stays an address, anything else becomes a search.</summary>
        public static string ToUrl(string input)
        {
            input = (input ?? "").Trim();
            if (Uri.TryCreate(input, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https")) return input;
            if (LooksLikeAddress(input)) return "https://" + input;
            return SearchEngines.Current.Url(input);
        }

        public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

        // ---------------------------------------------------------------- local suggestions (instant)

        public static List<Suggestion> Local(string text)
        {
            var list = new List<Suggestion>();
            bool address = LooksLikeAddress(text);
            list.Add(address
                ? new Suggestion { Kind = SuggestKind.Go, Text = text, Sub = "Go to " + ToUrl(text), Url = ToUrl(text) }
                : new Suggestion { Kind = SuggestKind.Search, Text = text, Sub = "Search with " + SearchEngines.Current.Name });

            if (address)
            {
                var fix = DidYouMean.ForHost(HostOf(ToUrl(text)));
                if (fix != null) list.Add(new Suggestion { Kind = SuggestKind.DidYouMean, Text = fix, Sub = "Did you mean this site?", Url = "https://" + fix });
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in BookmarkStore.Search(text, 3))
                if (seen.Add(BookmarkStore.Key(b.Url)))
                    list.Add(new Suggestion { Kind = SuggestKind.Bookmark, Text = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title, Sub = b.Url, Url = b.Url });
            int history = 0;
            foreach (var h in HistoryStore.Search(text, 40))
            {
                if (history >= 4) break;
                if (!seen.Add(BookmarkStore.Key(h.Url))) continue;
                list.Add(new Suggestion { Kind = SuggestKind.History, Text = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title, Sub = h.Url, Url = h.Url });
                history++;
            }
            return list;
        }

        // ---------------------------------------------------------------- remote suggestions

        private static string SuggestUrl(string q)
        {
            var e = Uri.EscapeDataString(q);
            switch (SearchEngines.Current.Id)
            {
                case "google": return "https://suggestqueries.google.com/complete/search?client=firefox&q=" + e;
                case "bing": return "https://api.bing.com/osjson.aspx?query=" + e;
                case "brave": return "https://search.brave.com/api/suggest?q=" + e;
                default: return "https://duckduckgo.com/ac/?type=list&q=" + e;   // privacy-friendly fallback for the other engines
            }
        }

        public static async Task<List<string>> RemoteAsync(string text, CancellationToken ct)
        {
            var result = new List<string>();
            if (text.Length < 2 || LooksLikeAddress(text)) return result;
            try
            {
                using (var resp = await Http.GetAsync(SuggestUrl(text), ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return result;
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (JsonArray.TryParse(body, out var root) && root.Count > 1 && root[1].ValueType == JsonValueType.Array)
                        foreach (var v in root[1].GetArray())
                            if (v.ValueType == JsonValueType.String) result.Add(v.GetString());
                }
            }
            catch { }
            return result;
        }

        public static List<Suggestion> Merge(string text, List<Suggestion> local, List<string> remote)
        {
            var list = new List<Suggestion>(local);
            var fix = DidYouMeanQuery(text, remote);
            if (fix != null)
                list.Insert(Math.Min(1, list.Count), new Suggestion { Kind = SuggestKind.DidYouMean, Text = fix, Sub = "Did you mean", Url = null });
            foreach (var r in remote.Take(6))
            {
                if (r.Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
                if (fix != null && r.Equals(fix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new Suggestion { Kind = SuggestKind.Remote, Text = r, Sub = null });
            }
            return list.Take(10).ToList();
        }

        /// <summary>
        /// A correction, not a completion: the first suggestion does not start with what was typed, but is within a
        /// small edit distance of it ("facebok" becomes "facebook").
        /// </summary>
        private static string DidYouMeanQuery(string text, List<string> remote)
        {
            if (remote.Count == 0 || text.Length < 4) return null;
            var t = text.Trim().ToLowerInvariant();
            var s = remote[0];
            var sl = s.ToLowerInvariant();
            if (sl.StartsWith(t) || sl == t) return null;
            var prefix = sl.Length > t.Length ? sl.Substring(0, t.Length) : sl;
            int d = DidYouMean.Distance(t, prefix);
            return d > 0 && d <= Math.Max(1, t.Length / 4) ? s : null;
        }
    }

    public static class DidYouMean
    {
        private static readonly string[] Popular =
        {
            "google.com","youtube.com","facebook.com","instagram.com","twitter.com","x.com","reddit.com","wikipedia.org","amazon.com",
            "netflix.com","twitch.tv","github.com","stackoverflow.com","linkedin.com","microsoft.com","apple.com","bing.com",
            "duckduckgo.com","yahoo.com","whatsapp.com","tiktok.com","discord.com","spotify.com","paypal.com","ebay.com","cnn.com",
            "bbc.com","nytimes.com","openai.com","anthropic.com","steampowered.com","xbox.com","playstation.com","roblox.com",
            "pinterest.com","medium.com","quora.com","imdb.com","etsy.com","zoom.us","dropbox.com","adobe.com","office.com",
            "outlook.com","gmail.com","unsplash.com","cloudflare.com","mozilla.org","brave.com","proton.me","telegram.org"
        };

        public static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var tmp = prev; prev = cur; cur = tmp;
            }
            return prev[b.Length];
        }

        private static string Bare(string host)
        {
            var h = (host ?? "").ToLowerInvariant();
            return h.StartsWith("www.") ? h.Substring(4) : h;
        }

        /// <summary>A known site whose name is one or two typos away from this host, or null.</summary>
        public static string ForHost(string host)
        {
            var h = Bare(host);
            if (h.Length < 5 || !h.Contains(".")) return null;

            var known = new List<string>(Popular);
            known.AddRange(BookmarkStore.All().Select(b => Bare(Suggestions.HostOf(b.Url))));
            known.AddRange(HistoryStore.TopSites(30).Select(t => Bare(Suggestions.HostOf(t.Url))));

            string best = null; int bestD = int.MaxValue;
            foreach (var k in known.Where(k => k.Length > 0).Distinct())
            {
                if (k == h) return null;                       // it is a known site: nothing to correct
                int d = Distance(h, k);
                if (d < bestD) { bestD = d; best = k; }
            }
            int limit = h.Length >= 9 ? 2 : 1;
            return best != null && bestD > 0 && bestD <= limit ? best : null;
        }
    }
}
