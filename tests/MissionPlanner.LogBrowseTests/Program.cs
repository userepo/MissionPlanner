extern alias sysdraw;
using System;
using System.Drawing;
using System.Globalization;
using SysBitmap = sysdraw::System.Drawing.Bitmap;
using SysImageFormat = sysdraw::System.Drawing.Imaging.ImageFormat;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using MissionPlanner.Log;
using MissionPlanner.Utilities;
using ZedGraph;

/// <summary>
/// Drives the real LogBrowse form: loads a log, graphs ATT.Roll and
/// IMU[0].GyrX through the real GraphItem path, saves the rendered graph as
/// PNG and every plotted point as CSV. Honors DFLOG_NATIVE like the app.
///
/// usage: LogBrowseVerify.exe graph <log.bin> <out-prefix>
///        LogBrowseVerify.exe compare <a-prefix> <b-prefix>
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "compare")
            return Compare(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "graph")
            return Graph(args[1], args[2]);

        Console.WriteLine("usage: graph <log.bin> <out-prefix> | compare <a-prefix> <b-prefix>");
        return 1;
    }

    static int Graph(string logfile, string outprefix)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new LogBrowse();
        form.logfilename = Path.GetFullPath(logfile);
        form.Size = new Size(1400, 900);
        form.StartPosition = FormStartPosition.Manual;
        // off-screen: the form must never sit under the real mouse cursor -
        // LogBrowse's MouseMove crosshair would draw into the capture and
        // make the pixel comparison depend on where the mouse happens to be
        form.Location = new Point(-2000, 20);

        var t = typeof(LogBrowse);
        var logdataField = t.GetField("logdata", BindingFlags.NonPublic | BindingFlags.Instance);
        var zg1Field = t.GetField("zg1", BindingFlags.NonPublic | BindingFlags.Instance);
        var chkTimeField = t.GetField("chk_time", BindingFlags.NonPublic | BindingFlags.Instance);
        var graphItem = t.GetMethod("GraphItem", BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[]
            {
                typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(string),
                typeof(string)
            }, null);

        if (logdataField == null || zg1Field == null || graphItem == null)
        {
            Console.WriteLine("FAIL reflection: logdata/zg1/GraphItem not found");
            return 1;
        }

        // configure checkboxes BEFORE the form shows: (a) the persist handlers
        // that write LB_* into the user's real settings are only subscribed
        // during Load, so pre-Show changes do not touch the settings store,
        // and (b) the mode/error/msg overlays rebuild asynchronously and make
        // pixel comparisons racy - the harness verifies curves, so turn the
        // overlays and the map off entirely
        void SetChk(string name, bool value)
        {
            var field = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(form) is CheckBox box)
                box.Checked = value;
        }

        SetChk("chk_time", Environment.GetEnvironmentVariable("LOGBROWSE_TIMEAXIS") == "1");
        foreach (var overlay in new[] { "chk_mode", "chk_errors", "chk_msg", "chk_events", "CHK_map" })
            SetChk(overlay, false);

        int state = 0;
        var started = DateTime.UtcNow;
        int exit = 2;
        var lastObjCount = -1;
        var lastObjChange = DateTime.UtcNow;

        var timer = new Timer { Interval = 250 };
        timer.Tick += (s, e) =>
        {
            try
            {
                if ((DateTime.UtcNow - started).TotalSeconds > 120)
                {
                    Console.WriteLine("FAIL timeout in state " + state);
                    timer.Stop();
                    form.Close();
                    return;
                }

                var zg1 = (ZedGraphControl)zg1Field.GetValue(form);

                switch (state)
                {
                    case 0: // wait for the log to load
                        if (logdataField.GetValue(form) == null)
                            return;
                        state = 1;
                        return;

                    case 1: // graph two fields through the real path
                        graphItem.Invoke(form, new object[] { "ATT", "Roll", true, false, false, "", null });
                        graphItem.Invoke(form, new object[] { "IMU", "GyrX", true, false, false, "0", null });
                        started = DateTime.UtcNow;
                        state = 2;
                        return;

                    case 2: // wait until both curves are populated
                        if (zg1.GraphPane.CurveList.Count < 2)
                            return;
                        foreach (var curve in zg1.GraphPane.CurveList)
                            if (curve.Points.Count == 0)
                                return;
                        state = 3;
                        started = DateTime.UtcNow;
                        return;

                    case 3: // capture once the async overlays (modes/msgs/errors)
                            // have stopped mutating the graph for 2 seconds
                        var objCount = zg1.GraphPane.GraphObjList.Count;
                        if (objCount != lastObjCount)
                        {
                            lastObjCount = objCount;
                            lastObjChange = DateTime.UtcNow;
                            return;
                        }

                        if ((DateTime.UtcNow - lastObjChange).TotalSeconds < 2)
                            return;

                        // no AxisChange/Refresh here: they fire zoom events that
                        // rebuild the overlays asynchronously mid-capture;
                        // DrawToBitmap forces its own paint
                        using (var bmp = new SysBitmap(zg1.Width, zg1.Height))
                        {
                            zg1.DrawToBitmap(bmp, new Rectangle(0, 0, zg1.Width, zg1.Height));
                            bmp.Save(outprefix + ".png", SysImageFormat.Png);
                        }

                        var sb = new StringBuilder();
                        foreach (var curve in zg1.GraphPane.CurveList)
                        {
                            for (var i = 0; i < curve.Points.Count; i++)
                            {
                                var p = curve.Points[i];
                                sb.AppendLine(FormattableString.Invariant(
                                    $"{curve.Label.Text},{p.X:R},{p.Y:R}"));
                            }
                        }

                        File.WriteAllText(outprefix + ".csv", sb.ToString());

                        var lastScanNative = (bool)typeof(DFLogBuffer)
                            .GetField("LastScanNative", BindingFlags.NonPublic | BindingFlags.Static)
                            .GetValue(null);

                        Console.WriteLine($"OK curves={zg1.GraphPane.CurveList.Count} " +
                                          $"points={string.Join("/", System.Linq.Enumerable.Select(zg1.GraphPane.CurveList, c => c.Points.Count))} " +
                                          $"nativescan={lastScanNative} usenative={DFLogBuffer.UseNativeScan}");
                        exit = 0;
                        timer.Stop();
                        form.Close();
                        return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex);
                timer.Stop();
                form.Close();
            }
        };

        form.Shown += (s, e) => timer.Start();
        Application.Run(form);
        return exit;
    }

    static int Compare(string a, string b)
    {
        // numeric: max per-row delta between the csv dumps. curve order can
        // differ between modes (sync vs threadpool add), so sort by label+x
        string[] Sorted(string path)
        {
            var lines = File.ReadAllLines(path);
            Array.Sort(lines, (l, r) =>
            {
                var pl = l.Split(',');
                var pr = r.Split(',');
                var c = string.CompareOrdinal(pl[0], pr[0]);
                if (c != 0)
                    return c;
                return double.Parse(pl[1], CultureInfo.InvariantCulture)
                    .CompareTo(double.Parse(pr[1], CultureInfo.InvariantCulture));
            });
            return lines;
        }

        var la = Sorted(a + ".csv");
        var lb = Sorted(b + ".csv");
        if (la.Length != lb.Length)
        {
            Console.WriteLine($"FAIL row count differs: {la.Length} vs {lb.Length}");
            return 1;
        }

        double maxdelta = 0;
        for (var i = 0; i < la.Length; i++)
        {
            var pa = la[i].Split(',');
            var pb = lb[i].Split(',');
            if (pa[0] != pb[0] || pa[1] != pb[1])
            {
                Console.WriteLine($"FAIL row {i} label/x differs: {la[i]} vs {lb[i]}");
                return 1;
            }

            var d = Math.Abs(double.Parse(pa[2], CultureInfo.InvariantCulture) -
                             double.Parse(pb[2], CultureInfo.InvariantCulture));
            maxdelta = Math.Max(maxdelta, d);
        }

        // pixels
        long diffpixels = 0;
        using (var ia = new SysBitmap(a + ".png"))
        using (var ib = new SysBitmap(b + ".png"))
        {
            if (ia.Size != ib.Size)
            {
                Console.WriteLine("FAIL image sizes differ");
                return 1;
            }

            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
            for (var y = 0; y < ia.Height; y++)
            for (var x = 0; x < ia.Width; x++)
                if (ia.GetPixel(x, y) != ib.GetPixel(x, y))
                {
                    diffpixels++;
                    minx = Math.Min(minx, x);
                    miny = Math.Min(miny, y);
                    maxx = Math.Max(maxx, x);
                    maxy = Math.Max(maxy, y);
                }

            if (diffpixels > 0)
                Console.WriteLine($"diff bbox: ({minx},{miny})-({maxx},{maxy}) of {ia.Width}x{ia.Height}");

            Console.WriteLine(FormattableString.Invariant(
                $"COMPARE rows={la.Length} maxYdelta={maxdelta:E3} diffpixels={diffpixels} of {ia.Width * ia.Height}"));
        }

        return 0;
    }
}
