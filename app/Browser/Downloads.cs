using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;

namespace Nyx.Browser
{
    public sealed class DownloadItem
    {
        public string Name, Url, TempPath, Status = "Downloading";
        public long Received, Total;
        public bool Done, Failed;
        public StorageFile Saved;
        public double Progress => Total > 0 ? Math.Min(1.0, (double)Received / Total) : 0;
    }

    /// <summary>
    /// Downloads. WebView2 writes the file where we tell it (inside the app's data folder); when it is complete the
    /// file is copied into your Downloads folder.
    /// </summary>
    public static class DownloadManager
    {
        public static readonly List<DownloadItem> Items = new List<DownloadItem>();
        public static event Action Changed;
        public static event Action<string> Notice;

        private static string TempDir
        {
            get
            {
                var d = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx", "downloads");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string TempPathFor(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "download";
            return Path.Combine(TempDir, Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name);
        }

        public static void Track(CoreWebView2DownloadOperation op, string name, string tempPath)
        {
            var item = new DownloadItem { Name = name, Url = op.Uri, TempPath = tempPath };
            lock (Items) Items.Insert(0, item);
            var last = DateTime.MinValue;

            op.BytesReceivedChanged += (s, a) =>
            {
                item.Received = (long)op.BytesReceived;
                item.Total = op.TotalBytesToReceive;
                if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(400)) { last = DateTime.UtcNow; Changed?.Invoke(); }
            };

            op.StateChanged += async (s, a) =>
            {
                try
                {
                    if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        item.Received = item.Total = Math.Max(item.Total, item.Received);
                        await SaveAsync(item);
                    }
                    else if (op.State == CoreWebView2DownloadState.Interrupted)
                    {
                        item.Failed = true; item.Done = true;
                        item.Status = "Interrupted (" + op.InterruptReason + ")";
                        NyxLog.Warn("Downloads", "WebView2 interrupted " + item.Name + " from " + OriginOf(item.Url) + ": " + op.InterruptReason);
                        Notice?.Invoke(item.Name + ": download interrupted.");
                    }
                }
                catch (Exception ex)
                {
                    NyxLog.Error("Downloads", "Save download failed for " + item.Name, ex);
                }
                Changed?.Invoke();
            };
        }

        private static StorageFolder DownloadsFolder
        {
            get
            {
                try
                {
                    var p = UserDataPaths.GetDefault().Downloads;
                    if (!string.IsNullOrEmpty(p)) return StorageFolder.GetFolderFromPathAsync(p).AsTask().GetAwaiter().GetResult();
                }
                catch { }
                try { return KnownFolders.SavedPictures; }
                catch { return ApplicationData.Current.LocalFolder; }
            }
        }

        private static string OriginOf(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url ?? ""; }
        }

        private static async Task SaveAsync(DownloadItem item)
        {
            try
            {
                var folder = DownloadsFolder;
                if (folder == null)
                {
                    item.Status = "Saved to " + item.TempPath;
                    item.Done = true;
                    return;
                }
                var file = await StorageFile.GetFileFromPathAsync(item.TempPath);
                var dest = await file.CopyAsync(folder, item.Name, NameCollisionOption.GenerateUniqueName);
                item.Saved = dest;
                item.Done = true;
                item.Status = "Saved to Downloads";
                Notice?.Invoke(item.Name + " saved to Downloads.");
            }
            catch (Exception ex)
            {
                NyxLog.Error("Downloads", "Save download failed for " + item.Name, ex);
                item.Status = "Could not move to Downloads";
                item.Done = true;
            }
        }

        public static async void StartHttp(string url, string name, string referer, string cookieHeader, string userAgent)
        {
            var temp = TempPathFor(name);
            var item = new DownloadItem { Name = name, Url = url, TempPath = temp };
            lock (Items) Items.Insert(0, item);
            Changed?.Invoke();

            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromHours(4) })
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(userAgent) ? "Mozilla/5.0 Nyx" : userAgent);
                    if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
                    if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        item.Total = resp.Content.Headers.ContentLength ?? -1;

                        using (var src = await resp.Content.ReadAsStreamAsync())
                        using (var dst = File.Create(temp))
                        {
                            var buf = new byte[64 * 1024];
                            int read;
                            var last = DateTime.MinValue;
                            while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                            {
                                await dst.WriteAsync(buf, 0, read);
                                item.Received += read;
                                if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(400))
                                {
                                    last = DateTime.UtcNow;
                                    Changed?.Invoke();
                                }
                            }
                        }

                        await SaveAsync(item);
                    }
                }
            }
            catch (Exception ex)
            {
                NyxLog.Error("Downloads", "HTTP download failed for " + item.Name + " from " + OriginOf(url), ex);
                item.Failed = true; item.Done = true;
                item.Status = "Failed: " + ex.Message;
                Notice?.Invoke(item.Name + ": the download failed (" + ex.Message + ").");
            }
            Changed?.Invoke();
        }

        public static void ClearFinished()
        {
            lock (Items) Items.RemoveAll(i => i.Done);
            Changed?.Invoke();
        }
    }
}
