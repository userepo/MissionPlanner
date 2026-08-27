using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Phase-C exit criterion: `dflog convert` must reproduce
    /// BinaryLog.ConvertBin byte for byte over the corpus (both headless, so
    /// M fields render as mode numbers in each).
    /// </summary>
    public class DflogCliTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");
        static string CliPath => Path.Combine(AppContext.BaseDirectory, "dflog.exe");

        [Theory]
        [InlineData("copter")]
        [InlineData("plane")]
        [InlineData("rover")]
        [InlineData("copter-isbd")]
        public void ConvertMatchesConvertBin(string name)
        {
            if (!File.Exists(CliPath))
                Assert.Skip("dflog.exe not built - run cargo build --release in rust/");

            var logfile = Path.Combine(TestDataDir, name + ".bin");
            var dir = Path.Combine(Path.GetTempPath(), "DflogCli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csharpOut = Path.Combine(dir, "csharp.log");
                var cliOut = Path.Combine(dir, "cli.log");

                BinaryLog.ConvertBin(logfile, csharpOut);

                var psi = new ProcessStartInfo(CliPath, $"convert \"{logfile}\" \"{cliOut}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(120000);
                    Assert.True(proc.ExitCode == 0, "dflog convert failed: " + stderr);
                }

                var expected = File.ReadAllBytes(csharpOut);
                var actual = File.ReadAllBytes(cliOut);

                if (!expected.SequenceEqual(actual))
                {
                    // line-level diagnostics for the first divergence
                    var expectedLines = File.ReadAllLines(csharpOut);
                    var actualLines = File.ReadAllLines(cliOut);
                    var n = Math.Min(expectedLines.Length, actualLines.Length);
                    for (var i = 0; i < n; i++)
                    {
                        if (expectedLines[i] != actualLines[i])
                            Assert.Fail($"line {i} differs\nC#:  {expectedLines[i]}\nCLI: {actualLines[i]}");
                    }

                    Assert.Fail($"line counts differ: C# {expectedLines.Length} vs CLI {actualLines.Length}");
                }
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
    }
}
