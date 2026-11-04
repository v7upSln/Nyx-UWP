using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Nyx.Browser
{
    /// <summary>
    /// New tab page: photography background, a clock and your protection statistics.
    /// Search happens in the address bar.
    /// </summary>
    public static class StartPage
    {
        private const string Css = @"
*{box-sizing:border-box}
html,body{height:100%;margin:0;overflow:hidden}
body{font-family:'Segoe UI',system-ui,sans-serif;color:#fff;background:#14102a}
.bg{position:fixed;inset:0;background:radial-gradient(1200px 700px at 25% 15%,#5b3aa8 0%,#2a1860 45%,#120d26 100%)}
.bg img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;opacity:0;transition:opacity 1.2s ease;image-rendering:auto}
.shade{position:fixed;inset:0;background:linear-gradient(180deg,rgba(8,6,20,.42) 0%,rgba(8,6,20,.08) 40%,rgba(8,6,20,.62) 100%)}
main{position:relative;height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:6px;padding:24px}
.clock{font-size:100px;font-weight:300;letter-spacing:.02em;line-height:1;text-shadow:0 2px 28px rgba(0,0,0,.40)}
.date{font-size:21px;opacity:.92;margin-bottom:34px;text-shadow:0 1px 14px rgba(0,0,0,.40)}
.stats{width:min(720px,92vw);padding:20px 26px 22px;border-radius:18px;background:rgba(36,42,56,.50);border:1px solid rgba(255,255,255,.14);
  backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);box-shadow:0 8px 40px rgba(0,0,0,.25)}
.label{font-size:11px;font-weight:600;letter-spacing:.14em;opacity:.78;margin-bottom:14px}
.cols{display:grid;grid-template-columns:repeat(3,1fr);gap:16px}
.num{font-size:32px;font-weight:600;line-height:1.1;white-space:nowrap}
.num small{font-size:15px;font-weight:600;margin-left:4px;opacity:.85}
.orange{color:#ff7e50}.violet{color:#a9aaff}.white{color:#fff}
.cap{font-size:12.5px;opacity:.82;margin-top:5px}
.rule{height:1px;background:rgba(255,255,255,.12);margin:18px 0 14px}
.lower{display:grid;grid-template-columns:1.1fr 1fr;gap:26px;align-items:end}
.lower .label{margin-bottom:8px}
.chart svg{width:100%;height:64px;display:block}
.top ol{list-style:none;margin:0;padding:0}
.top li{display:flex;justify-content:space-between;font-size:13px;padding:3px 0;opacity:.92}
.top li span:last-child{opacity:.7;font-variant-numeric:tabular-nums}
.empty{font-size:13px;opacity:.65}
.repo{position:fixed;right:18px;bottom:14px;display:flex;align-items:center;gap:11px;padding:9px 14px 9px 13px;border-radius:14px;
  background:rgba(20,16,42,.42);border:1px solid rgba(255,255,255,.16);backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
  color:#fff;text-decoration:none;transition:background .15s,border-color .15s}
.repo svg{width:22px;height:22px;fill:currentColor;flex:none}
.repo span{display:flex;flex-direction:column;line-height:1.25}
.repo b{font-size:13px;font-weight:600}
.repo small{font-size:11px;opacity:.72}
.repo em{font-style:normal;font-size:10.5px;padding:2px 8px;border-radius:999px;background:rgba(255,255,255,.16);transition:background .15s}
.repo.has-update em{background:#ff7e50;color:#14102a;font-weight:700}
.repo:hover,.repo:focus{background:rgba(114,90,193,.6);border-color:rgba(255,255,255,.4);outline:none}
@media(max-width:640px){.cols{grid-template-columns:1fr}.lower{grid-template-columns:1fr}.clock{font-size:68px}}
";

        private const string Script = @"
var P=[
'1668681919287-7367677cdc4c',
'1637825891028-564f672aa42c',
'1638272181967-7d3772a91265',
'1620641788421-7a1c342ea42e',
'1620121692029-d088224ddc74',
'1651870364199-fc5f9f46ac85',
'1687851898832-650714860119',
'1488972685288-c3fd157d7c7a',
'1759298928528-68604ad099f5',
'1460574283810-2aab119d8511',
'1776894873397-78ce93defb29',
'1464822759023-fed622ff2c3b',
'1778431193202-b0e2345eeab9',
'1515705576963-95cad62945b6',
'1504966981333-1ac8809be1ca',
'1470813740244-df37b8c1edcb'];
var p=P[Math.floor(Math.random()*P.length)];
// ask for the size this screen really needs (physical pixels, rounded up), at high JPEG quality
var dpr=window.devicePixelRatio||1,need=Math.max(window.innerWidth,screen.width)*dpr;
var w=Math.min(__MAXW__,Math.max(__MINW__,Math.ceil(need/320)*320));
var img=new Image();
img.decoding='async';
img.onload=function(){document.getElementById('bg').appendChild(img);requestAnimationFrame(function(){img.style.opacity=1;});};
img.src='https://images.unsplash.com/photo-'+p+'?fm=jpg&fit=max&q=__Q__&w='+w;
function tick(){var d=new Date();
document.getElementById('clock').textContent=d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'});
document.getElementById('date').textContent=d.toLocaleDateString([],{weekday:'long',month:'long',day:'numeric'});}
tick();setInterval(tick,15000);
";

        private static string Num(string value, string unit)
            => WebUtility.HtmlEncode(value) + (string.IsNullOrEmpty(unit) ? "" : " <small>" + WebUtility.HtmlEncode(unit) + "</small>");

        private static string Chart()
        {
            var days = Stats.Last7Days();
            long max = Math.Max(1, days.Max(d => d.Value));
            var sb = new StringBuilder("<svg viewBox='0 0 224 64' preserveAspectRatio='none' role='img' aria-label='Blocked per day, last 7 days'>");
            for (int i = 0; i < days.Count; i++)
            {
                double h = Math.Max(2, 44.0 * days[i].Value / max);
                bool today = i == days.Count - 1;
                sb.Append("<rect x='").Append((i * 32 + 4).ToString(CultureInfo.InvariantCulture)).Append("' y='").Append((48 - h).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append("' width='24' height='").Append(h.ToString("0.#", CultureInfo.InvariantCulture)).Append("' rx='4' fill='").Append(today ? "#ff7e50" : "rgba(255,255,255,.32)").Append("'><title>")
                  .Append(days[i].Key.ToString("ddd d MMM", CultureInfo.InvariantCulture)).Append(": ").Append(days[i].Value.ToString("N0", CultureInfo.InvariantCulture)).Append(" blocked</title></rect>")
                  .Append("<text x='").Append((i * 32 + 16).ToString(CultureInfo.InvariantCulture)).Append("' y='61' font-size='9' text-anchor='middle' fill='rgba(255,255,255,.7)'>")
                  .Append(days[i].Key.ToString("ddd", CultureInfo.InvariantCulture).Substring(0, 1)).Append("</text>");
            }
            return sb.Append("</svg>").ToString();
        }

        private static string TopList()
        {
            var top = Stats.TopHosts(3);
            if (top.Count == 0) return "<div class='empty'>Nothing blocked yet. Browse a few sites.</div>";
            var sb = new StringBuilder("<ol>");
            foreach (var h in top)
                sb.Append("<li><span>").Append(WebUtility.HtmlEncode(h.Key)).Append("</span><span>").Append(h.Value.ToString("N0", CultureInfo.InvariantCulture)).Append("</span></li>");
            return sb.Append("</ol>").ToString();
        }

        private static string _cached;
        private static DateTime _cachedAt;

        /// <summary>Lets the memory guard throw the built page away; it is cheap to build again.</summary>
        public static void ClearCache() { _cached = null; }

        private static bool IsXbox
        {
            get
            {
                try { return Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox"; }
                catch { return false; }
            }
        }

        public static string Build()
        {
            // Tabs are created one after another; rebuilding the whole page (and its stats) each time is wasted work.
            if (_cached != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(20)) return _cached;

            var bw = Stats.Bandwidth();
            var tm = Stats.TimeSaved();
            var stats = new StringBuilder();
            stats.Append("<section class='stats'><div class='label'>STATS</div><div class='cols'>")
                 .Append("<div><div class='num orange'>").Append(WebUtility.HtmlEncode(Stats.BlockedText)).Append("</div><div class='cap'>Trackers &amp; ads blocked</div></div>")
                 .Append("<div><div class='num violet'>").Append(Num(bw.Value, bw.Unit)).Append("</div><div class='cap'>Bandwidth saved</div></div>")
                 .Append("<div><div class='num white'>").Append(Num(tm.Value, tm.Unit)).Append("</div><div class='cap'>Time saved</div></div>")
                 .Append("</div><div class='rule'></div><div class='lower'>")
                 .Append("<div class='chart'><div class='label'>LAST 7 DAYS</div>").Append(Chart()).Append("</div>")
                 .Append("<div class='top'><div class='label'>MOST BLOCKED</div>").Append(TopList()).Append("</div>")
                 .Append("</div></section>");

            // The console draws at 1080p; a 3840px JPEG costs a lot of memory for no visible gain there.
            var script = Script
                .Replace("__MAXW__", IsXbox ? "1600" : "3840")
                .Replace("__MINW__", IsXbox ? "1280" : "1920")
                .Replace("__Q__", IsXbox ? "78" : "92");

            var html = @"<!doctype html><html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'><meta name='color-scheme' content='dark'>
<title>New Tab</title><style>" + Css + @"</style></head><body>
<div class='bg' id='bg'></div><div class='shade'></div>
<main><div class='clock' id='clock'></div><div class='date' id='date'></div>" + stats + @"</main>
" + BuildRepoCard() + @"
<script>" + script + @"</script></body></html>";

            _cached = html;
            _cachedAt = DateTime.UtcNow;
            return html;
        }

        private static string BuildRepoCard()
        {
            var update = UpdateChecker.LatestResult;
            var hasUpdate = update != null && update.Available;
            var href = hasUpdate && !string.IsNullOrEmpty(update.Url) ? update.Url : UpdateChecker.RepoUrl;
            var cls  = hasUpdate ? "repo has-update" : "repo";
            var badge = hasUpdate
                ? "&#8593; v" + System.Net.WebUtility.HtmlEncode(update.Version) + " available"
                : "v" + System.Net.WebUtility.HtmlEncode(UpdateChecker.Version);
            var sub = hasUpdate ? "Update available" : "Star it, report an issue, get updates";
            return "<a class='" + cls + "' href='" + href + "'>"
                 + "<svg viewBox='0 0 16 16' aria-hidden='true'><path d='M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z'/></svg>"
                 + "<span><b>Nyx on GitHub</b><small>" + sub + "</small></span>"
                 + "<em>" + badge + "</em></a>";
        }
    }

    public static class ErrorPage
    {
        public static string Build(string url, string status, string suggestedHost)
        {
            var u = WebUtility.HtmlEncode(url ?? "");
            var dym = string.IsNullOrEmpty(suggestedHost) ? ""
                : "<p>Did you mean <a class='plain' href='https://" + WebUtility.HtmlEncode(suggestedHost) + "'>" + WebUtility.HtmlEncode(suggestedHost) + "</a>?</p>";
            return @"<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='color-scheme' content='light dark'>
<title>Cannot reach this page</title><style>
:root{--bg:#FFFFFF;--text:#15141A;--muted:#5B5B66;--accent:#725AC1;--surface:#F0F0F4}
@media(prefers-color-scheme:dark){:root{--bg:#1C1B22;--text:#FBFBFE;--muted:#B2B2BC;--accent:#8D86C9;--surface:#2B2A33}}
body{background:var(--bg);color:var(--text);font-family:'Segoe UI',system-ui,sans-serif;margin:0;display:flex;align-items:center;justify-content:center;height:100vh}
main{max-width:640px;padding:32px}h1{font-size:28px;margin:0 0 12px}p{color:var(--muted);font-size:17px;line-height:1.5}
code{background:var(--surface);padding:2px 8px;border-radius:6px;color:var(--text);word-break:break-all}
a.plain{color:var(--accent);font-weight:600}
a.btn{display:inline-block;margin-top:16px;background:var(--accent);color:var(--bg);text-decoration:none;padding:12px 24px;border-radius:8px;font-size:17px}
</style></head><body><main><h1>Cannot reach this page</h1>
<p>Nyx could not load <code>" + u + @"</code>.</p>" + dym + @"
<p>Reason: " + WebUtility.HtmlEncode(status ?? "unknown") + @". Check the address and your connection.</p>
<a class='btn' href='" + u + @"'>Try again</a></main></body></html>";
        }
    }

    /// <summary>
    /// "Force dark" for sites with no dark mode of their own: after load, if the page background is light,
    /// invert it (and re-invert images and video). Sites that already honor prefers-color-scheme are left alone.
    /// </summary>
    public static class ForceDark
    {
        public const string Script = @"(function(){try{
if(window.top!==window)return;var done=false;
function lum(c){var m=c.match(/[\d.]+/g);if(!m||m.length<3)return 1;var a=m.length>3?parseFloat(m[3]):1;if(a===0)return -1;
return(0.2126*+m[0]+0.7152*+m[1]+0.0722*+m[2])/255;}
function check(){if(done)return;var b=document.body,h=document.documentElement;if(!b)return;
var l=lum(getComputedStyle(b).backgroundColor);if(l===-1)l=lum(getComputedStyle(h).backgroundColor);if(l===-1)l=1;
if(l>0.55){done=true;var s=document.createElement('style');
s.textContent='html{filter:invert(1) hue-rotate(180deg)!important;background:#fff}img,picture,video,canvas,iframe,[style*=""background-image""]{filter:invert(1) hue-rotate(180deg)!important}';
(document.head||h).appendChild(s);}}
document.addEventListener('DOMContentLoaded',check);window.addEventListener('load',check);
}catch(e){}})();";
    }
}
