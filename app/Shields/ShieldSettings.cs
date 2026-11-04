using System;
using System.Collections.Generic;
using Windows.Storage;

namespace Nyx.Shields
{
    public sealed class ShieldSettings
    {
        private readonly HashSet<string> _disabledHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Enabled { get; set; } = true;

        private static ShieldSettings _current;
        /// <summary>Shared instance used by the main window and the settings page.</summary>
        public static ShieldSettings Current => _current ?? (_current = Load());

        public static ShieldSettings Load()
        {
            var s = new ShieldSettings();
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (v.TryGetValue("shields.enabled", out var e) && e is bool b) s.Enabled = b;
                if (v.TryGetValue("shields.disabledHosts", out var d) && d is string str)
                    foreach (var h in str.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) s._disabledHosts.Add(h);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                v["shields.enabled"] = Enabled;
                v["shields.disabledHosts"] = string.Join(";", _disabledHosts);
            }
            catch { }
        }

        private static string HostOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        }

        public bool IsDisabledFor(string pageUrl) => _disabledHosts.Contains(HostOf(pageUrl));
        public bool IsHostDisabled(string host) => _disabledHosts.Contains(host);

        public bool ToggleSite(string pageUrl)
        {
            var h = HostOf(pageUrl);
            if (h.Length == 0) return false;
            if (!_disabledHosts.Remove(h)) _disabledHosts.Add(h);
            Save();
            return _disabledHosts.Contains(h);
        }
    }
}
