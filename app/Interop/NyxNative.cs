using System;
using System.Runtime.InteropServices;

namespace Nyx.Interop
{
    // Raw P/Invoke surface of Nyx-native\nyx-adblock\src\lib.rs. Keep in sync with the Rust file.
    internal static class NyxNative
    {
        private const string Dll = "nyx_adblock.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int nyx_selftest();

        // engine lifetime
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_engine_deserialize(byte[] data, UIntPtr len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_engine_serialize(IntPtr engine, out UIntPtr outLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void nyx_buffer_free(IntPtr ptr, UIntPtr len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void nyx_engine_free(IntPtr engine);

        // multi-list builder (per-list permission mask)
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_set_new();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int nyx_set_add(IntPtr set, byte[] rules, UIntPtr len, byte permission);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_set_build(IntPtr set);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void nyx_set_free(IntPtr set);

        // scriptlet resources
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int nyx_engine_use_resources(IntPtr engine, byte[] json, UIntPtr len);

        // matching
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nyx_engine_check(IntPtr engine, byte[] url, UIntPtr urlLen, byte[] source, UIntPtr sourceLen,
                                                    byte[] type, UIntPtr typeLen, byte[] method, UIntPtr methodLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nyx_engine_cosmetic_json(IntPtr engine, byte[] url, UIntPtr urlLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void nyx_string_free(IntPtr s);
    }
}
