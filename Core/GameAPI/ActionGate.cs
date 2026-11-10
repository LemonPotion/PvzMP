using System;
using HarmonyLib;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Not a gate: nothing here stops the game, every prefix returns true or is a postfix. The room has
    /// to be able to answer "what did the player just do?" from one log file, so the game's own input
    /// doors are named while a room decides: the click, the miss, the card, the return, the shovel, the
    /// glove and the tools of the strip. Each line carries what the hand held at that moment and the cell
    /// the pointer was on, read from the game's own state, because the same click means different things
    /// with a card, a tool, or an empty hand.
    ///
    /// The fertilizer already names its own doors, the hammer and the cart name the click that starts
    /// their bodies, and the merge paths are named by their own watch; this file does not repeat those.
    /// A failed patch costs the action lines and nothing else.
    /// </summary>
    public static class ActionGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.action").PatchAll(typeof(ActionGate));
                Log.Info("hook", "every player action on this lawn is named in the log while a room decides "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "naming the player's actions failed: " + e.GetType().Name + " " + e.Message
                    + " - the room behaves as before, only the action lines are missing");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "MouseClick")]
        public static class MouseClick
        {
            private static void Prefix()
            {
                // This door is walked every frame; only a press is an action. Without this the log grew
                // by one line per frame - 61,800 lines in one measured session.
                if (!FreshPress())
                {
                    return;
                }

                Report("the player clicked", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "LeftClickWithNothing")]
        public static class LeftClickWithNothing
        {
            private static void Prefix()
            {
                Report("the click found no plant and no zombie under the pointer", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "PutDownItem")]
        public static class PutDownItem
        {
            private static void Prefix()
            {
                Report("the hand put its item back", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "ClickOnCard")]
        public static class ClickOnCard
        {
            private static void Prefix(Il2Cpp.CardUI card)
            {
                Report("the player clicked a plant card", card == null
                    ? "no card"
                    : "card type " + card.theSeedType + " cost " + card.theSeedCost);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "ClickZombieCard")]
        public static class ClickZombieCard
        {
            private static void Prefix(Il2Cpp.IZECard card)
            {
                Report("the player clicked a zombie card", card == null
                    ? "no card"
                    : "zombie card type " + card.theSeedType);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "TryToSetPlantByGlove")]
        public static class TryToSetPlantByGlove
        {
            private static void Prefix()
            {
                Report("the glove is putting its plant down", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "TryToPickPlant")]
        public static class TryToPickPlant
        {
            private static void Prefix()
            {
                Report("the glove's pick ran on the pointed cell", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mouse), "DisassemblePlant")]
        public static class DisassemblePlant
        {
            private static void Prefix(Il2Cpp.Plant plant, bool force)
            {
                Report("the shovel takes a plant apart",
                    (ApiBase.Present(plant) ? Entities.Describe(plant) : "no plant")
                    + (force ? ", forced" : ""));
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Shovel), "Use")]
        public static class ShovelUse
        {
            private static void Prefix()
            {
                Report("the shovel was used on the pointed cell", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Glove), "Use")]
        public static class GloveUse
        {
            private static void Prefix()
            {
                Report("the glove was used on the pointed cell", Hand());
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.InGameTool), "PickUp")]
        public static class ToolPickUp
        {
            private static void Prefix(Il2Cpp.InGameTool __instance)
            {
                Report("a tool of the strip was picked up", ToolName(__instance));
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.InGameTool), "PutDown")]
        public static class ToolPutDown
        {
            private static void Prefix(Il2Cpp.InGameTool __instance)
            {
                Report("a tool of the strip was put down", ToolName(__instance));
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.InGameTool), "OnClick")]
        public static class ToolClick
        {
            private static void Prefix(Il2Cpp.InGameTool __instance)
            {
                Report("a tool of the strip was clicked", ToolName(__instance));
            }
        }

        private static string ToolName(Il2Cpp.InGameTool tool)
        {
            return tool == null ? "no tool" : tool.GetType().Name;
        }

        /// <summary>
        /// True only on the frame the left button went down. `Mouse.MouseClick` is walked every frame, so
        /// the press itself is the only thing that makes a click an action worth a line.
        /// </summary>
        private static bool FreshPress()
        {
            return ApiBase.Query("Input.GetMouseButtonDown(0)",
                () => UnityEngine.Input.GetMouseButtonDown(0));
        }

        /// <summary>
        /// The hand as the game holds it right now, and the cell the pointer is over. Every read is the
        /// game's own state; a read that throws says so in the line instead of losing the action.
        /// </summary>
        private static string Hand()
        {
            try
            {
                string hand = string.Empty;

                if (Cursor.ReadShovelPickedUp())
                {
                    hand += " shovel";
                }

                if (Cursor.ReadHammerPickedUp())
                {
                    hand += " hammer";
                }

                if (Cursor.ReadWheelPickedUp())
                {
                    hand += " cart";
                }

                if (Cursor.ReadFertilizerPickedUp())
                {
                    hand += " fertilizer";
                }

                int glovePlant = Cursor.ReadPlantOnGloveType();
                int gloveZombie = Cursor.ReadZombieOnGloveType();
                if (Cursor.ReadGlovePickedUp() || glovePlant != Cursor.NoCard || gloveZombie != Cursor.NoCard)
                {
                    hand += " glove(carrying " + (gloveZombie != Cursor.NoCard
                        ? "zombie type " + gloveZombie
                        : glovePlant != Cursor.NoCard ? "plant type " + glovePlant : "nothing") + ")";
                }

                int card = Cursor.ReadHeldCardPlantType();
                if (card != Cursor.NoCard)
                {
                    hand += " card type " + card;
                }

                if (hand.Length == 0)
                {
                    hand = " nothing";
                }

                return "holding" + hand + " on cell " + Cursor.ReadColumn() + "," + Cursor.ReadRow();
            }
            catch (Exception e)
            {
                return "its hand could not be read: " + e.GetType().Name + " " + e.Message;
            }
        }

        /// <summary>
        /// One line per action, and only while a room decides: in a single player game the question is
        /// already answered by the game working, and a click log nobody is reading is noise in a file
        /// that has to stay readable.
        /// </summary>
        private static void Report(string what, string detail)
        {
            try
            {
                if (ConnectionEntry.Room == null || !ConnectionEntry.Room.RoomDecides)
                {
                    return;
                }

                Log.Info("mp", what + ": " + detail + (CallInbox.IsApplying ? " (the room's own call)" : "")
                    + " " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "the action watch threw: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
