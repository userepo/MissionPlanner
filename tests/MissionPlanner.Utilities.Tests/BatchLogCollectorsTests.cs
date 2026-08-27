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
    /// Phase-B parity for the fftui batch collection fast paths: the native
    /// collectors must fill FFT2.datastate exactly like the legacy
    /// enumerator loops in fftui (replicated here verbatim as the reference).
    /// </summary>
    public class BatchLogCollectorsTests
    {
        static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "testdata");

        static FFT2.datastate[] NewStates(int n)
        {
            var all = new FFT2.datastate[n];
            for (var i = 0; i < n; i++)
                all[i] = new FFT2.datastate();
            return all;
        }

        static void AssertStatesEqual(FFT2.datastate[] expected, FFT2.datastate[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].type, actual[i].type);
                // documented divergence (plan phase B): legacy reads smp_rate
                // through its 7-significant-digit display string, the native
                // path keeps the raw float - compare within that rounding
                Assert.True(Math.Abs(expected[i].sample_rate - actual[i].sample_rate) <=
                            Math.Abs(expected[i].sample_rate) * 1e-6 + 1e-9,
                    $"sample_rate {expected[i].sample_rate} vs {actual[i].sample_rate}");
                Assert.Equal(expected[i].lasttime, actual[i].lasttime);
                Assert.Equal(expected[i].timedelta, actual[i].timedelta);
                Assert.Equal(expected[i].datax, actual[i].datax);
                Assert.Equal(expected[i].datay, actual[i].datay);
                Assert.Equal(expected[i].dataz, actual[i].dataz);
            }
        }

        [Fact]
        public void IsbhCollectorMatchesLegacyOnRealLog()
        {
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                using (var file = new DFLogBuffer(Path.Combine(TestDataDir, "copter-isbd.bin")))
                {
                    var native = NewStates(12);
                    Assert.True(BatchLogCollectors.TryCollectIsbhNative(file, native));
                    Assert.True(native.Any(d => d.datax.Count > 0), "no batch data collected");

                    var legacy = NewStates(12);
                    LegacyIsbhCollect(file, legacy);

                    AssertStatesEqual(legacy, native);
                }
            }
            finally
            {
                DFLogBuffer.UseNativeScan = old;
            }
        }

        /// <summary>verbatim replica of fftui.but_ISBH_Click's collection loop</summary>
        static void LegacyIsbhCollect(DFLogBuffer file, FFT2.datastate[] alldata)
        {
            int Ns = 0;
            int type = 0;
            int instance = 0;
            int sensorno = 0;
            double multiplier = -1;

            int offsetX = 0, offsetY = 0, offsetZ = 0, offsetTime = 0;

            foreach (var item in file.GetEnumeratorType(new string[] { "ISBH", "ISBD" }))
            {
                if (item.msgtype == null)
                    continue;

                if (item.msgtype.StartsWith("ISBH"))
                {
                    Ns = int.Parse(item.items[file.dflog.FindMessageOffset(item.msgtype, "N")],
                        CultureInfo.InvariantCulture);
                    type = int.Parse(item.items[file.dflog.FindMessageOffset(item.msgtype, "type")],
                        CultureInfo.InvariantCulture);
                    instance = int.Parse(item.items[file.dflog.FindMessageOffset(item.msgtype, "instance")],
                        CultureInfo.InvariantCulture);

                    sensorno = type * 6 + instance;

                    alldata[sensorno].sample_rate = double.Parse(
                        item.items[file.dflog.FindMessageOffset(item.msgtype, "smp_rate")],
                        CultureInfo.InvariantCulture);

                    multiplier = double.Parse(
                        item.items[file.dflog.FindMessageOffset(item.msgtype, "mul")],
                        CultureInfo.InvariantCulture);

                    if (type == 0)
                        alldata[sensorno].type = "ACC" + instance.ToString();
                    if (type == 1)
                        alldata[sensorno].type = "GYR" + instance.ToString();
                }
                else if (item.msgtype.StartsWith("ISBD"))
                {
                    if (sensorno >= alldata.Length)
                        continue;

                    var Nsdata = Convert.ToInt32(item.GetRaw("N"), CultureInfo.InvariantCulture);

                    if (Ns != Nsdata)
                        continue;

                    if (offsetX == 0) offsetX = file.dflog.FindMessageOffset(item.msgtype, "x");
                    if (offsetY == 0) offsetY = file.dflog.FindMessageOffset(item.msgtype, "y");
                    if (offsetZ == 0) offsetZ = file.dflog.FindMessageOffset(item.msgtype, "z");
                    if (offsetTime == 0) offsetTime = file.dflog.FindMessageOffset(item.msgtype, "TimeUS");

                    double time = Convert.ToDouble(item.raw[offsetTime], CultureInfo.InvariantCulture) / 1000.0;

                    if (time < alldata[sensorno].lasttime)
                        continue;

                    if (time != alldata[sensorno].lasttime)
                        alldata[sensorno].timedelta = alldata[sensorno].timedelta * 0.99 +
                                                      (time - alldata[sensorno].lasttime) * 0.01;

                    alldata[sensorno].lasttime = time;

                    var ua = (BinaryLog.UnionArray)item.raw[offsetX];
                    foreach (var aa in ua.Shorts.ToArray()) alldata[sensorno].datax.Add(aa / multiplier);
                    ua = (BinaryLog.UnionArray)item.raw[offsetY];
                    foreach (var aa in ua.Shorts.ToArray()) alldata[sensorno].datay.Add(aa / multiplier);
                    ua = (BinaryLog.UnionArray)item.raw[offsetZ];
                    foreach (var aa in ua.Shorts.ToArray()) alldata[sensorno].dataz.Add(aa / multiplier);
                }
            }
        }

        [Fact]
        public void AccGyrCollectorMatchesLegacyOnSyntheticLog()
        {
            // ACC1/GYR1 log with "QfffTimeUS,AccX.." style records including an
            // out-of-order timestamp (skipped) and a duplicate timestamp
            // (kept, no EMA update)
            var data = new List<byte>();

            void AddFmt(byte id, string name, string format, string labels)
            {
                data.AddRange(new byte[] { 0xA3, 0x95, 0x80 });
                var fmt = new byte[86];
                fmt[0] = id;
                fmt[1] = (byte)(3 + format.ToCharArray().Sum(c => c == 'Q' ? 8 : 4));
                System.Text.Encoding.ASCII.GetBytes(name).CopyTo(fmt, 2);
                System.Text.Encoding.ASCII.GetBytes(format).CopyTo(fmt, 6);
                System.Text.Encoding.ASCII.GetBytes(labels).CopyTo(fmt, 22);
                data.AddRange(fmt);
            }

            void AddRec(byte id, ulong timeus, float x, float y, float z)
            {
                data.AddRange(new byte[] { 0xA3, 0x95, id });
                data.AddRange(BitConverter.GetBytes(timeus));
                data.AddRange(BitConverter.GetBytes(x));
                data.AddRange(BitConverter.GetBytes(y));
                data.AddRange(BitConverter.GetBytes(z));
            }

            AddFmt(0xA0, "ACC1", "Qfff", "TimeUS,AccX,AccY,AccZ");
            AddFmt(0xA1, "GYR1", "Qfff", "TimeUS,GyrX,GyrY,GyrZ");

            var rnd = new Random(11);
            ulong t = 1000000;
            for (var i = 0; i < 50; i++)
            {
                t += 2500;
                AddRec(0xA0, t, (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());
                AddRec(0xA1, t, (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());
                if (i == 20)
                {
                    // out of order: must be skipped by both paths
                    AddRec(0xA0, t - 500000, 9f, 9f, 9f);
                    // duplicate time: kept, but no timedelta update
                    AddRec(0xA0, t, 8f, 8f, 8f);
                }
            }

            var dir = Path.Combine(Path.GetTempPath(), "DFLogAccGyr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var old = DFLogBuffer.UseNativeScan;
            DFLogBuffer.UseNativeScan = true;
            try
            {
                var file = Path.Combine(dir, "accgyr.bin");
                File.WriteAllBytes(file, data.ToArray());

                using (var buffer = new DFLogBuffer(file))
                {
                    var native = NewStates(6);
                    Assert.True(BatchLogCollectors.TryCollectAccGyrNative(buffer, native));

                    var legacy = NewStates(6);
                    LegacyAccGyrCollect(buffer, legacy);

                    Assert.Equal(51, native[3].datax.Count); // 50 + duplicate, minus out-of-order
                    AssertStatesEqual(legacy, native);
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

        /// <summary>verbatim replica of fftui.BUT_accgyrall_Click's collection loop</summary>
        static void LegacyAccGyrCollect(DFLogBuffer file, FFT2.datastate[] alldata)
        {
            int offsetAX = 0, offsetAY = 0, offsetAZ = 0, offsetTimeacc = 0;
            int offsetGX = 0, offsetGY = 0, offsetGZ = 0, offsetTimegyr = 0;

            foreach (var item in file.GetEnumeratorType(new string[]
                         { "ACC1", "GYR1", "ACC2", "GYR2", "ACC3", "GYR3", "ACC4", "GYR4" }))
            {
                if (item.msgtype == null)
                    continue;

                if (item.msgtype.StartsWith("ACC"))
                {
                    int sensorno = item.instance == ""
                        ? int.Parse(item.msgtype.Substring(3), CultureInfo.InvariantCulture) - 1 + 3
                        : int.Parse(item.instance) + 3;
                    alldata[sensorno].type = item.msgtype;

                    if (offsetAX == 0) offsetAX = file.dflog.FindMessageOffset(item.msgtype, "AccX");
                    if (offsetAY == 0) offsetAY = file.dflog.FindMessageOffset(item.msgtype, "AccY");
                    if (offsetAZ == 0) offsetAZ = file.dflog.FindMessageOffset(item.msgtype, "AccZ");
                    if (offsetTimeacc == 0) offsetTimeacc = file.dflog.FindMessageOffset(item.msgtype, "TimeUS");

                    double time = Convert.ToDouble(item.raw[offsetTimeacc], CultureInfo.InvariantCulture) / 1000.0;

                    if (time < alldata[sensorno].lasttime)
                        continue;

                    if (time != alldata[sensorno].lasttime)
                        alldata[sensorno].timedelta = alldata[sensorno].timedelta * 0.99 +
                                                      (time - alldata[sensorno].lasttime) * 0.01;

                    alldata[sensorno].lasttime = time;

                    alldata[sensorno].datax.Add(Convert.ToDouble(item.raw[offsetAX], CultureInfo.InvariantCulture));
                    alldata[sensorno].datay.Add(Convert.ToDouble(item.raw[offsetAY], CultureInfo.InvariantCulture));
                    alldata[sensorno].dataz.Add(Convert.ToDouble(item.raw[offsetAZ], CultureInfo.InvariantCulture));
                }
                else if (item.msgtype.StartsWith("GYR"))
                {
                    int sensorno = item.instance == ""
                        ? int.Parse(item.msgtype.Substring(3), CultureInfo.InvariantCulture) - 1
                        : int.Parse(item.instance);
                    alldata[sensorno].type = item.msgtype;

                    if (offsetGX == 0) offsetGX = file.dflog.FindMessageOffset(item.msgtype, "GyrX");
                    if (offsetGY == 0) offsetGY = file.dflog.FindMessageOffset(item.msgtype, "GyrY");
                    if (offsetGZ == 0) offsetGZ = file.dflog.FindMessageOffset(item.msgtype, "GyrZ");
                    if (offsetTimegyr == 0) offsetTimegyr = file.dflog.FindMessageOffset(item.msgtype, "TimeUS");

                    double time = Convert.ToDouble(item.raw[offsetTimegyr], CultureInfo.InvariantCulture) / 1000.0;

                    if (time < alldata[sensorno].lasttime)
                        continue;

                    if (time != alldata[sensorno].lasttime)
                        alldata[sensorno].timedelta = alldata[sensorno].timedelta * 0.99 +
                                                      (time - alldata[sensorno].lasttime) * 0.01;

                    alldata[sensorno].lasttime = time;

                    alldata[sensorno].datax.Add(Convert.ToDouble(item.raw[offsetGX], CultureInfo.InvariantCulture));
                    alldata[sensorno].datay.Add(Convert.ToDouble(item.raw[offsetGY], CultureInfo.InvariantCulture));
                    alldata[sensorno].dataz.Add(Convert.ToDouble(item.raw[offsetGZ], CultureInfo.InvariantCulture));
                }
            }
        }
    }
}
