using System.Collections.Generic;
using System.Text;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// Who is in the room, as the host sees it and as every client receives it. The host owns this list
    /// and broadcasts it; a client only ever replaces its copy wholesale, because a partial merge would
    /// let two peers disagree about who is connected - which is the one thing the whole design forbids.
    /// </summary>
    public sealed class SessionRoster
    {
        private readonly List<RosterEntry> _entries = new List<RosterEntry>();

        public IReadOnlyList<RosterEntry> Entries
        {
            get { return _entries; }
        }

        public int ConnectedCount
        {
            get
            {
                int count = 0;
                foreach (RosterEntry entry in _entries)
                {
                    if (entry.Connected)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public void Add(int peerId, string nick, bool isHost)
        {
            _entries.Add(new RosterEntry
            {
                PeerId = peerId,
                Nick = nick,
                IsHost = isHost,
                Connected = true,
                PingMillis = isHost ? 0 : -1
            });
        }

        public void Clear()
        {
            _entries.Clear();
        }

        public RosterEntry Find(int peerId)
        {
            foreach (RosterEntry entry in _entries)
            {
                if (entry.PeerId == peerId)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// Connected seats that have not pressed the chooser's LET'S ROCK yet. The host is never in the
        /// count: its own press is the decision, and waiting for it would be waiting for itself.
        /// </summary>
        public int SeatsNotReady()
        {
            int waiting = 0;

            foreach (RosterEntry entry in _entries)
            {
                if (entry.Connected && !entry.IsHost && !entry.Ready)
                {
                    waiting++;
                }
            }

            return waiting;
        }

        /// <summary>
        /// The seat said where it stands. False means it said the same word twice, or it has no seat: the
        /// chooser's LET'S ROCK is a toggle, so both words arrive on one frame and only a change is worth
        /// broadcasting.
        /// </summary>
        public bool SetReady(int peerId, bool ready)
        {
            RosterEntry entry = Find(peerId);

            if (entry == null || entry.Ready == ready)
            {
                return false;
            }

            entry.Ready = ready;

            return true;
        }

        /// <summary>Every seat is waiting again: how a level that began, or ended, leaves the chooser.</summary>
        public int ClearReadiness()
        {
            int cleared = 0;

            foreach (RosterEntry entry in _entries)
            {
                if (entry.Ready)
                {
                    entry.Ready = false;
                    cleared++;
                }
            }

            return cleared;
        }

        /// <summary>
        /// The peer left: its line goes with it. A stale "left" entry would keep a name reserved and make
        /// the list disagree with the seats the host will actually accept.
        /// </summary>
        public bool Remove(int peerId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].PeerId == peerId)
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A nick nobody in the room holds, or null when the numbering ran out. The native profile name is
        /// 'Player' for every install, so the second player in the room is the normal case.
        /// </summary>
        public string AllocateNick(string wanted)
        {
            return NickRules.FirstFree(wanted, TakenNicks());
        }

        public RosterPayload ToPayload()
        {
            return new RosterPayload { Entries = _entries.ToArray() };
        }

        /// <summary>Replaces the whole copy: what the host says the room is, is what the room is.</summary>
        public void Apply(RosterPayload payload)
        {
            _entries.Clear();

            if (payload == null || payload.Entries == null)
            {
                return;
            }

            foreach (RosterEntry entry in payload.Entries)
            {
                if (entry != null)
                {
                    _entries.Add(entry);
                }
            }
        }

        /// <summary>One line per roster change, human readable, and the only Stage 3 acceptance target.</summary>
        public string Describe()
        {
            var text = new StringBuilder();

            foreach (RosterEntry entry in _entries)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(entry.PeerId).Append(':');

                if (entry.IsHost)
                {
                    text.Append("host");
                }
                else
                {
                    text.Append("peer");
                }

                text.Append('(').Append(entry.Nick).Append(')');
            }

            return text.ToString();
        }

        private HashSet<string> TakenNicks()
        {
            var taken = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (RosterEntry entry in _entries)
            {
                if (entry.Connected)
                {
                    taken.Add(entry.Nick);
                }
            }

            return taken;
        }
    }
}
