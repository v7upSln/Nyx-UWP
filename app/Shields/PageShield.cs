using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Nyx.Browser;
using Windows.Data.Json;
using Windows.UI.Core;

namespace Nyx.Shields
{
    /// <summary>
    /// Hooks one CoreWebView2: network blocking, document-start cosmetic filtering and scriptlets,
    /// pop-up blocking and tracking-parameter stripping.
    /// </summary>
    public sealed class PageShield
    {
        // Sites where scriptlets matter most are registered up front, so they run before the very first load.
        private static readonly string[] PreloadHosts =
        {
            "www.youtube.com", "m.youtube.com", "music.youtube.com", "tv.youtube.com",
            "www.youtube-nocookie.com", "www.twitch.tv"
        };

        private readonly CoreWebView2 _core;
        private readonly Func<ShieldEngine> _engine;
        private readonly ShieldSettings _settings;
        private readonly CoreDispatcher _ui;
        private readonly Dictionary<string, string> _scriptIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _blocked;
        private string _ytScriptId;
        private string _compatScriptId;

        public int BlockedCount => Volatile.Read(ref _blocked);

        /// <summary>Raised for user-initiated new-window requests (links with target=_blank).</summary>
        public event Action<string> OpenInNewTab;

        public PageShield(CoreWebView2 core, Func<ShieldEngine> engine, ShieldSettings settings, CoreDispatcher ui)
        {
            _core = core; _engine = engine; _settings = settings; _ui = ui;
        }

        public void Attach()
        {
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnRequest;
            _core.NavigationStarting += OnNavStarting;
            _core.DOMContentLoaded += OnDomLoaded;
            _core.NewWindowRequested += OnNewWindow;
            _core.FrameCreated += (s, a) => HookFrame(a.Frame);
            _ = PreloadAsync();
            _ = ApplyYouTubeAsync();
            _ = ApplyCompatAsync();
        }

        /// <summary>Ad iframes: cancel the frame's navigation when the engine says block (page loads are never touched).</summary>
        private void HookFrame(CoreWebView2Frame frame)
        {
            frame.NavigationStarting += (s, a) =>
            {
                try
                {
                    var engine = _engine();
                    var page = _core.Source;
                    var uri = a.Uri;
                    if (engine == null || !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !Active(page)) return;
                    if (SiteCompatHelper.IsStreamingOrMediaUrl(uri, page))
                    {
                        NyxLog.Info("Shield", "Allowed video player iframe: " + uri);
                        return;
                    }
                    if ((engine.Check(uri, page, "sub_frame", "GET") & ShieldFlags.Block) != 0)
                    {
                        a.Cancel = true;
                        Interlocked.Increment(ref _blocked);
                        Stats.RecordBlock("sub_frame", uri);
                        NyxLog.Info("Shield", "Blocked iframe: " + uri + " on " + page);
                    }
                }
                catch { }
            };
            frame.FrameCreated += (s, a) => HookFrame(a.Frame);
        }

        private async Task ApplyYouTubeAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_ytScriptId)) { _core.RemoveScriptToExecuteOnDocumentCreated(_ytScriptId); _ytScriptId = null; }
                if (AppSettings.YouTubeHelper) _ytScriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeHelper.Script);
            }
            catch { }
        }

        private async Task ApplyCompatAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_compatScriptId)) { _core.RemoveScriptToExecuteOnDocumentCreated(_compatScriptId); _compatScriptId = null; }
                _compatScriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(SiteCompatHelper.Script);
            }
            catch { }
        }

        private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        private bool Active(string pageUrl) => _settings.Enabled && !_settings.IsDisabledFor(pageUrl);

        // ------------------------------------------------------------------ document-start scripts

        /// <summary>Call after the engine was swapped or a protection setting changed.</summary>
        public async Task ResetScriptsAsync()
        {
            foreach (var id in _scriptIds.Values)
            {
                if (string.IsNullOrEmpty(id)) continue;
                try { _core.RemoveScriptToExecuteOnDocumentCreated(id); } catch { }
            }
            _scriptIds.Clear();
            await PreloadAsync();
            await ApplyYouTubeAsync();
            await ApplyCompatAsync();
        }

        private async Task PreloadAsync()
        {
            foreach (var h in PreloadHosts) await EnsureHostAsync(h);
        }

        private async Task EnsureHostAsync(string host)
        {
            if (string.IsNullOrEmpty(host) || _scriptIds.ContainsKey(host)) return;
            var engine = _engine();
            if (engine == null || !_settings.Enabled || _settings.IsHostDisabled(host)) return;   // not remembered: retried later

            _scriptIds[host] = "";   // reserve first so concurrent calls do not double-register
            try
            {
                var c = engine.GetCosmetic("https://" + host + "/");
                if (c == null || (c.Css.Length == 0 && c.Script.Length == 0)) return;
                _scriptIds[host] = await _core.AddScriptToExecuteOnDocumentCreatedAsync(Bundle(host, c));
            }
            catch { _scriptIds.Remove(host); }
        }

        private static string JsString(string s) => JsonValue.CreateStringValue(s).Stringify();

        /// <summary>One script per host: runs in every frame at document start, only acts on that exact host.</summary>
        private static string Bundle(string host, CosmeticResult c)
        {
            var sb = new StringBuilder();
            sb.Append("(function(){try{if(location.hostname!==").Append(JsString(host)).Append(")return;");
            if (c.Css.Length > 0)
                sb.Append("try{var s=document.createElement('style');s.id='nyx-cosmetic';s.textContent=")
                  .Append(JsString(c.Css)).Append(";(document.head||document.documentElement).appendChild(s);}catch(e){}");
            if (c.Script.Length > 0)
                sb.Append("try{\n").Append(c.Script).Append("\n}catch(e){}");
            sb.Append("}catch(e){}})();");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ events

        /// <summary>
        /// "error" mode answers with a redirect to a scheme the browser refuses, so the request fails like it does in Brave.
        /// YouTube treats failing ad requests as proof of an ad blocker, so there blocked requests get a quiet empty success.
        /// </summary>
        private CoreWebView2WebResourceResponse BlockedResponse(CoreWebView2 sender, string page)
        {
            var env = sender.Environment;
            if (AppSettings.YouTubeHelper && YouTubeHelper.IsYouTube(page))
                return env.CreateWebResourceResponse(null, 200, "OK", "Content-Type: text/plain\r\nCache-Control: no-store\r\n");
            if (AppSettings.BlockMode == "empty")
                return env.CreateWebResourceResponse(null, 403, "Blocked by Nyx", "");
            return env.CreateWebResourceResponse(null, 302, "Found", "Location: nyx-blocked://blocked\r\nCache-Control: no-store\r\n");
        }

        private static string MapType(CoreWebView2WebResourceContext c)
        {
            switch (c)
            {
                case CoreWebView2WebResourceContext.Script: return "script";
                case CoreWebView2WebResourceContext.Image: return "image";
                case CoreWebView2WebResourceContext.Stylesheet: return "stylesheet";
                case CoreWebView2WebResourceContext.Font: return "font";
                case CoreWebView2WebResourceContext.Media: return "media";
                case CoreWebView2WebResourceContext.XmlHttpRequest:
                case CoreWebView2WebResourceContext.Fetch: return "xmlhttprequest";
                case CoreWebView2WebResourceContext.Ping: return "ping";
                case CoreWebView2WebResourceContext.Websocket: return "websocket";
                case CoreWebView2WebResourceContext.Document: return "sub_frame";
                default: return "other";
            }
        }

        private void OnRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var url = e.Request.Uri;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
                var ctx = e.ResourceContext;

                // Page loads are never touched here; ad iframes are cancelled by HookFrame.
                if (ctx == CoreWebView2WebResourceContext.Document) return;

                // ALWAYS inject Client Hints first so outgoing requests have modern desktop browser hints
                try
                {
                    var reqHdrs = e.Request.Headers;
                    if (!reqHdrs.Contains("Sec-Ch-Ua"))
                        reqHdrs.SetHeader("Sec-Ch-Ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
                    if (!reqHdrs.Contains("Sec-Ch-Ua-Mobile"))
                        reqHdrs.SetHeader("Sec-Ch-Ua-Mobile", "?0");
                    if (!reqHdrs.Contains("Sec-Ch-Ua-Platform"))
                        reqHdrs.SetHeader("Sec-Ch-Ua-Platform", "\"Windows\"");
                    if (!reqHdrs.Contains("Sec-Fetch-Site") && (ctx == CoreWebView2WebResourceContext.Fetch || ctx == CoreWebView2WebResourceContext.XmlHttpRequest))
                        reqHdrs.SetHeader("Sec-Fetch-Site", "same-origin");
                    if (!reqHdrs.Contains("Sec-Fetch-Mode") && (ctx == CoreWebView2WebResourceContext.Fetch || ctx == CoreWebView2WebResourceContext.XmlHttpRequest))
                        reqHdrs.SetHeader("Sec-Fetch-Mode", "cors");
                    if (!reqHdrs.Contains("Sec-Fetch-Dest") && (ctx == CoreWebView2WebResourceContext.Fetch || ctx == CoreWebView2WebResourceContext.XmlHttpRequest))
                        reqHdrs.SetHeader("Sec-Fetch-Dest", "empty");
                }
                catch { }

                var page = sender.Source;

                // Never block essential video player APIs, manifests, or stream hosters
                if (SiteCompatHelper.IsStreamingOrMediaUrl(url, page))
                {
                    NyxLog.Info("Shield", "Allowed media/stream: " + url);
                    return;
                }

                var engine = _engine();
                if (engine == null || !Active(page)) return;

                var flags = engine.Check(url, page, MapType(ctx), e.Request.Method);
                if ((flags & ShieldFlags.Block) != 0)
                {
                    e.Response = BlockedResponse(sender, page);
                    Interlocked.Increment(ref _blocked);
                    Stats.RecordBlock(MapType(ctx), url);
                    NyxLog.Info("Shield", "Blocked " + MapType(ctx) + ": " + url + " on " + page);
                }
            }
            catch { /* never let an exception escape into WebView2 */ }
        }

        private void OnNavStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Interlocked.Exchange(ref _blocked, 0);
            if (AppSettings.StripTracking && _settings.Enabled && UrlCleaner.TryClean(e.Uri, out var clean))
            {
                e.Cancel = true;
                var _ = _ui.RunAsync(CoreDispatcherPriority.Normal, () => _core.Navigate(clean));
                return;
            }
            // Register this host's scriptlets now. Not awaited: the navigation is never held up.
            var host = HostOf(e.Uri);
            if (host.Length > 0 && !_scriptIds.ContainsKey(host)) { var __ = EnsureHostAsync(host); }
        }

        // Fallback: hosts that were not registered in time still get their hiding CSS.
        private async void OnDomLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            try
            {
                var page = sender.Source;
                var host = HostOf(page);
                if (_scriptIds.TryGetValue(host, out var id) && id.Length > 0) return;   // already handled at document start
                var engine = _engine();
                if (engine == null || !Active(page)) return;
                var c = engine.GetCosmetic(page);
                if (c == null || c.Css.Length == 0) return;
                await sender.ExecuteScriptAsync(
                    "(function(){try{var s=document.getElementById('nyx-cosmetic')||document.createElement('style');" +
                    "s.id='nyx-cosmetic';s.textContent=" + JsString(c.Css) + ";(document.head||document.documentElement).appendChild(s);}catch(e){}})();");
            }
            catch { }
        }

        private static bool IsAdOrPopupUrl(string uri, string page)
        {
            if (string.IsNullOrEmpty(uri)) return true;
            if (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;

            var u = uri.ToLowerInvariant();
            if (u.Contains("popads") || u.Contains("popcash") || u.Contains("adsterra") ||
                u.Contains("propellerads") || u.Contains("exoclick") || u.Contains("hilltopads") ||
                u.Contains("onclick") || u.Contains("syndication") || u.Contains("adnxs") ||
                u.Contains("adclick") || u.Contains("1xbet") || u.Contains("bet365") ||
                u.Contains("trafficjunky") || u.Contains("clickadu") || u.Contains("monetag") ||
                u.Contains("yadro") || u.Contains("redirecting") || u.Contains("traffic") ||
                u.Contains("doubleclick") || u.Contains("adservice") || u.Contains("adserver") ||
                u.Contains("agl006") || u.Contains("lureful") || u.Contains("wingsmob") ||
                u.Contains("dtscout") || u.Contains("crwdcntrl") || u.Contains("spix"))
            {
                return true;
            }

            return false;
        }

        private void OnNewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            var uri = e.Uri ?? "";
            var page = sender.Source;

            if (_settings.Enabled && AppSettings.BlockPopups)
            {
                bool block = false;
                var engine = _engine();
                if (engine != null && uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Check popup rule in adblock engine
                    if ((engine.Check(uri, page, "popup", "GET") & ShieldFlags.Block) != 0)
                        block = true;
                    else if ((engine.Check(uri, page, "sub_frame", "GET") & ShieldFlags.Block) != 0)
                        block = true;
                }

                if (!block && IsAdOrPopupUrl(uri, page))
                    block = true;

                if (block)
                {
                    Interlocked.Increment(ref _blocked);
                    Stats.RecordBlock("popup", uri);
                    NyxLog.Info("Shield", "Blocked popup: " + uri + " from " + page);
                    return;
                }
            }

            // Legitimate link or popups allowed by settings
            if (OpenInNewTab != null) { var _ = _ui.RunAsync(CoreDispatcherPriority.Normal, () => OpenInNewTab(uri)); }
            else sender.Navigate(uri);
        }
    }

    internal static class UrlCleaner
    {
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fbclid","gclid","dclid","msclkid","yclid","igshid","mc_eid","mkt_tok","_hsenc","_hsmi","vero_id"
        };

        public static bool TryClean(string url, out string cleaned)
        {
            cleaned = url;
            if (string.IsNullOrEmpty(url)) return false;
            int q = url.IndexOf('?');
            if (q < 0) return false;
            int h = url.IndexOf('#', q);
            string frag = h >= 0 ? url.Substring(h) : "";
            string query = h >= 0 ? url.Substring(q + 1, h - q - 1) : url.Substring(q + 1);

            var kept = new List<string>();
            bool removed = false;
            foreach (var part in query.Split('&'))
            {
                var name = part.Split('=')[0];
                if (Names.Contains(name) || name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)) removed = true;
                else if (part.Length > 0) kept.Add(part);
            }
            if (!removed) return false;
            cleaned = url.Substring(0, q) + (kept.Count > 0 ? "?" + string.Join("&", kept) : "") + frag;
            return true;
        }
    }
}
