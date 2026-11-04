using System;
using System.IO;
using Windows.Data.Json;
using Windows.Storage;

namespace Nyx.Browser
{
    /// <summary>
    /// Configuration and update state stored locally in the Xbox / UWP file system (nyx/config.json).
    /// Used to track installed versions and ensure release changelogs are only displayed once upon updating.
    /// </summary>
    public static class AppConfig
    {
        private static readonly object Gate = new object();
        private static string _configPath;

        public static string ConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(_configPath))
                {
                    try
                    {
                        var dir = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx");
                        Directory.CreateDirectory(dir);
                        _configPath = Path.Combine(dir, "config.json");
                    }
                    catch
                    {
                        _configPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "config.json");
                    }
                }
                return _configPath;
            }
        }

        public static string GetLastSeenVersion()
        {
            lock (Gate)
            {
                try
                {
                    var path = ConfigPath;
                    if (File.Exists(path))
                    {
                        var text = File.ReadAllText(path);
                        if (JsonObject.TryParse(text, out var obj) && obj.ContainsKey("last_seen_version"))
                        {
                            return obj.GetNamedString("last_seen_version", "");
                        }
                    }
                }
                catch { }

                try
                {
                    if (ApplicationData.Current.LocalSettings.Values.TryGetValue("app.last_seen_version", out var val))
                        return val?.ToString() ?? "";
                }
                catch { }
                return "";
            }
        }

        public static void SetLastSeenVersion(string version)
        {
            lock (Gate)
            {
                try
                {
                    var path = ConfigPath;
                    JsonObject obj = new JsonObject();
                    if (File.Exists(path))
                    {
                        var text = File.ReadAllText(path);
                        if (JsonObject.TryParse(text, out var parsed)) obj = parsed;
                    }
                    obj.SetNamedValue("last_seen_version", JsonValue.CreateStringValue(version ?? ""));
                    obj.SetNamedValue("updated_at", JsonValue.CreateStringValue(DateTime.UtcNow.ToString("o")));
                    File.WriteAllText(path, obj.Stringify());
                }
                catch { }

                try
                {
                    ApplicationData.Current.LocalSettings.Values["app.last_seen_version"] = version;
                }
                catch { }
            }
        }

        /// <summary>
        /// Returns true if the installed version is different from the last version seen on this console.
        /// When true, it marks the current version as seen so this only returns true once per update.
        /// </summary>
        public static bool CheckFirstRunAfterUpdate(out string currentVersion, out string previousVersion)
        {
            currentVersion = UpdateChecker.Version;
            previousVersion = GetLastSeenVersion();

            if (string.IsNullOrEmpty(previousVersion))
            {
                SetLastSeenVersion(currentVersion);
                return true;
            }

            if (!string.Equals(previousVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                SetLastSeenVersion(currentVersion);
                return true;
            }

            return false;
        }
    }
}
