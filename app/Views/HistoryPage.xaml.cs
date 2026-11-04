using System;
using System.Collections.Generic;
using Nyx.Browser;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Nyx_UWP.Views
{
    public sealed partial class HistoryPage : Page
    {
        public HistoryPage()
        {
            InitializeComponent();
            Loaded += (s, a) => Reload();
        }

        private void Reload()
        {
            List.Items.Clear();
            var entries = HistoryStore.Search(SearchBox.Text, 400);
            if (entries.Count == 0)
            {
                List.Items.Add(new ListViewItem
                {
                    IsHitTestVisible = false,
                    Content = new TextBlock { Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "No history yet." : "Nothing matches your search.", Opacity = 0.7, Margin = new Thickness(4, 12, 0, 12) }
                });
                return;
            }

            string lastDay = null;
            foreach (var e in entries)
            {
                var local = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime();
                string day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("dddd, d MMMM yyyy");
                if (day != lastDay)
                {
                    lastDay = day;
                    List.Items.Add(new ListViewItem
                    {
                        IsHitTestVisible = false,
                        Content = new TextBlock { Text = day, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Opacity = 0.75, Margin = new Thickness(4, 14, 0, 4) }
                    });
                }

                var panel = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(e.Title) ? e.Url : e.Title,
                    FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis
                });
                panel.Children.Add(new TextBlock
                {
                    Text = local.ToString("HH:mm") + "   " + e.Url,
                    FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis
                });
                List.Items.Add(new ListViewItem { Content = panel, Tag = e.Url });
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) { Reload(); }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            HistoryStore.Clear();
            Reload();
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is string url) AppServices.OpenUrl?.Invoke(url);
        }
    }
}
