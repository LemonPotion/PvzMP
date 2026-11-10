using System;
using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The pause of a room. The game freezes a screen with `UIMgr.EnterPauseMenu` (status Pause,
    /// `Time.timeScale = 0`, the pause menu pushed) and unfreezes it with `UIMgr.BackToGame` (pop,
    /// time back, status InGame). The room makes those two the whole room's: any seat may pause, and only
    /// the host may lift it.
    ///
    /// A seat's own press always runs the game's menu first, so pausing never waits for a round trip; the
    /// pauser then tells the host. The host runs the same menu on its own screen and tells everyone else,
    /// and an end that is already in the state the order names does nothing, which is how the pauser's own
    /// copy of the order is harmless. A peer's resume is refused with the menu still up: one lawn moving
    /// while the others are frozen is the state this class exists to prevent.
    ///
    /// Only a lawn is paused. A pause pressed in a menu or the garden has no board to share and keeps the
    /// game's own behavior. Proof: ref/EVIDENCE.md E17.
    /// </summary>
    public static class PauseSync
    {
        public const string Capability = "mp-pause";

        private static MpSession _room;

        /// <summary>True only while this end runs an order, so its own gate does not send that order back.</summary>
        private static bool _applying;

        public static void Attach(MpSession room)
        {
            _room = room;
            PauseGate.Install();

            Log.Info("mp", "the pause is gated on the room: any seat may pause it and only the host lifts it "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the pause gate before the game freezes the screen. The native call always runs - this
        /// method only lets the room hear about it. Outside a room, before a seat, or off a lawn it does
        /// nothing, which leaves a single player's pause exactly as the game made it.
        /// </summary>
        public static bool TryCapturePause()
        {
            if (!TheRoomDecides() || _applying || Entities.Board() == null)
            {
                return false;
            }

            if (_room.IsHost)
            {
                Log.Info("mp", "the host pauses the room, everyone is told " + ApiBase.FrameTag());
                _room.BroadcastGameplay(MsgAction.PauseState, State(true));

                return false;
            }

            Log.Info("mp", "this end pauses and asks the host to pause the room too " + ApiBase.FrameTag());
            _room.SendToHost(MsgAction.PauseRequest, null);

            return false;
        }

        /// <summary>
        /// Asked by the resume gate before the game pops the pause menu. A peer's press is refused while
        /// the room is paused; when it is not paused there is nothing to keep frozen, so the game's own
        /// call is left alone.
        /// </summary>
        public static bool TryCaptureResume()
        {
            if (!TheRoomDecides() || _applying || Entities.Board() == null || !Paused())
            {
                return false;
            }

            if (_room.IsHost)
            {
                Log.Info("mp", "the host lifts the room's pause, everyone is told " + ApiBase.FrameTag());
                _room.BroadcastGameplay(MsgAction.PauseState, State(false));

                return false;
            }

            Log.Warn("mp", "the pause is not lifted here: only the host lifts the room's pause "
                + ApiBase.FrameTag());

            return true;
        }

        /// <summary>Where the pause frames arrive; WorldSync forwards what is neither a plant nor the pool.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.PauseRequest:
                    ApplyRequest(peerId);
                    break;

                case MsgAction.PauseState:
                    ApplyState(peerId, Protocol.Payload<PauseStatePayload>(frame));
                    break;
            }
        }

        /// <summary>
        /// A peer saying it pressed pause. The host freezes its own screen the same way and tells the rest;
        /// the sender already froze itself, so its own copy of the order is skipped by the state check.
        /// </summary>
        private static void ApplyRequest(int peerId)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null)
            {
                Log.Warn("mp", "peer " + peerId + " asked to pause but is not in the roster");
                return;
            }

            if (!_room.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " asked to pause, but this end is not the host");
                return;
            }

            if (Paused())
            {
                return;
            }

            if (Entities.Board() == null)
            {
                Log.Warn("mp", "peer " + peerId + " asked to pause, but this end has no lawn");
                return;
            }

            Freeze("peer " + peerId + " (" + entry.Nick + ") pressed pause");
            _room.BroadcastGameplay(MsgAction.PauseState, State(true));
        }

        /// <summary>The host's word on the room's pause, run on this screen unless it is already there.</summary>
        private static void ApplyState(int peerId, PauseStatePayload state)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to set the room's pause: only the host says when"
                    + " the room is paused");
                return;
            }

            if (Entities.Board() == null)
            {
                Log.Warn("mp", "the host " + (state.Paused ? "paused" : "resumed") + " the room, but this end"
                    + " has no lawn: the order is ignored");
                return;
            }

            if (state.Paused == Paused())
            {
                return;
            }

            if (state.Paused)
            {
                Freeze("the host paused the room");
            }
            else
            {
                Unfreeze("the host lifted the room's pause");
            }
        }

        private static void Freeze(string why)
        {
            _applying = true;

            try
            {
                ApiBase.TryCall("UIMgr.EnterPauseMenu", () => UIMgr.EnterPauseMenu(), Capability);
            }
            finally
            {
                _applying = false;
            }

            Log.Info("mp", "the room is paused: " + why + " " + ApiBase.FrameTag());
        }

        private static void Unfreeze(string why)
        {
            _applying = true;

            try
            {
                // The game's BackToGame pops exactly one screen: a peer sitting in a pause submenu
                // would come back to that submenu, still frozen. PopTo(PauseMenu) was tried and is too
                // much - it removes the pause menu itself, and then BackToGame pops the in-game UI, which
                // is the disappearing HUD the live run showed. So only the screens ABOVE the pause menu
                // are popped; the pause menu stays on top and BackToGame removes it as it always does.
                int popped = 0;

                ApiBase.TryCall("pop pause submenus", () =>
                {
                    Il2Cpp.PauseMenu pause = Il2Cpp.PauseMenu.Instance;
                    int stop = ApiBase.Present(pause) ? pause.GetInstanceID() : 0;

                    popped = Menu.PopAbove(stop, 8);
                }, Capability);

                Log.Info("mp", "the pause stack is taken down to the pause menu: " + popped
                    + " submenu(s) closed " + ApiBase.FrameTag());

                ApiBase.TryCall("UIMgr.BackToGame", () => UIMgr.BackToGame(), Capability);
            }
            finally
            {
                _applying = false;
            }

            Log.Info("mp", "the room moves again: " + why + " " + ApiBase.FrameTag());
        }

        /// <summary>The game's own pause flag, written by EnterPauseMenu and cleared by BackToGame.</summary>
        private static bool Paused()
        {
            return App.ReadGameStatus() == (int)Il2Cpp.GameStatus.Pause;
        }

        private static bool TheRoomDecides()
        {
            return _room != null && !ApiBase.IsBroken(Capability) && _room.RoomDecides;
        }

        private static PauseStatePayload State(bool paused)
        {
            return new PauseStatePayload { Paused = paused };
        }
    }
}
