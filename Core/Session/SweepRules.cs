namespace PvzMP.Core.Session
{
    /// <summary>
    /// The rule behind the sweep key, which takes down the objects this lawn is holding that the room
    /// has no word about. A cup the host announced, a coin the room minted, a plant on the host's layout
    /// are the room's sentence and only the seat that owns it may end them; a leftover the room never
    /// announced belongs to nobody, and leaving it on the lawn is what the measured runs did to a peer's
    /// screen: a cup that stays through the next level, clicked again and again against a gate that is
    /// right to refuse.
    /// </summary>
    public static class SweepRules
    {
        /// <summary>
        /// Whether the sweep may take one object down. Outside a room the answer is always no: the key
        /// is a room's tool, and a single player's lawn is not the mod's business.
        /// </summary>
        public static bool MayTakeDown(bool roomDecides, bool theRoomAnnouncedIt)
        {
            return roomDecides && !theRoomAnnouncedIt;
        }
    }
}
