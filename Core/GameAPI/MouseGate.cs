using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The mod's one detour so far, and the reason Stage 4 can be host-authoritative at all: the game
    /// places a plant from a card inside `Mouse.TryToSetPlantByCard`, so a peer that is in a room has to
    /// be stopped there rather than after the fact. The prefix asks the world sync first and runs the
    /// original only when the sync says the request is not it to make - which is every frame of a
    /// single-player game, where this code changes nothing.
    ///
    /// The patch is installed once at boot and stays, so there is no window where a click could slip
    /// through half-installed while a session is starting up.
    /// </summary>
    public static class MouseGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.mouse").PatchAll(typeof(MouseGate));
                Log.Info("hook", "Mouse.TryToSetPlantByCard is gated " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(WorldSync.Capability, "the placement gate could not be installed");
                Log.Error("hook", "gating Mouse.TryToSetPlantByCard failed: " + e.GetType().Name + " "
                    + e.Message + " - a click inside a room will plant locally and the lawns will differ");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "TryToSetPlantByCard")]
        public static class TryToSetPlantByCard
        {
            private static bool Prefix()
            {
                try
                {
                    return !WorldSync.TryCapturePlacement();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the placement gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game place the plant itself");
                    return true;
                }
            }
        }
    }
}
