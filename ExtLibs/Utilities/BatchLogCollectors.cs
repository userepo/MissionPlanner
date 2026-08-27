using System;
using System.Collections.Generic;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Typed fast paths for fftui's sensor-data collection loops via the
    /// native dflog library: whole-column decode replayed
    /// by line number instead of per-row DFItem enumeration. Each returns
    /// false (caller runs its legacy enumerator loop) when the native path
    /// is off/unavailable or the expected message types are missing.
    /// </summary>
    public static class BatchLogCollectors
    {
        /// <summary>
        /// ISBH/ISBD batch samples into per-sensor datastates, replaying the
        /// header state machine of fftui's but_ISBH_Click loop: sensorno =
        /// type*6+instance, sample_rate/multiplier from the last header, out
        /// of order timestamps skipped, timedelta EMA on time changes.
        /// </summary>
        public static bool TryCollectIsbhNative(DFLogBuffer file, FFT2.datastate[] alldata)
        {
            if (!file.dflog.logformat.ContainsKey("ISBH") || !file.dflog.logformat.ContainsKey("ISBD"))
                return false;

            if (!file.TryGetColumnsNative("ISBH",
                    new[] { "N", "type", "instance", "smp_rate", "mul" },
                    out var hdrLines, out var hdr))
                return false;

            if (!file.TryGetColumnsNative("ISBD", new[] { "N", "TimeUS" }, out var smpLines, out var smp) ||
                !file.TryGetArrayColumnNative("ISBD", "x", out _, out var xs) ||
                !file.TryGetArrayColumnNative("ISBD", "y", out _, out var ys) ||
                !file.TryGetArrayColumnNative("ISBD", "z", out _, out var zs))
                return false;

            int Ns = 0, sensorno = 0;
            double multiplier = -1;

            int h = 0, s = 0;
            while (h < hdrLines.Length || s < smpLines.Length)
            {
                var takeHeader = s >= smpLines.Length || (h < hdrLines.Length && hdrLines[h] <= smpLines[s]);
                if (takeHeader)
                {
                    Ns = (int)hdr[0][h];
                    var type = (int)hdr[1][h];
                    var instance = (int)hdr[2][h];

                    sensorno = type * 6 + instance;

                    alldata[sensorno].sample_rate = hdr[3][h];
                    multiplier = hdr[4][h];

                    if (type == 0)
                        alldata[sensorno].type = "ACC" + instance;
                    if (type == 1)
                        alldata[sensorno].type = "GYR" + instance;

                    h++;
                }
                else
                {
                    if (sensorno < alldata.Length && (int)smp[0][s] == Ns)
                    {
                        var time = smp[1][s] / 1000.0;
                        var sensor = alldata[sensorno];

                        if (time >= sensor.lasttime)
                        {
                            if (time != sensor.lasttime)
                                sensor.timedelta = sensor.timedelta * 0.99 + (time - sensor.lasttime) * 0.01;

                            sensor.lasttime = time;

                            AppendScaled(sensor.datax, xs[s], multiplier);
                            AppendScaled(sensor.datay, ys[s], multiplier);
                            AppendScaled(sensor.dataz, zs[s], multiplier);
                        }
                    }

                    s++;
                }
            }

            return true;
        }

        static void AppendScaled(List<double> target, short[] samples, double multiplier)
        {
            foreach (var sample in samples)
                target.Add(sample / multiplier);
        }

        /// <summary>
        /// ACC1..4/GYR1..4 (or instanced ACC/GYR) per-sample data into
        /// per-sensor datastates, matching fftui's BUT_accgyrall_Click loop:
        /// GYR sensors at 0..3 (name digit - 1, or the instance), ACC at
        /// 4..7 offset +3, out of order timestamps skipped, timedelta EMA on
        /// time changes.
        /// </summary>
        public static bool TryCollectAccGyrNative(DFLogBuffer file, FFT2.datastate[] alldata)
        {
            var anyLegacyStyle = false;
            var anyInstanced = false;
            foreach (var basename in new[] { "ACC", "GYR" })
            {
                for (var n = 1; n <= 4; n++)
                    anyLegacyStyle |= file.dflog.logformat.ContainsKey(basename + n);
                anyInstanced |= file.dflog.logformat.ContainsKey(basename);
            }

            if (!anyLegacyStyle && !anyInstanced)
                return false;
            // both conventions in one log: unexpected - let the legacy loop decide
            if (anyLegacyStyle && anyInstanced)
                return false;

            foreach (var (basename, x, y, z, sensorbase) in new[]
                     {
                         ("ACC", "AccX", "AccY", "AccZ", 3),
                         ("GYR", "GyrX", "GyrY", "GyrZ", 0)
                     })
            {
                if (anyLegacyStyle)
                {
                    for (var n = 1; n <= 4; n++)
                    {
                        var type = basename + n;
                        if (!file.dflog.logformat.ContainsKey(type))
                            continue;

                        if (!file.TryGetColumnsNative(type, new[] { "TimeUS", x, y, z }, out _, out var cols))
                            return false;

                        Collect(alldata[n - 1 + sensorbase], type, cols, null, -1);
                    }
                }
                else
                {
                    var instanceField = file.GetInstanceFieldName(basename);
                    var fields = instanceField != null
                        ? new[] { "TimeUS", x, y, z, instanceField }
                        : new[] { "TimeUS", x, y, z };

                    if (!file.TryGetColumnsNative(basename, fields, out _, out var cols))
                        return false;

                    if (instanceField == null)
                    {
                        Collect(alldata[sensorbase], basename, cols, null, -1);
                    }
                    else
                    {
                        for (var inst = 0; inst < 4; inst++)
                            Collect(alldata[inst + sensorbase], basename, cols, cols[4], inst);
                    }
                }
            }

            return true;
        }

        static void Collect(FFT2.datastate sensor, string typename, double[][] cols,
            double[] instances, int instance)
        {
            var rows = cols[0].Length;
            for (var i = 0; i < rows; i++)
            {
                if (instances != null && instances[i] != instance)
                    continue;

                sensor.type = typename;

                var time = cols[0][i] / 1000.0;
                if (time < sensor.lasttime)
                    continue;

                if (time != sensor.lasttime)
                    sensor.timedelta = sensor.timedelta * 0.99 + (time - sensor.lasttime) * 0.01;

                sensor.lasttime = time;

                sensor.datax.Add(cols[1][i]);
                sensor.datay.Add(cols[2][i]);
                sensor.dataz.Add(cols[3][i]);
            }
        }
    }
}
