using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Nyx.Browser;
using Nyx.Shields;
using Nyx_UWP.Views;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls;

namespace Nyx_UWP
{
    // Release features: startup, notices, downloads, sharp favicons, update check.
    public sealed partial class MainPage
    {
        private Task _engineTask;
        private string _programmaticText = "";
        private readonly Stack<string> _closedTabs = new Stack<string>();
        private DispatcherTimer _noticeTimer;

        private void InitFeatures()
        {
            AppServices.CheckUpdates = CheckUpdatesNowAsync;
            DownloadManager.Notice += m => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowNotice(m)); };
        }

        // ------------------------------------------------------------------ engine and environment

        private async Task LoadEngineAsync()
        {
            var (eng, report) = await FilterListManager.LoadAsync();
            _engine = eng;
            _listReport = report;
            foreach (var t in _tabs) { _ = t.Shield?.ResetScriptsAsync(); }   // register the document-start scripts now that rules exist
        }

        /// <summary>
        /// Starts the browser engine with extension support when it is turned on. A guard flag is set while this start is in
        /// progress: if the previous start with extensions never finished, extensions are turned off instead of locking you out.
        /// </summary>
        private async Task EnsureEnvironmentAsync()
        {
            if (_envTried) return;
            _envTried = true;

            try
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-accelerated-video-decode --disable-gpu-watchdog");
            }
            catch { }

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-accelerated-video-decode --disable-gpu-watchdog"
            };

            try
            {
                _env = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
            }
            catch (Exception ex)
            {
                NyxLog.Warn("WebView2", "CreateWithOptionsAsync failed, using default: " + ex.Message);
                try { _env = await CoreWebView2Environment.CreateAsync(); } catch { }
            }
        }

        // ------------------------------------------------------------------ small helpers

        private void SetUrlText(string text)
        {
            _programmaticText = text ?? "";
            UrlBox.Text = _programmaticText;
        }

        private static string TitleOf(TabKind kind)
        {
            switch (kind)
            {
                case TabKind.Settings: return "Settings";
                case TabKind.History: return "History";
                case TabKind.Bookmarks: return "Bookmarks";
                case TabKind.Downloads: return "Downloads";
                default: return "New Tab";
            }
        }

        private static Type PageOf(TabKind kind)
        {
            switch (kind)
            {
                case TabKind.Settings: return typeof(SettingsPage);
                case TabKind.History: return typeof(HistoryPage);
                case TabKind.Bookmarks: return typeof(BookmarksPage);
                default: return typeof(DownloadsPage);
            }
        }

        private UIElement BookmarkChip(Bookmark b)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(Favicons.CreateLive(Suggestions.HostOf(b.Url), 16));
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(b.Title) ? Suggestions.HostOf(b.Url) : b.Title,
                MaxWidth = 180, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            });
            return panel;
        }

        // ------------------------------------------------------------------ closed tabs

        private void PushClosed(string url)
        {
            _closedTabs.Push(url);
            if (_closedTabs.Count <= 20) return;
            var keep = _closedTabs.Reverse().Skip(1).ToList();   // oldest first, drop the oldest
            _closedTabs.Clear();
            foreach (var k in keep) _closedTabs.Push(k);
        }

        private void ReopenClosedTab()
        {
            if (_closedTabs.Count == 0) { ShowNotice("No closed tabs to reopen."); return; }
            _ = NewTabAsync(_closedTabs.Pop());
        }

        private void OnMenuReopenTab(object s, RoutedEventArgs e) { ReopenClosedTab(); }
        private void OnMenuDownloads(object s, RoutedEventArgs e) { OpenInternalTab(TabKind.Downloads); }

        // ------------------------------------------------------------------ notices

        private Action _noticeAction;

        private void ShowNotice(string text, string actionText = null, Action action = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            NoticeText.Text = text;
            _noticeAction = action;
            NoticeActionButton.Content = actionText ?? "";
            NoticeActionButton.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            NoticeBorder.RequestedTheme = RequestedTheme;
            NoticeBorder.Measure(new Size(600, double.PositiveInfinity));
            var size = NoticeBorder.DesiredSize;
            NoticePopup.HorizontalOffset = Math.Max(12, (Root.ActualWidth - size.Width) / 2);
            NoticePopup.VerticalOffset = Math.Max(12, Root.ActualHeight - size.Height - 28);
            NoticePopup.IsOpen = true;

            if (_noticeTimer == null)
            {
                _noticeTimer = new DispatcherTimer();
                _noticeTimer.Tick += (s, a) => { _noticeTimer.Stop(); NoticePopup.IsOpen = false; };
            }
            _noticeTimer.Stop();
            _noticeTimer.Interval = TimeSpan.FromSeconds(action != null ? 14 : 7);
            _noticeTimer.Start();
        }

        private void OnNoticeDismiss(object s, RoutedEventArgs e) { _noticeTimer?.Stop(); NoticePopup.IsOpen = false; }

        private void OnNoticeAction(object s, RoutedEventArgs e)
        {
            _noticeTimer?.Stop();
            NoticePopup.IsOpen = false;
            var run = _noticeAction;
            _noticeAction = null;
            run?.Invoke();
        }

        // ------------------------------------------------------------------ branded loading screen

        private bool _splashGone;

        /// <summary>The overlay continues the system splash screen; it fades out once the first tab is ready.</summary>
        private async Task HideSplashAsync()
        {
            if (_splashGone) return;
            _splashGone = true;
            await Task.Delay(250);
            try
            {
                var fade = new DoubleAnimation
                {
                    From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(380)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, SplashOverlay);
                Storyboard.SetTargetProperty(fade, "Opacity");
                var sb = new Storyboard();
                sb.Children.Add(fade);
                sb.Completed += (s, e) => { SplashOverlay.Visibility = Visibility.Collapsed; };
                sb.Begin();
            }
            catch { SplashOverlay.Visibility = Visibility.Collapsed; }
        }

        // ------------------------------------------------------------------ sharp favicons

        private void HookFavicons(BrowserTab tab, CoreWebView2 core)
        {
            // WebView2's own icon is small and soft: used only until Nyx has fetched the page's real one.
            core.FaviconChanged += async (s, a) =>
            {
                try
                {
                    string host = Suggestions.HostOf(core.Source ?? "");
                    if (tab.HqIconHost != null && tab.HqIconHost == host) return;
                    if (string.IsNullOrEmpty(core.FaviconUri)) return;
                    using (var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                    {
                        var bmp = new BitmapImage { DecodePixelWidth = 32 };
                        await bmp.SetSourceAsync(stream);
                        tab.Item.IconSource = new muxc.ImageIconSource { ImageSource = bmp };
                    }
                }
                catch { }
            };
        }

        private async Task UpdateFaviconAsync(BrowserTab tab)
        {
            var core = tab.Core;
            if (core == null) return;
            var src = core.Source ?? "";
            if (!src.StartsWith("http")) { tab.Item.IconSource = null; tab.HqIconHost = null; return; }

            string host = Suggestions.HostOf(src);
            var bmp = Favicons.Get(host);
            if (bmp == null || Favicons.IsStale(host)) bmp = await Favicons.EnsureAsync(core, src) ?? bmp;
            if (bmp != null && Suggestions.HostOf(core.Source ?? "") == host)
            {
                tab.Item.IconSource = new muxc.ImageIconSource { ImageSource = bmp };
                tab.HqIconHost = host;
            }
        }

        // ------------------------------------------------------------------ downloads



        private static async Task<string> CookieHeader(CoreWebView2 core, string uri)
        {
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(uri);
                return string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
            }
            catch { return ""; }
        }

        private static string OriginOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "[invalid address]";
        }

        private static string FileNameFor(string suggestedPath, string uri)
        {
            var name = Path.GetFileName(suggestedPath ?? "");
            if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(uri, UriKind.Absolute, out var u)) name = Path.GetFileName(u.AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }

        private void HookDownloads(CoreWebView2 core)
        {
            core.DownloadStarting += async (s, a) =>
            {
                var deferral = a.GetDeferral();
                try
                {
                    var op = a.DownloadOperation;
                    var uri = op.Uri ?? "";
                    string suggestedName = FileNameFor(a.ResultFilePath, uri);



                    string name = suggestedName;
                    if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        // Nyx downloads web links itself, with the page's cookies. WebView2's own download pipeline cannot write
                        // to a console's file system, which shows up as "Download interrupted".
                        a.Cancel = true;
                        var cookies = await CookieHeader(core, uri);
                        DownloadManager.StartHttp(uri, name, core.Source, cookies, core.Settings.UserAgent);
                        ShowNotice("Downloading " + name + ". Menu > Downloads shows the progress.");
                        return;
                    }

                    // blob: and data: links only exist inside the page, so WebView2 has to write those
                    var temp = DownloadManager.TempPathFor(name);
                    a.ResultFilePath = temp;
                    a.Handled = true;
                    DownloadManager.Track(op, name, temp);
                    ShowNotice("Downloading " + name + ". Menu > Downloads shows the progress.");
                }
                catch (Exception ex) { NyxLog.Error("Downloads", "Could not handle download from " + OriginOf(a.DownloadOperation?.Uri ?? ""), ex); }
                finally { deferral.Complete(); }
            };
        }

        // ------------------------------------------------------------------ updates

        private async Task<string> CheckUpdatesNowAsync()
        {
            var r = await UpdateChecker.CheckAsync();
            if (r.Error != null) return "Could not check for updates: " + r.Error;
            if (r.Available)
            {
                StartPage.ClearCache();
                await NewTabAsync(r.Url, true);
                return "Nyx " + r.Version + " is available. Its release page is open in a new tab.";
            }
            return r.Version.Length == 0 ? "No release has been published yet." : "You have the latest version (" + UpdateChecker.Version + ").";
        }

        private async Task CheckForUpdatesAsync()
        {
            if (!AppSettings.CheckUpdates) return;
            await Task.Delay(3500);   // after the first page is up
            var r = await UpdateChecker.CheckAsync();
            if (!r.Available) return;
            StartPage.ClearCache();
            ShowNotice("Nyx " + r.Version + " is available. Its release page opened in a new tab.");
            await NewTabAsync(r.Url, false);
        }

    }
}