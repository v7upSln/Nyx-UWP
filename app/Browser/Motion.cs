using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Nyx.Browser
{
    public static class Motion
    {
        /// <summary>The tab shrinks and fades out before it is removed.</summary>
        public static System.Threading.Tasks.Task TabCloseAsync(TabViewItem item)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            try
            {
                var sb = new Storyboard();
                var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
                item.RenderTransformOrigin = new Point(0.5, 0.5);
                item.RenderTransform = scale;
                Add(sb, item, "Opacity", 1, 0, 140);
                Add(sb, scale, "ScaleX", 1, 0.85, 140);
                Add(sb, scale, "ScaleY", 1, 0.85, 140);
                sb.Completed += (s, e) => tcs.TrySetResult(true);
                sb.Begin();
            }
            catch { tcs.TrySetResult(true); }
            return tcs.Task;
        }

        private static void Add(Storyboard sb, DependencyObject target, string property, double from, double to, int ms)
        {
            var a = new DoubleAnimation
            {
                From = from, To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, property);
            sb.Children.Add(a);
        }

        /// <summary>New tab: the tab grows and fades in; built-in pages also slide up slightly.</summary>
        public static void TabOpen(TabViewItem item, FrameworkElement builtinPage)
        {
            try
            {
                var sb = new Storyboard();
                var scale = new ScaleTransform { ScaleX = 0.82, ScaleY = 0.82 };
                item.RenderTransformOrigin = new Point(0.5, 0.5);
                item.RenderTransform = scale;
                item.Opacity = 0;
                Add(sb, item, "Opacity", 0, 1, 200);
                Add(sb, scale, "ScaleX", 0.82, 1, 240);
                Add(sb, scale, "ScaleY", 0.82, 1, 240);

                if (builtinPage != null)
                {
                    var slide = new TranslateTransform { Y = 14 };
                    builtinPage.RenderTransform = slide;
                    builtinPage.Opacity = 0;
                    Add(sb, builtinPage, "Opacity", 0, 1, 240);
                    Add(sb, slide, "Y", 14, 0, 260);
                }
                sb.Begin();
            }
            catch { /* animation is cosmetic: never fail a tab open because of it */ }
        }
    }
}
