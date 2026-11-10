using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The mod's second detour, on the game's own way into a level: every start the player can reach -
    /// the level menu, a save, the pause menu's restart - ends in this one public static call, so gating
    /// it is what makes "the host starts the level, the peers load that level" possible at all. A prefix
    /// that returns false is the only thing that keeps a peer out of a lawn of its own choosing.
    ///
    /// Installed once at boot and left in, exactly like the placement gate: there is no window where a
    /// click could be half-gated while a session starts up.
    /// </summary>
    public static class LevelGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.level").PatchAll(typeof(LevelGate));
                Log.Info("hook", "UIMgr.EnterGame is gated " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(LevelSync.Capability, "the level gate could not be installed");
                Log.Error("hook", "gating UIMgr.EnterGame failed: " + e.GetType().Name + " " + e.Message
                    + " - a peer can start its own level and the room ends up with two different lawns");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.UIMgr), "EnterGame")]
        public static class EnterGame
        {
            /// <summary>The original arguments, by name: levelType, levelNumber, id, name (dump.cs:93042).</summary>
            private static bool Prefix(Il2Cpp.LevelType levelType, int levelNumber, int id, string name)
            {
                try
                {
                    return !LevelSync.TryCaptureStart((int)levelType, levelNumber, id, name);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the level gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game load the level itself");
                    return true;
                }
            }
        }

        /// <summary>
        /// The one door out of a level that the host's own navigation uses. The gate only listens: a host
        /// standing in the room's lawn tells the peers, and the game's own menu call still runs.
        /// </summary>
        [HarmonyPatch(typeof(Il2Cpp.UIMgr), "EnterMainMenu")]
        public static class EnterMainMenu
        {
            private static bool Prefix()
            {
                try
                {
                    return !LevelSync.TryCaptureMenuExit();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the menu gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game leave the level alone");
                    return true;
                }
            }
        }
    }
}
