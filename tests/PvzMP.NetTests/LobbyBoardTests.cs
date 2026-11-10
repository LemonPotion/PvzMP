using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The lobby list the player is shown is this string, so the rules that matter - one line per seat, a
    /// header that counts the seats in use, and the same text at both ends of the star - are checked here
    /// instead of in a screenshot.
    /// </summary>
    public class LobbyBoardTests
    {
        [Fact]
        public void AHostOnItsOwnStillHasAHeaderAndItsOwnSeat()
        {
            SessionRoster roster = Roster();
            roster.Add(0, "Player", true);

            Assert.Equal("Lobby 1/4\n#0 Player (host) 0ms", LobbyBoard.Text(roster, 4));
        }

        [Fact]
        public void EveryJoinAddsOneLineAndOneSeat()
        {
            SessionRoster roster = Roster();
            roster.Add(0, "Player", true);

            Assert.Equal(2, Lines(LobbyBoard.Text(roster, 4)).Count);

            roster.Add(1, "Second", false);
            roster.Add(2, "Third", false);

            List<string> lines = Lines(LobbyBoard.Text(roster, 4));

            Assert.Equal("Lobby 3/4", lines[0]);
            Assert.Equal("#1 Second", lines[2]);
            Assert.Equal("#2 Third", lines[3]);
        }

        [Fact]
        public void APlayerThatLeavesLosesItsLineAndItsSeat()
        {
            SessionRoster roster = Roster();
            roster.Add(0, "Player", true);
            roster.Add(1, "Second", false);
            roster.Add(2, "Third", false);

            Assert.True(roster.Remove(2));

            string text = LobbyBoard.Text(roster, 4);

            Assert.StartsWith("Lobby 2/4", text);
            Assert.DoesNotContain("#2", text);
            Assert.Contains("#1 Second", text);
        }

        [Fact]
        public void APingIsShownNextToTheNameOnceTheHostHasMeasuredIt()
        {
            SessionRoster roster = Roster();
            roster.Add(0, "Player", true);
            roster.Add(1, "Second", false);

            roster.Find(1).PingMillis = 37;

            Assert.Contains("#1 Second 37ms", LobbyBoard.Text(roster, 4));
        }

        [Fact]
        public void ASeatIsGreenOnlyAfterItPressedLetsRock()
        {
            SessionRoster roster = Roster();
            roster.Add(0, "Player", true);
            roster.Add(1, "Second", false);

            Assert.DoesNotContain("ready", LobbyBoard.Text(roster, 4));

            Assert.True(roster.SetReady(1, true));

            // The same seat saying it twice changes nothing, and the host never waits for itself.
            Assert.False(roster.SetReady(1, true));
            Assert.Equal(0, roster.SeatsNotReady());

            string text = LobbyBoard.Text(roster, 4);

            Assert.Contains("#1 Second <color=" + LobbyBoard.ReadyColor + ">(ready)</color>", text);
            Assert.DoesNotContain("#0 Player <color", text);

            // Pressing a second time is the way back: the seat is waiting again, and the host waits for it.
            Assert.True(roster.SetReady(1, false));
            Assert.False(roster.SetReady(1, false));
            Assert.Equal(1, roster.SeatsNotReady());
            Assert.DoesNotContain("(ready)", LobbyBoard.Text(roster, 4));

            Assert.True(roster.SetReady(1, true));
            Assert.Equal(1, roster.ClearReadiness());
            Assert.DoesNotContain("ready", LobbyBoard.Text(roster, 4));
        }

        [Fact]
        public void TheHostAndTheClientPrintTheSameList()
        {
            SessionRoster host = Roster();
            host.Add(0, "Player", true);
            host.Add(1, "Second", false);
            host.Remove(1);
            host.Add(2, "Second", false);

            SessionRoster client = Roster();
            client.Apply(host.ToPayload());

            Assert.Equal(LobbyBoard.Text(host, MpSession.MaxPlayers),
                LobbyBoard.Text(client, MpSession.MaxPlayers));
        }

        [Fact]
        public void ARosterNobodyHasFilledIsJustTheHeader()
        {
            Assert.Equal("Lobby 0/4", LobbyBoard.Text(null, 4));
            Assert.Equal("Lobby 0/4", LobbyBoard.Text(Roster(), 4));
        }

        private static SessionRoster Roster()
        {
            return new SessionRoster();
        }

        private static List<string> Lines(string text)
        {
            return new List<string>(text.Split('\n'));
        }
    }
}
