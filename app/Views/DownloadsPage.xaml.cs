using System;
using Nyx.Browser;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using muxc = Microsoft.UI.Xaml.Controls;

namespace Nyx_UWP.Views
{
    public sealed partial class DownloadsPage : Page
    {
        public DownloadsPage()
        {
            InitializeComponent();
            Loaded += (s, a) => { DownloadManager.Changed += OnChanged; Reload(); };
            Unloaded += (s, a) => DownloadManager.Changed -= OnChanged;
        }

        private void OnChanged() { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, Reload); }

        private static string Size(long b) => b >= 1048576 ? (b / 1048576.0).ToString("0.#") + " MB" : Math.Max(1, b / 1024) + " KB";

        private void Reload()
        {
            List.Items.Clear();
            DownloadItem[] items;
            lock (DownloadManager.Items) items = DownloadManager.Items.ToArray();
            if (items.Length == 0)
            {
                List.Items.Add(new ListViewItem { IsHitTestVisible = false, Content = new TextBlock { Text = "No downloads yet.", Opacity = 0.7, Margin = new Thickness(4, 12, 0, 12) } });
                return;
            }

            foreach (var d in items)
            {
                var row = new Grid { Margin = new Thickness(4, 8, 4, 8), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 4 };
                text.Children.Add(new TextBlock { Text = d.Name, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
                string detail = d.Done ? d.Status : d.Total > 0 ? Size(d.Received) + " of " + Size(d.Total) : Size(d.Received);
                text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7 });
                if (!d.Done) text.Children.Add(new muxc.ProgressBar { Value = d.Progress * 100, Maximum = 100, IsIndeterminate = d.Total <= 0, Height = 4, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(2) });
                row.Children.Add(text);

                if (d.Saved != null)
                {
                    var file = d.Saved;
                    var open = new Button { Content = "Open", VerticalAlignment = VerticalAlignment.Center };
                    open.Click += async (s, e) => { try { await Launcher.LaunchFileAsync(file); } catch { } };
                    Grid.SetColumn(open, 1);
                    row.Children.Add(open);
                }
                List.Items.Add(new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }

        private void OnClear(object s, RoutedEventArgs e) { DownloadManager.ClearFinished(); }
    }
}
