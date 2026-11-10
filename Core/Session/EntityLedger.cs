using System.Collections.Generic;
using System.Text;

namespace PvzMP.Core.Session
{
    /// <summary>What a net id stands for. The id space is shared, so a despawn has to say which kind it removes.</summary>
    public enum NetKind
    {
        Plant,
        Zombie,
        Sun
    }

    /// <summary>
    /// One entity as the room agrees on it. Everything in here is a fact the host stated and every peer
    /// repeats; the local Unity instance id is the single machine-private value, which is why it is a
    /// mutable field on an otherwise host-written record instead of part of the payload.
    /// </summary>
    public sealed class NetEntity
    {
        public int NetId { get; internal set; }
        public NetKind Kind { get; internal set; }

        /// <summary>The peer whose click put it there. The room prints who grew what; digging is not decided by it.</summary>
        public int OwnerPeer { get; internal set; }

        public int Type { get; internal set; }
        public int Column { get; internal set; }
        public int Row { get; internal set; }

        /// <summary>0 until this machine has spawned its own copy of the entity.</summary>
        public int InstanceId { get; internal set; }
    }

    /// <summary>
    /// net id to the entity and to this machine's instance of it. The host is the only end that ever
    /// mints an id, so a client adds records when the host's spawn arrives and never invents one -
    /// two peers disagreeing about an id is the same class of bug as disagreeing about the roster.
    ///
    /// The id is a Unity instance id when it leaves this class, and instance ids are recycled by the
    /// game's own object pools; the net id is the value that survives that, which is the whole reason
    /// the registry exists.
    /// </summary>
    public sealed class EntityLedger
    {
        private readonly List<NetEntity> _entries = new List<NetEntity>();
        private readonly Dictionary<int, NetEntity> _byId = new Dictionary<int, NetEntity>();
        private int _nextNetId;

        public IReadOnlyList<NetEntity> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// The host's answer to an accepted request: a fresh id and the cell or type it was granted for.
        /// Ids only ever grow, so a stale frame naming a dead entity can never be mistaken for a live one.
        /// </summary>
        public NetEntity Add(NetKind kind, int ownerPeer, int type, int column, int row)
        {
            NetEntity entry = new NetEntity
            {
                NetId = ++_nextNetId,
                Kind = kind,
                OwnerPeer = ownerPeer,
                Type = type,
                Column = column,
                Row = row
            };

            _entries.Add(entry);
            _byId.Add(entry.NetId, entry);
            return entry;
        }

        /// <summary>
        /// A client's copy of a record the host already minted: the id comes from the frame, never from
        /// this machine. Null when the id is already here, which is a duplicate spawn frame and must not
        /// produce a second plant.
        /// </summary>
        public NetEntity Adopt(int netId, NetKind kind, int ownerPeer, int type, int column, int row)
        {
            if (netId <= 0 || Find(netId) != null)
            {
                return null;
            }

            NetEntity entry = new NetEntity
            {
                NetId = netId,
                Kind = kind,
                OwnerPeer = ownerPeer,
                Type = type,
                Column = column,
                Row = row
            };

            _entries.Add(entry);
            _byId.Add(netId, entry);
            return entry;
        }

        /// <summary>
        /// The same record an earlier host frame created, now that this machine has its own copy of the
        /// entity. False only when the id is unknown, which means the spawn frame was processed out of
        /// order or belongs to a session that is already over.
        /// </summary>
        public bool Bind(int netId, int instanceId)
        {
            NetEntity entry;
            if (!_byId.TryGetValue(netId, out entry) || instanceId == 0)
            {
                return false;
            }

            entry.InstanceId = instanceId;
            return true;
        }

        public NetEntity Find(int netId)
        {
            NetEntity entry;
            return _byId.TryGetValue(netId, out entry) ? entry : null;
        }

        /// <summary>The live plant on this cell, or null. A sun lying on the lawn does not block a planting.</summary>
        public NetEntity PlantAt(int column, int row)
        {
            foreach (NetEntity entry in _entries)
            {
                if (entry.Kind == NetKind.Plant && entry.Column == column && entry.Row == row)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>Which net id this machine's own object belongs to; null for anything the mod did not create.</summary>
        public NetEntity ByInstanceId(int instanceId)
        {
            if (instanceId == 0)
            {
                return null;
            }

            foreach (NetEntity entry in _entries)
            {
                if (entry.InstanceId == instanceId)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>The record it was, or null when the id was already gone. Only the host removes entries.</summary>
        public NetEntity Remove(int netId)
        {
            NetEntity entry = Find(netId);
            if (entry == null)
            {
                return null;
            }

            _entries.Remove(entry);
            _byId.Remove(netId);
            return entry;
        }

        /// <summary>
        /// A new room counts its ids from one again. Only the host ever mints, so as long as the minting
        /// end starts fresh every end agrees; a counter carried over from the last session would not.
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            _byId.Clear();
            _nextNetId = 0;
        }

        /// <summary>One line per entity, for the log and for a test failure message.</summary>
        public string Describe()
        {
            var text = new StringBuilder();

            foreach (NetEntity entry in _entries)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append('#').Append(entry.NetId).Append(':').Append(entry.Kind)
                    .Append("(owner ").Append(entry.OwnerPeer).Append(", type ").Append(entry.Type)
                    .Append(", cell ").Append(entry.Column).Append(',').Append(entry.Row)
                    .Append(", obj ").Append(entry.InstanceId).Append(')');
            }

            return text.ToString();
        }
    }
}
