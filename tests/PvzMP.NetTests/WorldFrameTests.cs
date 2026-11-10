using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using PvzMP.Core.Session;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// Stage 4's plumbing on the room's side: a gameplay frame has to reach the game side on the right
    /// end and with the sender's seat attached, because the host decides and every other end only
    /// repeats. The world itself is not here - WorldSync is the game's, and what it does with a frame is
    /// proven by a live run, not by these tests.
    /// </summary>
    public class WorldFrameTests : IDisposable
    {
        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void APlacementRequestFromAPeerReachesTheHostSide()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Received asked = Watch(host);
            client.SendToHost(MsgAction.PlacePlantRequest,
                new PlacePlantRequest { PlantType = 0, Column = 3, Row = 1 });

            PumpUntil(() => asked.Frames.Count == 1);

            Assert.Equal(MsgAction.PlacePlantRequest, asked.Frames[0].Action);
            Assert.Equal(MsgCategory.Gameplay, asked.Frames[0].Category);
            Assert.Equal(client.LocalPeerId, asked.PeerIds[0]);

            PlacePlantRequest payload = Protocol.Payload<PlacePlantRequest>(asked.Frames[0]);
            Assert.Equal(0, payload.PlantType);
            Assert.Equal(3, payload.Column);
            Assert.Equal(1, payload.Row);
        }

        [Fact]
        public void TheHostsGrantTravelsToEveryPeerInTheRoom()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Received onFirst = Watch(first);
            Received onSecond = Watch(second);

            host.BroadcastGameplay(MsgAction.SpawnPlant, new SpawnPlantPayload
            {
                NetId = 7,
                PlantType = 0,
                Column = 2,
                Row = 4,
                OwnerPeer = first.LocalPeerId
            });

            PumpUntil(() => onFirst.Frames.Count == 1 && onSecond.Frames.Count == 1);

            foreach (Received peer in new[] { onFirst, onSecond })
            {
                SpawnPlantPayload spawn = Protocol.Payload<SpawnPlantPayload>(peer.Frames[0]);
                Assert.Equal(7, spawn.NetId);
                Assert.Equal(2, spawn.Column);
                Assert.Equal(first.LocalPeerId, spawn.OwnerPeer);

                // The only other end a peer hears gameplay from is the host, and it arrives as seat 0.
                Assert.Equal(MpHost.HostPeerId, peer.PeerIds[0]);
            }
        }

        [Fact]
        public void ARefusalReachesThePeerThatAskedAndKeepsItsReason()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Received told = Watch(client);
            string reason = "cell 3,1 already holds plant #1";

            Assert.True(host.SendToPeer(client.LocalPeerId, MsgAction.RequestDenied, new DenyPayload
            {
                Requested = MsgAction.PlacePlantRequest,
                Reason = reason
            }));

            PumpUntil(() => told.Frames.Count == 1);

            DenyPayload denial = Protocol.Payload<DenyPayload>(told.Frames[0]);
            Assert.Equal(MsgAction.PlacePlantRequest, denial.Requested);
            Assert.Equal(reason, denial.Reason);
        }

        [Fact]
        public void ASendFromTheWrongEndOfTheStarIsRefusedWithoutThrowing()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            SunStatePayload sun = new SunStatePayload { Sun = 25, MaxSun = 500 };

            Assert.False(host.SendToHost(MsgAction.SunState, sun));
            Assert.False(client.SendToPeer(MpHost.HostPeerId, MsgAction.SunState, sun));

            // Refusing to send is not a reason to end the room.
            Assert.Equal(SessionPhase.Hosting, host.Phase);
            Assert.Equal(SessionPhase.Connected, client.Phase);
        }

        [Fact]
        public void AHandlerThatThrowsOnlyLosesItsOwnFrame()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            int seen = 0;
            host.GameplayHandler = (peerId, frame) =>
            {
                seen++;
                if (seen == 1)
                {
                    throw new InvalidOperationException("the world side is broken");
                }
            };

            client.SendToHost(MsgAction.PlacePlantRequest,
                new PlacePlantRequest { PlantType = 0, Column = 1, Row = 1 });
            PumpUntil(() => seen == 1);

            client.SendToHost(MsgAction.PlacePlantRequest,
                new PlacePlantRequest { PlantType = 0, Column = 1, Row = 2 });
            PumpUntil(() => seen == 2);

            Assert.Equal(SessionPhase.Hosting, host.Phase);
            Assert.Equal(2, host.Roster.ConnectedCount);
        }

        [Fact]
        public void ACosmeticFrameIsNotHandledAsGameplay()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            Received asked = Watch(host);

            MpClient stray = new MpClient();
            stray.Connect("127.0.0.1", host.Port, 3000);
            stray.Send(MsgCategory.Control, MsgAction.Hello,
                new HelloPayload { Version = Protocol.Version, Nick = "Peer" });

            try
            {
                PumpUntil(() => host.Roster.ConnectedCount == 2);

                // A cursor is Stage 4e's business: it must not reach the world as if it were a request.
                // The gameplay frame sent right after it arrives on the same ordered socket, so the
                // cosmetic frame in front of it was delivered too - it was dropped, not lost.
                stray.Send(MsgCategory.Cosmetic, MsgAction.CursorIntent, new CursorIntentPayload
                {
                    PeerId = 1,
                    Column = 2,
                    Row = 2,
                    PlantType = 0,
                    Shovel = false
                });
                stray.Send(MsgCategory.Gameplay, MsgAction.PlacePlantRequest,
                    new PlacePlantRequest { PlantType = 0, Column = 2, Row = 2 });

                PumpUntil(() => asked.Frames.Count == 1);

                Assert.Equal(MsgAction.PlacePlantRequest, asked.Frames[0].Action);
                Assert.Single(asked.Frames);
            }
            finally
            {
                stray.Dispose();
            }
        }

        [Fact]
        public void AnOverlongRefusalReasonIsRefusedByTheProtocol()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Gameplay, MsgAction.RequestDenied, new DenyPayload
            {
                Requested = MsgAction.PlacePlantRequest,
                Reason = new string('n', Protocol.MaxTextChars + 1)
            });

            DecodedFrame decoded = Protocol.Decode(frame, 4, frame.Length - 4);
            Exception thrown = Record.Exception(() => Protocol.Payload<DenyPayload>(decoded));

            Assert.IsType<ProtocolException>(thrown);
            Assert.Contains("chars", thrown.Message);
        }

        public void Dispose()
        {
            foreach (MpSession session in _sessions)
            {
                session.Leave("the test ended");
            }
        }

        /// <summary>What the game side was told, in the order the room delivered it.</summary>
        private sealed class Received
        {
            public readonly List<DecodedFrame> Frames = new List<DecodedFrame>();
            public readonly List<int> PeerIds = new List<int>();
        }

        private Received Watch(MpSession session)
        {
            Received received = new Received();
            session.GameplayHandler = (peerId, frame) =>
            {
                received.PeerIds.Add(peerId);
                received.Frames.Add(frame);
            };

            return received;
        }

        private MpSession NewSession()
        {
            MpSession session = new MpSession();
            _sessions.Add(session);
            return session;
        }

        private MpSession JoinAndConnect(MpSession host, string nick)
        {
            MpSession client = NewSession();
            client.Join("127.0.0.1", host.Port, nick);
            PumpUntil(() => client.Phase == SessionPhase.Connected
                && client.Roster.ConnectedCount == host.Roster.ConnectedCount);
            return client;
        }

        private void PumpUntil(Func<bool> condition)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 5000)
            {
                TickAll();

                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            Assert.True(condition(), "it never settled within 5000ms: " + DescribeRoom());
        }

        private void TickAll()
        {
            foreach (MpSession session in _sessions)
            {
                session.Tick();
            }
        }

        private string DescribeRoom()
        {
            var text = new StringBuilder();
            foreach (MpSession session in _sessions)
            {
                if (text.Length > 0)
                {
                    text.Append(" || ");
                }

                text.Append(session.Phase)
                    .Append(" id=").Append(session.LocalPeerId)
                    .Append(" reject=").Append(session.LastReject)
                    .Append(" failure=").Append(session.LastFailure)
                    .Append(" [").Append(session.Roster.Describe()).Append("] [")
                    .Append(session.Entities.Describe()).Append(']');
            }

            return text.ToString();
        }
    }
}
