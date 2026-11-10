namespace PvzMP.Core.Session
{
    /// <summary>
    /// The rule behind the game's own cup doors. Seventeen native methods build a victory cup
    /// (ref/EVIDENCE.md E39) and most of them are per-frame or per-round checks of this lawn's own
    /// zombie list, so every end reaches them at a moment of its own: measured live, a peer asked for a
    /// cup 21.6 seconds and again 45.8 seconds before the host's level ended. While the room stands in a
    /// level, the cup is the host's sentence and nothing else.
    /// </summary>
    public static class TrophyRules
    {
        /// <summary>
        /// True means the game's own cup code runs on this end. The host always runs its own, because
        /// the host's lawn is the lawn the room is told about, and a peer replays the host's cup through
        /// the same door, so that call has to get through too. A peer runs its own cup only where the
        /// room has no cup to speak of: outside a room, and inside one while no level is up - the garden
        /// and the menu are not the room's lawn, and their trophies stay the game's business.
        ///
        /// The room's level is the test rather than this end's board: during a level change the game
        /// builds its cup while no board can be named, and that is exactly when a cup of its own slipped
        /// onto a peer's lawn and stayed there through the next level.
        /// </summary>
        public static bool RunsOwnCup(bool roomDecides, bool theRoomIsInALevel, bool isHost,
                                      bool replayingTheHostsCup)
        {
            if (replayingTheHostsCup || !roomDecides || !theRoomIsInALevel)
            {
                return true;
            }

            return isHost;
        }

        /// <summary>
        /// Whether a cup on this lawn is the one the room's lift just started. The click is a coroutine on
        /// the cup's own object (E39: `PrizeMgr.Clicked` hides the cup's parts at its own pace), so the
        /// sweep that clears the cups the room no longer holds has to leave that object alone for the
        /// second the animation takes - hiding it on the same frame is a cup that vanishes instead of
        /// rising. No cup was clicked answers 0, and 0 is the id of nothing, so a sweep with no lift keeps
        /// no cup at all.
        /// </summary>
        public static bool IsLifting(int clickedInstanceId, int cupInstanceId)
        {
            return clickedInstanceId != 0 && clickedInstanceId == cupInstanceId;
        }
    }
}
