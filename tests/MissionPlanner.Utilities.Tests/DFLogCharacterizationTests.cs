using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Phase-0 characterization suite for the dataflash log core (BinaryLog /
    /// DFLog / DFLogBuffer) - see docs/dflog-rust-core-plan.md.
    ///
    /// These tests snapshot the CURRENT parser's observable behavior over a
    /// corpus of SITL-generated logs (plus deterministically derived corrupt
    /// variants) into golden files. They are the compatibility contract any
    /// replacement implementation must satisfy; where current behavior is a
    /// bug, the golden documents it until a deliberate decision changes it.
    ///
    /// To regenerate goldens after an intentional behavior change:
    ///   set DFLOG_UPDATE_GOLDEN=1 and run the suite once.
    /// </summary>
    public class DFLogCharacterizationTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");
        static string GoldenOutDir => Path.Combine(TestDataDir, "goldens");

        static bool UpdateGoldens =>
            Environment.GetEnvironmentVariable("DFLOG_UPDATE_GOLDEN") == "1";

        static string SourceDir([CallerFilePath] string path = "") =>
            Path.GetDirectoryName(path);

        [Theory]
        [InlineData("copter")]
        [InlineData("plane")]
        [InlineData("rover")]
        public void CorpusLogMatchesGolden(string name)
        {
            var report = GenerateReport(Path.Combine(TestDataDir, name + ".bin"));
            AssertMatchesGolden(name, report);
        }

        public static IEnumerable<object[]> Variants =>
            new[]
            {
                new object[] { "copter-truncated60" },
                new object[] { "copter-truncated-midrecord" },
                new object[] { "copter-garbage-prefix" },
                new object[] { "copter-corrupt-fmt" },
                new object[] { "empty" },
            };

        [Theory]
        [MemberData(nameof(Variants))]
        public void DerivedVariantMatchesGolden(string variant)
        {
            var source = File.ReadAllBytes(Path.Combine(TestDataDir, "copter.bin"));
            byte[] data;
            switch (variant)
            {
                case "copter-truncated60":
                    data = source.Take(source.Length * 6 / 10).ToArray();
                    break;
                case "copter-truncated-midrecord":
                    data = source.Take(source.Length - 37).ToArray();
                    break;
                case "copter-garbage-prefix":
                    data = Enumerable.Repeat((byte)0xA5, 128).Concat(source).ToArray();
                    break;
                case "copter-corrupt-fmt":
                    data = (byte[])source.Clone();
                    data[8] ^= 0xFF;
                    break;
                case "empty":
                    data = new byte[0];
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }

            var dir = Path.Combine(Path.GetTempPath(), "DFLogChar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, variant + ".bin");
                File.WriteAllBytes(file, data);
                var report = GenerateReport(file);
                AssertMatchesGolden(variant, report);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        static void AssertMatchesGolden(string name, string report)
        {
            var goldenFile = name + ".golden.txt";

            if (UpdateGoldens)
            {
                var sourceGolden = Path.Combine(SourceDir(), "testdata", "goldens", goldenFile);
                Directory.CreateDirectory(Path.GetDirectoryName(sourceGolden));
                File.WriteAllText(sourceGolden, report);
                Directory.CreateDirectory(GoldenOutDir);
                File.WriteAllText(Path.Combine(GoldenOutDir, goldenFile), report);
                return;
            }

            var path = Path.Combine(GoldenOutDir, goldenFile);
            Assert.True(File.Exists(path),
                $"golden {goldenFile} missing - run once with DFLOG_UPDATE_GOLDEN=1");
            var expected = File.ReadAllText(path);
            Assert.Equal(expected, report);
        }

        /// <summary>
        /// Deterministic, culture-invariant dump of everything a DFLogBuffer
        /// consumer can observe: counts, metadata tables, sample rows, per-type
        /// record counts, first records of common types, units and instances.
        /// </summary>
        static string GenerateReport(string file)
        {
            var oldCulture = CultureInfo.CurrentCulture;
            var oldUiCulture = CultureInfo.CurrentUICulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            try
            {
                var sb = new StringBuilder();
                try
                {
                    using (var buffer = new DFLogBuffer(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)))
                    {
                        Describe(buffer, sb);
                    }
                }
                catch (Exception ex)
                {
                    // characterize failures too: which exception type surfaces
                    sb.AppendLine("threw=" + ex.GetType().FullName);
                }

                return sb.ToString();
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = oldCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = oldUiCulture;
            }
        }

        static void Describe(DFLogBuffer buffer, StringBuilder sb)
        {
            sb.AppendLine("count=" + buffer.Count);
            sb.AppendLine("seen=" + string.Join(",", buffer.SeenMessageTypes.OrderBy(a => a, StringComparer.Ordinal)));

            foreach (var kvp in buffer.FMT.OrderBy(a => a.Key))
                sb.AppendLine(FormattableString.Invariant(
                    $"fmt[{kvp.Key}]={kvp.Value.name};len={kvp.Value.length};fmt={kvp.Value.format};cols={kvp.Value.columns}"));

            foreach (var kvp in buffer.FMTU.OrderBy(a => a.Key))
                sb.AppendLine(FormattableString.Invariant(
                    $"fmtu[{kvp.Key}]={kvp.Value.Item1};{kvp.Value.Item2}"));

            foreach (var kvp in buffer.Unit.OrderBy(a => a.Key))
                sb.AppendLine(FormattableString.Invariant($"unit[{kvp.Key}]={kvp.Value}"));

            foreach (var kvp in buffer.Mult.OrderBy(a => a.Key))
                sb.AppendLine(FormattableString.Invariant($"mult[{kvp.Key}]={kvp.Value}"));

            sb.AppendLine("gpsstarttime=" +
                buffer.dflog.gpsstarttime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            if (buffer.Count > 0)
            {
                sb.AppendLine("row[first]=" + buffer[0]);
                sb.AppendLine("row[mid]=" + buffer[buffer.Count / 2]);
                sb.AppendLine("row[last]=" + buffer[buffer.Count - 1]);
            }

            // one pass over everything: records per message type
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in buffer.GetEnumeratorTypeAll())
            {
                var key = item.msgtype ?? "(null)";
                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
            }

            foreach (var kvp in counts)
                sb.AppendLine(FormattableString.Invariant($"typecount[{kvp.Key}]={kvp.Value}"));

            foreach (var type in new[] { "ATT", "IMU", "GPS", "PARM", "MSG" })
            {
                if (!buffer.SeenMessageTypes.Contains(type))
                    continue;
                var first = buffer.GetEnumeratorType(type).FirstOrDefault();
                sb.AppendLine($"first[{type}]=" + string.Join("|", first.items ?? new string[0]));
            }

            foreach (var pair in new[] { ("ATT", "Roll"), ("IMU", "GyrX"), ("GPS", "Lat"), ("BARO", "Alt") })
            {
                try
                {
                    var unit = buffer.GetUnit(pair.Item1, pair.Item2);
                    sb.AppendLine(FormattableString.Invariant(
                        $"getunit[{pair.Item1}.{pair.Item2}]={unit.Item1};{unit.Item2}"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"getunit[{pair.Item1}.{pair.Item2}]=threw:{ex.GetType().Name}");
                }
            }

            foreach (var type in new[] { "IMU", "GPS" })
                sb.AppendLine(FormattableString.Invariant(
                    $"instanceindex[{type}]={buffer.getInstanceIndex(type)}"));
        }
    }
}
