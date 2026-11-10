using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PvzMP;
using PvzMP.Core.Session;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The Stage 3 acceptance target: a room forms over real sockets and every end agrees on one roster.
    /// The session is pumped here exactly the way the game pumps it from OnUpdate.
    /// </summary>
    public class SessionTests : IDisposable
    {
        private readonly List<MpSession> _sessions = new List<MpSession>();

        [Fact]
        public void HostAndThreeClientsAllAgreeOnOneRoster()
        {
            MpSession host = NewSession();
            host.Host("Player", 0);

            Assert.Equal(SessionPhase.Hosting, host.Phase);
            Assert.Equal(0, host.LocalPeerId);
            Assert.True(host.Port > 0, "the host did not bind a port");

            for (int i = 0; i < 3; i++)
            {
                JoinAndConnect(host, "Player");
            }

            string expected = "0:host(Player) 1:peer(Player#2) 2:peer(Player#3) 3:peer(Player#4)";
            foreach (MpSession session in _sessions)
            {
                Assert.Equal(expected, session.Roster.Describe());
            }

            Assert.Equal("Player", host.LocalNick);
        }

        [Fact]
        public void EachPeerGetsItsOwnOnlineIdAndCollidingNicksGetNumbered()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession first = JoinAndConnect(host, "SameName");
            MpSession second = JoinAndConnect(host, "SameName");

            Assert.Equal(0, host.LocalPeerId);
            Assert.NotEqual(first.LocalPeerId, second.LocalPeerId);
            Assert.Equal("SameName", first.LocalNick);
            Assert.Equal("SameName#2", second.LocalNick);
        }

        [Fact]
        public void AHostRefusesAnOlderModAndSaysWhy()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpClient stray = new MpClient();
            stray.Connect("127.0.0.1", host.Port, 3000);
            stray.Send(MsgCategory.Control, MsgAction.Hello,
                new HelloPayload { Version = "0.0.1", Nick = "Ancient" });

            try
            {
                RejectPayload reject = ReceiveReject(stray, host.Tick);

                Assert.Equal(RejectCode.VersionMismatch, reject.Code);
                Assert.Contains("0.0.1", reject.Reason);
            }
            finally
            {
                stray.Dispose();
            }
        }

        [Fact]
        public void AFifthPeerIsToldTheRoomIsFull()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession earliest = JoinAndConnect(host, "Player");
            JoinAndConnect(host, "Player");
            JoinAndConnect(host, "Player");

            MpSession late = NewSession();
            late.Join("127.0.0.1", host.Port, "Player");

            PumpUntil(() => late.Phase == SessionPhase.Idle);

            Assert.Equal(RejectCode.RoomFull, late.LastReject);
            Assert.Equal(4, host.Roster.ConnectedCount);
            Assert.Equal(SessionPhase.Connected, earliest.Phase);
        }

        [Fact]
        public void APeerThatLeavesIsRemovedFromEveryOtherEnd()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession stays = JoinAndConnect(host, "Stay");
            MpSession leaves = JoinAndConnect(host, "Leave");

            leaves.Leave("the player closed the dialog");

            // The host and the staying peer remain: the leaving seat is removed, not kept as "left".
            PumpUntil(() => host.Roster.ConnectedCount == 2 && stays.Roster.ConnectedCount == 2);

            Assert.Equal("0:host(Host) 1:peer(Stay)", host.Roster.Describe());
            Assert.Equal(host.Roster.Describe(), stays.Roster.Describe());
        }

        [Fact]
        public void ALeaveFreesTheNickAndTheLine()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession leaves = JoinAndConnect(host, "Player");

            leaves.Leave("testing");

            // The seat only counts as free once the host has noticed the link died, which is the poll.
            PumpUntil(() => host.Roster.ConnectedCount == 1);

            MpSession next = JoinAndConnect(host, "Player");

            Assert.Equal("0:host(Host) 2:peer(Player)", host.Roster.Describe());
            Assert.Equal(host.Roster.Describe(), next.Roster.Describe());
            Assert.Equal("Player", next.LocalNick);
        }

        [Fact]
        public void JoiningAPortNobodyListensOnEndsBackAtIdleWithAReason()
        {
            MpSession lone = NewSession();
            lone.Join("127.0.0.1", ClosedPort(), "Solo");

            PumpUntil(() => lone.Phase == SessionPhase.Idle, 8000);

            Assert.Equal(SessionPhase.Idle, lone.Phase);
            Assert.False(string.IsNullOrEmpty(lone.LastFailure), "the join gave no reason for failing");
            Assert.Empty(lone.Roster.Entries);
        }

        [Fact]
        public void StoppingTheHostEndsTheClientsSession()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession client = JoinAndConnect(host, "Client");

            host.Leave("the host quit");

            PumpUntil(() => client.Phase == SessionPhase.Idle);

            Assert.Empty(client.Roster.Entries);
            Assert.Equal(SessionPhase.Idle, host.Phase);
        }

        [Fact]
        public void ALostHostIsSaidToThePlayerOnceAndNeverRepeated()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession client = JoinAndConnect(host, "Client");

            host.Leave("the host quit");

            PumpUntil(() => client.Phase == SessionPhase.Idle);

            string notice = client.TakeRoomEndNotice();
            Assert.NotNull(notice);
            Assert.Contains("host", notice);
            Assert.False(string.IsNullOrEmpty(client.LastFailure), "the session kept no reason");

            // The game side asks once a frame; the same ending must not sit on the screen for the
            // hundred and eighty frames the player spends reading it.
            Assert.Null(client.TakeRoomEndNotice());
        }

        [Fact]
        public void APlayerWhoLeavesOnPurposeIsNotToldAnythingHappened()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession client = JoinAndConnect(host, "Client");

            client.Leave("the player pressed the leave key");

            Assert.Null(client.TakeRoomEndNotice());
        }

        [Fact]
        public void ASecondHostOnABusySessionIsRefused()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            host.Host("Again", 0);

            Assert.Equal(SessionPhase.Hosting, host.Phase);
            Assert.Equal("0:host(Host)", host.Roster.Describe());
        }

        [Fact]
        public void AClientAcceptsTheSeatTheHostGivesIt()
        {
            MpSession host = NewSession();
            host.Host("Host", 0);

            MpSession client = JoinAndConnect(host, "Player");

            Assert.Equal(1, client.LocalPeerId);
            Assert.Equal("Player", client.LocalNick);
            Assert.Equal("0:host(Host) 1:peer(Player)", client.Roster.Describe());
        }

        public void Dispose()
        {
            foreach (MpSession session in _sessions)
            {
                session.Leave("the test ended");
            }
        }

        private MpSession NewSession()
        {
            MpSession session = new MpSession();
            _sessions.Add(session);
            return session;
        }

        private MpSession JoinTo(MpSession host, string nick)
        {
            MpSession client = NewSession();
            client.Join("127.0.0.1", host.Port, nick);
            return client;
        }

        /// <summary>
        /// The seat order on the wire is the accept order, so a join is settled one at a time. Settled
        /// means every end that is still in the room, not only the one that just joined: the roster is a
        /// broadcast, so an end already seated is a frame behind the newcomer, and asserting at the
        /// instant the newcomer connected reads a room that has not finished telling the others. That
        /// gap is not theory - it is how this test failed once the suite grew busy enough to hit it.
        /// </summary>
        private MpSession JoinAndConnect(MpSession host, string nick)
        {
            MpSession client = JoinTo(host, nick);
            PumpUntil(() => client.Phase == SessionPhase.Connected && EveryActiveEndAgrees(host));
            return client;
        }

        private bool EveryActiveEndAgrees(MpSession host)
        {
            string agreed = host.Roster.Describe();

            foreach (MpSession session in _sessions)
            {
                // An end that left or was refused keeps its own last answer and is no longer in the room.
                if (session.Phase == SessionPhase.Idle)
                {
                    continue;
                }

                if (session.Roster.Describe() != agreed)
                {
                    return false;
                }
            }

            return true;
        }

        private void PumpUntil(Func<bool> condition, int timeoutMillis = 5000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMillis)
            {
                TickAll();

                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            Assert.True(condition(), "the room never settled within " + timeoutMillis + "ms: " + DescribeRoom());
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

        private void TickAll()
        {
            foreach (MpSession session in _sessions)
            {
                session.Tick();
            }
        }

        private static RejectPayload ReceiveReject(MpClient client, Action pump)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 3000)
            {
                pump();

                DecodedFrame frame;
                if (client.TryReceive(out frame))
                {
                    Assert.Equal(MsgAction.Reject, frame.Action);
                    return Protocol.Payload<RejectPayload>(frame);
                }

                Thread.Sleep(5);
            }

            PeerLink link = client.Link;
            throw new TimeoutException("no answer within 3000ms: open=" + client.IsConnected
                + " close=" + (link == null ? "no link" : link.CloseReason)
                + " unread=" + (link == null ? -1 : link.IncomingQueued));
        }

        /// <summary>A port the OS handed out and then gave back, so nothing can be listening on it.</summary>
        private static int ClosedPort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
