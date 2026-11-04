using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

namespace Nyx.Shields
{
    public sealed class FilterList
    {
        public string Id, Name, Url;
        public byte Permission;   // 1 = trusted (may use trusted scriptlets), as Brave does for its default lists
        public bool Lite;         // part of the smaller "Lite" profile
        public FilterList(string id, string name, string url, byte permission, bool lite)
        { Id = id; Name = name; Url = url; Permission = permission; Lite = lite; }
    }

    /// <summary>
    /// Keeps the filter lists fresh without hurting browsing:
    ///  * the compiled engine is cached on disk, so normal startups never compile anything;
    ///  * lists are fetched with conditional requests (ETag), 3 at a time, and only changed lists trigger a rebuild;
    ///  * downloading and compiling run on a below-normal-priority background thread.
    /// The list set mirrors Brave's default catalog (uBlock Origin lists + EasyList + Brave lists).
    /// </summary>
    public static class FilterListManager
    {
        // Smaller filter set when memory is tight (Settings > Filters). Applies after a restart.
        public static bool UseLiteProfile => Nyx.Browser.AppSettings.LiteLists;

        private const string U = "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/";
        private const string B = "https://raw.githubusercontent.com/brave/adblock-lists/master/";

        public static readonly FilterList[] Lists =
        {
            new FilterList("ubo-filters",   "uBlock filters",         U + "filters.txt", 1, true),
            new FilterList("ubo-2020",      "uBlock filters 2020",    U + "filters-2020.txt", 1, false),
            new FilterList("ubo-2021",      "uBlock filters 2021",    U + "filters-2021.txt", 1, false),
            new FilterList("ubo-2022",      "uBlock filters 2022",    U + "filters-2022.txt", 1, false),
            new FilterList("ubo-2023",      "uBlock filters 2023",    U + "filters-2023.txt", 1, false),
            new FilterList("ubo-2024",      "uBlock filters 2024",    U + "filters-2024.txt", 1, false),
            new FilterList("ubo-2025",      "uBlock filters 2025",    U + "filters-2025.txt", 1, true),
            new FilterList("ubo-2026",      "uBlock filters 2026",    U + "filters-2026.txt", 1, true),
            new FilterList("ubo-general",   "uBlock general",         U + "filters-general.txt", 1, false),
            new FilterList("ubo-badware",   "uBlock badware",         U + "badware.txt", 1, false),
            new FilterList("ubo-abuse",     "uBlock resource abuse",  U + "resource-abuse.txt", 1, false),
            new FilterList("ubo-unbreak",   "uBlock unbreak",         U + "unbreak.txt", 1, true),
            new FilterList("ubo-quickfix",  "uBlock quick fixes",     U + "quick-fixes.txt", 1, true),
            new FilterList("ubo-shorteners","uBlock link shorteners", U + "ubo-link-shorteners.txt", 1, false),
            new FilterList("ubo-privacy",   "uBlock privacy",         U + "privacy.txt", 1, false),
            new FilterList("easylist",      "EasyList",               "https://easylist.to/easylist/easylist.txt", 1, true),
            new FilterList("easyprivacy",   "EasyPrivacy",            "https://easylist.to/easylist/easyprivacy.txt", 1, true),
            new FilterList("urlhaus",       "URLhaus malware",        "https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-agh-online.txt", 1, false),
            new FilterList("brave-unbreak", "Brave unbreak",          B + "brave-unbreak.txt", 1, true),
            new FilterList("brave-lists-unbreak", "Brave unbreak (lists)", B + "brave-lists/brave-unbreak.txt", 1, false),
            new FilterList("brave-specific","Brave specific",         B + "brave-lists/brave-specific.txt", 1, true),
            new FilterList("brave-social",  "Brave social",           B + "brave-lists/brave-social.txt", 1, false),
            new FilterList("brave-sugarcoat","Brave sugarcoat",       B + "brave-lists/brave-sugarcoat.txt", 1, false),
            new FilterList("brave-firstparty","Brave first party",    B + "brave-lists/brave-firstparty.txt", 1, false),
        };

        private static IEnumerable<FilterList> Active => UseLiteProfile ? Lists.Where(l => l.Lite) : Lists;

        // One HttpClient for the whole app (cheap, reuses connections, handles gzip/brotli).
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        { Timeout = TimeSpan.FromSeconds(120) };

        private static string Dir
        {
            get
            {
                var d = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string ListPath(FilterList l) => Path.Combine(Dir, l.Id + ".txt");
        private static string CachePath => Path.Combine(Dir, "engine.bin");
        private static string StampPath => Path.Combine(Dir, "engine.stamp");

        public static bool HasDownloads => Active.Any(l => File.Exists(ListPath(l)));

        private static object Setting(string key)
        {
            try { return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var v) ? v : null; }
            catch { return null; }
        }

        private static void SetSetting(string key, object value)
        {
            try { ApplicationData.Current.LocalSettings.Values[key] = value; } catch { }
        }

        public static bool UpdateDue()
        {
            if (Setting("lists.lastUpdateTicks") is long ticks)
                return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(12);
            return true;
        }

        public static string MemoryInfo()
        {
            try { return (MemoryManager.AppMemoryUsage / 1048576) + " / " + (MemoryManager.AppMemoryUsageLimit / 1048576) + " MB"; }
            catch { return "n/a"; }
        }

        // ---------------------------------------------------------------- engine build / load

        private static string Stamp()
        {
            var parts = Active.Where(l => File.Exists(ListPath(l))).Select(l =>
            {
                var fi = new FileInfo(ListPath(l));
                return l.Id + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            });
            long starter = 0;
            try { starter = new FileInfo(Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "filters", "starter.txt")).Length; } catch { }
            long resources = 0;
            try { resources = new FileInfo(Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "filters", "resources.json")).Length; } catch { }
            // the bundled starter list is part of the engine: a new starter list must rebuild the cached engine
            return string.Join("|", parts) + "|" + ShieldEngine.NativeVersion() + "|" + (UseLiteProfile ? "lite" : "std") + "|s" + starter + "|r" + resources;
        }

        private static byte[] LoadResourcesBytes()
        {
            try
            {
                var overridePath = Path.Combine(Dir, "resources.json");
                if (File.Exists(overridePath)) return File.ReadAllBytes(overridePath);
                var bundled = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "filters", "resources.json");
                if (File.Exists(bundled)) return File.ReadAllBytes(bundled);
            }
            catch { }
            return null;
        }

        private static Task<T> RunBackground<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, 16 * 1024 * 1024)   // large stack: list compilation recurses
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "nyx-engine" };
            t.Start();
            return tcs.Task;
        }

        /// <summary>Loads the cached engine when the lists are unchanged, otherwise compiles. Never blocks the UI thread.</summary>
        public static Task<(ShieldEngine Engine, string Report)> LoadAsync()
        {
            return RunBackground<(ShieldEngine Engine, string Report)>(() =>
            {
                var sw = Stopwatch.StartNew();
                var stamp = Stamp();
                var resources = LoadResourcesBytes();

                try
                {
                    if (File.Exists(CachePath) && File.Exists(StampPath) && File.ReadAllText(StampPath) == stamp)
                    {
                        var cached = ShieldEngine.FromSerialized(File.ReadAllBytes(CachePath));
                        if (cached != null)
                        {
                            int n = cached.UseResources(resources);
                            return (cached, $"engine from cache in {sw.ElapsedMilliseconds} ms, {n} scriptlets, mem {MemoryInfo()}");
                        }
                    }
                }
                catch { /* fall through to a fresh build */ }

                ShieldEngine engine;
                int loaded = 0;
                using (var builder = new ShieldEngine.Builder())
                {
                    var starter = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "filters", "starter.txt");
                    if (File.Exists(starter)) builder.Add(File.ReadAllBytes(starter), 1);
                    foreach (var l in Active.Where(l => File.Exists(ListPath(l))))
                    {
                        if (builder.Add(File.ReadAllBytes(ListPath(l)), l.Permission)) loaded++;   // one list at a time: low peak memory
                    }
                    engine = builder.Build();
                }
                if (engine == null) return (null, "engine build FAILED");
                int scriptlets = engine.UseResources(resources);
                var buildMs = sw.ElapsedMilliseconds;

                try
                {
                    var bytes = engine.Serialize();
                    if (bytes != null)
                    {
                        File.WriteAllBytes(CachePath, bytes);
                        File.WriteAllText(StampPath, stamp);
                    }
                }
                catch { }
                return (engine, $"engine built from {loaded} lists in {buildMs} ms, {scriptlets} scriptlets, mem {MemoryInfo()}");
            });
        }

        // ---------------------------------------------------------------- downloading

        /// <summary>Conditional download of all lists. Returns true when at least one list changed.</summary>
        public static async Task<bool> UpdateAsync(Action<string> log)
        {
            int changed = 0, failed = 0, done = 0;
            var lists = Active.ToList();
            using (var gate = new SemaphoreSlim(3))
            {
                var tasks = lists.Select(async l =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (await DownloadAsync(l).ConfigureAwait(false)) Interlocked.Increment(ref changed);
                    }
                    catch (Exception) { Interlocked.Increment(ref failed); }
                    finally
                    {
                        gate.Release();
                        log?.Invoke($"updating filters {Interlocked.Increment(ref done)}/{lists.Count}");
                    }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            if (failed < lists.Count) SetSetting("lists.lastUpdateTicks", DateTime.UtcNow.Ticks);
            log?.Invoke(changed == 0 ? "filters up to date" : $"{changed} lists updated" + (failed > 0 ? $", {failed} failed" : ""));
            return changed > 0;
        }

        private static async Task<bool> DownloadAsync(FilterList l)
        {
            var path = ListPath(l);
            var req = new HttpRequestMessage(HttpMethod.Get, l.Url);
            var etag = Setting("etag." + l.Id) as string;
            if (!string.IsNullOrEmpty(etag) && File.Exists(path)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotModified) return false;
                resp.EnsureSuccessStatusCode();

                var tmp = path + ".tmp";
                using (var net = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = File.Create(tmp))
                    await net.CopyToAsync(file).ConfigureAwait(false);

                if (new FileInfo(tmp).Length < 200) { File.Delete(tmp); return false; }   // reject empty / truncated answers
                File.Move(tmp, path, true);
                if (resp.Headers.ETag != null) SetSetting("etag." + l.Id, resp.Headers.ETag.Tag);
                return true;
            }
        }
    }
}
