using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// One plant as both lawns can read it: a local Unity instance id plus what the game says the plant
    /// is and where it stands. The id is machine-private, so it is only used to pair the two snapshots
    /// of one lawn and never goes on the wire.
    /// </summary>
    public struct LawnPlant
    {
        public int InstanceId;
        public int PlantType;
        public int Column;
        public int Row;
    }

    /// <summary>
    /// What one native call did to the lawn, already in net ids: the plants that left and the plants
    /// that appeared. The host applies it to its ledger and repeats it in this order - every despawn
    /// first, every spawn after - because a fusion can put the result on the cell of a plant that died.
    ///
    /// The game's fusion is not reimplemented here. CreatePlant.SetPlant runs CheckMix itself, so the
    /// host only measures what that call left behind and names it; the two snapshots are the whole
    /// rule. Proof: ref/EVIDENCE.md E12.
    /// </summary>
    public sealed class LawnChange
    {
        public static readonly LawnChange None = new LawnChange(new int[0], new LawnPlant[0], 0);

        public LawnChange(int[] goneNetIds, LawnPlant[] appeared, int unnamed)
        {
            GoneNetIds = goneNetIds;
            Appeared = appeared;
            Unnamed = unnamed;
        }

        /// <summary>Net ids the room already knew and that the game took away.</summary>
        public int[] GoneNetIds { get; private set; }

        /// <summary>Plants that exist after the call and did not exist before it.</summary>
        public LawnPlant[] Appeared { get; private set; }

        /// <summary>
        /// Plants the game changed or removed that the room never minted, so it has no id to announce.
        /// A level can start with such plants; they stay a known limit and the log has to show them.
        /// </summary>
        public int Unnamed { get; private set; }

        /// <summary>
        /// The lawn did not change at all. An unnamed change is still a change: the game took something
        /// away, and the caller must not report that as a refusal.
        /// </summary>
        public bool IsEmpty
        {
            get { return GoneNetIds.Length == 0 && Appeared.Length == 0 && Unnamed == 0; }
        }
    }

    /// <summary>
    /// Compares two snapshots of the lawn taken around one native call. Instance ids pair the entries,
    /// because a plant that survived keeps its id; anything else is a plant that appeared, and an id
    /// that is gone is a plant the game took away. A type change on the same id is a fusion in place:
    /// the old record dies and the same object is named again under a new net id, because a peer can
    /// only repeat a creation, not a mutation.
    /// </summary>
    public static class LawnDiff
    {
        public static LawnChange Of(EntityLedger ledger, LawnPlant[] before, LawnPlant[] after)
        {
            var gone = new List<int>();
            var appeared = new List<LawnPlant>();
            var wasThere = new Dictionary<int, LawnPlant>();
            var stillThere = new HashSet<int>();
            int unnamed = 0;

            if (before != null)
            {
                foreach (LawnPlant old in before)
                {
                    if (old.InstanceId != 0)
                    {
                        wasThere[old.InstanceId] = old;
                    }
                }
            }

            if (after != null)
            {
                foreach (LawnPlant now in after)
                {
                    if (now.InstanceId == 0)
                    {
                        continue;
                    }

                    stillThere.Add(now.InstanceId);

                    LawnPlant old;
                    if (!wasThere.TryGetValue(now.InstanceId, out old))
                    {
                        appeared.Add(now);
                        continue;
                    }

                    if (old.PlantType == now.PlantType)
                    {
                        continue;
                    }

                    NetEntity record = Named(ledger, now.InstanceId);
                    if (record == null)
                    {
                        unnamed++;
                        continue;
                    }

                    gone.Add(record.NetId);
                    appeared.Add(now);
                }
            }

            if (before != null)
            {
                foreach (LawnPlant old in before)
                {
                    if (old.InstanceId == 0 || stillThere.Contains(old.InstanceId))
                    {
                        continue;
                    }

                    NetEntity record = Named(ledger, old.InstanceId);
                    if (record == null)
                    {
                        unnamed++;
                        continue;
                    }

                    gone.Add(record.NetId);
                }
            }

            if (gone.Count == 0 && appeared.Count == 0 && unnamed == 0)
            {
                return LawnChange.None;
            }

            return new LawnChange(gone.ToArray(), appeared.ToArray(), unnamed);
        }

        /// <summary>The one-plant change the creator itself reported, for when the lawn could not be read.</summary>
        public static LawnChange OnlyNew(LawnPlant plant)
        {
            return new LawnChange(new int[0], new LawnPlant[] { plant }, 0);
        }

        private static NetEntity Named(EntityLedger ledger, int instanceId)
        {
            if (ledger == null)
            {
                return null;
            }

            NetEntity record = ledger.ByInstanceId(instanceId);

            return record != null && record.Kind == NetKind.Plant ? record : null;
        }
    }
}
