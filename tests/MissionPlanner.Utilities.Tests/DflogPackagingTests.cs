using System;
using System.IO;
using System.Runtime.InteropServices;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Phase-E packaging guard: the checked-in native library the app ships
    /// (ExtLibs/Utilities/dflog_ffi.dll) must exist and report the ABI this
    /// build of the managed code expects. Fails loudly when a Rust change
    /// bumps the ABI without running rust/update-dll.bat.
    /// </summary>
    public class DflogPackagingTests
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint AbiVersionFn();

        static string CheckedInDllPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "ExtLibs", "Utilities", "dflog_ffi.dll"));

        [Fact]
        public void CheckedInDllMatchesExpectedAbi()
        {
            var path = CheckedInDllPath;
            Assert.True(File.Exists(path),
                $"checked-in native library missing: {path} - run rust/update-dll.bat");

            var module = LoadLibrary(path);
            Assert.True(module != IntPtr.Zero,
                $"could not load {path} (error {Marshal.GetLastWin32Error()}) - wrong architecture?");
            try
            {
                var proc = GetProcAddress(module, "dflog_abi_version");
                Assert.True(proc != IntPtr.Zero, "dflog_abi_version export missing");

                var abi = Marshal.GetDelegateForFunctionPointer<AbiVersionFn>(proc)();
                Assert.True(DFLogNative.AbiVersion == abi,
                    $"checked-in dflog_ffi.dll reports ABI {abi} but this build expects " +
                    $"{DFLogNative.AbiVersion} - run rust/update-dll.bat and commit the DLL");
            }
            finally
            {
                FreeLibrary(module);
            }
        }
    }
}
