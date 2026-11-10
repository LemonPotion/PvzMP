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
    /// What the shovel decides and what the room carries about a dig. The rules are pure: any seat may take
    /// back any plant the room grew, and a plant the room never minted stays the game's own. Which plant that
    /// is comes from the game's hit test, so the ledger is only asked whether it knows any of them. That the
    /// hole is actually dug - and paid for - is the game's, and is proven by a live run, not here.
    /// </summary>
    public class ShovelTests : IDisposable
    {
        /// <summary>Plant.DieReason.ByShovel in this build, which is what the host sends and every peer dies with.</summary>
        private const int ByShovel = 7;

        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void AnySeatMayDigAnyPlantTheRoomGrew()
        {
            Assert.Null(ShovelRules.Refusal(Plant(1, 3, 1)));
            Assert.Null(ShovelRules.Refusal(Plant(0, 3, 1)));
            Assert.Null(ShovelRules.Refusal(Plant(2, 3, 1)));
        }

        [Fact]
        public void APlantTheRoomNeverGrewStaysWhereItIs()
        {
            Assert.Contains("no plant of this room", ShovelRules.Refusal(null));
        }

        [Fact]
        public void TheShovelIsNotAToolForThingsThatAreNotPlants()
        {
            NetEntity zombie = new EntityLedger().Add(NetKind.Zombie, 1, 0, 0, 2);

            Assert.Contains("Zombie", ShovelRules.Refusal(zombie));
        }

        [Fact]
        public void TheRoomOnlyNamesPlantsItHasSeenOnItsOwnLawn()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity plant = ledger.Add(NetKind.Plant, 1, 0, 3, 1);
            ledger.Bind(plant.NetId, 4211);

            Assert.Null(ShovelRules.TrackedPlant(ledger, new int[] { 900 }));
            Assert.Equal(plant.NetId, ShovelRules.TrackedPlant(ledger, new int[] { 4211 }).NetId);
        }

        [Fact]
        public void TheFirstPlantUnderTheCursorTheRoomKnowsIsTheOneItAsksAbout()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity pumpkin = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            ledger.Bind(pumpkin.NetId, 100);

            // A cell answers the game's hit test with more than one plant and the game picks the order, so
            // the mod takes the first name its own ledger knows and never chooses between two it does not.
            Assert.Equal(pumpkin.NetId, ShovelRules.TrackedPlant(ledger, new int[] { 900, 100 }).NetId);
            Assert.Null(ShovelRules.TrackedPlant(ledger, new int[0]));
            Assert.Null(ShovelRules.TrackedPlant(ledger, null));
        }

        [Fact]
        public void AHostDecidesForItsRoomFromTheMomentItListens()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            Assert.True(host.RoomDecides);
        }

        [Fact]
        public void APeerStillDiallingKeepsItsOwnShovel()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession client = NewSession();
            client.Join("127.0.0.1", host.Port, "Client");

            // Without a seat there is nobody to contradict, so the clicks of a single player stay clickable.
            Assert.False(client.RoomDecides);

            PumpUntil(() => client.Phase == SessionPhase.Connected);

            Assert.True(client.RoomDecides);
        }

        [Fact]
        public void ANobodyInARoomDecidesNothing()
        {
            Assert.False(new MpSession().RoomDecides);
        }

        [Fact]
        public void ADigRequestTravelsToTheHostWithTheSeatThatAsked()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Received asked = Watch(host);
            client.SendToHost(MsgAction.RemovePlantRequest, new RemovePlantRequest { NetId = 7 });

            PumpUntil(() => asked.Frames.Count == 1);

            Assert.Equal(MsgAction.RemovePlantRequest, asked.Frames[0].Action);
            Assert.Equal(client.LocalPeerId, asked.PeerIds[0]);
            Assert.Equal(7, Protocol.Payload<RemovePlantRequest>(asked.Frames[0]).NetId);
        }

        [Fact]
        public void TheHostsDespawnTravelsToEveryPeerAndSaysHowThePlantDied()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Received onFirst = Watch(first);
            Received onSecond = Watch(second);

            host.BroadcastGameplay(MsgAction.DespawnPlant, new DespawnPlantPayload
            {
                NetId = 7,
                Reason = ByShovel
            });

            PumpUntil(() => onFirst.Frames.Count == 1 && onSecond.Frames.Count == 1);

            foreach (Received peer in new[] { onFirst, onSecond })
            {
                DespawnPlantPayload despawn = Protocol.Payload<DespawnPlantPayload>(peer.Frames[0]);

                Assert.Equal(7, despawn.NetId);
                Assert.Equal(ByShovel, despawn.Reason);
                Assert.Equal(MpHost.HostPeerId, peer.PeerIds[0]);
            }
        }

        [Fact]
        public void ARefusedDigComesBackToTheSeatThatAskedWithTheHostsWords()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Received told = Watch(client);

            Assert.True(host.SendToPeer(client.LocalPeerId, MsgAction.RequestDenied, new DenyPayload
            {
                Requested = MsgAction.RemovePlantRequest,
                Reason = "no plant of this room grows there"
            }));

            PumpUntil(() => told.Frames.Count == 1);

            DenyPayload denial = Protocol.Payload<DenyPayload>(told.Frames[0]);

            Assert.Equal(MsgAction.RemovePlantRequest, denial.Requested);
            Assert.Contains("no plant of this room", denial.Reason);
        }

        public void Dispose()
        {
            foreach (MpSession session in _sessions)
            {
                session.Leave("the test ended");
            }
        }

        private static NetEntity Plant(int ownerPeer, int column, int row)
        {
            return new EntityLedger().Add(NetKind.Plant, ownerPeer, 0, column, row);
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
