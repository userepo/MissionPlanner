using System;
using System.Collections.Generic;
using System.IO;
using MissionPlanner.Utilities;
using Xunit;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Phase-B parity for the converted Spectrogram ISBH/ISBD batch path:
    /// the full GenerateImage pipeline must produce the same FFT output with
    /// the native columns as with the legacy enumerator extraction.
    /// </summary>
    public class SpectrogramTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");

        [Fact]
        public void GenerateImageMatchesLegacyOnIsbdLog()
        {
            var file = Path.Combine(TestDataDir, "copter-isbd.bin");

            var old = DFLogBuffer.UseNativeScan;
            try
            {
                DFLogBuffer.UseNativeScan = true;
                double[] freqtNative;
                List<(double timeus, double[] value)> fftNative;
                using (var buffer = new DFLogBuffer(file))
                {
                    Assert.Contains("ISBH", buffer.SeenMessageTypes);
                    using (Spectrogram.GenerateImage(buffer, out freqtNative, out fftNative))
                    {
                    }
                }

                DFLogBuffer.UseNativeScan = false;
                double[] freqtLegacy;
                List<(double timeus, double[] value)> fftLegacy;
                using (var buffer = new DFLogBuffer(file))
                {
                    using (Spectrogram.GenerateImage(buffer, out freqtLegacy, out fftLegacy))
                    {
                    }
                }

                Assert.Equal(freqtLegacy, freqtNative);
                Assert.Equal(fftLegacy.Count, fftNative.Count);
                Assert.True(fftLegacy.Count > 0, "no fft windows produced - log too short?");

                for (var w = 0; w < fftLegacy.Count; w++)
                {
                    Assert.Equal(fftLegacy[w].timeus, fftNative[w].timeus);
                    Assert.Equal(fftLegacy[w].value, fftNative[w].value);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }
    }
}
