using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the room said about the seed draft while this end had no chooser up, kept in the order it was
    /// said. A seat that appears while the host is already choosing is told the draft at a moment its own
    /// seed selection is still loading, and an order that arrives into a closed chooser belongs to the
    /// chooser that is coming rather than the one that is gone.
    ///
    /// The order is what has to survive, not the last word: a plant given and then put back is a plant
    /// nobody holds, and replaying only the newest state of each type would leave it taken.
    ///
    /// No Unity types live here, which is what lets the rule be checked without a running game.
    /// </summary>
    public sealed class DraftHold
    {
        private readonly List<Order> _orders = new List<Order>();

        /// <summary>How many orders are waiting for a chooser.</summary>
        public int Count
        {
            get { return _orders.Count; }
        }

        /// <summary>Keep one draft order: a plant type given to, or taken back from, a seat.</summary>
        public void Add(bool taken, int plantType, int ownerPeer)
        {
            _orders.Add(new Order(taken, plantType, ownerPeer));
        }

        /// <summary>
        /// The orders in the order they were said, with the hold emptied by the same move so a drain that
        /// is interrupted cannot apply an order twice.
        /// </summary>
        public List<Order> Take()
        {
            var orders = new List<Order>(_orders);
            _orders.Clear();

            return orders;
        }

        /// <summary>
        /// The chooser these orders were meant for is not coming: a draft order outlives its own seed
        /// selection, and the next level starts from what the host says from now on.
        /// </summary>
        public void GiveUp()
        {
            _orders.Clear();
        }

        /// <summary>One order kept on hold.</summary>
        public sealed class Order
        {
            public Order(bool taken, int plantType, int ownerPeer)
            {
                Taken = taken;
                PlantType = plantType;
                OwnerPeer = ownerPeer;
            }

            /// <summary>True when the seat took the plant, false when it put it back.</summary>
            public bool Taken { get; private set; }

            public int PlantType { get; private set; }

            public int OwnerPeer { get; private set; }
        }
    }
}
