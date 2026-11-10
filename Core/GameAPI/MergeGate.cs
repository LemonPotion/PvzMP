using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Not a gate: nothing here stops the game, every prefix returns true. The room copies a fusion by
    /// measuring the lawn around the creator call, but the dump cannot say which of the game's own merge
    /// paths a click walked - all three bodies are empty. Three candidates are watched, and the lines
    /// they print say which path a live click took:
    ///
    /// - `Mouse.LeftClickWithSomeThing` is the dispatch of a click that found something under the pointer;
    ///   hearing it without hearing the placement gate means the merge does not go through planting at all.
    /// - `Mouse.ReinforcePlant()` is the no-argument reinforcement, most likely what "one plant on top of
    ///   another" runs when a card is held.
    /// - `Mouse.TurnPlantsToNewPlants(CardUI, PlantType, PlantType)` is the named one: it takes two types
    ///   and hands back an array, which is the shape of "several plants become a different one".
    ///
    /// A failed patch costs the diagnosis and nothing else, so this one does not own a capability: the
    /// room keeps working exactly as it did before the lines were added.
    /// </summary>
    public static class MergeGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.merge").PatchAll(typeof(MergeGate));
                Log.Info("hook", "the game's merge paths are watched " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "watching the merge paths failed: " + e.GetType().Name + " " + e.Message
                    + " - the room behaves as before, only the answer about merging is not measured");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "LeftClickWithSomeThing")]
        public static class LeftClickWithSomeThing
        {
            private static bool Prefix()
            {
                Report("the click found something under the pointer");

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "ReinforcePlant")]
        public static class ReinforcePlant
        {
            private static bool Prefix()
            {
                Report("the game is reinforcing a plant in place");

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "TurnPlantsToNewPlants")]
        public static class TurnPlantsToNewPlants
        {
            private static bool Prefix()
            {
                Report("the game is turning plants into a new plant");

                return true;
            }
        }

        /// <summary>
        /// One line per call, and only while a room decides: in a single player game the question is already
        /// answered by the game working, and a click log nobody is reading is noise in a file that has to
        /// stay readable.
        /// </summary>
        private static void Report(string what)
        {
            try
            {
                WorldSync.DescribeMergeCall(what);
            }
            catch (Exception e)
            {
                Log.Warn("hook", "the merge watch threw: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
