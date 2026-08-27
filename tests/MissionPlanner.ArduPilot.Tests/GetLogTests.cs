using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;
using Xunit;

namespace MissionPlanner.ArduPilot.Tests
{
    public class GetLogTests
    {
        const byte VehicleSysid = 1;
        const byte VehicleCompid = 1;
        const ushort LogId = 3;
        const int BlockSize = 90;

        /// <summary>
        /// Simulated vehicle end of the MAVLink log download protocol, connected
        /// to a MAVLinkInterface through an in-memory CommsInjection link.
        /// </summary>
        sealed class FakeLogVehicle : IDisposable
        {
            public readonly CommsInjection Link = new CommsInjection();
            public readonly MAVLinkInterface Mav = new MAVLinkInterface();
            public readonly List<MAVLink.mavlink_log_request_data_t> Requests = new List<MAVLink.mavlink_log_request_data_t>();
            public Action<MAVLink.mavlink_log_request_data_t> OnRequest;

            readonly MAVLink.MavlinkParse _parse = new MAVLink.MavlinkParse();
            readonly byte[] _log;
            readonly CancellationTokenSource _stop = new CancellationTokenSource();
            readonly Task _pump;

            public FakeLogVehicle(byte[] log)
            {
                _log = log;

                Link.WriteCallback += (sender, outbytes) =>
                {
                    MAVLink.MAVLinkMessage msg;
                    try
                    {
                        msg = new MAVLink.MAVLinkMessage(outbytes.ToArray());
                    }
                    catch
                    {
                        return;
                    }

                    if (msg.msgid != (uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_DATA)
                        return;

                    var req = msg.ToStructure<MAVLink.mavlink_log_request_data_t>();
                    if (req.id != LogId)
                        return;

                    lock (Requests)
                        Requests.Add(req);
                    OnRequest?.Invoke(req);
                };

                Mav.BaseStream = Link;

                // real-time protocol timeouts scaled down so the suite stays fast
                Mav.LogDataTimeoutMs = 500;
                Mav.LogDataRefetchMs = 100;

                // GetLog consumes packets via OnPacketReceived, which fires from the
                // receive loop - pump it the same way MainV2.SerialReader does
                _pump = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        try
                        {
                            await Mav.readPacketAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            try
                            {
                                await Task.Delay(1, _stop.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    }
                });
            }

            public void SendBlock(uint ofs, int count)
            {
                var payload = new byte[BlockSize];
                Array.Copy(_log, ofs, payload, 0, count);
                Send(MAVLink.MAVLINK_MSG_ID.LOG_DATA, new MAVLink.mavlink_log_data_t(ofs, LogId, (byte)count, payload));
            }

            public void SendEndMarker(uint ofs)
            {
                Send(MAVLink.MAVLINK_MSG_ID.LOG_DATA, new MAVLink.mavlink_log_data_t(ofs, LogId, 0, new byte[BlockSize]));
            }

            public void Send(MAVLink.MAVLINK_MSG_ID msgid, object indata)
            {
                Link.AppendBuffer(_parse.GenerateMAVLinkPacket20(msgid, indata, false, VehicleSysid, VehicleCompid));
            }

            /// <summary>
            /// Serve a LOG_REQUEST_DATA the way ArduPilot does: 90 byte blocks with
            /// a short block at end of log, or a zero count marker when the log ends
            /// on an exact block boundary. skipBlocks are dropped, but only the
            /// first time each is requested.
            /// </summary>
            public void Serve(MAVLink.mavlink_log_request_data_t req, HashSet<uint> skipBlocks = null)
            {
                var end = Math.Min((ulong)req.ofs + req.count, (ulong)_log.Length);
                for (var ofs = (ulong)req.ofs; ofs < end; ofs += BlockSize)
                {
                    var block = (uint)(ofs / BlockSize);
                    if (skipBlocks != null && skipBlocks.Remove(block))
                        continue;
                    SendBlock((uint)ofs, (int)Math.Min(BlockSize, end - ofs));
                }

                if (end == (ulong)_log.Length && (ulong)req.ofs + req.count > end && _log.Length % BlockSize == 0)
                    SendEndMarker((uint)end);
            }

            public void Dispose()
            {
                _stop.Cancel();
                Link.Close();
                try
                {
                    _pump.Wait(2000);
                }
                catch
                {
                }
            }
        }

        static byte[] MakeLog(int length)
        {
            var data = new byte[length];
            new Random(42).NextBytes(data);
            return data;
        }

        /// <summary>A private directory owned by a single test, removed on dispose.</summary>
        sealed class TestDir : IDisposable
        {
            public string Root { get; } =
                Path.Combine(Path.GetTempPath(), "GetLogTests-" + Guid.NewGuid().ToString("N"));

            public TestDir()
            {
                Directory.CreateDirectory(Root);
            }

            public string File(string name) => Path.Combine(Root, name);

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Root, true);
                }
                catch
                {
                }
            }
        }

        static async Task<byte[]> Download(FakeLogVehicle vehicle, CancellationToken cancel)
        {
            using (var dir = new TestDir())
            {
                var file = await vehicle.Mav.GetLogInternal(VehicleSysid, VehicleCompid, LogId,
                    dir.File("log.bin"), cancel);
                return File.ReadAllBytes(file);
            }
        }

        [Fact]
        public async Task DownloadsCompleteLogInOrder()
        {
            var log = MakeLog(1234);
            using (var vehicle = new FakeLogVehicle(log))
            {
                vehicle.OnRequest = req => vehicle.Serve(req);

                var result = await Download(vehicle, TestContext.Current.CancellationToken);

                Assert.Equal(log, result);
            }
        }

        [Fact]
        public async Task HandlesLogSizeExactMultipleOfBlockSize()
        {
            var log = MakeLog(900);
            using (var vehicle = new FakeLogVehicle(log))
            {
                vehicle.OnRequest = req => vehicle.Serve(req);

                var result = await Download(vehicle, TestContext.Current.CancellationToken);

                Assert.Equal(log, result);
                // the fill in phase must not ask for a phantom block past end of log
                lock (vehicle.Requests)
                    Assert.False(vehicle.Requests.Skip(1).Any(r => r.ofs >= 900),
                        "requested data past the end of the log");
            }
        }

        [Fact]
        public async Task RefetchesDroppedBlocks()
        {
            var log = MakeLog(1000);
            using (var vehicle = new FakeLogVehicle(log))
            {
                var drop = new HashSet<uint> { 3, 7 };
                vehicle.OnRequest = req => vehicle.Serve(req, drop);

                var result = await Download(vehicle, TestContext.Current.CancellationToken);

                Assert.Equal(log, result);
                lock (vehicle.Requests)
                    Assert.True(vehicle.Requests.Any(r => r.ofs == 3 * BlockSize),
                        "first dropped block was never re-requested");
            }
        }

        [Fact]
        public async Task IgnoresStrayShortPacketBeforeEndOfLog()
        {
            var log = MakeLog(1234);
            using (var vehicle = new FakeLogVehicle(log))
            {
                var first = true;
                vehicle.OnRequest = req =>
                {
                    if (!first)
                    {
                        vehicle.Serve(req);
                        return;
                    }

                    first = false;
                    var end = Math.Min((ulong)req.ofs + req.count, (ulong)log.Length);
                    for (var ofs = (ulong)req.ofs; ofs < end; ofs += BlockSize)
                    {
                        // a stale short retransmit mid stream must not truncate the log
                        if (ofs == 8 * BlockSize)
                            vehicle.SendBlock(3 * BlockSize, 40);
                        vehicle.SendBlock((uint)ofs, (int)Math.Min(BlockSize, end - ofs));
                    }
                };

                var result = await Download(vehicle, TestContext.Current.CancellationToken);

                Assert.True(log.Length == result.Length, "stray short packet truncated the download");
                Assert.Equal(log, result);
            }
        }

        [Fact]
        public async Task EmptyLogProducesEmptyFile()
        {
            using (var vehicle = new FakeLogVehicle(new byte[0]))
            {
                vehicle.OnRequest = req => vehicle.SendEndMarker(0);

                var result = await Download(vehicle, TestContext.Current.CancellationToken);

                Assert.Empty(result);
            }
        }

        [Fact]
        public async Task CancelStopsDownloadAndDeletesFile()
        {
            var log = MakeLog(100 * BlockSize);
            using (var vehicle = new FakeLogVehicle(log))
            using (var dir = new TestDir())
            {
                // stream a few blocks, then go silent so the download hangs mid way
                vehicle.OnRequest = req =>
                {
                    for (uint block = 0; block < 5; block++)
                        vehicle.SendBlock(block * BlockSize, BlockSize);
                };

                var path = dir.File("log.bin");

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
                {
                    cts.CancelAfter(300);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => vehicle.Mav.GetLogInternal(VehicleSysid, VehicleCompid, LogId, path, cts.Token));
                }

                Assert.False(File.Exists(path), "partial file left behind after cancel");
            }
        }

        [Fact(Timeout = 15000)]
        public async Task TimesOutWhenVehicleNeverResponds()
        {
            using (var vehicle = new FakeLogVehicle(MakeLog(10)))
            using (var dir = new TestDir())
            {
                var path = dir.File("log.bin");

                // no OnRequest wired - total silence; LogDataTimeoutMs with 3 retries
                await Assert.ThrowsAsync<TimeoutException>(
                    () => vehicle.Mav.GetLogInternal(VehicleSysid, VehicleCompid, LogId, path,
                        TestContext.Current.CancellationToken));

                Assert.False(File.Exists(path), "partial file left behind after timeout");
            }
        }
    }
}
