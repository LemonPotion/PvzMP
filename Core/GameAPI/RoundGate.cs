using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The survival level's round transition. `Board.EnterNextRound` is the game's own "more zombies are
    /// coming" step: it shows the line, pays the travel coins and schedules `StartNextRound` by name. That
    /// scheduled body destroys the board (`Board.Die`) and then calls `UIMgr.EnterGame` for the same level,
    /// which is the step the level gate refuses to a peer.
    ///
    /// Refusing only EnterGame is too late here: by then the peer's board is already dead, and every grave
    /// read, every zombie the host sends and every particle call fails on a lawn whose creator object is
    /// gone. A live pair run showed exactly that - the peer's level picture changed, 48 zombie spawns threw
    /// NullReferenceException inside `CreateZombie.SetTransform`, and mp-effects was switched off. So the
    /// peer's transition is stopped at both doors; the host's own transition runs in full and its EnterGame
    /// is the `LevelStart` the room follows. Proof: ref/EVIDENCE.md E67.
    ///
    /// Installed once at boot and left in, like every other gate.
    /// </summary>
    public static class RoundGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.round").PatchAll(typeof(RoundGate));
                Log.Info("hook", "the survival round transition is gated on the room " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Error("hook", "gating the survival round transition failed: " + e.GetType().Name + " "
                    + e.Message + " - a peer that reaches the next round takes its own lawn down and"
                    + " loads nothing");
            }
        }

        private static bool Captured(string door)
        {
            try
            {
                return LevelSync.TryCaptureNextRound(door);
            }
            catch (Exception e)
            {
                Log.Error("hook", "the round gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game run the transition itself");

                return false;
            }
        }

        /// <summary>The step that schedules the transition; refusing it keeps the peer from paying its coins.</summary>
        [HarmonyPatch(typeof(Il2Cpp.Board), "EnterNextRound")]
        public static class EnterNextRound
        {
            private static bool Prefix()
            {
                return !Captured("EnterNextRound");
            }
        }

        /// <summary>The scheduled step that destroys the board; refused even if it was reached another way.</summary>
        [HarmonyPatch(typeof(Il2Cpp.Board), "StartNextRound")]
        public static class StartNextRound
        {
            private static bool Prefix()
            {
                return !Captured("StartNextRound");
            }
        }
    }
}
