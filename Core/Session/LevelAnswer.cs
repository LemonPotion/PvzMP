namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the room answers when someone starts a level. Three situations and no fourth: nobody decides
    /// for this end, so the game is on its own; the host starts one, so the room is told and the game runs;
    /// a peer that has a seat clicks its own level, and that is the one thing the room refuses, because two
    /// lawns in two levels cannot be synchronised afterwards.
    ///
    /// A peer that is still dialling is not refused: nobody has promised it a level yet, and taking away
    /// its menu while a socket is being opened would look like the mod broke the game.
    ///
    /// Applying the host's order is the host's decision a second time over, so it runs the game as if it
    /// were the peer's own - which is why it is not the refusal case.
    /// </summary>
    public sealed class LevelAnswer
    {
        public bool RunNative { get; private set; }
        public bool TellRoom { get; private set; }
        public string Refusal { get; private set; }

        private LevelAnswer(bool runNative, bool tellRoom, string refusal)
        {
            RunNative = runNative;
            TellRoom = tellRoom;
            Refusal = refusal;
        }

        public static LevelAnswer Decide(bool roomDecides, bool isHost, bool applyingTheHostsLevel)
        {
            if (!roomDecides)
            {
                return new LevelAnswer(true, false, null);
            }

            if (isHost)
            {
                return new LevelAnswer(true, true, null);
            }

            if (applyingTheHostsLevel)
            {
                return new LevelAnswer(true, false, null);
            }

            return new LevelAnswer(false, false, "the host starts the level, your own pick is ignored");
        }

        /// <summary>
        /// Why this level cannot be put on the wire, or null when it can. The name is the only argument
        /// with no size the mod controls, and a frame over the protocol's cap is not a refused send but a
        /// closed link, which would take the whole room down for one long save name.
        /// </summary>
        public static string Unshareable(string levelName, int maxTextChars)
        {
            if (levelName == null || levelName.Length <= maxTextChars)
            {
                return null;
            }

            return "the level name is " + levelName.Length + " chars, the room carries " + maxTextChars;
        }
    }
}
