using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Nyx.Browser;
using Nyx.Input;
using Nyx.Shields;
using Nyx_UWP.Views;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.System.Profile;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls;

namespace Nyx_UWP
{
    public sealed partial class MainPage : Page
    {
        private readonly ShieldSettings _settings = ShieldSettings.Current;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        private const int MaxXboxTabs = 2;   // each live tab owns a WebView2 renderer; favor media stability over many tabs on Xbox
        private DateTime _lastMemoryWarning = DateTime.MinValue;
        private ShieldEngine _engine;
        private InputShortcuts _input;
        private string _nativeInfo = "";
        private string _listReport = "";
        private volatile string _listStatus = "";
        private bool _syncing;
        private bool _maintenanceRunning;
        private bool _shieldOpen;

        private CoreWebView2Environment _env;
        private bool _envTried;

        private CancellationTokenSource _suggestCts;
        private List<Suggestion> _suggestions = new List<Suggestion>();

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // 85% page scale on Xbox; pages that are not http(s) (the new tab page, error page) are left alone.
        private const string XboxZoomScript =
            "(function(){if(!/^https?:$/.test(location.protocol))return;" +
            "function z(){var d=document.documentElement;if(d)d.style.zoom='0.85';}" +
            "if(document.documentElement)z();document.addEventListener('DOMContentLoaded',z);})();";

        private BrowserTab Current => (Tabs.SelectedItem as muxc.TabViewItem)?.Tag as BrowserTab;
        private static bool IsXbox => AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

        // ------------------------------------------------------------------ startup

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                AppServices.OpenUrl = url => { _ = NewTabAsync(url); };
                AppServices.SettingChanged = OnSettingChanged;
                AppServices.UpdateFilters = () => { _ = MaintenanceAsync(true); };
                AppServices.FilterStatus = () => $"{_listReport}\n{_listStatus}\nmemory {FilterListManager.MemoryInfo()}";
                AppServices.ClearSiteData = async () =>
                {
                    try
                    {
                        var p = Current?.Core?.Profile;
                        if (p != null) await p.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
                    }
                    catch { }
                };
                BookmarkStore.Changed += () => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { UpdateBookmarkUi(); RebuildBookmarksBar(); }); };

                SetupTitleBar();
                InitFeatures();
                ApplyTheme(false);
                RebuildBookmarksBar();

                int st = ShieldEngine.SelfTest();
                _nativeInfo = $"{ShieldEngine.NativeVersion()}, self-test {(st == 0 ? "ok" : "FAILED #" + st)}";

                _engineTask = LoadEngineAsync();   // does not hold up the first page

                SetupInput();
                _uiTimer.Tick += (s, a) => { RefreshChrome(); Stats.FlushIfDirty(); CheckMemory(); };
                _uiTimer.Start();

                if (AppConfig.CheckFirstRunAfterUpdate(out var currentVer, out var previousVer))
                {
                    var releaseTab = await NewTabAsync(null);
                    releaseTab.IsReleaseNotes = true;
                    releaseTab.IsStartPage = false;
                    releaseTab.Item.Header = "What's New";
                    var html = await ReleaseNotes.BuildHtmlAsync();
            releaseTab.Core?.NavigateToString(html);
                }
                else
                {
                    await NewTabAsync(null);
                }
                _ = MaintenanceAsync(false);
                _ = CheckForUpdatesAsync();
                await HideSplashAsync();
            }
            catch (Exception ex)
            {
                UrlBox.Text = "Startup error: " + ex.Message;
                NyxLog.Error("Startup", "Main page initialization failed", ex);
                System.Diagnostics.Debug.WriteLine("NYX STARTUP ERROR: " + ex);
                _ = HideSplashAsync();
            }
        }

        private void SetupInput()
        {
            SystemNavigationManager.GetForCurrentView().BackRequested += (s, a) =>
            {
                var c = Current?.Core;
                if (c != null && c.CanGoBack) { a.Handled = true; c.GoBack(); }
            };

            _input = new InputShortcuts();
            _input.FocusUrl += FocusAddressBar;
            _input.Reload += UserReload;
            _input.Back += () => { var c = Current?.Core; if (c != null && c.CanGoBack) c.GoBack(); };
            _input.Forward += () => { var c = Current?.Core; if (c != null && c.CanGoForward) c.GoForward(); };
            _input.NewTab += () => { _ = NewTabAsync(null); };
            _input.CloseTab += () => CloseTab(Current);
            _input.NextTab += () => CycleTab(+1);
            _input.PrevTab += () => CycleTab(-1);
            _input.OpenShield += () => ShieldButton.Flyout?.ShowAt(ShieldButton);
            _input.OpenMenu += () => MenuButton.Flyout?.ShowAt(MenuButton);
            _input.ToggleBookmark += ToggleBookmark;
            _input.ReopenTab += ReopenClosedTab;
            _input.SuggestDown += () => { if (SuggestPopup.IsOpen) MoveSuggestion(+1); };
            _input.SuggestUp += () => { if (SuggestPopup.IsOpen) MoveSuggestion(-1); };
        }

        private void FocusAddressBar()
        {
            UrlBox.Focus(FocusState.Programmatic);
            UrlBox.SelectAll();
        }

        // ------------------------------------------------------------------ theme, title bar, site appearance

        private bool IsDark()
        {
            switch (AppSettings.Theme)
            {
                case "dark": return true;
                case "light": return false;
                default: return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
        }

        private void ApplyTheme(bool updateTabs = true)
        {
            var theme = AppSettings.Theme == "dark" ? ElementTheme.Dark : AppSettings.Theme == "light" ? ElementTheme.Light : ElementTheme.Default;
            RequestedTheme = theme;
            ShieldPanel.RequestedTheme = theme;
            SitePanel.RequestedTheme = theme;
            SuggestBorder.RequestedTheme = theme;

            var presenter = new Style(typeof(FlyoutPresenter));
            presenter.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
            ShieldFlyout.FlyoutPresenterStyle = presenter;
            SiteFlyout.FlyoutPresenterStyle = presenter;
            NoticeBorder.RequestedTheme = theme;
            var menu = new Style(typeof(MenuFlyoutPresenter));
            menu.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
            MainMenu.MenuFlyoutPresenterStyle = menu;

            UpdateTitleBarColors();
            if (updateTabs) foreach (var t in _tabs.Where(t => t.Kind == TabKind.Web)) ConfigureAppearance(t);
        }

        private void SetupTitleBar()
        {
            try
            {
                var bar = CoreApplication.GetCurrentView().TitleBar;
                bar.ExtendViewIntoTitleBar = true;
                DragRegion.MinWidth = Math.Max(48, bar.SystemOverlayRightInset);
                bar.LayoutMetricsChanged += (s, a) => DragRegion.MinWidth = Math.Max(48, bar.SystemOverlayRightInset);
                Window.Current.SetTitleBar(DragRegion);
            }
            catch { }
        }

        private void UpdateTitleBarColors()
        {
            try
            {
                var tb = ApplicationView.GetForCurrentView().TitleBar;
                var fg = IsDark() ? Color.FromArgb(255, 0xFB, 0xFB, 0xFE) : Color.FromArgb(255, 0x15, 0x14, 0x1A);
                var hover = IsDark() ? Color.FromArgb(255, 0x42, 0x41, 0x4D) : Color.FromArgb(255, 0xE0, 0xE0, 0xE6);
                tb.ButtonBackgroundColor = Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Colors.Transparent;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = Color.FromArgb(255, 0x8F, 0x8F, 0x9D);
                tb.ButtonHoverBackgroundColor = hover;
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = hover;
                tb.ButtonPressedForegroundColor = fg;
            }
            catch { }
        }

        private async void ConfigureAppearance(BrowserTab tab)
        {
            var core = tab.Core;
            if (core == null) return;
            try
            {
                string mode = AppSettings.SiteAppearance;
                bool dark = mode == "dark" || mode == "force" || (mode == "auto" && IsDark());
                core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(tab.ForceDarkScriptId))
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(tab.ForceDarkScriptId);
                    tab.ForceDarkScriptId = null;
                }
                if (AppSettings.SiteAppearance == "force")
                    tab.ForceDarkScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(ForceDark.Script);
            }
            catch { }
        }

        private void OnSettingChanged(string key)
        {
            switch (key)
            {
                case "theme": ApplyTheme(); break;
                case "appearance":
                    foreach (var t in _tabs.Where(t => t.Kind == TabKind.Web)) { ConfigureAppearance(t); RefreshTab(t); }
                    break;
                case "protection": ApplyProtectionChange(); break;
                case "youtube": foreach (var t in _tabs) { _ = t.Shield?.ResetScriptsAsync(); } break;
                case "bookmarksbar": RebuildBookmarksBar(); break;
                // "engine": only the address bar uses it, nothing to reload
            }
        }

        // ------------------------------------------------------------------ background filter maintenance

        private async Task MaintenanceAsync(bool force)
        {
            if (_maintenanceRunning) return;
            _maintenanceRunning = true;
            try
            {
                if (!force)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    if (FilterListManager.HasDownloads && !FilterListManager.UpdateDue()) return;
                }

                bool changed = await FilterListManager.UpdateAsync(m => _listStatus = m);
                if (!changed) return;

                _listStatus = "building engine...";
                var (eng, report) = await FilterListManager.LoadAsync();
                if (eng == null) { _listStatus = report; return; }

                var old = _engine;
                _engine = eng;
                _listReport = report;
                _listStatus = "filters active";
                foreach (var t in _tabs) { _ = t.Shield?.ResetScriptsAsync(); }
                old?.Dispose();
            }
            catch (Exception ex) { _listStatus = "update failed: " + ex.Message; }
            finally { _maintenanceRunning = false; }
        }

        // ------------------------------------------------------------------ tabs

        private void ShowStartPage(BrowserTab tab)
        {
            tab.IsStartPage = true;
            tab.FailedUrl = null;
            tab.Core?.NavigateToString(StartPage.Build());
        }

        /// <summary>Reload that does the right thing for the start page and the error page.</summary>
        private void RefreshTab(BrowserTab tab)
        {
            var core = tab?.Core;
            if (core == null) return;
            if (tab.FailedUrl != null) { var u = tab.FailedUrl; tab.FailedUrl = null; core.Navigate(u); }
            else if (tab.IsStartPage) ShowStartPage(tab);
            else core.Reload();
        }

        private async Task<BrowserTab> NewTabAsync(string url, bool select = true)
        {
            if (IsXbox && _tabs.Count >= MaxXboxTabs)
            {
                var oldest = _tabs.Where(t => t != Current).OrderBy(t => t.LastUsed).FirstOrDefault();
                if (oldest != null)
                {
                    DiscardTab(oldest);
                    ShowNotice("Closed a tab you were not using, to stay within the console's memory limit.");
                }
            }

            var web = new muxc.WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var item = new muxc.TabViewItem { Header = "New Tab", IsClosable = true };
            var tab = new BrowserTab { Kind = TabKind.Web, Item = item, Web = web, IsStartPage = url == null };
            item.Tag = tab;

            _tabs.Add(tab);
            ContentHost.Children.Add(web);
            Tabs.TabItems.Add(item);
            if (select) Tabs.SelectedItem = item;
            Motion.TabOpen(item, null);

            await EnsureEnvironmentAsync();
            try { await web.EnsureCoreWebView2Async(); }
            catch (Exception ex)
            {
                NyxLog.Error("WebView2", "Startup failed", ex);
                return tab;
            }
            var core = web.CoreWebView2;
            core.ProcessFailed += (s, a) =>
            {
                tab.IsLoading = false;
                int usage = 0, budget = 0;
                try { usage = (int)(MemoryManager.AppMemoryUsage / 1024 / 1024); budget = (int)(MemoryManager.AppMemoryUsageLimit / 1024 / 1024); } catch { }
                NyxLog.Error("WebView2", $"Process failed: kind={a.ProcessFailedKind}, reason={a.Reason}, exit={a.ExitCode}, memory={usage}/{budget} MB, tab={tab.Item.Header}");
                if (tab == Current)
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowNotice("The page renderer stopped. Reload the page; if it repeats, include the Nyx log in a bug report."));
            };
            core.Settings.IsStatusBarEnabled = false;
            try
            {
                // Standard desktop Chrome UA without WebView2/Edg tokens to prevent Cloudflare/WAF bot-blocks on streaming sites
                core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            }
            catch { }
            // Pages and their UI are a bit smaller on a TV. The UWP WebView2 control exposes no zoom setting,
            // so pages get a document-start rule that scales the top document to 85%.
            if (IsXbox)
            {
                try { _ = core.AddScriptToExecuteOnDocumentCreatedAsync(XboxZoomScript); } catch { }
            }
            // Profile initialized

            tab.Shield = new PageShield(core, () => _engine, _settings, Dispatcher);
            tab.Shield.OpenInNewTab += u => { _ = NewTabAsync(u); };
            tab.Shield.Attach();
            HookTab(tab, core);
            ConfigureAppearance(tab);

            if (url == null) { ShowStartPage(tab); if (select) { SetUrlText(""); if (!IsXbox) FocusAddressBar(); } }
            else core.Navigate(url);

            ShowTab(select ? tab : Current);
            return tab;
        }

        private void OpenInternalTab(TabKind kind)
        {
            var existing = _tabs.FirstOrDefault(t => t.Kind == kind);
            if (existing != null) { Tabs.SelectedItem = existing.Item; return; }

            var frame = new Frame { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            string title = TitleOf(kind);
            var item = new muxc.TabViewItem { Header = title, IsClosable = true };
            var tab = new BrowserTab { Kind = kind, Item = item, Frame = frame };
            item.Tag = tab;

            _tabs.Add(tab);
            ContentHost.Children.Add(frame);
            Tabs.TabItems.Add(item);
            frame.Navigate(PageOf(kind));
            Tabs.SelectedItem = item;
            Motion.TabOpen(item, frame);
            ShowTab(tab);
        }

        private void HookTab(BrowserTab tab, CoreWebView2 core)
        {
            core.NavigationStarting += (s, a) =>
            {
                tab.IsLoading = true;
                tab.LoadProgress = 8;
                if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var navUri) && (navUri.Scheme == "http" || navUri.Scheme == "https"))
                    NyxLog.Info("Navigation", "Start " + navUri.GetLeftPart(UriPartial.Authority));
                if (a.Uri.StartsWith("http")) { tab.IsStartPage = false; tab.FailedUrl = null; }
                if (tab == Current) { CloseSuggest(); RefreshChrome(); }
            };

            core.DOMContentLoaded += (s, a) =>
            {
                tab.LoadProgress = 82;
                if (tab == Current) Loading.Value = tab.LoadProgress;
            };

            core.WebResourceResponseReceived += (s, a) =>
            {
                try
                {
                    var response = a.Response;
                    if (response == null || (response.StatusCode != 401 && response.StatusCode != 403 && response.StatusCode < 500)) return;
                    NyxLog.Warn("HTTP", "HTTP " + response.StatusCode + " from " + Suggestions.HostOf(a.Request.Uri));
                }
                catch (Exception ex) { NyxLog.Error("HTTP", "Could not inspect response status", ex); }
            };

            core.NavigationCompleted += (s, a) =>
            {
                tab.IsLoading = false;
                tab.LoadProgress = 100;
                string src = core.Source ?? "";

                if (!a.IsSuccess && a.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && src.StartsWith("http"))
                {
                    NyxLog.Warn("Navigation", "Failed " + Suggestions.HostOf(src) + " with " + a.WebErrorStatus);
                    tab.FailedUrl = src;
                    tab.IsStartPage = false;
                    string fix = DidYouMean.ForHost(Suggestions.HostOf(src));
                    core.NavigateToString(ErrorPage.Build(src, a.WebErrorStatus.ToString(), fix));
                }
                else if (a.IsSuccess && AppSettings.SaveHistory && (src.StartsWith("https://") || src.StartsWith("http://")))
                {
                    HistoryStore.Add(src, core.DocumentTitle);
                }

                UpdateTabHeader(tab);
                if (a.IsSuccess) { var _ = UpdateFaviconAsync(tab); }
                if (tab == Current) RefreshChrome();
            };

            core.SourceChanged += (s, a) => { if (tab == Current) RefreshChrome(); };
            core.HistoryChanged += (s, a) => { if (tab == Current) RefreshChrome(); };
            core.DocumentTitleChanged += (s, a) =>
            {
                UpdateTabHeader(tab);
                var src = core.Source ?? "";
                if (AppSettings.SaveHistory && src.StartsWith("http")) HistoryStore.UpdateTitle(src, core.DocumentTitle);
            };

            HookFavicons(tab, core);
            HookDownloads(core);

            core.ContainsFullScreenElementChanged += (s, a) =>
            {
                if (tab != Current) return;
                var view = ApplicationView.GetForCurrentView();
                bool fs = core.ContainsFullScreenElement;
                Tabs.Visibility = Toolbar.Visibility = BookmarksBarHost.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
                if (!fs) RebuildBookmarksBar();
                if (fs) view.TryEnterFullScreenMode(); else view.ExitFullScreenMode();
            };
        }

        private void UpdateTabHeader(BrowserTab tab)
        {
            var core = tab.Core;
            if (core == null) return;
            string title = core.DocumentTitle;
            if (tab.IsStartPage && IsInternal(core.Source)) title = "New Tab";
            if (string.IsNullOrWhiteSpace(title))
                title = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : "New Tab";
            tab.Item.Header = title;
        }

        private void ShowTab(BrowserTab tab)
        {
            CloseSuggest();
            foreach (var t in _tabs)
            {
                bool on = t == tab;
                t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                try
                {
                    var c = t.Core;
                    if (c == null) continue;
                    if (on)
                    {
                        c.Resume();
                        try { c.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
                    }
                    else
                    {
                        try { c.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                        var _ = c.TrySuspendAsync();
                    }
                }
                catch { }
            }
            if (tab != null) tab.LastUsed = DateTime.UtcNow;
            RefreshChrome(true);
        }

        /// <summary>Removes a tab without the close animation, for the Xbox tab cap and memory pressure.</summary>
        private void DiscardTab(BrowserTab tab)
        {
            if (tab == null || tab == Current || !_tabs.Contains(tab)) return;
            _tabs.Remove(tab);
            Tabs.TabItems.Remove(tab.Item);
            ContentHost.Children.Remove(tab.View);
            try { tab.Web?.Close(); } catch { }
        }

        /// <summary>
        /// The console hands a UWP app far less memory than a game gets. When the budget runs low, put every
        /// background tab into low-memory mode and drop the caches that can be rebuilt for free.
        /// </summary>
        private void CheckMemory()
        {
            try
            {
                ulong used = MemoryManager.AppMemoryUsage, limit = MemoryManager.AppMemoryUsageLimit;
                if (limit == 0) return;
                var percent = (int)(used * 100 / limit);
                int threshold = IsXbox ? 70 : 78;
                if (percent >= threshold && DateTime.UtcNow - _lastMemoryWarning > TimeSpan.FromSeconds(30))
                {
                    _lastMemoryWarning = DateTime.UtcNow;
                    NyxLog.Warn("Memory", $"App memory reached {percent}% of its {limit / 1024 / 1024} MB budget; device={AnalyticsInfo.VersionInfo.DeviceFamily}.");
                }
                if (percent < threshold) return;

                foreach (var t in _tabs)
                {
                    if (t == Current || t.Core == null) continue;
                    try
                    {
                        t.Core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                        var _ = t.Core.TrySuspendAsync();
                    }
                    catch { }
                }
                Favicons.TrimCache();
                StartPage.ClearCache();
            }
            catch { }
        }

        private async void CloseTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            if (tab.Kind == TabKind.Web)
            {
                var closed = tab.FailedUrl ?? tab.Core?.Source ?? "";
                if (closed.StartsWith("http")) PushClosed(closed);
            }
            await Motion.TabCloseAsync(tab.Item);
            if (!_tabs.Contains(tab)) return;

            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            Tabs.TabItems.Remove(tab.Item);
            ContentHost.Children.Remove(tab.View);
            try { tab.Web?.Close(); } catch { }

            if (_tabs.Count == 0) { _ = NewTabAsync(null); return; }
            Tabs.SelectedItem = _tabs[Math.Min(idx, _tabs.Count - 1)].Item;
        }

        private void CycleTab(int step)
        {
            if (_tabs.Count < 2) return;
            int i = _tabs.IndexOf(Current);
            i = (i + step + _tabs.Count) % _tabs.Count;
            Tabs.SelectedItem = _tabs[i].Item;
        }

        // ------------------------------------------------------------------ chrome state

        private static bool IsInternal(string src) => string.IsNullOrEmpty(src) || src == "about:blank" || src.StartsWith("data:");

        private void SetSiteIcons(bool lockOn, bool warn, bool info)
        {
            SiteLockIcon.Visibility = lockOn ? Visibility.Visible : Visibility.Collapsed;
            SiteWarnIcon.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
            SiteInfoIcon.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshChrome(bool forceUrl = false)
        {
            var tab = Current;
            if (tab == null) { Loading.Visibility = Visibility.Collapsed; return; }

            if (tab.Kind != TabKind.Web)
            {
                BackButton.IsEnabled = ForwardButton.IsEnabled = false;
                Loading.Visibility = Visibility.Collapsed;
                if (forceUrl || UrlBox.FocusState == FocusState.Unfocused) SetUrlText("nyx://" + TitleOf(tab.Kind).ToLowerInvariant());
                SetSiteIcons(false, false, true);
                ReloadButton.IsEnabled = false;
                HomeButton.IsEnabled = true;
                SiteInfoButton.Visibility = BookmarkButton.Visibility = Visibility.Collapsed;
                BookmarkButton.IsEnabled = false;
                StarOnIcon.Visibility = Visibility.Collapsed; StarOffIcon.Visibility = Visibility.Visible;
                ShieldCount.Visibility = Visibility.Collapsed;
                return;
            }

            var core = tab.Core;
            if (core == null) { Loading.Visibility = Visibility.Collapsed; return; }

            BackButton.IsEnabled = core.CanGoBack;
            ForwardButton.IsEnabled = core.CanGoForward;
            ReloadButton.IsEnabled = HomeButton.IsEnabled = !(tab.IsStartPage && tab.FailedUrl == null);   // nothing to reload or go home to on the new tab page
            Loading.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            Loading.Value = Math.Max(0, Math.Min(100, tab.LoadProgress));

            string src = tab.FailedUrl ?? core.Source ?? "";
            bool internalPage = IsInternal(src);
            if (forceUrl || UrlBox.FocusState == FocusState.Unfocused) SetUrlText(internalPage ? "" : src);
            SetSiteIcons(!internalPage && src.StartsWith("https://"), !internalPage && src.StartsWith("http://"), internalPage);
            // no site information or bookmark button on the new tab page
            SiteInfoButton.Visibility = BookmarkButton.Visibility = src.StartsWith("http") ? Visibility.Visible : Visibility.Collapsed;

            bool active = _settings.Enabled && !_settings.IsDisabledFor(src);
            ShieldOnIcon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            ShieldOffIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            int n = tab.Shield?.BlockedCount ?? 0;
            ShieldCount.Text = n > 99 ? "99+" : n.ToString();
            ShieldCount.Visibility = active && n > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateBookmarkUi(src);
            if (_shieldOpen) FillShieldPanel();
        }

        // ------------------------------------------------------------------ bookmarks

        private void UpdateBookmarkUi(string src = null)
        {
            var tab = Current;
            if (tab == null || tab.Kind != TabKind.Web) return;
            src = src ?? tab.FailedUrl ?? tab.Core?.Source ?? "";
            bool canMark = src.StartsWith("http");
            bool marked = canMark && BookmarkStore.IsBookmarked(src);
            BookmarkButton.IsEnabled = canMark;
            StarOnIcon.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
            StarOffIcon.Visibility = marked ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ToggleBookmark()
        {
            var tab = Current;
            var core = tab?.Core;
            if (tab == null || tab.Kind != TabKind.Web || core == null) return;
            var src = tab.FailedUrl ?? core.Source ?? "";
            if (!src.StartsWith("http")) return;
            string title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? Suggestions.HostOf(src) : core.DocumentTitle;
            BookmarkStore.Toggle(src, title);
        }

        private void RebuildBookmarksBar()
        {
            BookmarksBar.Children.Clear();
            var items = BookmarkStore.All();
            bool show = AppSettings.BookmarksBar && items.Count > 0;
            BookmarksBarHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            foreach (var b in items.Take(40))
            {
                var url = b.Url;
                var btn = new Button
                {
                    Height = 32,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Content = BookmarkChip(b)
                };
                ToolTipService.SetToolTip(btn, url);
                btn.Click += (s, e) => NavigateTo(url);
                BookmarksBar.Children.Add(btn);
            }
        }

        private void OnBookmarkClick(object s, RoutedEventArgs e) { ToggleBookmark(); }

        // ------------------------------------------------------------------ site information

        private async void OnSiteOpening(object sender, object e)
        {
            var tab = Current;
            var core = tab?.Core;
            string src = tab?.FailedUrl ?? core?.Source ?? "";

            if (tab == null || tab.Kind != TabKind.Web || IsInternal(src))
            {
                SiteTitle.Text = "Nyx";
                SiteConnection.Text = "This is a built-in Nyx page. Nothing was loaded from the internet.";
                SiteCert.Text = SiteBlocked.Text = SiteCookies.Text = "";
                ClearCookiesButton.Visibility = Visibility.Collapsed;
                return;
            }

            ClearCookiesButton.Visibility = Visibility.Visible;
            string host = Suggestions.HostOf(src);
            bool https = src.StartsWith("https://");
            SiteTitle.Text = host;
            SiteConnection.Text = https ? "Connection is secure (HTTPS)."
                                        : "Connection is not secure. Information you enter on this page can be seen by others.";
            SiteCert.Text = https ? "Reading certificate..." : "";
            SiteBlocked.Text = $"{tab.Shield?.BlockedCount ?? 0} ads and trackers blocked on this page.";
            SiteCookies.Text = "Counting cookies...";

            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(src);
                SiteCookies.Text = $"{cookies.Count} cookies stored by this site.";
            }
            catch { SiteCookies.Text = ""; }

            if (https)
            {
                var info = await SiteInfo.GetAsync(host);
                if (SiteTitle.Text != host) return;   // the panel was reopened for another page meanwhile
                SiteCert.Text = info == null
                    ? "Certificate details are unavailable."
                    : $"Issued to: {info.IssuedTo}\nIssued by: {info.IssuedBy}\nValid until: {info.NotAfter:d}\nProtocol: {info.Protocol}" +
                      (info.TrustProblem ? "\nThis device does not trust the certificate." : "");
            }
        }

        private async void OnClearSiteCookies(object sender, RoutedEventArgs e)
        {
            var core = Current?.Core;
            if (core == null) return;
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(Current.FailedUrl ?? core.Source);
                foreach (var c in cookies) core.CookieManager.DeleteCookie(c);
                SiteCookies.Text = "Cookies cleared. Reload the page to apply.";
            }
            catch { }
        }

        private void OnOpenShieldFromSite(object sender, RoutedEventArgs e)
        {
            SiteFlyout.Hide();
            ShieldButton.Flyout?.ShowAt(ShieldButton);
        }

        // ------------------------------------------------------------------ protection panel

        private void FillShieldPanel()
        {
            var tab = Current;
            string src = tab?.Core?.Source ?? "";
            string host = Suggestions.HostOf(src);
            int blocked = tab?.Shield?.BlockedCount ?? 0;

            ShieldTitle.Text = host.Length > 0 ? host : "Nyx";
            ShieldSummary.Text = !_settings.Enabled ? "Protection is off everywhere."
                               : _settings.IsDisabledFor(src) ? "Protection is off on this site."
                               : $"{blocked} blocked on this page.";

            _syncing = true;
            GlobalSwitch.IsOn = _settings.Enabled;
            SiteSwitch.IsOn = !_settings.IsDisabledFor(src);
            SiteSwitch.IsEnabled = _settings.Enabled && host.Length > 0;
            _syncing = false;

            ShieldStatus.Text = $"{_listReport}\n{_listStatus}\n{_nativeInfo}\nmemory {FilterListManager.MemoryInfo()}";
        }

        private void OnShieldOpening(object sender, object e) { _shieldOpen = true; FillShieldPanel(); }
        private void OnShieldClosed(object sender, object e) { _shieldOpen = false; }

        private void ApplyProtectionChange()
        {
            _settings.Save();
            foreach (var t in _tabs) { _ = t.Shield?.ResetScriptsAsync(); }
            RefreshTab(Current);
            RefreshChrome();
        }

        private void OnSiteToggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var src = Current?.Core?.Source;
            if (string.IsNullOrEmpty(src)) return;
            _settings.ToggleSite(src);
            ApplyProtectionChange();
        }

        private void OnGlobalToggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _settings.Enabled = GlobalSwitch.IsOn;
            ApplyProtectionChange();
        }

        private void OnUpdateFilters(object sender, RoutedEventArgs e) { _ = MaintenanceAsync(true); }

        // ------------------------------------------------------------------ navigation

        private async void NavigateTo(string url)
        {
            if (_engine == null && _engineTask != null && !_engineTask.IsCompleted) { try { await _engineTask; } catch { } }
            var tab = Current;
            if (tab == null || tab.Kind != TabKind.Web) { _ = NewTabAsync(url); return; }   // built-in pages: open in a new tab
            tab.IsStartPage = false;
            tab.FailedUrl = null;
            tab.Core?.Navigate(url);
            tab.Web.Focus(FocusState.Programmatic);
        }

        private void Go(string input)
        {
            input = Suggestions.Normalize((input ?? "").Trim());
            if (input.Length == 0) return;
            CloseSuggest();

            if (input.Equals("nyx://settings", StringComparison.OrdinalIgnoreCase)) { OpenInternalTab(TabKind.Settings); return; }
            if (input.Equals("nyx://history", StringComparison.OrdinalIgnoreCase)) { OpenInternalTab(TabKind.History); return; }
            if (input.Equals("nyx://bookmarks", StringComparison.OrdinalIgnoreCase)) { OpenInternalTab(TabKind.Bookmarks); return; }
            if (input.Equals("nyx://downloads", StringComparison.OrdinalIgnoreCase)) { OpenInternalTab(TabKind.Downloads); return; }
            if (input.Equals("nyx://whatsnew", StringComparison.OrdinalIgnoreCase) || input.Equals("nyx://changelog", StringComparison.OrdinalIgnoreCase)) { ShowReleaseNotesTab(); return; }

            NavigateTo(Suggestions.ToUrl(input));
        }

        // ------------------------------------------------------------------ address bar and suggestions

        private async void OnUrlTextChanged(object sender, TextChangedEventArgs e)
        {
            if (UrlBox.Text == _programmaticText) return;   // the page set this text, the user did not type it
            if (UrlBox.FocusState == FocusState.Unfocused) return;   // programmatic updates do not open suggestions
            var text = UrlBox.Text.Trim();
            if (text.Length == 0) { CloseSuggest(); return; }

            _suggestCts?.Cancel();
            var cts = _suggestCts = new CancellationTokenSource();

            var local = Suggestions.Local(text);
            ShowSuggestions(local);
            if (!AppSettings.Suggestions) return;

            try
            {
                await Task.Delay(140, cts.Token);                         // wait for a pause in typing
                var remote = await Suggestions.RemoteAsync(text, cts.Token);
                if (cts.IsCancellationRequested) return;
                ShowSuggestions(Suggestions.Merge(text, local, remote));
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private PathIcon MakeIcon(string data, double size)
        {
            try
            {
                return (PathIcon)XamlReader.Load("<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data +
                                                  "' Width='" + size + "' Height='" + size + "'/>");
            }
            catch { return null; }
        }

        private int _suggestIndex = -1;

        private UIElement BuildSuggestRow(Suggestion s)
        {
            var grid = new Grid { ColumnSpacing = 10, MinHeight = 30 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string data = s.Kind == SuggestKind.Bookmark ? IconData.Bookmark
                        : s.Kind == SuggestKind.History ? IconData.History
                        : s.Kind == SuggestKind.DidYouMean ? IconData.Info
                        : IconData.Search;
            bool site = (s.Kind == SuggestKind.History || s.Kind == SuggestKind.Bookmark || s.Kind == SuggestKind.Go ||
                         (s.Kind == SuggestKind.DidYouMean && s.Url != null)) && !string.IsNullOrEmpty(s.Url);
            // sites get their own icon (fetched in the background the first time), searches get the magnifier
            FrameworkElement icon = site ? Favicons.CreateLive(Suggestions.HostOf(s.Url), 22) : MakeIcon(data, 16);
            if (icon != null)
            {
                icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = s.Kind == SuggestKind.DidYouMean ? "Did you mean " + s.Text + "?" : s.Text,
                FontSize = 13,
                FontWeight = s.Kind == SuggestKind.DidYouMean ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if ((!string.IsNullOrEmpty(s.Sub) && s.Kind != SuggestKind.DidYouMean) || (s.Kind == SuggestKind.DidYouMean && s.Url != null))
                text.Children.Add(new TextBlock { Text = s.Sub, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        private void ShowSuggestions(List<Suggestion> list)
        {
            _suggestions = list;
            _suggestIndex = -1;
            SuggestList.Items.Clear();
            foreach (var s in list)
            {
                var suggestion = s;
                // every row is a Button: mouse, keyboard focus and the controller cursor all click it the same way
                var row = new Button
                {
                    Content = BuildSuggestRow(s), Tag = s, MinHeight = 38,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(6)
                };
                row.Click += (b, e) => Accept(suggestion);
                SuggestList.Items.Add(new ListViewItem { Content = row, Padding = new Thickness(0), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }

            if (list.Count == 0) { SuggestPopup.IsOpen = false; return; }

            var pt = UrlPill.TransformToVisual(Root).TransformPoint(new Point(0, UrlPill.ActualHeight + 4));
            SuggestPopup.HorizontalOffset = pt.X;
            SuggestPopup.VerticalOffset = pt.Y;
            SuggestBorder.Width = Math.Max(320, UrlPill.ActualWidth);
            SuggestPopup.IsOpen = true;
        }

        private void CloseSuggest()
        {
            _suggestCts?.Cancel();
            SuggestPopup.IsOpen = false;
        }

        private void MoveSuggestion(int step)
        {
            int n = _suggestions.Count;
            if (n == 0) return;
            _suggestIndex = ((_suggestIndex + 1 + step) % (n + 1) + (n + 1)) % (n + 1) - 1;   // -1 means "what I typed"
            HighlightSuggestion();
        }

        private void HighlightSuggestion()
        {
            var on = new SolidColorBrush(IsDark() ? Color.FromArgb(255, 0x3A, 0x39, 0x44) : Color.FromArgb(255, 0xE4, 0xE4, 0xEA));
            var off = new SolidColorBrush(Colors.Transparent);
            for (int i = 0; i < SuggestList.Items.Count; i++)
                if ((SuggestList.Items[i] as ListViewItem)?.Content is Button b) b.Background = i == _suggestIndex ? on : off;
            if (_suggestIndex >= 0) (SuggestList.Items[_suggestIndex] as ListViewItem)?.StartBringIntoView();
        }

        private void Accept(Suggestion s)
        {
            CloseSuggest();
            switch (s.Kind)
            {
                case SuggestKind.Go:
                case SuggestKind.History:
                case SuggestKind.Bookmark:
                    NavigateTo(s.Url);
                    break;
                case SuggestKind.DidYouMean when s.Url != null:
                    NavigateTo(s.Url);
                    break;
                default:   // search, remote suggestion, or a corrected search query
                    NavigateTo(SearchEngines.Current.Url(s.Text));
                    break;
            }
        }

        private void OnUrlKeyDown(object s, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Down:
                    if (SuggestPopup.IsOpen) { MoveSuggestion(+1); e.Handled = true; }
                    break;
                case Windows.System.VirtualKey.Up:
                    if (SuggestPopup.IsOpen) { MoveSuggestion(-1); e.Handled = true; }
                    break;
                case Windows.System.VirtualKey.Enter:
                    e.Handled = true;
                    if (SuggestPopup.IsOpen && _suggestIndex >= 0 && _suggestIndex < _suggestions.Count) Accept(_suggestions[_suggestIndex]);
                    else Go(UrlBox.Text);
                    break;
                case Windows.System.VirtualKey.Escape:
                    e.Handled = true;
                    if (SuggestPopup.IsOpen) CloseSuggest();
                    else { SetUrlText(""); Current?.View.Focus(FocusState.Programmatic); RefreshChrome(true); }
                    break;
            }
        }

        private void OnUrlGotFocus(object s, RoutedEventArgs e)
        {
            UrlPill.BorderBrush = new SolidColorBrush(IsDark() ? Color.FromArgb(255, 0x8D, 0x86, 0xC9) : Color.FromArgb(255, 0x72, 0x5A, 0xC1));
            UrlBox.SelectAll();
        }

        private static bool IsInside(DependencyObject node, DependencyObject root)
        {
            while (node != null) { if (node == root) return true; node = VisualTreeHelper.GetParent(node); }
            return false;
        }

        private async void OnUrlLostFocus(object s, RoutedEventArgs e)
        {
            UrlPill.BorderBrush = new SolidColorBrush(Colors.Transparent);
            RefreshChrome();

            // Clicking a suggestion moves focus into the popup first: only close when focus really left.
            await Task.Delay(200);
            if (UrlBox.FocusState != FocusState.Unfocused) return;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsInside(focused, SuggestList)) return;
            CloseSuggest();
        }

        // ------------------------------------------------------------------ toolbar

        private void OnBack(object s, RoutedEventArgs e) { var c = Current?.Core; if (c != null && c.CanGoBack) c.GoBack(); }
        private void OnForward(object s, RoutedEventArgs e) { var c = Current?.Core; if (c != null && c.CanGoForward) c.GoForward(); }
        private void OnReload(object s, RoutedEventArgs e) { UserReload(); }

        /// <summary>The new tab page cannot be reloaded.</summary>
        private void UserReload()
        {
            var tab = Current;
            if (tab == null || tab.Kind != TabKind.Web) return;
            if (tab.IsStartPage && tab.FailedUrl == null) return;
            RefreshTab(tab);
        }

        private void OnHome(object s, RoutedEventArgs e)
        {
            var tab = Current;
            if (tab == null) return;
            if (tab.Kind != TabKind.Web) { _ = NewTabAsync(null); return; }
            if (tab.IsStartPage && tab.FailedUrl == null) { FocusAddressBar(); return; }   // already on the new tab page
            ShowStartPage(tab);
            SetUrlText("");
        }

        private void OnAddTab(muxc.TabView sender, object args) { _ = NewTabAsync(null); }
        private void OnMenuNewTab(object s, RoutedEventArgs e) { _ = NewTabAsync(null); }
        private void OnMenuCloseTab(object s, RoutedEventArgs e) { CloseTab(Current); }
        private void OnMenuBookmarks(object s, RoutedEventArgs e) { OpenInternalTab(TabKind.Bookmarks); }
        private void OnToolbarBookmarks(object s, RoutedEventArgs e) { OpenInternalTab(TabKind.Bookmarks); }
        private void OnMenuHistory(object s, RoutedEventArgs e) { OpenInternalTab(TabKind.History); }
        private void OnMenuSettings(object s, RoutedEventArgs e) { OpenInternalTab(TabKind.Settings); }
        private void OnMenuWhatsNew(object s, RoutedEventArgs e) { ShowReleaseNotesTab(); }

        private async void ShowReleaseNotesTab()
        {
            var releaseTab = await NewTabAsync(null);
            releaseTab.IsReleaseNotes = true;
            releaseTab.IsStartPage = false;
            releaseTab.Item.Header = "What's New";
            var html = await ReleaseNotes.BuildHtmlAsync();
            releaseTab.Core?.NavigateToString(html);
        }

        private void OnTabCloseRequested(muxc.TabView sender, muxc.TabViewTabCloseRequestedEventArgs args)
        {
            CloseTab(args.Tab?.Tag as BrowserTab);
        }

        private void OnTabSelected(object sender, SelectionChangedEventArgs e)
        {
            var tab = Current;
            if (tab != null) ShowTab(tab);
        }

        private async void OnAbout(object s, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Nyx",
                Content = $"Nyx {UpdateChecker.Version}\nAd-blocking browser for Xbox and Windows.\n{UpdateChecker.RepoUrl}\n\nEngine: {_nativeInfo}\n{_listReport}\nMemory: {FilterListManager.MemoryInfo()}\n\n" +
                          "Filter engine: adblock-rust by Brave (MPL-2.0).",
                CloseButtonText = "Close",
                RequestedTheme = RequestedTheme,
            };
            try { await dialog.ShowAsync(); } catch { }
        }
    }
}
