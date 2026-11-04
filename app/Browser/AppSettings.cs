using System;
using Windows.Storage;

namespace Nyx.Browser
{
    /// <summary>User preferences, persisted in LocalSettings.</summary>
    public static class AppSettings
    {
        private static object Raw(string key)
        {
            try { return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var v) ? v : null; }
            catch { return null; }
        }

        private static void Put(string key, object value)
        {
            try { ApplicationData.Current.LocalSettings.Values[key] = value; } catch { }
        }

        private static string Str(string key, string def) => Raw(key) as string ?? def;
        private static bool Flag(string key, bool def) => Raw(key) is bool b ? b : def;

        /// <summary>system | light | dark</summary>
        public static string Theme { get => Str("ui.theme", "system"); set => Put("ui.theme", value); }

        /// <summary>auto (follow the app theme) | light | dark | force (dark, and darken light sites)</summary>
        public static string SiteAppearance { get => Str("ui.siteAppearance", "auto"); set => Put("ui.siteAppearance", value); }

        public static string SearchEngine { get => Str("search.engine", "google"); set => Put("search.engine", value); }

        public static bool SaveHistory { get => Flag("privacy.history", true); set => Put("privacy.history", value); }
        public static bool StripTracking { get => Flag("privacy.stripTracking", true); set => Put("privacy.stripTracking", value); }
        public static bool Suggestions { get => Flag("search.suggestions", true); set => Put("search.suggestions", value); }
        public static bool BookmarksBar { get => Flag("ui.bookmarksBar", false); set => Put("ui.bookmarksBar", value); }
        /// <summary>error (the request fails, like Brave) | empty (an empty 403 answer, for compatibility)</summary>
        public static string BlockMode { get => Str("filters.blockMode", "error"); set => Put("filters.blockMode", value); }

        public static bool CheckUpdates { get => Flag("app.checkUpdates", true); set => Put("app.checkUpdates", value); }
        /// <summary>Last package version whose release notes have already been shown.</summary>
        public static string LastShownVersion { get => Str("app.lastShownVersion", ""); set => Put("app.lastShownVersion", value); }
        /// <summary>Latest release already announced, so startup checks do not repeat the same notice.</summary>
        public static string LastNotifiedRelease { get => Str("app.lastNotifiedRelease", ""); set => Put("app.lastNotifiedRelease", value); }
        public static bool YouTubeHelper { get => Flag("filters.youtube", true); set => Put("filters.youtube", value); }

        public static bool BlockPopups { get => Flag("privacy.blockPopups", true); set => Put("privacy.blockPopups", value); }

        private static bool IsXbox
        {
            get
            {
                try { return Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox"; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Smaller filter set for low-memory situations (applies after restart). Defaults to on for Xbox, where the
        /// app memory budget is small and the full catalog plus the browser engine does not fit comfortably.
        /// </summary>
        public static bool LiteLists { get => Flag("filters.lite", IsXbox); set => Put("filters.lite", value); }
    }

    /// <summary>Hooks the built-in pages (settings, history) use to talk to the main window.</summary>
    public static class AppServices
    {
        public static Action<string> OpenUrl;            // open an address in a new tab
        public static Action<string> SettingChanged;     // theme | appearance | engine | protection
        public static Action UpdateFilters;
        public static Func<string> FilterStatus;
        public static Action ClearSiteData;
        public static Func<System.Threading.Tasks.Task<string>> CheckUpdates;
        public static string PendingSettingsSection;          // set before opening Settings to land on a category
        public static Action<string> SelectSettingsSection;   // set by the Settings page while it is open
    }
}
