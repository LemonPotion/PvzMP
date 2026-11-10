using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The two doors of the room's pause. Pausing is `UIMgr.EnterPauseMenu` - the game's own Pause status,
    /// `Time.timeScale = 0` and the pause menu pushed - and every seat may press it: the pauser's menu opens
    /// at once and the host is told. Resuming is `UIMgr.BackToGame`, and it is the host's alone: one peer
    /// popping the pause menu would set one lawn moving while the others stayed frozen.
    ///
    /// Both are the doors the game itself uses, so two detours cover every entry instead of one per button:
    /// `EnterPauseMenu` is called by `InGameUI.PauseGame`, `Mouse.Update` and the menu screens, and
    /// `BackToGame` by the pause menu's continue button and its own Escape/Space handling.
    /// Proof: ref/EVIDENCE.md E17.
    /// </summary>
    public static class PauseGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.pause").PatchAll(typeof(PauseGate));
                Log.Info("hook", "UIMgr.EnterPauseMenu and UIMgr.BackToGame are gated " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                // The capability stays usable: receiving a pause order does not need this patch, and a
                // broken end is better frozen with the room than moving alone.
                Log.Error("hook", "gating the pause failed: " + e.GetType().Name + " " + e.Message
                    + " - this end's own pause stays local, though orders from the host are still obeyed");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.UIMgr), "EnterPauseMenu")]
        public static class EnterPauseMenu
        {
            private static bool Prefix()
            {
                try
                {
                    return !PauseSync.TryCapturePause();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the pause gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game pause this screen alone");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.UIMgr), "BackToGame")]
        public static class BackToGame
        {
            private static bool Prefix()
            {
                try
                {
                    return !PauseSync.TryCaptureResume();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the resume gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game resume this screen alone");
                    return true;
                }
            }
        }
    }
}
