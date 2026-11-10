using System;
using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The level the room plays. The host's own navigation call is heard by the gate and copied into a
    /// `LevelStart` frame with its four arguments unchanged; a peer answers that frame with the game's own
    /// `EnterGame`, so the level is loaded by the game on both ends and the mod carries nothing but the
    /// numbers saying which one. A restart is the same order: the host's pause-menu restart ends in
    /// `EnterGame` too, and the peer takes its old lawn down before loading the host's level.
    ///
    /// A peer that clicks a level of its own is refused, and so is its pause-menu restart: the game's own
    /// restart destroys the board it is standing on before `EnterGame` runs, so a peer's restart would take
    /// down the room's lawn without anything to put in its place. That is the one thing the room cannot
    /// undo: every later frame - a plant in a cell, a pool of sun, a wave - is about one board, and two
    /// boards have no meaning between them.
    ///
    /// Nobody waits for anybody yet. A peer answers with `LevelReady` when a lawn it did not have before
    /// is up, and the host logs it; making the host hold the first wave until those answers arrive is
    /// WaveGate's job, and it is the reason the answer carries the level the game really loaded.
    /// </summary>
    public static class LevelSync
    {
        public const string Capability = "mp-level";

        private static MpSession _room;

        /// <summary>The host's order this end has not seen a lawn for yet. Null on the host.</summary>
        private static LevelStartPayload _waiting;

        /// <summary>
        /// The order the host last gave the room, kept so the one seat that was not here to hear it can
        /// be told. Null outside a level: a newcomer must never be dropped into a lawn that has ended.
        /// </summary>
        private static LevelStartPayload _hostsOrder;

        /// <summary>The lawn that existed when the order arrived, by instance id - ids are never recycled.</summary>
        private static int _lawnBefore;

        /// <summary>True only while this end is calling EnterGame for the host, so the gate lets its own call through.</summary>
        private static bool _applying;

        /// <summary>True while this end is standing in the room's level, so a menu exit can tell the room.</summary>
        private static bool _inLevel;

        /// <summary>The same flag for the other doors: this end pressing the host's button is the host pressing it.</summary>
        private static bool _battleApplying;

        /// <summary>The host pressed LET'S ROCK before this end's chooser was up; the press is played on Tick.</summary>
        private static bool _rockPending;

        /// <summary>
        /// Told by the boot: which room decides the level, that the native navigation is now gated, and
        /// that a seat appearing later is asked of this module, because the level is the one thing here
        /// that is not a native door.
        /// </summary>
        public static void Attach(MpSession room)
        {
            _room = room;
            room.PeerSeated += Resync;
            LevelGate.Install();
            BattleGate.Install();
            RestartGate.Install();
            RoundGate.Install();

            Log.Info("mp", "the level is gated on the room: only the host starts one, restarts one, and says"
                + " when the lawn moves " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Whether the room stands in a level right now: true from the host's own start or the host's
        /// order this end loaded, false once either end has said the lawn moved. Doors that belong to the
        /// room's lawn ask this instead of looking for a board, because during a level change the room is
        /// still in its level and the board is already gone.
        /// </summary>
        public static bool TheRoomIsInALevel
        {
            get { return _inLevel; }
        }

        /// <summary>
        /// Asked by the gate before the game leaves the screen it is on. True means the mod handled the
        /// call and the native navigation must not run, which happens to exactly one case: a peer's own
        /// pick. The host's call is answered with a broadcast and then left alone.
        /// </summary>
        public static bool TryCaptureStart(int levelType, int levelNumber, int id, string name)
        {
            if (_room == null || ApiBase.IsBroken(Capability))
            {
                return false;
            }

            bool roomDecides = _room.RoomDecides;

            LevelAnswer answer = LevelAnswer.Decide(roomDecides, _room.IsHost, _applying);

            if (answer.Refusal != null)
            {
                Log.Warn("mp", "level " + Describe(levelType, levelNumber) + " is not started here: "
                    + answer.Refusal + " " + ApiBase.FrameTag());

                return true;
            }

            if (!answer.TellRoom)
            {
                return false;
            }

            LevelStartPayload start = new LevelStartPayload
            {
                LevelType = levelType,
                LevelNumber = levelNumber,
                LevelId = id,
                LevelName = name
            };

            string unshareable = LevelAnswer.Unshareable(name, Protocol.MaxTextChars);
            if (unshareable != null)
            {
                Log.Error("mp", "the room is not told about level " + Describe(levelType, levelNumber) + ": "
                    + unshareable + " - the host plays on alone and the peers stay out");

                return false;
            }

            ForgetTheLawn();
            _inLevel = true;
            _hostsOrder = start;

            Log.Info("mp", "the host starts level " + Describe(levelType, levelNumber) + " (id=" + id
                + " name='" + name + "'), the room is told " + ApiBase.FrameTag());

            _room.BroadcastGameplay(MsgAction.LevelStart, start);

            return false;
        }

        /// <summary>
        /// What a seat that arrived in the middle of a level is told: the host's own order, once more, to
        /// that peer alone. The game loads it the same way the broadcast loaded it for everybody else, so
        /// the newcomer ends up standing on the room's lawn with the rest; what is on that lawn arrives
        /// after, through the censuses and the values the room keeps.
        /// </summary>
        public static void Resync(int peerId)
        {
            LevelStartPayload order = _hostsOrder;
            if (order == null || _room == null || !_room.IsHost)
            {
                return;
            }

            Log.Info("mp", "peer " + peerId + " joins the level the room is already in: " + Describe(order)
                + " " + ApiBase.FrameTag());

            _room.SendToPeer(peerId, MsgAction.LevelStart, order);
        }

        /// <summary>
        /// Asked by the restart gate before the pause menu's restart button runs anything. In a room only
        /// the host's press is the room's press: the game's restart destroys this end's board long before
        /// its own EnterGame call reaches the level gate, so a peer that was let through would take the
        /// room's lawn down and have nothing to put in its place.
        /// </summary>
        public static bool TryCaptureRestart()
        {
            if (_room == null || ApiBase.IsBroken(Capability) || !_room.RoomDecides || _room.IsHost)
            {
                return false;
            }

            Log.Warn("mp", "a restart is not run here: only the host restarts the lawn " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>
        /// Asked by the round gate before the game moves a survival level to its next round. The game's own
        /// transition destroys this end's board and then calls EnterGame for the same level, which the level
        /// gate refuses to a peer - so a peer that was let through would be left standing on a dead board.
        /// The host's transition is the room's: its EnterGame is heard by the level gate and the next round
        /// reaches the peers as a `LevelStart`, which takes their old lawn down and loads the host's.
        /// Proof: ref/EVIDENCE.md E67.
        /// </summary>
        public static bool TryCaptureNextRound(string door)
        {
            if (_room == null || ApiBase.IsBroken(Capability) || !_room.RoomDecides || _room.IsHost)
            {
                return false;
            }

            Log.Warn("mp", "the game's next round is not run here (" + door + "): the host decides when"
                + " the lawn moves to it " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>
        /// Asked by the main-menu gate before the game leaves the level. A host that is standing in the
        /// room's lawn tells every peer first; the peers take their own lawn down and follow, because a
        /// room with one lawn left is not a room. The host's own navigation is left alone.
        /// </summary>
        public static bool TryCaptureMenuExit()
        {
            if (_room == null || ApiBase.IsBroken(Capability) || !_room.RoomDecides || !_room.IsHost
                || !_inLevel)
            {
                return false;
            }

            _inLevel = false;
            _hostsOrder = null;
            Log.Info("mp", "the host leaves the level for the main menu, the room follows "
                + ApiBase.FrameTag());

            _room.BroadcastGameplay(MsgAction.LevelLeave, null);

            return false;
        }

        /// <summary>Where the level frames arrive: WorldSync forwards what is not a plant or the pool.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.LevelStart:
                    ApplyStart(peerId, Protocol.Payload<LevelStartPayload>(frame));
                    break;

                case MsgAction.LevelReady:
                    ApplyReady(peerId, Protocol.Payload<LevelReadyPayload>(frame));
                    break;

                case MsgAction.BattleStart:
                    ApplyBattle(peerId);
                    break;

                case MsgAction.LevelLeave:
                    ApplyLeave(peerId);
                    break;

                case MsgAction.GameOver:
                    EndGate.HandleFrame(peerId, frame);
                    break;

                default:
                    Log.Warn("mp", "no one handles " + frame.Name + " from peer " + peerId + " yet");
                    break;
            }
        }

        /// <summary>
        /// Asked by the Let's Rock gate before the chooser's start button runs. This is the press the room
        /// actually plays: `StartGameBtn.OnMouseUp` saves the chosen cards, pays the chosen buff and sets
        /// `InitBoard.ready`, which is what ends the seed chooser and starts the lawn. Proof:
        /// ref/EVIDENCE.md E17b.
        ///
        /// Inside a room the press is a seat's word about itself and not a start: a peer's press says
        /// ready and stops, and the host's press runs only when no seat is still waiting. That is the one
        /// place the room waits for its players, and it is why the button is blocked rather than the
        /// lawn started and corrected afterwards - a lawn that began twice cannot be unbegun.
        /// </summary>
        public static bool TryCaptureLetsRock()
        {
            if (_room == null || ApiBase.IsBroken(Capability))
            {
                return false;
            }

            BattleAnswer answer = BattleAnswer.DecideLetsRock(_room.RoomDecides, _room.IsHost,
                _battleApplying, _room.Roster.SeatsNotReady());

            if (answer.MarksReady)
            {
                // The press is a seat's word about itself, and a word about a state: a seat that is already
                // standing ready is taking that word back. What the seat believes about itself is the
                // roster's answer and nothing this end keeps, so the second press cannot mean something
                // other than what the first one said.
                bool ready = !ThisSeatIsReady();

                _room.TellHostIAmReady(ready);

                Log.Info("mp", "this seat presses Let's Rock and is " + (ready ? "ready" : "not ready again")
                    + "; " + _room.Roster.SeatsNotReady() + " seat(s) still are not "
                    + ApiBase.FrameTag());

                return true;
            }

            if (answer.Refusal != null)
            {
                Log.Warn("mp", "Let's Rock is not pressed here: " + answer.Refusal + " " + ApiBase.FrameTag());

                return true;
            }

            if (!answer.TellRoom)
            {
                return false;
            }

            Log.Info("mp", "the host presses Let's Rock, the room is told " + ApiBase.FrameTag());
            _room.BroadcastGameplay(MsgAction.BattleStart, null);

            // The room began: every seat's ready was for this lawn, so the next chooser waits for them again.
            _room.ClearReadiness();

            return false;
        }

        /// <summary>
        /// Whether this end's own seat stands ready in the roster the host last sent. The chooser's button
        /// is a toggle, so this is the question that decides what the next press means, and the answer is
        /// the room's word rather than a flag this end would have to keep in step with it.
        /// </summary>
        private static bool ThisSeatIsReady()
        {
            RosterEntry entry = _room.Roster == null ? null : _room.Roster.Find(_room.LocalPeerId);

            return entry != null && entry.Ready;
        }

        /// <summary>
        /// Asked by the battle gate before the game lets the waves run. Only one answer blocks anything: a
        /// seat of its own pressing ready. The host's press is broadcast and then left alone, because the
        /// button has to keep doing everything the game hides inside it.
        ///
        /// The number is the button the game was about to run, which the gate already checked against
        /// BattleAnswer.StartBattleButton; it is carried here so the log says which press the room took over.
        /// </summary>
        public static bool TryCaptureBattle(int buttonNumber)
        {
            if (_room == null || ApiBase.IsBroken(Capability))
            {
                return false;
            }

            BattleAnswer answer = BattleAnswer.Decide(_room.RoomDecides, _room.IsHost, _battleApplying);

            if (answer.Refusal != null)
            {
                Log.Warn("mp", "the battle is not started here (button " + buttonNumber + "): "
                    + answer.Refusal + " " + ApiBase.FrameTag());
                return true;
            }

            if (!answer.TellRoom)
            {
                return false;
            }

            Log.Info("mp", "the host starts the battle, the room is told " + ApiBase.FrameTag());
            _room.BroadcastGameplay(MsgAction.BattleStart, null);

            return false;
        }

        /// <summary>
        /// The host saying the lawn moves. This end runs its own copy of the same press, under the flag that
        /// stops its own gate from refusing the order it was given.
        ///
        /// The chooser's LET'S ROCK button is pressed when it is up, because that press is what the host
        /// really clicked and the game's own body is what saves the cards, pays the buff and sets
        /// `InitBoard.ready`. When the chooser is still loading, the press is kept and played on Tick; the
        /// in-level ready button remains as the other screens' door. Either way the mod copies a press, it
        /// does not write the game's fields. Proof: ref/EVIDENCE.md E17b.
        /// </summary>
        private static void ApplyBattle(int peerId)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to start the battle: only the host says when"
                    + " the waves begin");

                return;
            }

            _battleApplying = true;

            try
            {
                Il2Cpp.StartGameBtn rock = LetsRock();

                if (ApiBase.Present(rock))
                {
                    bool played = ApiBase.TryCall("StartGameBtn.OnMouseUp",
                        () => rock.OnMouseUp(), Capability);

                    Log.Info("mp", "the host's Let's Rock is played here: " + DescribeRock(played)
                        + " " + ApiBase.FrameTag());

                    return;
                }

                InGameUI screen = ApiBase.Query("FindInGameScreen",
                    () => ApiBase.Present(InGameUI.Instance) ? InGameUI.Instance : null, Capability);

                if (!ApiBase.Present(screen))
                {
                    _rockPending = true;

                    Log.Info("mp", "the host pressed Let's Rock but this end's chooser is not up yet:"
                        + " the press is kept and played as soon as the button appears "
                        + ApiBase.FrameTag());

                    return;
                }

                Il2Cpp.InGameBtn button = StartBattleButton(screen);

                bool pressed = ApiBase.Present(button)
                    ? ApiBase.TryCall("InGameBtn.OnMouseUpAsButton(4)",
                        () => button.OnMouseUpAsButton(), Capability)
                    : ApiBase.TryCall("InGameUI.OnStartBattleButtonClick", () =>
                        screen.OnStartBattleButtonClick(), Capability);

                Log.Info("mp", "the host's battle start is played here: " + DescribePress(button, pressed)
                    + " " + ApiBase.FrameTag());
            }
            finally
            {
                _battleApplying = false;
            }
        }

        /// <summary>The chooser's LET'S ROCK button, when this screen has one up; null in a loading screen.</summary>
        private static Il2Cpp.StartGameBtn LetsRock()
        {
            return ApiBase.Query("FindLetsRock",
                () => UnityEngine.Object.FindObjectOfType<Il2Cpp.StartGameBtn>(), Capability);
        }

        /// <summary>
        /// The host's press that arrived before this end could run it. Played once the chooser's button
        /// exists, under the same flag that keeps the gate from refusing the order it was given.
        /// </summary>
        private static void PressPendingRock()
        {
            Il2Cpp.StartGameBtn rock = LetsRock();

            if (!ApiBase.Present(rock))
            {
                return;
            }

            _rockPending = false;
            _battleApplying = true;

            try
            {
                bool played = ApiBase.TryCall("StartGameBtn.OnMouseUp",
                    () => rock.OnMouseUp(), Capability);

                Log.Info("mp", "the host's Let's Rock is played here at last: " + DescribeRock(played)
                    + " " + ApiBase.FrameTag());
            }
            finally
            {
                _battleApplying = false;
            }
        }

        private static string DescribeRock(bool played)
        {
            return played ? "the game's own start button ran" : "the game threw";
        }

        /// <summary>The ready button of this screen, or null when the screen does not carry it on its object.</summary>
        private static Il2Cpp.InGameBtn StartBattleButton(InGameUI screen)
        {
            UnityEngine.GameObject start = screen.StartBattle;
            if (!ApiBase.Present(start))
            {
                return null;
            }

            Il2Cpp.InGameBtn button = start.GetComponent<Il2Cpp.InGameBtn>();

            return ApiBase.Present(button) && BattleAnswer.IsStartButton(button.buttonNumber) ? button : null;
        }

        private static string DescribePress(Il2Cpp.InGameBtn button, bool pressed)
        {
            if (!pressed)
            {
                return "the game threw";
            }

            return button == null
                ? "the game's own battle method ran (no ready button on this screen)"
                : "the game's own ready button ran";
        }

        /// <summary>Run once per frame: the only place that can learn a peer's new lawn is up.</summary>
        public static void Tick()
        {
            if (_room == null || !_room.IsActive)
            {
                return;
            }

            if (_rockPending)
            {
                PressPendingRock();
            }

            if (_waiting == null)
            {
                return;
            }

            Board lawn = Entities.Board();
            if (lawn == null || lawn.GetInstanceID() == _lawnBefore)
            {
                return;
            }

            LevelStartPayload order = _waiting;
            _waiting = null;
            _inLevel = true;

            int levelNumber = App.ReadBoardLevel();
            int levelType = App.ReadBoardType();

            Log.Info("mp", "the host's level " + Describe(order) + " is up here as "
                + Describe(levelType, levelNumber) + " " + ApiBase.FrameTag());

            if (levelNumber != order.LevelNumber || levelType != order.LevelType)
            {
                Log.Warn("mp", "this lawn is not the level the host named: the game's own fields disagree, "
                    + "so the two ends are not in the same place");
            }

            _room.SendToHost(MsgAction.LevelReady, new LevelReadyPayload
            {
                LevelType = levelType,
                LevelNumber = levelNumber
            });
        }

        private static void ApplyStart(int peerId, LevelStartPayload start)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to start level " + Describe(start)
                    + ": only the host decides what is loaded");
                return;
            }

            ForgetTheLawn();

            Board oldLawn = Entities.Board();

            if (oldLawn != null)
            {
                TakeDownTheLawn(oldLawn, "loading the host's level " + Describe(start));
            }

            LoadTheHostsLevel(start);
        }

        /// <summary>
        /// The host's word that it left the level. The peer does what the game does on its own way out:
        /// takes the board down and asks the game's menu to come up. EnterMainMenu alone only moves the
        /// UI and the clock, so the plants have to be destroyed first, exactly as on a restart.
        /// </summary>
        private static void ApplyLeave(int peerId)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to end the room's level: only the host leaves it");
                return;
            }

            _waiting = null;
            _inLevel = false;

            Board lawn = Entities.Board();
            if (lawn != null)
            {
                TakeDownTheLawn(lawn, "leaving the room's level");
            }

            ForgetTheLawn();

            bool left = ApiBase.TryCall("UIMgr.EnterMainMenu", () => UIMgr.EnterMainMenu(), Capability);

            Log.Info("mp", "the host left the level: this lawn is taken down and the main menu is asked for"
                + (left ? string.Empty : "; the game refused the menu") + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// The game's own restart, replayed on a lawn this end is standing in: the level save is cleared,
        /// the menus of the old screen are popped and the board object is destroyed, which is what takes
        /// the plants down. EnterGame alone cannot do it - the game tears a board down on the way out of a
        /// level, not on the way in - and going through the main menu does not either: the real peer log
        /// left all 10 plants standing after EnterMainMenu. The sequence is the one PauseMenu_Btn.Restart
        /// runs on the host. Proof: ref/EVIDENCE.md E16.
        /// </summary>
        private static void TakeDownTheLawn(Board lawn, string next)
        {
            int plants = BoardCollections.PlantInstanceIds().Length;
            int oldType = App.ReadBoardType();
            int oldLevel = App.ReadBoardLevel();

            ApiBase.TryCall("Board.ClearTheBoard", () => lawn.ClearTheBoard(), Capability);

            ApiBase.TryCall("UIResourcesLoader.PopAll", () =>
            {
                UIResourcesLoader loader = Menu.Loader();
                if (loader != null)
                {
                    loader.PopAll();
                }
            }, Capability);

            bool destroyed = ApiBase.TryCall("Board.Die", () => lawn.Die(), Capability);

            Log.Info("mp", "the old lawn of " + Describe(oldType, oldLevel) + " with " + plants
                + " plant(s) is taken down the way the game's restart does it (ClearTheBoard, PopAll, Die),"
                + " " + next
                + (destroyed ? string.Empty : "; the board destroy failed, the old plants may still stand")
                + " " + ApiBase.FrameTag());
        }

        /// <summary>The host's level, asked of the game's own navigation, with the gate told to let it pass.</summary>
        private static void LoadTheHostsLevel(LevelStartPayload start)
        {
            _waiting = start;
            _applying = true;

            try
            {
                bool loaded = ApiBase.TryCall("UIMgr.EnterGame(" + Describe(start) + ")", () =>
                    UIMgr.EnterGame((LevelType)start.LevelType, start.LevelNumber, start.LevelId,
                        start.LevelName), Capability);

                if (!loaded)
                {
                    _waiting = null;
                    Log.Error("mp", "the game refused to load the host's level " + Describe(start)
                        + " on this lawn");
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private static void ApplyReady(int peerId, LevelReadyPayload ready)
        {
            RosterEntry entry = _room.Roster.Find(peerId);

            Log.Info("mp", "peer " + peerId + " (" + (entry == null ? "gone" : entry.Nick) + ") is in "
                + Describe(ready.LevelType, ready.LevelNumber) + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// A new level takes the old lawn's entities with it. Both ends do this at the same moment - the
        /// host before it mints ids again, the peer before it adopts them - so the numbering restarts
        /// together, and a wish asked about a cell of the previous level is forgotten with it.
        /// </summary>
        private static void ForgetTheLawn()
        {
            _inLevel = false;
            _room.Entities.Clear();

            // A ready was pressed for the lawn that is gone, so the next chooser waits for every seat again.
            _room.ClearReadiness();

            WorldSync.ForgetWishes();
            ZombieSync.ForgetTheLawn();
            PlantSync.ForgetTheLawn();
            GraveSync.ForgetTheLawn();
            TrophySync.ForgetTheLawn();
            PreviewSync.ForgetTheLawn();
            PlantSelect.ForgetTheLawn();
            RewardSync.ForgetTheLawn();
            SlowSync.ForgetTheLawn();
            EffectSync.ForgetTheLawn();
            HammerSync.ForgetTheLawn();
            EndGate.ForgetTheLawn();

            Board lawn = Entities.Board();
            _lawnBefore = lawn == null ? 0 : lawn.GetInstanceID();
        }

        private static string Describe(LevelStartPayload start)
        {
            return Describe(start.LevelType, start.LevelNumber);
        }

        private static string Describe(int levelType, int levelNumber)
        {
            return "type " + levelType + " level " + levelNumber;
        }
    }
}
