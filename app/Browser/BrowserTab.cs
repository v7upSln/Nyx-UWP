using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Nyx.Shields;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Nyx.Browser
{
    public enum TabKind { Web, Settings, History, Bookmarks, Downloads }

    /// <summary>One tab: either a web page (own WebView2 and shield hooks) or a built-in page (settings, history).</summary>
    public sealed class BrowserTab
    {
        public TabKind Kind;
        public TabViewItem Item;
        public WebView2 Web;          // Kind == Web
        public Frame Frame;           // built-in pages
        public PageShield Shield;
        public bool IsStartPage;
        public bool IsLoading;
        public double LoadProgress;
        public bool IsReleaseNotes;
        public string FailedUrl;      // shown in the address bar while the error page is displayed
        public string ForceDarkScriptId;
        public string HqIconHost;     // host whose sharp icon is already shown on the tab
        public System.DateTime LastUsed;   // for the Xbox tab cap: the tab used least recently is the one dropped

        public Control View => Web != null ? (Control)Web : Frame;
        public CoreWebView2 Core => Web?.CoreWebView2;
    }
}
