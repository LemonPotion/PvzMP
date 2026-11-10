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
    /// Who may start a level, and what the room carries about it. The rules themselves are pure (a peer's
    /// own click is refused, the host's call is copied), and the arguments have to survive the wire
    /// unchanged, because a peer runs the game's own EnterGame with exactly what the host passed in.
    /// Loading the level is the game's - proven by a live run, not here.
    /// </summary>
    public class LevelStartTests : IDisposable
    {
        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void TheHostStartsTheLevelAndTellsTheRoom()
        {
            LevelAnswer answer = LevelAnswer.Decide(true, true, false);

            Assert.True(answer.RunNative);
            Assert.True(answer.TellRoom);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void APeerCannotStartALevelOfItsOwn()
        {
            LevelAnswer answer = LevelAnswer.Decide(true, false, false);

            Assert.False(answer.RunNative);
            Assert.False(answer.TellRoom);
            Assert.Contains("host", answer.Refusal);
        }

        [Fact]
        public void ThePeerRunningTheHostsOrderIsNotRefusedByItself()
        {
            LevelAnswer answer = LevelAnswer.Decide(true, false, true);

            Assert.True(answer.RunNative);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void NobodyDecidesForThisEndSoTheGameStartsLevelsAlone()
        {
            // The one boolean covers both cases the game side can hand it: no room at all, and a peer that
            // pressed F2 and is still dialling - without a seat nobody has promised it a level, so its own
            // clicks stay its own.
            LevelAnswer answer = LevelAnswer.Decide(false, false, false);

            Assert.True(answer.RunNative);
            Assert.False(answer.TellRoom);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void TheHostsLevelReachesEveryPeerWithItsArguments()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Received onFirst = Watch(first);
            Received onSecond = Watch(second);

            host.BroadcastGameplay(MsgAction.LevelStart, new LevelStartPayload
            {
                LevelType = 0,
                LevelNumber = 3,
                LevelId = -1,
                LevelName = "Pool"
            });

            PumpUntil(() => onFirst.Frames.Count == 1 && onSecond.Frames.Count == 1);

            foreach (Received peer in new[] { onFirst, onSecond })
            {
                Assert.Equal(MsgAction.LevelStart, peer.Frames[0].Action);
                Assert.Equal(MsgCategory.Gameplay, peer.Frames[0].Category);
                Assert.Equal(MpHost.HostPeerId, peer.PeerIds[0]);

                LevelStartPayload start = Protocol.Payload<LevelStartPayload>(peer.Frames[0]);
                Assert.Equal(0, start.LevelType);
                Assert.Equal(3, start.LevelNumber);
                Assert.Equal(-1, start.LevelId);
                Assert.Equal("Pool", start.LevelName);
            }

            Assert.Equal(first.Roster.Describe(), second.Roster.Describe());
        }

        [Fact]
        public void TheHostCanTellOneSeatTheLevelTheRoomIsAlreadyIn()
        {
            // The way a seat that arrives mid-level is told what to load: the host repeats its own order
            // to that one peer. A broadcast would tear down every lawn already standing in the level and
            // build it again, which is what the room does not need twice.
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Received onFirst = Watch(first);
            Received onSecond = Watch(second);

            Assert.True(host.SendToPeer(second.LocalPeerId, MsgAction.LevelStart, new LevelStartPayload
            {
                LevelType = 0,
                LevelNumber = 3,
                LevelId = -1,
                LevelName = "Pool"
            }));

            PumpUntil(() => onSecond.Frames.Count == 1);

            Assert.Equal(MsgAction.LevelStart, onSecond.Frames[0].Action);
            Assert.Equal(MpHost.HostPeerId, onSecond.PeerIds[0]);
            Assert.Equal("Pool", Protocol.Payload<LevelStartPayload>(onSecond.Frames[0]).LevelName);

            // The seat already standing in the level is left standing in it.
            Assert.Empty(onFirst.Frames);
        }

        [Fact]
        public void APeerAnswersTheHostWithTheLevelItActuallyLoaded()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);
            MpSession client = JoinAndConnect(host, "Client");

            Received told = Watch(host);

            Assert.True(client.SendToHost(MsgAction.LevelReady, new LevelReadyPayload
            {
                LevelType = 0,
                LevelNumber = 3
            }));

            PumpUntil(() => told.Frames.Count == 1);

            Assert.Equal(MsgAction.LevelReady, told.Frames[0].Action);
            Assert.Equal(client.LocalPeerId, told.PeerIds[0]);

            LevelReadyPayload ready = Protocol.Payload<LevelReadyPayload>(told.Frames[0]);
            Assert.Equal(0, ready.LevelType);
            Assert.Equal(3, ready.LevelNumber);
        }

        [Fact]
        public void ALevelNameUpToTheCapTravelsAndOneCharOverItIsCaughtFirst()
        {
            string fits = new string('n', Protocol.MaxTextChars);
            string over = new string('n', Protocol.MaxTextChars + 1);

            Assert.Null(LevelAnswer.Unshareable(fits, Protocol.MaxTextChars));
            Assert.Null(LevelAnswer.Unshareable(null, Protocol.MaxTextChars));

            string reason = LevelAnswer.Unshareable(over, Protocol.MaxTextChars);
            Assert.Contains(over.Length.ToString(), reason);
            Assert.Contains(Protocol.MaxTextChars.ToString(), reason);

            // Without that check the room would not be told and would stay alive: the frame decodes into a
            // protocol error on the peer's reader thread, and the reader answers an error by closing.
            byte[] frame = Protocol.Encode(MsgCategory.Gameplay, MsgAction.LevelStart,
                new LevelStartPayload { LevelType = 0, LevelNumber = 1, LevelId = -1, LevelName = over });

            DecodedFrame decoded = Protocol.Decode(frame, 4, frame.Length - 4);
            Assert.IsType<ProtocolException>(
                Record.Exception(() => Protocol.Payload<LevelStartPayload>(decoded)));
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
                    .Append(" [").Append(session.Roster.Describe()).Append(']');
            }

            return text.ToString();
        }
    }
}
