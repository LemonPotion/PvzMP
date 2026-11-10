using System.Text;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// The lobby list: a header that counts the seats taken and one line per player. The host and every
    /// client run this over the roster the host broadcast, so the two ends can only disagree if the roster
    /// itself disagrees - which is the one thing the room's tests already forbid.
    ///
    /// The lines are also what the HUD puts on the screen, one line per player, so they are made here one at
    /// a time and joined only for <see cref="Text"/>: the screen needs to know which seat a line speaks of
    /// before it draws it, and a seat's line is what a mouse can be over.
    ///
    /// No Unity types live here, for the same reason the rest of Core/Session has none: this is what the
    /// HUD is checked against without a running game.
    /// </summary>
    public static class LobbyBoard
    {
        /// <summary>
        /// The green a ready seat is drawn in. Written as the rich-text tag the game's own text mesh reads,
        /// so one label shows the list and the colour is the game's, not a second object the mod builds.
        /// </summary>
        public const string ReadyColor = "#00FF00";

        public static string Header(int taken, int capacity)
        {
            return "Lobby " + taken + "/" + capacity;
        }

        /// <summary>One seat: its number, its name, and what the room knows about it.</summary>
        public static string Line(RosterEntry entry)
        {
            var text = new StringBuilder();

            text.Append('#').Append(entry.PeerId).Append(' ').Append(entry.Nick);

            if (entry.IsHost)
            {
                text.Append(" (host)");
            }

            if (entry.Ready)
            {
                text.Append(" <color=").Append(ReadyColor).Append(">(ready)</color>");
            }

            if (entry.PingMillis >= 0)
            {
                text.Append(' ').Append(entry.PingMillis).Append("ms");
            }

            return text.ToString();
        }

        public static string Text(SessionRoster roster, int capacity)
        {
            var text = new StringBuilder();
            int taken = roster == null ? 0 : roster.ConnectedCount;

            text.Append(Header(taken, capacity));

            if (roster == null)
            {
                return text.ToString();
            }

            foreach (RosterEntry entry in roster.Entries)
            {
                text.Append('\n').Append(Line(entry));
            }

            return text.ToString();
        }
    }
}
