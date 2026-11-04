using System;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.System.Profile;

namespace Nyx.Browser
{
    /// <summary>Small local rolling log for Xbox Dev Mode diagnostics. It never records cookies or request bodies.</summary>
    public static class NyxLog
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 512 * 1024;
        private static string _path;

        public static string PathName
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(_path))
                    {
                        var dir = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nyx", "logs");
                        Directory.CreateDirectory(dir);
                        _path = Path.Combine(dir, "nyx.log");
                    }
                    return _path;
                }
                catch { return "Log unavailable"; }
            }
        }

        public static void Initialize()
        {
            string device;
            try { device = AnalyticsInfo.VersionInfo.DeviceFamily; }
            catch { device = "unknown"; }
            Write("INFO", "Startup", "Nyx " + UpdateChecker.Version + " on " + device);
        }

        public static void Info(string category, string message) => Write("INFO", category, message);
        public static void Warn(string category, string message) => Write("WARN", category, message);
        public static void Error(string category, string message, Exception error = null)
        {
            string detail = error == null ? message : message + " | " + error.GetType().Name + ": " + error.Message;
            Write("ERROR", category, detail);
        }

        private static string Clean(string value)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return Regex.Replace(value, @"(https?://[^\s?]+)\?[^\s]*", "$1?[redacted]");
        }

        private static void Write(string level, string category, string message)
        {
            try
            {
                lock (Gate)
                {
                    var path = PathName;
                    if (path == "Log unavailable") return;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length >= MaxBytes)
                    {
                        var old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path,
                        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " [" + level + "] [" + Clean(category) + "] " + Clean(message) + Environment.NewLine);
                }
            }
            catch { /* Diagnostics must never take down the browser. */ }
        }
    }
}
