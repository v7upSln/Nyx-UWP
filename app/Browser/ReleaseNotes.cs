using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nyx.Browser
{
    /// <summary>
    /// Fetches the latest CHANGELOG.md directly from the GitHub repository and renders it
    /// in a clean, minimal developer timeline layout (Linear / Vercel style).
    /// </summary>
    public static class ReleaseNotes
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        public const string ChangelogRawUrl = "https://raw.githubusercontent.com/v7upSln/Nyx-UWP/main/CHANGELOG.md";
        public const string ChangelogFallbackUrl = "https://raw.githubusercontent.com/v7upSln/Nyx-UWP/master/CHANGELOG.md";

        public const string BundledFallback = @"# Changelog

## 0.7.0
- Video playback on Xbox should be a lot smoother now without random quits
- Fixed streaming sites throwing expired session errors or refusing to show players
- YouTube doesn't randomly pause at the start of videos anymore
- Quick bookmarks button right in the top bar
- Nicer looking page loading bar instead of the old dots
- Basic logging to make reporting bugs simpler
- Shows what's new the first time you open an update

## 0.6.0
- Check for updates from GitHub releases
- Better downloads handling and reopened tabs
- Sharper icons across tabs and bookmarks
- Refreshed new tab page with photo backgrounds and stats
- Overall playback and site compatibility fixes";

        public static async Task<string> BuildHtmlAsync()
        {
            string md = null;
            try
            {
                Http.DefaultRequestHeaders.UserAgent.ParseAdd("Nyx/" + UpdateChecker.Version);
                var resp = await Http.GetAsync(ChangelogRawUrl).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    resp = await Http.GetAsync(ChangelogFallbackUrl).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                    md = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch { }

            return BuildHtml(md ?? BundledFallback);
        }

        public static string BuildHtml(string markdown = null)
        {
            var raw = string.IsNullOrWhiteSpace(markdown) ? BundledFallback : markdown;
            var sections = ParseMarkdown(raw);

            var sb = new StringBuilder();
            sb.Append(@"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Changelog · Nyx</title>
<style>
:root {
  color-scheme: dark;
}
* {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
}
body {
  background: #121118;
  color: #c5c3d2;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
  font-size: 14.5px;
  line-height: 1.6;
  -webkit-font-smoothing: antialiased;
}
.container {
  max-width: 780px;
  margin: 0 auto;
  padding: 56px 28px 80px;
}
header {
  margin-bottom: 48px;
  padding-bottom: 24px;
  border-bottom: 1px solid #22202c;
}
.label {
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #8d86c9;
  margin-bottom: 8px;
}
h1 {
  font-size: 28px;
  font-weight: 600;
  color: #fbfbfe;
  letter-spacing: -0.02em;
  margin-bottom: 6px;
}
.sub {
  font-size: 14.5px;
  color: #7b788a;
}
.timeline {
  display: flex;
  flex-direction: column;
  gap: 48px;
}
.release {
  display: grid;
  grid-template-columns: 140px 1fr;
  gap: 24px;
}
.tag-col {
  padding-top: 2px;
}
.ver {
  display: inline-block;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 13px;
  font-weight: 600;
  color: #dcd7fa;
  background: #1c1a24;
  border: 1px solid #2f2b3e;
  padding: 2px 8px;
  border-radius: 6px;
}
.items-col {
  padding-left: 24px;
  border-left: 1px solid #22202c;
}
.list {
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.list li {
  position: relative;
  padding-left: 16px;
  color: #bab7ca;
}
.list li::before {
  content: '';
  position: absolute;
  left: 0;
  top: 9px;
  width: 4px;
  height: 4px;
  border-radius: 50%;
  background: #5c5872;
}
.tag-fix {
  color: #f28b82;
  font-weight: 600;
}
.tag-new {
  color: #81c995;
  font-weight: 600;
}
.tag-change {
  color: #aecbfa;
  font-weight: 600;
}
code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 12.5px;
  color: #ddd9f3;
  background: #1c1a26;
  border: 1px solid #2d293d;
  padding: 1px 5px;
  border-radius: 4px;
}
footer {
  margin-top: 64px;
  padding-top: 20px;
  border-top: 1px solid #22202c;
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 13px;
  color: #6a6778;
}
footer a {
  color: #8d86c9;
  text-decoration: none;
}
footer a:hover {
  text-decoration: underline;
}
@media (max-width: 600px) {
  .container {
    padding: 32px 18px 56px;
  }
  .release {
    grid-template-columns: 1fr;
    gap: 10px;
  }
  .items-col {
    padding-left: 0;
    border-left: none;
  }
}
</style>
</head>
<body>
<div class='container'>
  <header>
    <div class='label'>Nyx</div>
    <h1>Changelog</h1>
    <p class='sub'>Recent changes pulled from GitHub.</p>
  </header>
  <div class='timeline'>");

            foreach (var sec in sections)
            {
                sb.Append(@"
    <article class='release'>
      <div class='tag-col'>
        <span class='ver'>v").Append(SecurityElement.Escape(sec.Version)).Append(@"</span>
      </div>
      <div class='items-col'>
        <ul class='list'>");

                foreach (var line in sec.Lines)
                {
                    sb.Append("<li>").Append(FormatLine(line)).Append("</li>");
                }

                sb.Append(@"
        </ul>
      </div>
    </article>");
            }

            sb.Append(@"
  </div>
  <footer>
    <span>Logs: <code>nyx/logs/nyx.log</code></span>
    <a href='https://github.com/v7upSln/Nyx-UWP' target='_blank'>GitHub repository</a>
  </footer>
</div>
</body>
</html>");
            return sb.ToString();
        }

        private sealed class Section
        {
            public string Version;
            public readonly List<string> Lines = new List<string>();
        }

        private static List<Section> ParseMarkdown(string md)
        {
            var list = new List<Section>();
            Section cur = null;
            var lines = md.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.StartsWith("## "))
                {
                    cur = new Section { Version = t.Substring(3).Trim() };
                    list.Add(cur);
                }
                else if (t.StartsWith("- ") && cur != null)
                {
                    cur.Lines.Add(t.Substring(2).Trim());
                }
            }
            return list;
        }

        private static string FormatLine(string line)
        {
            var escaped = SecurityElement.Escape(line);

            // Turn `code` into <code>code</code>
            escaped = Regex.Replace(escaped, @"`([^`]+)`", "<code>$1</code>");

            // Format nonchalant category tags cleanly
            if (escaped.StartsWith("Fix: ", StringComparison.OrdinalIgnoreCase))
                escaped = "<span class='tag-fix'>Fix:</span> " + escaped.Substring(5);
            else if (escaped.StartsWith("New: ", StringComparison.OrdinalIgnoreCase))
                escaped = "<span class='tag-new'>New:</span> " + escaped.Substring(5);
            else if (escaped.StartsWith("Change: ", StringComparison.OrdinalIgnoreCase))
                escaped = "<span class='tag-change'>Change:</span> " + escaped.Substring(8);

            return escaped;
        }
    }
}
