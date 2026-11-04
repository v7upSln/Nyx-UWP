using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nyx.Browser;
using Nyx.Shields;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Nyx_UWP.Views
{
    public sealed partial class SettingsPage : Page
    {
        private bool _loading = true;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        public SettingsPage()
        {
            InitializeComponent();

            foreach (var e in SearchEngines.All)
                EngineBox.Items.Add(new ComboBoxItem { Content = e.Name, Tag = e.Id });

            Select(ThemeBox, AppSettings.Theme);
            Select(SiteBox, AppSettings.SiteAppearance);
            Select(EngineBox, SearchEngines.Current.Id);
            Select(BlockBox, AppSettings.BlockMode);
            ProtectionSwitch.IsOn = ShieldSettings.Current.Enabled;
            TrackingSwitch.IsOn = AppSettings.StripTracking;
            PopupSwitch.IsOn = AppSettings.BlockPopups;
            HistorySwitch.IsOn = AppSettings.SaveHistory;
            LiteSwitch.IsOn = AppSettings.LiteLists;
            SuggestSwitch.IsOn = AppSettings.Suggestions;
            BarSwitch.IsOn = AppSettings.BookmarksBar;
            UpdateSwitch.IsOn = AppSettings.CheckUpdates;
            YouTubeSwitch.IsOn = AppSettings.YouTubeHelper;
            _loading = false;
            Nav.SelectedIndex = 0;
            AppServices.SelectSettingsSection = SelectSection;
            if (AppServices.PendingSettingsSection != null) { SelectSection(AppServices.PendingSettingsSection); AppServices.PendingSettingsSection = null; }

            _timer.Tick += (s, a) => FilterStatus.Text = AppServices.FilterStatus?.Invoke() ?? "";
            Loaded += (s, a) =>
            {
                FilterStatus.Text = AppServices.FilterStatus?.Invoke() ?? "";
                _timer.Start();
            };
            Unloaded += (s, a) => _timer.Stop();
        }

        // ------------------------------------------------------------------ navigation and search

        private IEnumerable<StackPanel> Sections => new[] { SecGeneral, SecSearch, SecPrivacy, SecFilters, SecAbout };

        private string CurrentTag => (Nav.SelectedItem as ListViewItem)?.Tag as string ?? "general";

        private void ShowSection(string tag)
        {
            SecGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
            SecSearch.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
            SecPrivacy.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
            SecFilters.Visibility = tag == "filters" ? Visibility.Visible : Visibility.Collapsed;
            SecAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearFilter()
        {
            foreach (var sec in Sections)
                foreach (var child in sec.Children)
                {
                    child.Visibility = Visibility.Visible;
                    if (child is Border card && card.Child is StackPanel rows)
                        foreach (var r in rows.Children) r.Visibility = Visibility.Visible;
                }
            NoResults.Visibility = Visibility.Collapsed;
            ShowSection(CurrentTag);
        }

        private void OnNavChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindBox == null) return;
            if (FindBox.Text.Length > 0) FindBox.Text = "";   // picking a category leaves the search
            else ShowSection(CurrentTag);
        }

        private void OnFindChanged(object sender, TextChangedEventArgs e)
        {
            var q = FindBox.Text.Trim();
            if (q.Length == 0) { ClearFilter(); return; }

            int sectionsShown = 0;
            foreach (var sec in Sections)
            {
                bool secHas = false;
                TextBlock lastHead = null;
                foreach (var child in sec.Children)
                {
                    if (child is Border card && card.Child is StackPanel rows)
                    {
                        bool cardHas = false;
                        foreach (var r in rows.Children)
                        {
                            var fe = r as FrameworkElement;
                            if (fe?.Tag as string == "divider") { r.Visibility = Visibility.Collapsed; continue; }
                            bool match = ((fe?.Tag as string) ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                            r.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                            cardHas |= match;
                        }
                        card.Visibility = cardHas ? Visibility.Visible : Visibility.Collapsed;
                        if (lastHead != null) lastHead.Visibility = cardHas ? Visibility.Visible : Visibility.Collapsed;
                        secHas |= cardHas;
                    }
                    else if (child is TextBlock tb) { lastHead = tb; tb.Visibility = Visibility.Collapsed; }
                }
                sec.Visibility = secHas ? Visibility.Visible : Visibility.Collapsed;
                if (secHas) sectionsShown++;
            }
            NoResults.Visibility = sectionsShown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectSection(string tag)
        {
            var item = Nav.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null) Nav.SelectedItem = item;
        }

        // ------------------------------------------------------------------ helpers

        private static void Select(ComboBox box, string tag)
        {
            var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
            box.SelectedItem = item;
        }

        private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        // ------------------------------------------------------------------ general, search

        private void OnThemeChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(ThemeBox) == null) return;
            AppSettings.Theme = TagOf(ThemeBox);
            AppServices.SettingChanged?.Invoke("theme");
        }

        private void OnSiteAppearanceChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(SiteBox) == null) return;
            AppSettings.SiteAppearance = TagOf(SiteBox);
            AppServices.SettingChanged?.Invoke("appearance");
        }

        private void OnBarToggled(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.BookmarksBar = BarSwitch.IsOn;
            AppServices.SettingChanged?.Invoke("bookmarksbar");
        }

        private void OnEngineChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(EngineBox) == null) return;
            AppSettings.SearchEngine = TagOf(EngineBox);
            AppServices.SettingChanged?.Invoke("engine");
        }

        private void OnSuggestToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.Suggestions = SuggestSwitch.IsOn; }

        // ------------------------------------------------------------------ privacy

        private void OnProtectionToggled(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            ShieldSettings.Current.Enabled = ProtectionSwitch.IsOn;
            ShieldSettings.Current.Save();
            AppServices.SettingChanged?.Invoke("protection");
        }

        private void OnPopupToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.BlockPopups = PopupSwitch.IsOn; }
        private void OnTrackingToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.StripTracking = TrackingSwitch.IsOn; }
        private void OnHistoryToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.SaveHistory = HistorySwitch.IsOn; }

        private void OnClearHistory(object s, RoutedEventArgs e)
        {
            HistoryStore.Clear();
            PrivacyNote.Text = "Browsing history cleared.";
        }

        private void OnClearSiteData(object s, RoutedEventArgs e)
        {
            AppServices.ClearSiteData?.Invoke();
            PrivacyNote.Text = "Cookies and site data cleared.";
        }

        private void OnResetStats(object s, RoutedEventArgs e)
        {
            Stats.Reset();
            PrivacyNote.Text = "Statistics reset.";
        }

        // ------------------------------------------------------------------ updates and about

        private void OnUpdateToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.CheckUpdates = UpdateSwitch.IsOn; }

        private async void OnCheckUpdatesNow(object s, RoutedEventArgs e)
        {
            UpdateNote.Text = "Checking...";
            UpdateNote.Text = AppServices.CheckUpdates != null ? await AppServices.CheckUpdates() : "Not available right now.";
        }

        private async void OnOpenRepo(object s, RoutedEventArgs e)
        {
            try { await Launcher.LaunchUriAsync(new Uri(UpdateChecker.RepoUrl)); } catch { }
        }

        private void OnYouTubeToggled(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.YouTubeHelper = YouTubeSwitch.IsOn;
            AppServices.SettingChanged?.Invoke("youtube");
        }

        // ------------------------------------------------------------------ filters

        private void OnUpdateFilters(object s, RoutedEventArgs e) { AppServices.UpdateFilters?.Invoke(); }

        private void OnBlockModeChanged(object s, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(BlockBox) == null) return;
            AppSettings.BlockMode = TagOf(BlockBox);
        }

        private void OnLiteToggled(object s, RoutedEventArgs e) { if (!_loading) AppSettings.LiteLists = LiteSwitch.IsOn; }

        // ------------------------------------------------------------------ extensions removed (UWP platform limitation)
    }
}
