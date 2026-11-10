using System;
using HarmonyLib;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The mod's gates on the presses that start a lawn.
    ///
    /// The press a room actually plays is the seed chooser's LET'S ROCK: `StartGameBtn.OnMouseUp` saves the
    /// chosen cards, pays the chosen buff and sets `InitBoard.ready`, which is what ends the chooser. A live
    /// pair run showed the earlier two doors below never fire for it - the player pressed LET'S ROCK and no
    /// gate spoke - so the chooser's button is gated where the click lands.
    ///
    /// Two more doors are kept because the in-level ready button is a second way to the same order, and the
    /// live test settled which one the game walks there: a peer pressed its own ready button while only
    /// `InGameUI.OnStartBattleButtonClick` was gated, and the battle began. The image says why - that method
    /// has no static caller at all, while `InGameBtn.OnMouseUpAsButton` branches on `buttonNumber` and case 4
    /// repeats the four steps inline. Only number 4 is taken from the game: `OnMouseUpAsButton` is twenty
    /// buttons in one method - pause, camera, glove, health display, the menu - and stopping those would be a
    /// mod that broke the screen it was trying to synchronise.
    ///
    /// A room is one board, and the moment that board starts moving is as little a peer's decision as which
    /// board it is: a peer's press is refused, the host's press is broadcast, and the refusal is silent
    /// because the button hiding nothing looks like a button that was not pressed.
    /// Proof: ref/EVIDENCE.md E17b.
    /// </summary>
    public static class BattleGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.battle").PatchAll(typeof(BattleGate));
                Log.Info("hook", "Let's Rock is gated on StartGameBtn.OnMouseUp, and the in-level start on"
                    + " InGameBtn button 4 and InGameUI.OnStartBattleButtonClick " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(LevelSync.Capability, "the battle gate could not be installed");
                Log.Error("hook", "gating the battle start failed: " + e.GetType().Name + " " + e.Message
                    + " - a peer can start waves on its own lawn and the room is in two games at once");
            }
        }

        /// <summary>Asked by both doors: true when the room, and not this click, owns the battle start.</summary>
        private static bool Captured(int buttonNumber)
        {
            try
            {
                return LevelSync.TryCaptureBattle(buttonNumber);
            }
            catch (Exception e)
            {
                Log.Error("hook", "the battle gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game start the battle itself");

                return false;
            }
        }

        /// <summary>The seed chooser's start button; the one press the room really plays.</summary>
        private static bool RockCaptured()
        {
            try
            {
                return LevelSync.TryCaptureLetsRock();
            }
            catch (Exception e)
            {
                Log.Error("hook", "the Let's Rock gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game start the level itself");

                return false;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.StartGameBtn), "OnMouseUp")]
        public static class StartGameBtnOnMouseUp
        {
            private static bool Prefix()
            {
                return !RockCaptured();
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.InGameUI), "OnStartBattleButtonClick")]
        public static class OnStartBattleButtonClick
        {
            private static bool Prefix()
            {
                // The method carries no number, and it is only ever the battle: gate it as case 4 would be.
                return !Captured(BattleAnswer.StartBattleButton);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.InGameBtn), "OnMouseUpAsButton")]
        public static class OnMouseUpAsButton
        {
            private static bool Prefix(Il2Cpp.InGameBtn __instance)
            {
                int buttonNumber = __instance == null ? -1 : __instance.buttonNumber;

                if (!BattleAnswer.IsStartButton(buttonNumber))
                {
                    return true;
                }

                return !Captured(buttonNumber);
            }
        }
    }
}
