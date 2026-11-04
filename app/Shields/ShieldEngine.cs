using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nyx.Interop;
using Windows.Data.Json;

namespace Nyx.Shields
{
    [Flags]
    public enum ShieldFlags { None = 0, Block = 1, Exception = 2, Redirect = 4, Rewrite = 8 }

    public sealed class CosmeticResult
    {
        public string Css = "";
        public string Script = "";
    }

    /// <summary>Managed wrapper over the Rust engine. Safe to call from any thread.</summary>
    public sealed class ShieldEngine : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private IntPtr _h;

        private ShieldEngine(IntPtr h) { _h = h; }

        public static int SelfTest() => NyxNative.nyx_selftest();
        public static string NativeVersion() => Marshal.PtrToStringAnsi(NyxNative.nyx_version());

        /// <summary>Collects lists (each with a permission mask) and compiles them into one engine.</summary>
        public sealed class Builder : IDisposable
        {
            private IntPtr _set = NyxNative.nyx_set_new();

            /// <param name="permission">1 = list may use trusted scriptlets (uBlock / Brave lists).</param>
            public bool Add(byte[] rulesUtf8, byte permission)
            {
                if (_set == IntPtr.Zero || rulesUtf8 == null || rulesUtf8.Length == 0) return false;
                return NyxNative.nyx_set_add(_set, rulesUtf8, (UIntPtr)rulesUtf8.Length, permission) == 0;
            }

            public ShieldEngine Build()
            {
                if (_set == IntPtr.Zero) return null;
                var h = NyxNative.nyx_set_build(_set);   // consumes the set
                _set = IntPtr.Zero;
                return h == IntPtr.Zero ? null : new ShieldEngine(h);
            }

            public void Dispose()
            {
                if (_set != IntPtr.Zero) { NyxNative.nyx_set_free(_set); _set = IntPtr.Zero; }
            }
        }

        public static ShieldEngine FromSerialized(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            var h = NyxNative.nyx_engine_deserialize(data, (UIntPtr)data.Length);
            return h == IntPtr.Zero ? null : new ShieldEngine(h);
        }

        /// <summary>Loads the scriptlet / redirect resources (JSON). Required again after every deserialize.</summary>
        public int UseResources(byte[] json)
        {
            if (json == null || json.Length == 0) return 0;
            _lock.EnterWriteLock();
            try
            {
                return _h == IntPtr.Zero ? 0 : NyxNative.nyx_engine_use_resources(_h, json, (UIntPtr)json.Length);
            }
            finally { _lock.ExitWriteLock(); }
        }

        public byte[] Serialize()
        {
            _lock.EnterReadLock();
            try
            {
                if (_h == IntPtr.Zero) return null;
                var p = NyxNative.nyx_engine_serialize(_h, out var len);
                if (p == IntPtr.Zero) return null;
                var bytes = new byte[(int)len];
                Marshal.Copy(p, bytes, 0, bytes.Length);
                NyxNative.nyx_buffer_free(p, len);
                return bytes;
            }
            finally { _lock.ExitReadLock(); }
        }

        public ShieldFlags Check(string url, string sourceUrl, string type, string method = "GET")
        {
            _lock.EnterReadLock();
            try
            {
                if (_h == IntPtr.Zero) return ShieldFlags.None;
                var u = Utf8.GetBytes(url);
                var s = Utf8.GetBytes(string.IsNullOrEmpty(sourceUrl) ? "about:blank" : sourceUrl);
                var t = Utf8.GetBytes(type);
                var m = Utf8.GetBytes(string.IsNullOrEmpty(method) ? "GET" : method);
                int r = NyxNative.nyx_engine_check(_h, u, (UIntPtr)u.Length, s, (UIntPtr)s.Length,
                                                   t, (UIntPtr)t.Length, m, (UIntPtr)m.Length);
                return r < 0 ? ShieldFlags.None : (ShieldFlags)r;
            }
            finally { _lock.ExitReadLock(); }
        }

        public CosmeticResult GetCosmetic(string pageUrl)
        {
            _lock.EnterReadLock();
            try
            {
                if (_h == IntPtr.Zero || string.IsNullOrEmpty(pageUrl)) return null;
                var u = Utf8.GetBytes(pageUrl);
                var p = NyxNative.nyx_engine_cosmetic_json(_h, u, (UIntPtr)u.Length);
                if (p == IntPtr.Zero) return null;
                string json = PtrToUtf8(p);   // UTF-8, not ANSI: selectors may contain non-ASCII
                NyxNative.nyx_string_free(p);
                if (!JsonObject.TryParse(json, out var o)) return null;
                return new CosmeticResult { Css = o.GetNamedString("css", ""), Script = o.GetNamedString("script", "") };
            }
            finally { _lock.ExitReadLock(); }
        }

        private static string PtrToUtf8(IntPtr p)
        {
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Utf8.GetString(b);
        }

        public void Dispose()
        {
            _lock.EnterWriteLock();
            try
            {
                if (_h != IntPtr.Zero) { NyxNative.nyx_engine_free(_h); _h = IntPtr.Zero; }
            }
            finally { _lock.ExitWriteLock(); }
        }
    }
}
