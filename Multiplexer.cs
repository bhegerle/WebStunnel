﻿using System.Net.Sockets;
using static WebStunnel.Timeouts;

namespace WebStunnel {
    internal class Multiplexer : IDisposable {
        private readonly Tunnel tunnel;
        private readonly ISocketMap sockMap;

        internal Multiplexer(Tunnel tunnel, ISocketMap sockMap) {
            this.tunnel = tunnel;
            this.sockMap = sockMap;
        }

        internal async Task Multiplex(CancellationToken token) {
            var trecv = TunnelReceive(token);
            var srecv = SocketMapLop(token);
            await Task.WhenAll(trecv, srecv);
        }

        public void Dispose() {
            tunnel.Dispose();
            sockMap.Dispose();
        }

        private async Task TunnelReceive(CancellationToken token) {
            try {
                var seg = NewSeg();
                var idBuf = new byte[sizeof(ulong)].AsSegment();

                while (true) {
                    using var recvTimeout = IdleTimeout();
                    using var recvCts = recvTimeout.Token.Link(token);

                    var msg = await tunnel.Receive(seg, token);

                    var f = new Frame(msg, idBuf.Count, false);
                    var id = BitConverter.ToUInt64(f.Suffix);
                    var sock = await sockMap.GetSocket(id);

                    using var sendTimeout = Timeout();
                    await sock.Send(f.Message, sendTimeout.Token);
                }
            } catch {
                Console.WriteLine("tunnel receive loop terminated");
            }
        }

        private async Task SocketMapLop(CancellationToken token) {
            try{
            var taskMap = new Dictionary<ulong, Task>();

            while (true) {
                using var snap = await sockMap.Snapshot();

                var newInSnap = snap.Sockets.Keys.Except(taskMap.Keys);
                foreach (var sid in newInSnap) {
                    taskMap.Add(sid, SocketReceive(sid, snap.Sockets[sid]));
                    Console.WriteLine($"multiplexing connection {sid}");
                }

                var dropFromSnap = taskMap.Keys.Except(snap.Sockets.Keys);
                foreach (var tid in dropFromSnap) {
                    var t = taskMap[tid];
                    if (await t.DidCompleteWithin(TimeSpan.FromMilliseconds(1)))
                        taskMap.Remove(tid);
                }

                await snap.Lifetime.WhileAlive(token);
            }
            } catch {
                Console.WriteLine("socket map loop terminated");
            }
        }

        private async Task SocketReceive(ulong id, Socket s) {
            var seg = NewSeg();
            var idBuf = BitConverter.GetBytes(id).AsSegment();

            try {
                while (true) {
                    using var recvTimeout = IdleTimeout();
                    var msg = await s.Receive(seg, recvTimeout.Token);

                    var f = new Frame(msg, idBuf.Count, true);
                    idBuf.CopyTo(f.Suffix);

                    using var sendTimeout = Timeout();
                    await tunnel.Send(f.Complete, sendTimeout.Token);
                }
            } catch {
                await sockMap.RemoveSocket(id);
            }
        }

        private static ArraySegment<byte> NewSeg() {
            return new byte[1024 * 1024];
        }
    }
}
