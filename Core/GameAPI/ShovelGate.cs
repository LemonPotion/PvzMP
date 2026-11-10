using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The second gate on the local player's hands. Only <c>Shovel.Use</c> is gated: the dump and the
    /// disassembly say key "1" answers through <c>OnClick</c>, which only picks the tool up, and that
    /// <c>Use</c> is the left click that cuts the hole - so gating the selection turned a press of "1"
    /// into a dig. The game's own selection is left alone. Proof: ref/EVIDENCE.md E14.
    ///
    /// On the host <c>Use</c> runs; its postfix sees the lawn move, names the hole and pushes the pool.
    /// On a peer the host answers first, so <c>Use</c> is stopped, and because the stopped body is also
    /// what puts the tool back on the card bank, the room repeats that one prelude itself.
    ///
    /// A click the mod cannot read is a click it refuses. The room survives a shovel that did not dig; it
    /// does not survive two lawns that disagree about a plant.
    /// </summary>
    public static class ShovelGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.shovel").PatchAll(typeof(ShovelGate));
                Log.Info("hook", "Shovel.Use is gated, the selection is the game's " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(WorldSync.ShovelCapability, "the shovel gate could not be installed");
                Log.Error("hook", "gating the shovel failed: " + e.GetType().Name + " " + e.Message
                    + " - a dig inside a room happens on one lawn only and the room keeps the plant");
            }
        }

        /// <summary>
        /// Whether the room took this click over. The game keeps the dig when the gate itself is what broke,
        /// because a player who cannot shovel is a smaller loss than a plant the mod killed twice.
        /// </summary>
        private static bool Captured()
        {
            try
            {
                return WorldSync.TryCaptureShovel();
            }
            catch (Exception e)
            {
                Log.Error("hook", "the shovel gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game dig");
                return false;
            }
        }

        private static void AfterDig()
        {
            try
            {
                WorldSync.AfterShovel();
            }
            catch (Exception e)
            {
                Log.Error("hook", "the shovel postfix threw: " + e.GetType().Name + " " + e.Message);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Shovel), "Use")]
        public static class Use
        {
            private static bool Prefix()
            {
                return !Captured();
            }

            private static void Postfix()
            {
                AfterDig();
            }
        }
    }
}
