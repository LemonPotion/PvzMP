using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The hooks behind GraveSync. Three of them stop the game on a peer and one only listens on the host:
    ///
    /// - `Board.SetGrave` lays the level's graves and `Board.SupplyGrave` lays the one a wave delivers. Both
    ///   roll their cells, so a peer skips them and waits for the host's layout.
    /// - `Grave.SetZombie` is the opening: it starts the animation that rolls which zombie walks out. A peer
    ///   skips it, because the zombie it would roll is a second answer to the host's question.
    /// - `GridItem.SetGridItem` is the door every one of those rolls ends in, and the only place a host grave
    ///   can be noticed the frame it appears. Its postfix asks for the layout on the next frame rather than
    ///   in a second.
    ///
    /// Every prefix answers through GraveSync and returns true on any throw, so a broken hook leaves the
    /// game doing what it did before instead of freezing it.
    /// </summary>
    public static class GraveGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.graves").PatchAll(typeof(GraveGate));
                Log.Info("hook", "grave cells and grave openings are mirrored from the host "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(GraveSync.Capability, "the grave hooks could not be installed");
                Log.Error("hook", "gating the graves failed: " + e.GetType().Name + " " + e.Message
                    + " - every end rolls its own graves again and the lawns drift apart");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Board), "SetGrave")]
        public static class LevelGraves
        {
            private static bool Prefix()
            {
                try
                {
                    return GraveSync.MayRollGraves("level's graves");
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the grave lay gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game lay its graves itself");

                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Board), "SupplyGrave")]
        public static class WaveGrave
        {
            private static bool Prefix()
            {
                try
                {
                    return GraveSync.MayRollGraves("wave's grave");
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the wave grave gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game supply its own grave");

                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Grave), "SetZombie")]
        public static class GraveOpening
        {
            private static bool Prefix()
            {
                try
                {
                    return GraveSync.MayOpenGrave();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the grave opening gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting this grave open itself");

                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.GridItem), "SetGridItem")]
        public static class GraveMint
        {
            private static void Postfix()
            {
                try
                {
                    GraveSync.NoteGraveRolled();
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the grave watch threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
