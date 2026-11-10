using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// Who took which plant while the seed chooser is up. The host owns the decision: a plant type is
    /// claimed by exactly one seat until that seat puts it back, and every end keeps a copy of this map so
    /// the chooser can grey out what another player already holds. No Unity types live here, which is what
    /// lets the rule be checked without a running game.
    /// </summary>
    public sealed class PlantDraft
    {
        private readonly Dictionary<int, int> _owners = new Dictionary<int, int>();

        /// <summary>Plant type to the peer that claimed it. Read-only view for the tick that releases peers.</summary>
        public IReadOnlyDictionary<int, int> Owners
        {
            get { return _owners; }
        }

        /// <summary>
        /// Claim a plant for a peer. True when the caller now owns it: either it was free or this peer
        /// already held it. False means another seat has it.
        /// </summary>
        public bool TryTake(int plantType, int peerId)
        {
            int owner;
            if (_owners.TryGetValue(plantType, out owner))
            {
                return owner == peerId;
            }

            _owners[plantType] = peerId;
            return true;
        }

        public int OwnerOf(int plantType)
        {
            int owner;
            return _owners.TryGetValue(plantType, out owner) ? owner : -1;
        }

        /// <summary>Only the seat that took the plant may put it back.</summary>
        public bool Release(int plantType, int peerId)
        {
            int owner;
            if (!_owners.TryGetValue(plantType, out owner) || owner != peerId)
            {
                return false;
            }

            _owners.Remove(plantType);
            return true;
        }

        /// <summary>
        /// Every plant one seat holds, smallest type first. The order is fixed because the same list is
        /// drawn on every screen, and a dictionary walks in the order its keys happened to arrive in.
        /// </summary>
        public List<int> PlantsHeldBy(int peerId)
        {
            var held = new List<int>();

            foreach (KeyValuePair<int, int> pair in _owners)
            {
                if (pair.Value == peerId)
                {
                    held.Add(pair.Key);
                }
            }

            held.Sort();
            return held;
        }

        /// <summary>Every claim a seat held, removed and returned: a player that left takes nothing with it.</summary>
        public List<int> ReleasePeer(int peerId)
        {
            var freed = new List<int>();

            foreach (KeyValuePair<int, int> pair in _owners)
            {
                if (pair.Value == peerId)
                {
                    freed.Add(pair.Key);
                }
            }

            for (int i = 0; i < freed.Count; i++)
            {
                _owners.Remove(freed[i]);
            }

            return freed;
        }

        /// <summary>The same plant types and owners, snapshotted so the caller may change the map while reading.</summary>
        public KeyValuePair<int, int>[] Snapshot()
        {
            var pairs = new KeyValuePair<int, int>[_owners.Count];
            int index = 0;

            foreach (KeyValuePair<int, int> pair in _owners)
            {
                pairs[index++] = pair;
            }

            return pairs;
        }

        public void Clear()
        {
            _owners.Clear();
        }
    }
}
