using System;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Nyx.Browser
{
    /// <summary>
    /// Looks for a newer release in the project repository. It only checks and points you to the release page:
    /// nothing is downloaded or installed automatically.
    /// </summary>
    public static class UpdateChecker
    {
        public static readonly string Version = InstalledVersion();
        public const string Repo = "v7upSln/Nyx-UWP";
        public const string RepoUrl = "https://github.com/v7upSln/Nyx-UWP";

        private static string InstalledVersion()
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "0.7.0"; }
        }

        public sealed class Result
        {
            public bool Available;
            public string Version = "";
            public string Url = RepoUrl;
            public string Error;
        }

        private static readonly HttpClient Http = Create();

        private static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Nyx/" + Version);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return c;
        }

        private static bool Parse(string tag, out System.Version v)
        {
            tag = (tag ?? "").Trim().TrimStart('v', 'V');
            int dash = tag.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) tag = tag.Substring(0, dash);
            int dots = tag.Split('.').Length;
            while (dots++ < 3) tag += ".0";
            return System.Version.TryParse(tag, out v);
        }

        public static Result LatestResult { get; private set; }

        public static async Task<Result> CheckAsync()
        {
            var result = new Result();
            try
            {
                string tag = null, url = null;

                using (var resp = await Http.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (JsonObject.TryParse(body, out var o) && !o.GetNamedBoolean("prerelease", false))
                        {
                            tag = o.GetNamedString("tag_name", "");
                            url = o.GetNamedString("html_url", "");
                        }
                    }
                    else if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        result.Error = "GitHub returned HTTP " + (int)resp.StatusCode + " while checking releases.";
                        LatestResult = result;
            return result;
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    // no published release yet: fall back to the newest tag
                    using (var resp = await Http.GetAsync("https://api.github.com/repos/" + Repo + "/tags?per_page=1").ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (JsonArray.TryParse(body, out var arr) && arr.Count > 0)
                            {
                                tag = arr[0].GetObject().GetNamedString("name", "");
                                url = RepoUrl + "/releases/tag/" + Uri.EscapeDataString(tag);
                            }
                        }
                        else
                        {
                            result.Error = "GitHub returned HTTP " + (int)resp.StatusCode + " while checking tags.";
                            LatestResult = result;
            return result;
                        }
                    }
                }

                if (string.IsNullOrEmpty(tag)) { LatestResult = result; return result; }
                result.Version = tag.TrimStart('v', 'V');
                if (!string.IsNullOrEmpty(url)) result.Url = url;

                if (Parse(tag, out var remote) && Parse(Version, out var local) && remote > local) result.Available = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                NyxLog.Warn("Updater", "Release check failed: " + ex.Message);
            }
            LatestResult = result;
            return result;
        }
    }
}
