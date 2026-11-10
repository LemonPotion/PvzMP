using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// End to end over real loopback sockets: what the session layer will actually call in Stage 3.
    /// The host always speaks to the raw peer endpoints here, never through another mod class, so a
    /// routing bug cannot hide behind a helper.
    /// </summary>
    public class HostClientTests
    {
        [Fact]
        public void BothDirectionsCarryPayloadsAndTheHostAssignsThePeerId()
        {
            TestLog.Clear();
            using (var host = new MpHost(4))
            using (var client = new MpClient())
            {
                host.Start(0);
                ConnectTo(host, client);

                Assert.True(Wait.Until(() => host.Peers().Length == 1), "host never registered the client");
                int peerId = host.Peers()[0].PeerId;
                Assert.Equal(1, peerId);

                Assert.True(client.Send(MsgCategory.Control, MsgAction.Hello,
                    new HelloPayload { Nick = "Lemon", Version = Protocol.Version }));

                DecodedFrame hello = ReceiveFrom(host, peerId);
                Assert.Equal(MsgAction.Hello, hello.Action);
                Assert.Equal("Lemon", Protocol.Payload<HelloPayload>(hello).Nick);

                Assert.True(host.SendTo(peerId, MsgCategory.Control, MsgAction.Welcome,
                    new WelcomePayload { PeerId = peerId, Version = Protocol.Version }));

                DecodedFrame welcome = ReceiveAt(client);
                Assert.Equal(MsgCategory.Control, welcome.Category);
                Assert.Equal(Protocol.Version, Protocol.Payload<WelcomePayload>(welcome).Version);
                Assert.Equal(peerId, Protocol.Payload<WelcomePayload>(welcome).PeerId);

                // The client's own link still carries the host's peer id, which is 0: the transport
                // must not have invented a local identity for it.
                Assert.Equal(MpHost.HostPeerId, client.Link.PeerId);
                Assert.True(TestLog.Has("[net] recv Control/Hello from peer " + peerId), TestLog.Describe());
            }
        }

        [Fact]
        public void BroadcastReachesEveryOtherPeerAndIsLogged()
        {
            TestLog.Clear();
            using (var host = new MpHost(4))
            using (var first = new MpClient())
            using (var second = new MpClient())
            {
                host.Start(0);
                ConnectTo(host, first);
                ConnectTo(host, second);

                Assert.True(Wait.Until(() => host.Peers().Length == 2), "only one client registered");

                Assert.True(host.Broadcast(MsgCategory.Gameplay, MsgAction.SpawnZombie,
                    new SpawnZombiePayload { NetId = 44, ZombieType = 7, Row = 4, SpawnX = 780f, Wave = 3 }));

                SpawnZombiePayload a = Protocol.Payload<SpawnZombiePayload>(ReceiveAt(first));
                SpawnZombiePayload b = Protocol.Payload<SpawnZombiePayload>(ReceiveAt(second));
                Assert.Equal(44, a.NetId);
                Assert.Equal(7, b.ZombieType);

                Assert.True(host.Broadcast(MsgCategory.Gameplay, MsgAction.LevelStart,
                    new LevelStartPayload { LevelType = 1, LevelNumber = 2, LevelName = "Pool" },
                    exceptPeerId: 1));

                // Which client is peer 1 is not knowable from the client side, so assert the
                // semantics instead: exactly one of the two sees the frame.
                Assert.NotEqual(AnyArrives(first), AnyArrives(second));

                Assert.True(TestLog.Has("broadcast Gameplay/SpawnZombie to 2 of 2 peers"), TestLog.Describe());
                Assert.True(TestLog.Has("broadcast Gameplay/LevelStart to 1 of 2 peers"), TestLog.Describe());
            }
        }

        [Fact]
        public void ExtraClientBeyondCapacityGetsARoomFullRejectAndIsNotRegistered()
        {
            TestLog.Clear();
            using (var host = new MpHost(1))
            using (var client = new MpClient())
            {
                host.Start(0);
                ConnectTo(host, client);
                Assert.True(Wait.Until(() => host.Peers().Length == 1));

                var raw = new TcpClient();
                raw.Connect(IPAddress.Loopback, host.Port);
                DecodedFrame reject = FrameIo.Read(raw.GetStream());
                raw.Dispose();

                Assert.Equal(MsgCategory.Control, reject.Category);
                Assert.Equal(MsgAction.Reject, reject.Action);
                Assert.Equal(RejectCode.RoomFull, Protocol.Payload<RejectPayload>(reject).Code);
                Assert.Single(host.Peers());
                Assert.True(TestLog.Has("room full at 1 peers"), TestLog.Describe());
            }
        }

        [Fact]
        public void DisconnectIsReportedOnceAndRemovesThePeer()
        {
            TestLog.Clear();
            using (var host = new MpHost(4))
            {
                host.Start(0);
                var lost = new List<string>();
                host.PeerLost += (peer, reason) =>
                {
                    lock (lost)
                    {
                        lost.Add(reason);
                    }
                };

                var client = new MpClient();
                ConnectTo(host, client);
                Assert.True(Wait.Until(() => host.Peers().Length == 1));
                int peerId = host.Peers()[0].PeerId;

                client.Disconnect("player left");

                Assert.True(Wait.Until(() =>
                {
                    lock (lost)
                    {
                        return lost.Count > 0;
                    }
                }), "PeerLost never fired");
                Assert.True(Wait.Until(() => host.Peers().Length == 0), "peer stayed in the roster");

                // Nothing may arrive at a host that has no peers: TryReceive must say so rather than
                // hand the session layer a frame with an invented peer id.
                DecodedFrame frame;
                int dropped;
                Assert.False(host.TryReceive(out dropped, out frame));
                Assert.Equal(-1, dropped);
                Assert.Null(frame);
                Assert.False(host.SendTo(peerId, MsgCategory.Control, MsgAction.Bye, null));
                Assert.True(TestLog.Has("no such peer"), TestLog.Describe());

                client.Dispose();
            }
        }

        [Fact]
        public void ConnectToAPortNobodyListensOnFailsInsteadOfHanging()
        {
            int freePort;
            var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            using (var client = new MpClient())
            {
                Stopwatch watch = Stopwatch.StartNew();
                // Either a refused connect or the promised timeout is acceptable; what must not happen
                // is the game thread sitting there past the budget the dialog showed the player.
                Exception error = Record.Exception(() => client.Connect("127.0.0.1", freePort, 1500));
                Assert.True(watch.ElapsedMilliseconds < 4000, "connect took " + watch.ElapsedMilliseconds + "ms");
                Assert.NotNull(error);
                Assert.True(error is SocketException || error is TimeoutException,
                    error.GetType().Name + ": " + error.Message);
                Assert.False(client.IsConnected);
                Assert.False(client.Send(MsgCategory.Control, MsgAction.Hello, null));
            }
        }

        [Fact]
        public void StoppingTheHostClosesTheClientLinkWithAReason()
        {
            TestLog.Clear();
            using (var host = new MpHost(4))
            using (var client = new MpClient())
            {
                host.Start(0);
                ConnectTo(host, client);
                Assert.True(Wait.Until(() => client.IsConnected));

                string lost = null;
                client.Lost += reason => lost = reason;

                host.Stop();

                Assert.True(Wait.Until(() => lost != null), "client never noticed the host going away");
                Assert.False(host.IsRunning);
                Assert.True(TestLog.Has("host stopped"), TestLog.Describe());
            }
        }

        [Fact]
        public void AGuestThatDiesOnTheWayInDoesNotStopTheHostTakingTheNextOne()
        {
            TestLog.Clear();
            using (var host = new MpHost(4))
            using (var player = new MpClient())
            {
                host.Start(0);

                // A seat that appears and then stops answering is one of the things a room has to
                // survive. The accept loop used to leave on the first exception it met, which would
                // have ended every later join while the screen went on reading "hosting".
                DieOnTheWayIn(host.Port);

                ConnectTo(host, player);
                Assert.True(player.Send(MsgCategory.Control, MsgAction.Hello,
                    new HelloPayload { Nick = "Afterwards", Version = Protocol.Version }));

                DecodedFrame hello = null;
                int peerId = -1;
                Assert.True(Wait.Until(() => host.TryReceive(out peerId, out hello)
                    && hello.Action == MsgAction.Hello, 4000), "the host never carried a hello: "
                    + TestLog.Describe());
                Assert.Equal("Afterwards", Protocol.Payload<HelloPayload>(hello).Nick);

                // The dead seat may not keep its place at the table either.
                Assert.True(Wait.Until(() =>
                {
                    PeerLink[] peers = host.Peers();
                    return peers.Length == 1 && peers[0].PeerId == peerId;
                }, 4000), "the seat of the guest that died never emptied");
            }
        }

        private static void DieOnTheWayIn(int port)
        {
            // The connect is still on its way when the socket is thrown away, so the host sees a guest
            // that appeared and then stopped answering - an abrupt death as the listener experiences it.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                Blocking = false
            };
            socket.BeginConnect(IPAddress.Loopback, port, null, null);
            socket.Close();
        }

        private static void ConnectTo(MpHost host, MpClient client)
        {
            client.Connect("127.0.0.1", host.Port, 3000);
        }

        private static bool AnyArrives(MpClient client)
        {
            DecodedFrame frame = null;
            return Wait.Until(() => client.TryReceive(out frame), 800);
        }

        private static DecodedFrame ReceiveAt(MpClient client)
        {
            DecodedFrame frame = null;
            Assert.True(Wait.Until(() => client.TryReceive(out frame)), "nothing arrived at the client");
            return frame;
        }

        private static DecodedFrame ReceiveFrom(MpHost host, int expectedPeerId)
        {
            DecodedFrame frame = null;
            int peerId = -1;
            Assert.True(Wait.Until(() =>
            {
                int found;
                DecodedFrame candidate;
                if (!host.TryReceive(out found, out candidate))
                {
                    return false;
                }

                peerId = found;
                frame = candidate;
                return true;
            }), "nothing arrived at the host");
            Assert.Equal(expectedPeerId, peerId);
            return frame;
        }
    }
}
