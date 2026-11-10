namespace PvzMP.Core.Session
{
    /// <summary>
    /// The rule behind the game's own gift door. A gift plant does not grow the plant it was planted as:
    /// about a second later the game's animation calls `Present.AnimEvent`, and that body rolls
    /// `UnityEngine.Random` for the plant that comes out (ref/EVIDENCE.md E40). Two ends running that roll
    /// grow two plants, and the measured live run says they did: a gift the peer planted on cell 1,1 grew
    /// `WaterShieldshroom` on that end while the same box on the host's lawn grew `Shieldshroom`, and the
    /// client's lawn only agreed with the host's 1.8 seconds later, when the layout retype landed. A roll
    /// is worth one sentence, so the sentence is the host's.
    /// </summary>
    public static class GiftRules
    {
        /// <summary>
        /// True means the game's own dice runs on this end. The host always rolls, because the host's lawn
        /// is the lawn the room is told about. A peer does not roll while the room stands in a level, and
        /// the gift box it already shows is replaced by the plant the host rolled. Outside a room, and
        /// inside one while no level is up, every end rolls: the garden and the menu are not the room's
        /// lawn, and a gift grown there is nobody else's business.
        /// </summary>
        public static bool RollsOwnDice(bool roomDecides, bool theRoomIsInALevel, bool isHost)
        {
            if (!roomDecides || !theRoomIsInALevel)
            {
                return true;
            }

            return isHost;
        }
    }
}
