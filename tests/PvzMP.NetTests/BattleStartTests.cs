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
    /// Who may say that the lawn starts moving. The rule is the level's rule in different words - one board,
    /// one decision - and the frame carries nothing, because there is no argument the host could hand a peer
    /// that the peer's own button would not read from its own game.
    /// </summary>
    public class BattleStartTests : IDisposable
    {
        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void TheHostStartsTheBattleAndTellsTheRoom()
        {
            BattleAnswer answer = BattleAnswer.Decide(true, true, false);

            Assert.True(answer.RunNative);
            Assert.True(answer.TellRoom);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void ASeatCannotPressReadyOfItsOwn()
        {
            BattleAnswer answer = BattleAnswer.Decide(true, false, false);

            Assert.False(answer.RunNative);
            Assert.False(answer.TellRoom);
            Assert.Contains("host", answer.Refusal);
        }

        [Fact]
        public void TheSeatPlayingTheHostsOrderIsNotRefusedByItself()
        {
            BattleAnswer answer = BattleAnswer.Decide(true, false, true);

            Assert.True(answer.RunNative);
            Assert.Null(answer.Refusal);
        }

        /// <summary>
        /// The one case worth naming twice: a player who pressed F2 and is still dialling has no seat, so no
        /// host that could be contradicted - its ready button keeps working until the room says otherwise.
        /// </summary>
        [Fact]
        public void NobodyDecidesForThisEndSoTheGamePlaysAlone()
        {
            BattleAnswer answer = BattleAnswer.Decide(false, false, false);

            Assert.True(answer.RunNative);
            Assert.False(answer.TellRoom);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void TheHostsBattleStartReachesEverySeat()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            Received onFirst = Watch(first);
            Received onSecond = Watch(second);

            host.BroadcastGameplay(MsgAction.BattleStart, null);

            PumpUntil(() => onFirst.Frames.Count == 1 && onSecond.Frames.Count == 1);

            foreach (Received peer in new[] { onFirst, onSecond })
            {
                Assert.Equal(MsgAction.BattleStart, peer.Frames[0].Action);
                Assert.Equal(MsgCategory.Gameplay, peer.Frames[0].Category);
                Assert.Equal(MpHost.HostPeerId, peer.PeerIds[0]);
                Assert.Equal("{}", peer.Frames[0].Json);
            }

            Assert.Equal(first.Roster.Describe(), second.Roster.Describe());
        }

        [Fact]
        public void ASeatThatPressedLetsRockIsPressingReadyNotStarting()
        {
            BattleAnswer answer = BattleAnswer.DecideLetsRock(true, false, false, 3);

            Assert.False(answer.RunNative);
            Assert.True(answer.MarksReady);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void TheHostStartsOnlyWhenNoSeatIsWaiting()
        {
            BattleAnswer blocked = BattleAnswer.DecideLetsRock(true, true, false, 2);

            Assert.False(blocked.RunNative);
            Assert.False(blocked.TellRoom);
            Assert.Contains("2 seat", blocked.Refusal);

            BattleAnswer clear = BattleAnswer.DecideLetsRock(true, true, false, 0);

            Assert.True(clear.RunNative);
            Assert.True(clear.TellRoom);
            Assert.Null(clear.Refusal);
        }

        [Fact]
        public void TheSeatPlayingTheHostsLetsRockPressesItAndDoesNotWait()
        {
            BattleAnswer answer = BattleAnswer.DecideLetsRock(true, false, true, 1);

            Assert.True(answer.RunNative);
            Assert.False(answer.MarksReady);
        }

        /// <summary>The chooser outside a room presses exactly as the game made it press.</summary>
        [Fact]
        public void ASinglePlayersLetsRockStillStartsTheLevel()
        {
            BattleAnswer answer = BattleAnswer.DecideLetsRock(false, false, false, 4);

            Assert.True(answer.RunNative);
            Assert.False(answer.TellRoom);
            Assert.False(answer.MarksReady);
            Assert.Null(answer.Refusal);
        }

        [Fact]
        public void ASeatThatPressedReadyIsShownOnEveryScreen()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "One");
            MpSession second = JoinAndConnect(host, "Two");

            // The host is never in the waiting count, or its own button would block on itself.
            Assert.Equal(2, host.Roster.SeatsNotReady());

            first.TellHostIAmReady(true);

            PumpUntil(() => second.Roster.SeatsNotReady() == 1);

            Assert.True(host.Roster.Find(first.LocalPeerId).Ready);
            Assert.False(host.Roster.Find(second.LocalPeerId).Ready);
            Assert.Equal(1, host.Roster.SeatsNotReady());
            Assert.True(second.Roster.Find(first.LocalPeerId).Ready);

            host.ClearReadiness();

            PumpUntil(() => first.Roster.SeatsNotReady() == 2 && second.Roster.SeatsNotReady() == 2);

            Assert.Equal(LobbyBoard.Text(first.Roster, MpSession.MaxPlayers),
                         LobbyBoard.Text(second.Roster, MpSession.MaxPlayers));
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
