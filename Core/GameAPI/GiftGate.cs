using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The doors a gift box comes and goes through. `Present.AnimEvent` is the animation callback that
    /// reaches `Present.RandomPlant` and `Present.SuperRandomPlant`; `SnowPresent.AnimEvent` does not call
    /// them and rolls its own list inline, so the question has to be asked at both doors. Nothing else is
    /// touched there: the box itself arrives through the planting door, which already travels, and the
    /// plant that grows out of it is the host's roll (ref/EVIDENCE.md E40).
    ///
    /// The two doors that *place* a box are asked the same question, because they answer it the same way:
    /// the artist sunflower's periodic ability mints one in front of itself and the level mints one at a
    /// rolled cell every minute, both of them with this end's dice. A peer that let them run showed a box
    /// no other lawn held, lost it to the next census and called that opening it (ref/EVIDENCE.md E43).
    /// Both bodies return nothing, so skipping one hands no caller a null object - which is the property
    /// that decides where this rule can sit, and why it does not sit on `CreatePlant.SetPlant`. Neither
    /// refusal is a storm either: the artist's countdown and the level's box timer are both re-armed by
    /// the code that calls them, so a round this end skips costs one round and the next arrives when it
    /// would have.
    ///
    /// Outside a room, and inside one while no level is up, every end is answered yes, so a single
    /// player's gift opens exactly as it did before this class existed.
    /// </summary>
    public static class GiftGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.gift").PatchAll(typeof(GiftGate));
                Log.Info("hook", "a gift box opens into the host's roll, and only the host places one "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                // The cost of no gate is the bug this class exists for: two ends grow two different plants
                // out of one box, and the lawn diff has to rewrite the client's plant afterwards, which
                // takes that plant's health, level and animation with it.
                Log.Error("hook", "gating the gift doors failed: " + e.GetType().Name + " " + e.Message
                    + " - a gift can still grow a different plant on each lawn");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Present), "AnimEvent")]
        public static class PresentDoor
        {
            private static bool Prefix()
            {
                return MayRoll();
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.SnowPresent), "AnimEvent")]
        public static class SnowPresentDoor
        {
            private static bool Prefix()
            {
                return MayRoll();
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Board), "CreateRandomPresent")]
        public static class LevelBox
        {
            private static bool Prefix()
            {
                return MayPlace("level's box");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Paintingflower), "AttributeEvent")]
        public static class ArtistBox
        {
            private static bool Prefix()
            {
                return MayPlace("artist's box");
            }
        }

        /// <summary>
        /// The room's answer at a door that puts a box on the lawn. Nothing is asked of the census
        /// afterwards: on the end that is answered no there is nothing to describe, because the box it
        /// would have rolled was never made.
        /// </summary>
        private static bool MayPlace(string what)
        {
            try
            {
                return PlantSync.MayPlaceGift(what);
            }
            catch (Exception e)
            {
                Log.Error("hook", "the gift placement gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game place its own box");

                return true;
            }
        }

        /// <summary>
        /// The room's answer, and the one thing said back when it is yes: this lawn now holds a plant no
        /// frame of ours described, so the walk that describes it runs on the next Tick instead of up to a
        /// second later.
        /// </summary>
        private static bool MayRoll()
        {
            try
            {
                bool rolls = PlantSync.MayRollGift();

                if (rolls)
                {
                    PlantSync.CensusNow();
                }

                return rolls;
            }
            catch (Exception e)
            {
                Log.Error("hook", "the gift gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game grow the plant itself");
                return true;
            }
        }
    }
}
