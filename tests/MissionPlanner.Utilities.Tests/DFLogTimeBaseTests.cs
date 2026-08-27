using System;
using System.IO;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// The native time correlation (general access layer) must match the
    /// managed DFLog GPS correlation: for any board-time value, the native
    /// wall clock equals DFLog.GetTimeFromMs converted to UTC epoch ms,
    /// within 1 ms (tick truncation in the managed conversion).
    /// </summary>
    public class DFLogTimeBaseTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");

        static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("copter")]
        [InlineData("plane")]
        [InlineData("rover")]
        [InlineData("copter-isbd")]
        public void NativeTimeBaseMatchesManagedGetTimeFromMs(string name)
        {
            var logfile = Path.Combine(TestDataDir, name + ".bin");

            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = false;
            try
            {
                using (var buffer = new DFLogBuffer(logfile))
                using (var reader = DFLogNative.ColumnReader.Open(logfile))
                {
                    Assert.True(buffer.dflog.gpsstarttime != DateTime.MinValue,
                        "managed path found no gps time - corpus log unusable for this test");
                    Assert.NotNull(reader);
                    Assert.True(reader.TryGetTimeBase(out var gpsStartUnixMs, out var msOffset),
                        "native time base unavailable");

                    foreach (var boardMs in new[] { 0.0, msOffset, msOffset + 123456.789, 1e9 })
                    {
                        var managed = buffer.dflog.GetTimeFromMs(boardMs).ToUniversalTime();
                        var managedUnixMs = (managed - UnixEpoch).TotalMilliseconds;
                        var native = gpsStartUnixMs + (boardMs - msOffset);

                        Assert.True(Math.Abs(managedUnixMs - native) <= 1.0,
                            $"boardMs={boardMs}: managed {managedUnixMs} vs native {native}");
                    }
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }
    }
}
