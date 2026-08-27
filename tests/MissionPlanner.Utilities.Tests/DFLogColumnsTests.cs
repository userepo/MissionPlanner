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

        /// <summary>
        /// The pattern the converted consumers use for instanced types: fetch
        /// the value column plus the instance column and filter client-side.
        /// Must select exactly the rows GetEnumeratorType("TYPE[n]") yields.
        /// </summary>
        [Fact]
        public void InstanceFilteredColumnsMatchEnumerator()
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var buffer = new DFLogBuffer(Path.Combine(TestDataDir, "copter.bin")))
                {
                    var instanceField = buffer.GetInstanceFieldName("IMU");
                    Assert.NotNull(instanceField);

                    var ok = buffer.TryGetColumnsNative("IMU", new[] { "GyrX", instanceField },
                        out var linenos, out var columns);
                    Assert.True(ok);

                    var nativeRows = new List<(long lineno, double value)>();
                    for (var i = 0; i < linenos.Length; i++)
                        if (columns[1][i] == 0)
                            nativeRows.Add((linenos[i], columns[0][i]));

                    var col = buffer.dflog.FindMessageOffset("IMU", "GyrX");
                    var managedRows = new List<(long lineno, double value)>();
                    foreach (var item in buffer.GetEnumeratorType("IMU[0]"))
                        managedRows.Add((item.lineno,
                            Convert.ToDouble(item.raw[col], CultureInfo.InvariantCulture)));

                    Assert.Equal(managedRows, nativeRows);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }

        /// <summary>
        /// Array-column parity on a synthetic ISBD-like log: the native
        /// short[32] rows must equal the managed BinaryLog.UnionArray shorts.
        /// </summary>
        [Fact]
        public void ArrayColumnMatchesUnionArray()
        {
            // FMT: type 0xAB "ISB", format "Ha", labels "N,x", len 3+2+64
            var data = new List<byte> { 0xA3, 0x95, 0x80 };
            var fmt = new byte[86];
            fmt[0] = 0xAB;
            fmt[1] = 3 + 2 + 64;
            System.Text.Encoding.ASCII.GetBytes("ISB").CopyTo(fmt, 2);
            System.Text.Encoding.ASCII.GetBytes("Ha").CopyTo(fmt, 6);
            System.Text.Encoding.ASCII.GetBytes("N,x").CopyTo(fmt, 22);
            data.AddRange(fmt);

            var rnd = new Random(7);
            for (var rec = 0; rec < 5; rec++)
            {
                data.AddRange(new byte[] { 0xA3, 0x95, 0xAB });
                data.AddRange(BitConverter.GetBytes((ushort)rec));
                for (var e = 0; e < 32; e++)
                    data.AddRange(BitConverter.GetBytes((short)rnd.Next(short.MinValue, short.MaxValue)));
            }

            var dir = Path.Combine(Path.GetTempPath(), "DFLogArr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                var file = Path.Combine(dir, "isb.bin");
                File.WriteAllBytes(file, data.ToArray());

                using (var buffer = new DFLogBuffer(file))
                {
                    var ok = buffer.TryGetArrayColumnNative("ISB", "x", out var linenos, out var rows);
                    Assert.True(ok, "native array column query failed - dflog_ffi.dll missing?");
                    Assert.Equal(5, rows.Length);

                    var idx = buffer.dflog.FindMessageOffset("ISB", "x");
                    var managedLinenos = new List<long>();
                    var managedRows = new List<short[]>();
                    foreach (var item in buffer.GetEnumeratorType("ISB"))
                    {
                        managedLinenos.Add(item.lineno);
                        var ua = (BinaryLog.UnionArray)item.raw[idx];
                        managedRows.Add(ua.Shorts.ToArray());
                    }

                    Assert.Equal(managedLinenos, linenos);
                    for (var r = 0; r < rows.Length; r++)
                        Assert.Equal(managedRows[r], rows[r]);

                    // non-array field fails cleanly
                    Assert.False(buffer.TryGetArrayColumnNative("ISB", "N", out _, out _));
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

        /// <summary>
        /// Same parity check over real vehicle data: ISBD batch samples from
        /// a SITL log recorded with INS_LOG_BAT_MASK=1.
        /// </summary>
        [Fact]
        public void ArrayColumnMatchesUnionArrayOnRealIsbdLog()
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var buffer = new DFLogBuffer(Path.Combine(TestDataDir, "copter-isbd.bin")))
                {
                    Assert.Contains("ISBD", buffer.SeenMessageTypes);

                    var ok = buffer.TryGetArrayColumnNative("ISBD", "x", out var linenos, out var rows);
                    Assert.True(ok);
                    Assert.True(rows.Length > 0, "no ISBD rows decoded");

                    var idx = buffer.dflog.FindMessageOffset("ISBD", "x");
                    var r = 0;
                    foreach (var item in buffer.GetEnumeratorType("ISBD"))
                    {
                        Assert.Equal(item.lineno, linenos[r]);
                        var ua = (BinaryLog.UnionArray)item.raw[idx];
                        Assert.Equal(ua.Shorts.ToArray(), rows[r]);
                        r++;
                    }

                    Assert.Equal(r, rows.Length);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }

        /// <summary>
        /// The LogBrowse time-axis computation: TimeUS column / 1000 through
        /// DFLog.GetTimeFromMs must equal DFItem.time tick for tick.
        /// </summary>
        [Fact]
        public void ColumnwiseTimeMatchesDFItemTime()
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var buffer = new DFLogBuffer(Path.Combine(TestDataDir, "copter.bin")))
                {
                    var ok = buffer.TryGetColumnsNative("ATT", new[] { "TimeUS" }, out _, out var cols);
                    Assert.True(ok);

                    var r = 0;
                    foreach (var item in buffer.GetEnumeratorType("ATT"))
                    {
                        var columnwise = buffer.dflog.GetTimeFromMs(cols[0][r] / 1000.0);
                        Assert.Equal(item.time.Ticks, columnwise.Ticks);
                        r++;
                    }

                    Assert.Equal(r, cols[0].Length);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
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
