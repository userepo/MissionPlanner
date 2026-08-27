using System;
using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// P/Invoke bindings for the Rust dataflash index scanner
    /// (rust/crates/dflog-ffi). See docs/dflog-rust-core-plan.md phase A.
    /// All failures degrade to "not available" so callers can fall back to
    /// the managed scanner.
    /// </summary>
    internal static class DFLogNative
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        const string Dll = "dflog_ffi";
        const uint AbiVersion = 1;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern uint dflog_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_scan_file(byte[] pathUtf8, out IntPtr index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_index_free(IntPtr index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_last_error(byte[] buf, UIntPtr cap);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeIndex
        {
            public ulong count;
            public IntPtr offsets;
            public IntPtr types;
            // followed by rust-owned storage; opaque to this side
        }

        static readonly Lazy<bool> _available = new Lazy<bool>(() =>
        {
            try
            {
                var abi = dflog_abi_version();
                if (abi != AbiVersion)
                {
                    log.WarnFormat("dflog_ffi ABI {0} does not match expected {1}", abi, AbiVersion);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Debug("dflog_ffi not available: " + ex.Message);
                return false;
            }
        });

        public static bool Available => _available.Value;

        static string LastError()
        {
            try
            {
                var buf = new byte[1024];
                var n = dflog_last_error(buf, (UIntPtr)buf.Length);
                if (n > 0)
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            catch
            {
            }

            return "(unknown)";
        }

        /// <summary>
        /// Index the log at <paramref name="path"/>. Returns false (never
        /// throws) when the native library is missing or the scan fails, so
        /// the caller can use the managed scanner instead.
        /// </summary>
        public static bool TryScan(string path, out long[] offsets, out byte[] types)
        {
            offsets = null;
            types = null;

            if (!Available)
                return false;

            var handle = IntPtr.Zero;
            try
            {
                // netstandard2.0 has no LPUTF8Str - marshal the path by hand
                var pathUtf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
                var rc = dflog_scan_file(pathUtf8, out handle);
                if (rc != 0)
                {
                    log.WarnFormat("dflog_scan_file failed ({0}): {1}", rc, LastError());
                    return false;
                }

                var index = Marshal.PtrToStructure<NativeIndex>(handle);
                if (index.count > int.MaxValue)
                {
                    log.WarnFormat("dflog index too large for managed copy: {0}", index.count);
                    return false;
                }

                var count = (int)index.count;
                offsets = new long[count];
                types = new byte[count];
                if (count > 0)
                {
                    Marshal.Copy(index.offsets, offsets, 0, count);
                    Marshal.Copy(index.types, types, 0, count);
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Warn("dflog native scan failed", ex);
                offsets = null;
                types = null;
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    dflog_index_free(handle);
            }
        }
    }
}
