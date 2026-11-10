using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The link is exercised against a raw socket on the other end, which is exactly the peer the
    /// tests need: one that behaves (answers heartbeats), one that goes silent, and one that hangs up,
    /// without any of the session logic getting in the way.
    /// </summary>
    public class PeerLinkTests
    {
        [Fact]
        public void ReliableFramesArriveInOrder()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 0))
            using (pair[1])
            {
                link.Start();
                NetworkStream raw = pair[1].GetStream();

                for (int i = 1; i <= 5; i++)
                {
                    Assert.True(link.Send(MsgCategory.Gameplay, MsgAction.SpawnPlant,
                        new SpawnPlantPayload { NetId = i, PlantType = 3, Column = i, Row = 0, OwnerPeer = 2 }));
                }

                for (int i = 1; i <= 5; i++)
                {
                    DecodedFrame frame = FrameIo.Read(raw);
                    SpawnPlantPayload payload = Protocol.Payload<SpawnPlantPayload>(frame);
                    Assert.Equal(i, payload.NetId);
                    Assert.Equal(i, payload.Column);
                }

                Assert.True(link.IsOpen);
            }
        }

        [Fact]
        public void HeartbeatsKeepAnIdleLinkOpenAndNeverReachTheSession()
        {
            // Timeout is a quarter of the observation window: without heartbeats this link dies long
            // before the assertion, and every heartbeat must be swallowed by the receive loop.
            TcpClient[] quiet = ConnectedPair();
            TcpClient[] talking = ConnectedPair();
            using (PeerLink quietServer = Wrap(quiet[0], 1, heartbeat: 0, timeout: 200, cosmeticInterval: 0))
            using (quiet[1])
            using (PeerLink talker = Wrap(talking[0], 2, heartbeat: 20, timeout: 200, cosmeticInterval: 0))
            using (PeerLink listener = Wrap(talking[1], 3, heartbeat: 20, timeout: 200, cosmeticInterval: 0))
            {
                quietServer.Start();
                talker.Start();
                listener.Start();

                Thread.Sleep(1000);

                Assert.True(listener.IsOpen, "heartbeat peer was closed: " + listener.CloseReason);
                Assert.True(talker.IsOpen, "heartbeat link was closed: " + talker.CloseReason);
                Assert.Equal(0, listener.IncomingQueued);
                Assert.Equal(0, talker.IncomingQueued);

                Assert.True(Wait.Until(() => !quietServer.IsOpen, 2000), "silent link outlived its timeout");
            }
        }

        [Fact]
        public void SilentPeerClosesTheLinkWithTheTimeoutAsTheReason()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 150, cosmeticInterval: 0))
            using (pair[1])
            {
                link.Start();

                Assert.True(Wait.Until(() => !link.IsOpen), "link to a silent peer stayed open");
                Assert.Contains("no traffic for", link.CloseReason);
            }
        }

        [Fact]
        public void PeerHangingUpIsReportedAsAnEndOfConnectionNotATimeout()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 0))
            {
                link.Start();
                string reason = null;
                link.Closed += (peer, why) => reason = why;

                pair[1].Close();

                Assert.True(Wait.Until(() => reason != null), "closing the peer was never noticed");
                Assert.Equal("connection ended by peer", reason);
            }
        }

        [Fact]
        public void OnlyTheNewestCosmeticFrameIsSent()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 200))
            using (pair[1])
            {
                link.Start();

                var arrived = new List<int>();
                NetworkStream raw = pair[1].GetStream();
                Thread reader = new Thread(() =>
                {
                    while (true)
                    {
                        DecodedFrame frame;
                        try
                        {
                            frame = FrameIo.Read(raw);
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        if (frame == null)
                        {
                            return;
                        }

                        lock (arrived)
                        {
                            arrived.Add(Protocol.Payload<CursorIntentPayload>(frame).Column);
                        }
                    }
                }) { IsBackground = true };
                reader.Start();

                for (int column = 1; column <= 20; column++)
                {
                    link.SendCosmetic(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                        new CursorIntentPayload { PeerId = 1, Column = column, Row = 0, PlantType = 0 });
                }

                // 20 frames inside a 200ms spacing: at most a handful can leave, and the last one must
                // be the newest intent, otherwise a lagging peer would paint a cursor that was left
                // behind half a second ago.
                Thread.Sleep(700);

                lock (arrived)
                {
                    Assert.InRange(arrived.Count, 1, 5);
                    Assert.Equal(20, arrived[arrived.Count - 1]);
                }
            }
        }

        [Fact]
        public void TwoCosmeticIdentitiesDoNotReplaceEachOther()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 200))
            using (pair[1])
            {
                link.Start();

                var arrived = new List<string>();
                NetworkStream raw = pair[1].GetStream();
                Thread reader = new Thread(() =>
                {
                    while (true)
                    {
                        DecodedFrame frame;
                        try
                        {
                            frame = FrameIo.Read(raw);
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        if (frame == null)
                        {
                            return;
                        }

                        MethodCallPayload call = Protocol.Payload<MethodCallPayload>(frame);
                        lock (arrived)
                        {
                            arrived.Add(call.Key + "=" + call.Args);
                        }
                    }
                }) { IsBackground = true };
                reader.Start();

                // Newest-wins needs an identity to be newest for, and the link is told it by name: one
                // shared slot would let the door written last win every round and keep the other door's
                // value on this end for good.
                for (int i = 1; i <= 20; i++)
                {
                    SendDoor(link, SunDoor, i);
                    SendDoor(link, BankDoor, i);
                }

                Thread.Sleep(900);

                lock (arrived)
                {
                    Assert.NotEmpty(arrived);
                    Assert.Equal(SunDoor + "=[20]", LastOf(arrived, SunDoor));
                    Assert.Equal(BankDoor + "=[20]", LastOf(arrived, BankDoor));
                }
            }
        }

        [Fact]
        public void ACosmeticBacklogCannotCloseALink()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 0))
            using (pair[1])
            {
                link.Start();

                // A peer painting a cursor at an end that is busy and not reading: the newest one is the
                // whole message, so the rest are thrown away where a reliable frame would have filled the
                // unread cap and hung the room up over a pointer.
                NetworkStream raw = pair[1].GetStream();
                int sent = PeerLink.MaxIncomingUnread + PeerLink.MaxIncomingCosmetic + 64;

                for (int i = 0; i < sent; i++)
                {
                    FrameIo.Write(raw, Protocol.Encode(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                        new CursorIntentPayload { PeerId = 1, Column = i % 9, Row = 0, PlantType = 0 }));
                }

                Thread.Sleep(500);

                Assert.True(link.IsOpen, "a cosmetic backlog closed the link: " + link.CloseReason);
                Assert.InRange(link.CosmeticQueued, 1, PeerLink.MaxIncomingCosmetic);
                Assert.True(link.DroppedCosmeticCount > 0, "nothing was thrown away");
                Assert.Equal(0, link.IncomingQueued);
            }
        }

        [Fact]
        public void SendAfterCloseFailsAndSaysSo()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 0))
            using (pair[1])
            {
                link.Start();
                link.Close("test asked for it");

                Assert.False(link.Send(MsgCategory.Gameplay, MsgAction.SunState,
                    new SunStatePayload { Sun = 25, MaxSun = 5000 }));
                Assert.False(link.SendCosmetic(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                    new CursorIntentPayload { PeerId = 1 }));
                Assert.True(TestLog.Has("already closed"));
                Assert.Equal(0, link.ReliableQueued);
            }
        }

        [Fact]
        public void CloseIsIdempotentAndReportsTheFirstReasonOnly()
        {
            TcpClient[] pair = ConnectedPair();
            using (PeerLink link = Wrap(pair[0], 1, heartbeat: 0, timeout: 60000, cosmeticInterval: 0))
            using (pair[1])
            {
                int events = 0;
                link.Closed += (peer, why) => Interlocked.Increment(ref events);

                link.Close("first");
                link.Close("second");
                link.Dispose();

                Assert.Equal("first", link.CloseReason);
                Assert.Equal(1, Volatile.Read(ref events));
            }
        }

        private const string SunDoor = "Board.SetSun/1";
        private const string BankDoor = "Board.GetSun/2";

        /// <summary>One latest-wins frame the link is told to keep newest under its door's own name.</summary>
        private static void SendDoor(PeerLink link, string key, int amount)
        {
            link.SendCosmetic(MsgCategory.Cosmetic, MsgAction.MethodCall,
                new MethodCallPayload { Key = key, Args = "[" + amount + "]", Seq = 0, OriginPeer = 0 },
                key);
        }

        private static string LastOf(List<string> arrived, string key)
        {
            string last = null;
            foreach (string entry in arrived)
            {
                if (entry.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    last = entry;
                }
            }

            return last;
        }

        private static PeerLink Wrap(TcpClient client, int peerId, int heartbeat, int timeout,
                                     int cosmeticInterval)
        {
            return PeerLink.Wrap(client, peerId, heartbeat, timeout, cosmeticInterval);
        }

        /// <summary>Two connected sockets on loopback: [0] is the one the link wraps, [1] the raw peer.</summary>
        private static TcpClient[] ConnectedPair()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var client = new TcpClient();
            try
            {
                client.Connect(IPAddress.Loopback, port);
                TcpClient server = listener.AcceptTcpClient();
                return new[] { server, client };
            }
            catch (Exception)
            {
                client.Dispose();
                throw;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
