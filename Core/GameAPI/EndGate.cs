using System;
using HarmonyLib;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The end of a lawn. The game opens its lose screen through one door, `UIMgr.EnterLoseMenu`, from
    /// every lose path. In a room that door is the host's alone: a peer whose own lawn breaks through
    /// keeps standing, and the screen opens here only when the host's `GameOver` frame arrives and this
    /// end runs the same door the game would have run - so the lawn ends once, at the host's word,
    /// instead of each seat ending its own copy early.
    ///
    /// What the game does inside the door is untouched: the statistics, the time scale and the audio
    /// pause all still belong to it. The ghosts the cursor preview left on the lawn are taken down by
    /// the same postfix as before. Proof: ref/EVIDENCE.md E64.
    /// </summary>
    public static class EndGate
    {
        public const string Capability = "mp-end";

        private static MpSession _room;

        /// <summary>True only while this end runs the lose screen the host ordered.</summary>
        private static bool _applying;

        /// <summary>True once the host's word has been sent, or taken here. Reset with the lawn.</summary>
        private static bool _settled;

        /// <summary>Set by the prefix that refused the menu, so the postfix does not take the ghosts down.</summary>
        private static bool _stopped;

        /// <summary>True once this end has said it is waiting for the host, so the wait is printed once.</summary>
        private static bool _toldWaiting;

        /// <summary>Told by the boot: the room that decides when the room's lawn has ended.</summary>
        public static void Attach(MpSession room)
        {
            _room = room;

            Install();
        }

        private static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.end").PatchAll(typeof(EndGate));
                Log.Info("hook", "the end of the lawn is the host's: the lose screen waits for its word "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "watching the lose screen failed: " + e.GetType().Name + " " + e.Message
                    + " - the room behaves as before: every seat ends its own lawn");
            }
        }

        /// <summary>
        /// Asked by the lose screen's prefix. True means the menu must not open here: a peer waits for
        /// the host's word. The host's own screen runs, and its postfix tells the room.
        /// </summary>
        public static bool TryCaptureLose()
        {
            _stopped = false;

            if (_applying || _room == null || !_room.RoomDecides || ApiBase.IsBroken(Capability)
                || _room.IsHost)
            {
                return false;
            }

            _stopped = true;

            if (!_toldWaiting)
            {
                _toldWaiting = true;

                Log.Warn("mp", "this lawn is lost here, but the lose screen waits: the host decides when"
                    + " the room's lawn has ended " + ApiBase.FrameTag());
            }

            return true;
        }

        /// <summary>
        /// The screen really opened. The host tells the room once; the peers open their own screen when
        /// that word arrives, and their own door is not told again.
        /// </summary>
        private static void TheScreenOpened(string reason)
        {
            if (_stopped)
            {
                _stopped = false;
                return;
            }

            PreviewSync.TheLevelWasLost(reason);

            if (_room == null || !_room.RoomDecides || !_room.IsHost || _settled
                || ApiBase.IsBroken(Capability))
            {
                return;
            }

            _settled = true;

            Log.Info("mp", "this lawn has ended, the room is told " + ApiBase.FrameTag());

            _room.BroadcastGameplay(MsgAction.GameOver, new GameOverPayload
            {
                Won = false,
                Reason = reason
            });
        }

        /// <summary>The host's word, opened here through the game's own door.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Action != MsgAction.GameOver)
            {
                Log.Warn("mp", "no one handles " + frame.Name + " from peer " + peerId + " yet");
                return;
            }

            GameOverPayload over = Protocol.Payload<GameOverPayload>(frame);

            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to end the room: only the host decides when"
                    + " the lawn has ended");
                return;
            }

            if (_settled)
            {
                return;
            }

            _settled = true;
            _applying = true;

            try
            {
                Log.Info("mp", "the host says the lawn has ended ('" + over.Reason + "'), the lose screen"
                    + " is opened here " + ApiBase.FrameTag());

                bool opened = ApiBase.TryCall("UIMgr.EnterLoseMenu", () =>
                    Il2Cpp.UIMgr.EnterLoseMenu(over.Reason), Capability);

                if (!opened)
                {
                    Log.Error("mp", "the game refused the host's lose screen on this lawn");
                }
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>A new lawn owes nothing to the old end: the next end is the host's word again.</summary>
        public static void ForgetTheLawn()
        {
            _applying = false;
            _settled = false;
            _stopped = false;
            _toldWaiting = false;
        }

        [HarmonyPatch(typeof(Il2Cpp.UIMgr), "EnterLoseMenu")]
        public static class EnterLoseMenu
        {
            private static bool Prefix(string reason)
            {
                try
                {
                    return !TryCaptureLose();
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the lose screen gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game open the screen");
                    return true;
                }
            }

            private static void Postfix(string reason)
            {
                try
                {
                    TheScreenOpened(reason);
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "taking the ghosts down on the lose screen threw: "
                        + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
