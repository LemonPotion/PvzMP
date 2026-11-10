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
    /// The one call frame routed over the real transport, which is the part of the redesign a unit test
    /// cannot fake: the host's broadcast, a peer's call reaching the host and then the third end, the
    /// seat a frame has to come from for an authority to be believed, and the room's answer to a frame
    /// it does not cover.
    ///
    /// The applier stands in for the game here, as it must: NativeCalls is the game's side and needs a
    /// lawn, so what these tests prove is that a frame arrives, is decided and is counted - not that the
    /// native method answers, which a live run proves.
    ///
    /// Shared collection with the coverage tests: both read the log.
    /// </summary>
    [Collection("coverage")]
    public class CallRoutingTests : IDisposable
    {
        private const string SunKey = "Board.SetSun/1";
        private const string PlantKey = "Mouse.SetPlantFromCard/3";
        private const string ShovelKey = "Shovel.Use/1";

        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void TheHostsOrderedDoorReachesEveryPeerAsOneAppliedCall()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Ran onFirst = Watch(first);
            Ran onSecond = Watch(second);

            Assert.True(host.BroadcastCall(new MethodCall(ShovelKey, CallArgs.None, "[\"#7\"]",
                                                      host.Calls.NextSeq(), host.LocalPeerId)));

            PumpUntil(() => first.Calls.AppliedCount == 1 && second.Calls.AppliedCount == 1);

            foreach (Ran peer in new[] { onFirst, onSecond })
            {
                Assert.Equal(ShovelKey, Assert.Single(peer.Keys));
                Assert.Equal("[\"#7\"]", Assert.Single(peer.Args));

                // The number and the seat it started from survive the star: that is what lets a later
                // frame be recognised as the same call instead of a second one.
                Assert.Equal(1, Assert.Single(peer.Seqs));
                Assert.Equal(MpHost.HostPeerId, Assert.Single(peer.Origins));
            }
        }

        [Fact]
        public void TheHostsLatestValueReachesEveryPeerOnTheCosmeticLane()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Ran onFirst = Watch(first);
            Watch(second);

            // A value carries no number: the ordered lane reads a jump as a frame that never arrived, and
            // for a door whose arguments are the state the newest value is the only answer that matters.
            Assert.True(host.BroadcastCall(new MethodCall(SunKey, CallArgs.None, "[225]", 0,
                                                          host.LocalPeerId)));

            PumpUntil(() => first.Calls.AppliedCount == 1 && second.Calls.AppliedCount == 1);

            Assert.Equal(SunKey, Assert.Single(onFirst.Keys));
            Assert.Equal(0, Assert.Single(onFirst.Seqs));
            Assert.Equal(MpHost.HostPeerId, Assert.Single(onFirst.Origins));

            foreach (MpSession peer in new[] { first, second })
            {
                Assert.Equal(0, peer.Calls.GapCount);
                Assert.Equal(0, peer.Calls.DuplicateCount);

                // What arrived is kept as the room's answer, which is what a seat that joins this level
                // later is told.
                Assert.Equal("[225]", Assert.Single(peer.Calls.LastValues()).Args);
            }
        }

        [Fact]
        public void AFrameOnTheWrongLaneIsRefusedAndTheRoomCarriesOn()
        {
            // The lane is chosen by the row, so a sender that puts one door's frame on the other lane is
            // a sender with a different table - and both mistakes have to be said out loud, not run.
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Watch(client);

            MethodCallPayload latest = new MethodCall(SunKey, CallArgs.None, "[225]", 0,
                                                      MpHost.HostPeerId).ToPayload();
            Assert.True(host.SendToPeer(client.LocalPeerId, MsgAction.MethodCall, latest));
            PumpUntil(() => client.Calls.RefusedCount == 1);
            Assert.Contains("Mirror door", client.Calls.LastRefusal);

            MethodCallPayload ordered = new MethodCall(ShovelKey, CallArgs.None, "[\"#7\"]", 1,
                                                       MpHost.HostPeerId).ToPayload();
            host.BroadcastCosmetic(MsgAction.MethodCall, ordered);
            PumpUntil(() => client.Calls.RefusedCount == 2);
            Assert.Contains("Replicate door", client.Calls.LastRefusal);

            Assert.Equal(0, client.Calls.AppliedCount);
            Assert.Equal(SessionPhase.Connected, client.Phase);
        }

        [Fact]
        public void APeersCallIsAppliedOnTheHostAndPassedToTheThirdEndUnchanged()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Watch(host);
            Ran onSecond = Watch(second);

            MethodCall asked = new MethodCall(PlantKey, CallArgs.None, "[0,3,1]",
                                              first.Calls.NextSeq(), first.LocalPeerId);

            Assert.True(first.SendCallToHost(asked));

            PumpUntil(() => host.Calls.AppliedCount == 1 && second.Calls.AppliedCount == 1);

            Assert.Equal(asked.Args, Assert.Single(onSecond.Args));
            Assert.Equal(asked.Seq, Assert.Single(onSecond.Seqs));
            Assert.Equal(first.LocalPeerId, Assert.Single(onSecond.Origins));

            // The seat that asked is the one end that never hears the call back.
            Assert.Equal(0, first.Calls.AppliedCount);
            Assert.Equal(0, second.Calls.RefusedCount);
        }

        [Fact]
        public void AHostOnlyDoorSentByAPeerIsRefusedAndNeverPassedOn()
        {
            // The authority hole the ten old gates each had their own way of forgetting: a payload can
            // write any OriginPeer it likes, so the only claim the room believes is the seat the
            // transport says the frame came from.
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Watch(host);
            Ran onSecond = Watch(second);

            MethodCall forged = new MethodCall(SunKey, CallArgs.None, "[9999]",
                                               first.Calls.NextSeq(), MpHost.HostPeerId);

            Assert.True(first.SendCallToHost(forged));

            PumpUntil(() => host.Calls.RefusedCount == 1);

            Assert.Contains("only the host", host.Calls.LastRefusal);
            Assert.Equal(0, host.Calls.AppliedCount);

            // Nothing was run, so nothing was relayed: the third lawn never sees a call the room refused.
            Assert.Equal(0, second.Calls.AppliedCount);
            Assert.Equal(0, second.Calls.RefusedCount);
            Assert.Empty(onSecond.Keys);
            Assert.Equal(SessionPhase.Connected, second.Phase);
        }

        [Fact]
        public void AFrameNamingAnUncoveredMethodIsRefusedAndTheRoomCarriesOn()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Watch(host);

            Assert.True(client.SendCallToHost(new MethodCall("Board.Update/0", CallArgs.None, "[]",
                                                             1, client.LocalPeerId)));

            PumpUntil(() => host.Calls.RefusedCount == 1);

            Assert.Equal(0, host.Calls.AppliedCount);
            Assert.Contains("not in the coverage table", host.Calls.LastRefusal);

            // A refusal is a verdict about one frame, not a reason to stop the room.
            Assert.True(client.SendCallToHost(new MethodCall(PlantKey, CallArgs.None, "[0,1,1]",
                                                             2, client.LocalPeerId)));
            PumpUntil(() => host.Calls.AppliedCount == 1);

            Assert.Equal(2, host.Roster.ConnectedCount);
        }

        [Fact]
        public void TheSameFrameArrivingTwiceIsAppliedOnce()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Ran onClient = Watch(client);
            MethodCall once = new MethodCall(ShovelKey, CallArgs.None, "[\"#7\"]", 1, MpHost.HostPeerId);

            Assert.True(host.BroadcastCall(once));
            PumpUntil(() => client.Calls.AppliedCount == 1);

            Assert.True(host.BroadcastCall(once));
            PumpUntil(() => client.Calls.DuplicateCount == 1);

            Assert.Equal("[\"#7\"]", Assert.Single(onClient.Args));
            Assert.Equal(1, client.Calls.AppliedCount);
            Assert.Equal(1, client.Calls.DuplicateCount);
        }

        [Fact]
        public void ACoveredCallThatSkippedANumberIsStillAppliedAndSaysSo()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Watch(client);
            host.BroadcastCall(new MethodCall(ShovelKey, CallArgs.None, "[\"#1\"]", 1, MpHost.HostPeerId));
            PumpUntil(() => client.Calls.AppliedCount == 1);

            host.BroadcastCall(new MethodCall(ShovelKey, CallArgs.None, "[\"#2\"]", 4, MpHost.HostPeerId));
            PumpUntil(() => client.Calls.GapCount == 1);

            Assert.Equal(2, client.Calls.AppliedCount);
            Assert.True(TestLog.Has("skipped 2 call(s)"));
        }

        [Fact]
        public void ACallFrameIsNeverHandedToTheWorldAsARequest()
        {
            // The two halves of a room's traffic stay apart on purpose: a call is not a question about
            // the lawn, and the module that answers requests must not be able to mistake it for one.
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Ran onClient = Watch(client);
            onClient.WatchesHandler = true;

            Assert.True(host.BroadcastCall(new MethodCall(SunKey, CallArgs.None, "[10]", 1,
                                                          MpHost.HostPeerId)));
            PumpUntil(() => client.Calls.AppliedCount == 1);

            Assert.Empty(onClient.KeysFromTheHandler);
            Assert.Equal(SunKey, Assert.Single(onClient.Keys));
        }

        [Fact]
        public void LeavingARoomForgetsItsCallNumbers()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Watch(client);
            host.BroadcastCall(new MethodCall(SunKey, CallArgs.None, "[10]", 1, MpHost.HostPeerId));
            PumpUntil(() => client.Calls.AppliedCount == 1);

            client.Leave("the test asked");

            Assert.Equal(0, client.Calls.AppliedCount);
            Assert.Equal(0, client.Calls.DuplicateCount);
            Assert.Equal(0, client.Calls.PendingCount);
            Assert.Equal(0, client.Calls.LastValueCount);
            Assert.Null(client.Calls.LastRefusal);

            // The next room's first call is a new number, not a repeat of the last room's.
            Assert.Equal(1, client.Calls.NextSeq());
        }

        [Fact]
        public void ANewSeatIsToldTheValuesTheRoomAlreadyHasAndNotTheChanges()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            Watch(first);

            Assert.True(host.BroadcastCall(new MethodCall(ShovelKey, CallArgs.None, "[\"#7\"]",
                                                          host.Calls.NextSeq(), host.LocalPeerId)));
            PumpUntil(() => first.Calls.AppliedCount == 1);

            // The host's own lawn already has the number it announced, so no frame ever came back to it and
            // nothing applied one: the store the announce path writes is all a late seat can be given.
            host.Calls.Remember(new MethodCall(SunKey, CallArgs.None, "[300]", 0, MpHost.HostPeerId));

            List<int> seated = new List<int>();
            host.PeerSeated += seated.Add;

            MpSession second = NewSession();
            Ran onSecond = Watch(second);
            second.Join("127.0.0.1", host.Port, "Two");

            PumpUntil(() => second.Calls.AppliedCount == 1);

            Assert.Equal(SunKey, Assert.Single(onSecond.Keys));
            Assert.Equal("[300]", Assert.Single(onSecond.Args));
            Assert.Equal(0, second.Calls.GapCount);

            // Replaying an ordered call would dig a second hole: the room's history is not a value.
            Assert.DoesNotContain(ShovelKey, onSecond.Keys);

            // The feature modules are asked for the part of a room no native door describes, which is the
            // level every seat is standing in.
            Assert.Equal(new[] { second.LocalPeerId }, seated.ToArray());
        }

        [Fact]
        public void EveryFeatureIsToldAboutANewSeatAndNotOnlyTheLastOne()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            // The level and the seed draft both keep something a late seat cannot know. Whoever subscribes
            // second must not be the one that stops the first from being asked.
            List<int> forTheLevel = new List<int>();
            List<int> forTheDraft = new List<int>();
            host.PeerSeated += forTheLevel.Add;
            host.PeerSeated += forTheDraft.Add;

            MpSession guest = JoinAndConnect(host, "Guest");

            Assert.Equal(new[] { guest.LocalPeerId }, forTheLevel.ToArray());
            Assert.Equal(forTheLevel, forTheDraft);
        }

        [Fact]
        public void ACallCannotBeSentFromTheWrongEndOfTheStar()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            MethodCall call = new MethodCall(SunKey, CallArgs.None, "[1]", 1, MpHost.HostPeerId);

            Assert.False(client.BroadcastCall(call));
            Assert.False(host.SendCallToHost(call));

            // Not being able to send is not a reason to end the room.
            Assert.Equal(SessionPhase.Hosting, host.Phase);
            Assert.Equal(SessionPhase.Connected, client.Phase);
        }

        [Fact]
        public void ADeferredCallIsAppliedWhenTheRoomHasTickedEnough()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Ran onClient = Watch(client);
            onClient.DeferredUntil = 3;

            Assert.True(host.BroadcastCall(new MethodCall(ShovelKey, CallArgs.None, "[\"#7\"]",
                                                          1, MpHost.HostPeerId)));

            PumpUntil(() => client.Calls.PendingCount == 1);
            Assert.Equal(0, client.Calls.AppliedCount);

            // The retry is driven by the room's own clock, so it is Tick that has to ask for it.
            PumpUntil(() => client.Calls.AppliedCount == 1);

            Assert.Equal(0, client.Calls.PendingCount);
            Assert.Equal(ShovelKey, Assert.Single(onClient.Keys));
            Assert.Equal("[\"#7\"]", Assert.Single(onClient.Args));
        }

        public void Dispose()
        {
            foreach (MpSession session in _sessions)
            {
                session.Leave("the test ended");
            }
        }

        /// <summary>What this end's inbox ran, in the order the room delivered it.</summary>
        private sealed class Ran
        {
            public readonly List<string> Keys = new List<string>();
            public readonly List<string> Args = new List<string>();
            public readonly List<long> Seqs = new List<long>();
            public readonly List<int> Origins = new List<int>();

            /// <summary>The frames the gameplay handler was handed, which a call never should be.</summary>
            public readonly List<string> KeysFromTheHandler = new List<string>();

            public bool WatchesHandler;
            public int DeferredUntil;

            private int _asked;

            public CallOutcome Answer(MethodCall call)
            {
                _asked++;
                if (_asked <= DeferredUntil)
                {
                    return CallOutcome.Deferred;
                }

                Keys.Add(call.Key);
                Args.Add(call.Args);
                Seqs.Add(call.Seq);
                Origins.Add(call.OriginPeer);

                return CallOutcome.Applied;
            }
        }

        private Ran Watch(MpSession session)
        {
            Ran ran = new Ran();
            session.Calls.Applier = ran.Answer;
            session.GameplayHandler = (peerId, frame) =>
            {
                if (ran.WatchesHandler)
                {
                    ran.KeysFromTheHandler.Add(frame.Name);
                }
            };

            return ran;
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

            Assert.True(condition(), "it never settled within 5000ms: " + DescribeRooms());
        }

        private void TickAll()
        {
            foreach (MpSession session in _sessions)
            {
                session.Tick();
            }
        }

        private string DescribeRooms()
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
                    .Append(" [").Append(session.Roster.Describe()).Append("] applied=")
                    .Append(session.Calls.AppliedCount).Append(" refused=")
                    .Append(session.Calls.RefusedCount).Append(" pending=")
                    .Append(session.Calls.PendingCount).Append(" duplicate=")
                    .Append(session.Calls.DuplicateCount).Append(" kept=")
                    .Append(session.Calls.LastValueCount)
                    .Append(" reason=").Append(session.Calls.LastRefusal);
            }

            return text.ToString();
        }
    }
}
