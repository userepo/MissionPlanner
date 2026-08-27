using System;
using System.IO;
using System.Linq;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Validates the BinaryFormatter index cache for the managed path - the
    /// path every platform without the native library uses (Linux/Mono,
    /// 32-bit, ARM; here simulated by UseNativeScan=false, which disables the
    /// same nativeCapable gate an unavailable library would). The cache only
    /// engages for >300MB files, so the fixture is the copter log plus a
    /// sparse zero tail: real records, big file, instant to create.
    /// </summary>
    public class DFLogCacheTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");

        /// <summary>replica of DFLogBuffer.CachePath (private) for cleanup
        /// and existence assertions</summary>
        static string CachePathFor(string filename)
        {
            return Path.GetTempPath() +
                   Path.GetFullPath(filename).Replace("/", "_").Replace("\\", "_").Replace(":", "_") +
                   Path.GetFileNameWithoutExtension(filename) + new FileInfo(filename).Length;
        }

        static (int count, string seen, string first, string mid, string last) Fingerprint(DFLogBuffer buffer)
        {
            return (buffer.Count,
                string.Join(",", buffer.SeenMessageTypes.OrderBy(a => a, StringComparer.Ordinal)),
                buffer[0], buffer[buffer.Count / 2], buffer[buffer.Count - 1]);
        }

        [Fact]
        public void CacheSavesLoadsAndSurvivesCorruptionOnManagedPath()
        {
            var dir = Path.Combine(Path.GetTempPath(), "DFLogCache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var logfile = Path.Combine(dir, "big.bin");
            string cachefile = null;

            var old = DFLogBuffer.UseNativeScan;
            try
            {
                // copter records followed by a sparse zero tail past the
                // 300MB cache threshold
                var copter = File.ReadAllBytes(Path.Combine(TestDataDir, "copter.bin"));
                using (var fs = File.Create(logfile))
                {
                    fs.Write(copter, 0, copter.Length);
                    fs.SetLength(320L * 1024 * 1024);
                }

                cachefile = CachePathFor(logfile);
                File.Delete(cachefile);

                // 1: managed cold open - scans and saves the cache
                DFLogBuffer.UseNativeScan = false;
                (int, string, string, string, string) baseline;
                using (var buffer = new DFLogBuffer(logfile))
                {
                    Assert.False(DFLogBuffer.LastScanNative);
                    Assert.False(DFLogBuffer.LastLoadFromCache);
                    baseline = Fingerprint(buffer);
                }

                Assert.True(File.Exists(cachefile), "managed open did not save the index cache");

                // 2: managed warm open - must load the cache, same results
                using (var buffer = new DFLogBuffer(logfile))
                {
                    Assert.True(DFLogBuffer.LastLoadFromCache, "cache present but not loaded");
                    Assert.False(DFLogBuffer.LastScanNative);
                    Assert.Equal(baseline, Fingerprint(buffer));
                }

                // 3: corrupted cache - must fall back to a scan, same results
                using (var fs = File.Open(cachefile, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Position = fs.Length / 2;
                    fs.WriteByte(0xFF);
                    fs.WriteByte(0xFF);
                }

                using (var buffer = new DFLogBuffer(logfile))
                {
                    Assert.False(DFLogBuffer.LastLoadFromCache, "corrupted cache was not rejected");
                    Assert.Equal(baseline, Fingerprint(buffer));
                }

                // the fallback scan re-saved a good cache
                Assert.True(File.Exists(cachefile));
                var cacheWritten = File.GetLastWriteTimeUtc(cachefile);

                // 4: native open - skips the cache in both directions and
                // agrees with the managed results
                DFLogBuffer.UseNativeScan = true;
                using (var buffer = new DFLogBuffer(logfile))
                {
                    Assert.True(DFLogBuffer.LastScanNative, "native scan did not run - dflog_ffi.dll missing?");
                    Assert.False(DFLogBuffer.LastLoadFromCache, "native path read the cache");
                    Assert.Equal(baseline, Fingerprint(buffer));
                }

                Assert.Equal(cacheWritten, File.GetLastWriteTimeUtc(cachefile));
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
                if (cachefile != null)
                    try
                    {
                        File.Delete(cachefile);
                    }
                    catch
                    {
                    }

                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }
    }
}
