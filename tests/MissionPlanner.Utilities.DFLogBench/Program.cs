using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MissionPlanner.Utilities;

class Program
{
    static void Main(string[] args)
    {
        var file = args[0];

        if (args.Length >= 2 && args[1] == "cachebench")
        {
            CacheBench(file);
            return;
        }

        if (args.Length >= 3 && args[1] == "convert")
        {
            var sw0 = Stopwatch.StartNew();
            BinaryLog.ConvertBin(file, args[2]);
            sw0.Stop();
            Console.WriteLine($"convertbin time={sw0.Elapsed.TotalSeconds:F1}s");
            return;
        }

        // scan-only comparison
        var sw2 = Stopwatch.StartNew();
        var nativeType = typeof(DFLogBuffer).Assembly.GetType("MissionPlanner.Utilities.DFLogNative");
        var tryScan = nativeType.GetMethod("TryScan", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var scanArgs = new object[] { Path.GetFullPath(file), null, null };
        var ok = (bool)tryScan.Invoke(null, scanArgs);
        sw2.Stop();
        var nativeCount = ok ? ((long[])scanArgs[1]).Length : -1;
        Console.WriteLine($"scanonly native ok={ok} count={nativeCount} time={sw2.Elapsed.TotalSeconds:F2}s");

        sw2.Restart();
        var managedCount = ManagedScan(file);
        sw2.Stop();
        Console.WriteLine($"scanonly managed count={managedCount} time={sw2.Elapsed.TotalSeconds:F2}s");

        // phase B: LogBrowse-style graph extraction vs typed columns
        DFLogBuffer.UseNativeScan = true;
        using (var buf = new DFLogBuffer(file))
        {
            var sw3 = Stopwatch.StartNew();
            var colok = buf.TryGetColumnsNative("ATT", new[] { "Roll" }, out var linenos, out var cols);
            sw3.Stop();
            Console.WriteLine($"columns native ok={colok} rows={(colok ? cols[0].Length : -1)} time={sw3.Elapsed.TotalSeconds:F2}s");

            sw3.Restart();
            var idx = buf.dflog.FindMessageOffset("ATT", "Roll");
            long rows = 0;
            double sum = 0;
            foreach (var item in buf.GetEnumeratorType("ATT"))
            {
                // the LogBrowse.cs graphing pattern: display string -> double.Parse
                sum += double.Parse(item.items[idx], System.Globalization.CultureInfo.InvariantCulture);
                rows++;
            }

            sw3.Stop();
            Console.WriteLine($"columns managed rows={rows} time={sw3.Elapsed.TotalSeconds:F2}s (sum={sum:F1})");

            // highest-rate type in the corpus: SIM2 (1.57M rows in big.bin);
            // second native query excludes the one-time dflog_open cost
            sw3.Restart();
            buf.TryGetColumnsNative("SIM2", new[] { "PN" }, out _, out var sim2);
            sw3.Stop();
            Console.WriteLine($"columns native SIM2 rows={sim2[0].Length} time={sw3.Elapsed.TotalSeconds:F2}s");

            sw3.Restart();
            var idx2 = buf.dflog.FindMessageOffset("SIM2", "PN");
            long rows2 = 0;
            double sum2 = 0;
            foreach (var item in buf.GetEnumeratorType("SIM2"))
            {
                sum2 += double.Parse(item.items[idx2], System.Globalization.CultureInfo.InvariantCulture);
                rows2++;
            }

            sw3.Stop();
            Console.WriteLine($"columns managed SIM2 rows={rows2} time={sw3.Elapsed.TotalSeconds:F2}s (sum={sum2:F1})");
        }

        foreach (var native in new[] { true, false })
        {
            DFLogBuffer.UseNativeScan = native;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            using (var buf = new DFLogBuffer(file))
            {
                sw.Stop();
                var after = GC.GetTotalMemory(false);
                Console.WriteLine($"native={native} count={buf.Count} open={sw.Elapsed.TotalSeconds:F2}s " +
                                  $"managedheap={(after - before) / 1024.0 / 1024.0:F0}MiB");
            }
        }
    }

    // native fresh-scan vs managed cache path on a >300MB log
    static void CacheBench(string file)
    {
        var cachePath = Path.GetTempPath() +
                        Path.GetFullPath(file).Replace("/", "_").Replace("\\", "_").Replace(":", "_") +
                        Path.GetFileNameWithoutExtension(file) + new FileInfo(file).Length;

        var t = typeof(DFLogBuffer);
        bool Flag(string name) => (bool)t.GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        double Open(bool native, out int count, out string mode)
        {
            DFLogBuffer.UseNativeScan = native;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            using (var buf = new DFLogBuffer(file))
            {
                sw.Stop();
                count = buf.Count;
                mode = Flag("LastScanNative") ? "nativescan"
                    : Flag("LastLoadFromCache") ? "cacheload"
                    : "managedscan";
                return sw.Elapsed.TotalSeconds;
            }
        }

        File.Delete(cachePath);

        var t1 = Open(false, out var c1, out var m1);
        Console.WriteLine($"managed cold (scan+savecache): {t1:F2}s mode={m1} count={c1} " +
                          $"cache={(File.Exists(cachePath) ? new FileInfo(cachePath).Length / 1024 / 1024 + "MiB" : "none")}");

        for (var i = 0; i < 2; i++)
        {
            var t2 = Open(false, out var c2, out var m2);
            Console.WriteLine($"managed warm (cacheload) run{i + 1}: {t2:F2}s mode={m2} count={c2}");
        }

        for (var i = 0; i < 2; i++)
        {
            var t3 = Open(true, out var c3, out var m3);
            Console.WriteLine($"native (cache skipped) run{i + 1}: {t3:F2}s mode={m3} count={c3}");
        }

        File.Delete(cachePath);
    }

    // faithful copy of the BinaryLog.ReadMessageTypeOffset scan loop, for
    // timing the managed scan in isolation
    static long ManagedScan(string file)
    {
        long count = 0;
        var lengths = new int[256];
        using (var s = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long length = s.Length;
            int step = 0;
            while (s.Position < length)
            {
                int bi = s.ReadByte();
                if (bi < 0)
                    break;
                byte b = (byte)bi;
                switch (step)
                {
                    case 0:
                        if (b == 0xA3) step = 1;
                        break;
                    case 1:
                        step = b == 0x95 ? 2 : 0;
                        break;
                    default:
                        step = 0;
                        long start = s.Position - 3;
                        if (b == 0x80)
                        {
                            var payload = new byte[86];
                            s.Read(payload, 0, 86);
                            lengths[payload[0]] = payload[1];
                        }
                        else
                        {
                            var size = lengths[b];
                            if (size >= 3)
                            {
                                var skip = new byte[size - 3];
                                s.Read(skip, 0, skip.Length);
                            }
                            else if (size != 0)
                                break;
                        }

                        if (b == 0 && start == 0)
                            break;
                        count++;
                        break;
                }
            }
        }

        return count;
    }
}
