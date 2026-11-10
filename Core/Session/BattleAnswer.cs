namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the room answers when someone presses the game's own "start the battle" button. The shape is
    /// the level's shape on purpose - a room is one board, and the moment that board starts moving is as
    /// little a peer's decision as which board it is.
    ///
    /// Still, the two rules are kept apart: whether the host has to wait for every peer to be ready is a
    /// question about the battle and not about the level, so it is answered here and only for the chooser's
    /// button - the in-level start is the host's alone, because a screen the peer does not have cannot be
    /// waited for.
    /// </summary>
    public sealed class BattleAnswer
    {
        /// <summary>
        /// The button number that begins the waves. `InGameBtn.OnMouseUpAsButton` is one method with a jump
        /// table of twenty entries over `buttonNumber` (GameAssembly rva 0x7D2FE0, table rva 0x7D3718), and
        /// entry 4 is the branch that hides the button, clears one flag in `Board.boardTag` and sets
        /// `Board.timeUntilNextWave` to 3 seconds - the same four steps `InGameUI.OnStartBattleButtonClick`
        /// takes. The other nineteen numbers are pause, camera, glove, health display and the menu, and the
        /// room has no opinion about them. Proof: ref/EVIDENCE.md E1, E2.
        /// </summary>
        public const int StartBattleButton = 4;

        /// <summary>Whether this button of the lawn screen is the battle's, which is the only one gated.</summary>
        public static bool IsStartButton(int buttonNumber)
        {
            return buttonNumber == StartBattleButton;
        }

        public bool RunNative { get; private set; }
        public bool TellRoom { get; private set; }
        public bool MarksReady { get; private set; }
        public string Refusal { get; private set; }

        private BattleAnswer(bool runNative, bool tellRoom, string refusal)
        {
            RunNative = runNative;
            TellRoom = tellRoom;
            Refusal = refusal;
        }

        private BattleAnswer(bool runNative, bool tellRoom, string refusal, bool marksReady)
            : this(runNative, tellRoom, refusal)
        {
            MarksReady = marksReady;
        }

        public static BattleAnswer Decide(bool roomDecides, bool isHost, bool applyingTheHostsOrder)
        {
            if (!roomDecides)
            {
                return new BattleAnswer(true, false, null);
            }

            if (isHost)
            {
                return new BattleAnswer(true, true, null);
            }

            if (applyingTheHostsOrder)
            {
                return new BattleAnswer(true, false, null);
            }

            return new BattleAnswer(false, false, "the host starts the battle, your own ready is ignored");
        }

        /// <summary>
        /// The chooser's LET'S ROCK, which is the seat's own "I am ready" and not a start. The native press
        /// is what ends the seed chooser and sets `InitBoard.ready` (E17b), so the only press allowed to run
        /// is the one the room has agreed on: the host's, and only once every other seat has said it is
        /// ready. A seat that is waiting for nothing else presses as before, outside a room and while it is
        /// playing the host's order; a peer's press marks the seat and stops there.
        /// </summary>
        public static BattleAnswer DecideLetsRock(bool roomDecides, bool isHost,
                                                  bool applyingTheHostsOrder, int seatsNotReady)
        {
            if (!roomDecides || applyingTheHostsOrder)
            {
                return new BattleAnswer(true, false, null);
            }

            if (isHost)
            {
                return seatsNotReady == 0
                    ? new BattleAnswer(true, true, null)
                    : new BattleAnswer(false, false, "the level waits for " + seatsNotReady
                        + " seat(s) to press Let's Rock");
            }

            return new BattleAnswer(false, false, null, true);
        }
    }
}
