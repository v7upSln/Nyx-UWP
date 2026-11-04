using System;
using Nyx.Browser;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Nyx_UWP.Views
{
    public sealed partial class BookmarksPage : Page
    {
        public BookmarksPage()
        {
            InitializeComponent();
            Loaded += (s, a) => { BookmarkStore.Changed += OnStoreChanged; Reload(); };
            Unloaded += (s, a) => BookmarkStore.Changed -= OnStoreChanged;
        }

        private void OnStoreChanged() { var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, Reload); }

        private void Reload()
        {
            List.Items.Clear();
            var items = BookmarkStore.Search(SearchBox.Text, 500);
            if (items.Count == 0)
            {
                List.Items.Add(new ListViewItem
                {
                    IsHitTestVisible = false,
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "No bookmarks yet. Press the star in the address bar (or Ctrl+D) to add one." : "Nothing matches your search.",
                        Opacity = 0.7, Margin = new Thickness(4, 12, 0, 12), TextWrapping = TextWrapping.Wrap
                    }
                });
                return;
            }

            foreach (var b in items)
            {
                var url = b.Url;
                var row = new Grid { Margin = new Thickness(4, 6, 4, 6), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = b.Url, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
                remove.Click += (s, e) => BookmarkStore.Remove(url);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);

                List.Items.Add(new ListViewItem { Content = row, Tag = url, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) { Reload(); }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is string url) AppServices.OpenUrl?.Invoke(url);
        }
    }
}
