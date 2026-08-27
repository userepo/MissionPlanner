using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Phase-B parity tests: the native typed-column path must produce exactly
    /// the values the managed decoder produces for the raw (pre-string)
    /// objects, row for row, for every record of the requested type.
    /// </summary>
    public class DFLogColumnsTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");

        public static IEnumerable<object[]> Cases =>
            new[]
            {
                new object[] { "copter", "ATT", new[] { "Roll", "Pitch", "Yaw" } },
                new object[] { "copter", "IMU", new[] { "I", "GyrX", "AccZ" } },
                new object[] { "copter", "GPS", new[] { "Lat", "Lng", "Alt" } },
                new object[] { "copter", "ATT", new[] { "TimeUS" } },
                new object[] { "plane", "ATT", new[] { "Roll", "Pitch" } },
                new object[] { "rover", "GPS", new[] { "Lat", "Lng" } },
            };

        [Theory]
        [MemberData(nameof(Cases))]
        public void NativeColumnsMatchManagedDecode(string logname, string type, string[] fields)
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var buffer = new DFLogBuffer(Path.Combine(TestDataDir, logname + ".bin")))
                {
                    var ok = buffer.TryGetColumnsNative(type, fields, out var linenos, out var columns);
                    Assert.True(ok, "native column query failed - dflog_ffi.dll missing?");

                    var (expectedLinenos, expectedCols) = ManagedReference(buffer, type, fields);

                    Assert.Equal(expectedLinenos, linenos);
                    for (var c = 0; c < fields.Length; c++)
                        Assert.Equal(expectedCols[c], columns[c]);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }

        [Fact]
        public void TruncatedTailDecodesLikeManaged()
        {
            var source = File.ReadAllBytes(Path.Combine(TestDataDir, "copter.bin"));
            var dir = Path.Combine(Path.GetTempPath(), "DFLogCols-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                var file = Path.Combine(dir, "trunc.bin");
                File.WriteAllBytes(file, source.Take(source.Length - 37).ToArray());

                using (var buffer = new DFLogBuffer(file))
                {
                    var ok = buffer.TryGetColumnsNative("ATT", new[] { "Roll" }, out var linenos, out var columns);
                    Assert.True(ok);

                    var (expectedLinenos, expectedCols) = ManagedReference(buffer, "ATT", new[] { "Roll" });
                    Assert.Equal(expectedLinenos, linenos);
                    Assert.Equal(expectedCols[0], columns[0]);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void UnknownFieldFailsCleanly()
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var buffer = new DFLogBuffer(Path.Combine(TestDataDir, "copter.bin")))
                {
                    Assert.False(buffer.TryGetColumnsNative("ATT", new[] { "NoSuchField" }, out _, out _));
                    Assert.False(buffer.TryGetColumnsNative("NOTYPE", new[] { "Roll" }, out _, out _));
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }

        /// <summary>
        /// The managed truth: enumerate DFItems and convert the raw decoded
        /// objects (not the display strings) to double, as a precision-exact
        /// consumer would.
        /// </summary>
        static (long[] linenos, double[][] cols) ManagedReference(DFLogBuffer buffer, string type, string[] fields)
        {
            var indices = fields.Select(f => buffer.dflog.FindMessageOffset(type, f)).ToArray();
            Assert.All(indices, i => Assert.True(i > 0, "field not found in managed logformat"));

            var linenos = new List<long>();
            var cols = fields.Select(_ => new List<double>()).ToArray();
            foreach (var item in buffer.GetEnumeratorType(type))
            {
                linenos.Add(item.lineno);
                for (var c = 0; c < indices.Length; c++)
                {
                    var raw = item.raw[indices[c]];
                    cols[c].Add(raw is string s
                        ? double.Parse(s, CultureInfo.InvariantCulture)
                        : Convert.ToDouble(raw, CultureInfo.InvariantCulture));
                }
            }

            return (linenos.ToArray(), cols.Select(c => c.ToArray()).ToArray());
        }
    }
}
