using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The card a click spends. In a single-player game that card is spent inside the game's own
    /// `Mouse.TryToSetPlantByCard`: the click refuses while the card is recharging, and once the plant
    /// is out it starts the recharge, counts the use and puts the card down. A room placement never
    /// reaches that method - the gate turns the click into a request - so the same three things are
    /// done here, on the game's own card object, with the value the game itself writes into `CD`.
    /// Proof: ref/EVIDENCE.md E15.
    /// </summary>
    public static class CardUse
    {
        /// <summary>PlantType.SuperCherryShooter in this build (dump.cs:84982).</summary>
        public const int SuperCherryShooter = 1005;

        /// <summary>
        /// What the game forgives a SuperCherryShooter card when its recharge starts: the native tail
        /// subtracts this constant from `fullCD` (Mouse.TryToSetPlantByCard +0xF2C, float 7.5 at
        /// RVA 0x2A9CC70). Proof: ref/EVIDENCE.md E15.
        /// </summary>
        private const float SuperCherryDiscount = 7.5f;

        /// <summary>The card currently on the mouse, or null when the hand is empty.</summary>
        public static Il2Cpp.CardUI Held()
        {
            return ApiBase.Query("HeldCard", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.theCardOnMouse))
                {
                    return null;
                }

                return mouse.theCardOnMouse;
            }, "input");
        }

        /// <summary>
        /// True while the game would refuse this card. `CD` counts up from 0 to `fullCD`, and the native
        /// click refuses while `fullCD` is still above it (Mouse.TryToSetPlantByCard +0x3F7, +0x400).
        /// </summary>
        public static bool Recharging(Il2Cpp.CardUI card)
        {
            return ApiBase.Query("CardRecharging", () =>
                ApiBase.Present(card) && card.fullCD > card.CD, "input");
        }

        /// <summary>
        /// The card tail of a room placement: the recharge the game would have started, the use it would
        /// have counted, the card it would have put down, and the hand it would have cleared. plantedType
        /// is the type of the plant that actually appeared, which is what the game's own tail compares
        /// against SuperCherryShooter. Proof: ref/EVIDENCE.md E15.
        /// </summary>
        public static bool Spend(Il2Cpp.CardUI card, int plantedType)
        {
            if (!ApiBase.Present(card))
            {
                Log.Warn("api", "SpendCard -> the card is gone, nothing was spent");

                return false;
            }

            return ApiBase.TryCall("SpendCard(plantedType=" + plantedType + ")", () =>
            {
                float cooldown = plantedType == SuperCherryShooter
                    ? card.fullCD - SuperCherryDiscount
                    : 0f;

                card.CD = cooldown < 0f ? 0f : cooldown;
                card.UseOnce();
                card.PutDown();

                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                string hand = "no mouse";

                if (ApiBase.Present(mouse))
                {
                    Il2Cpp.CardUI held = mouse.theCardOnMouse;
                    int heldCardType = ApiBase.Present(held) ? (int)held.thePlantType : -1;
                    int handType = (int)mouse.thePlantTypeOnMouse;

                    // The game's click tail destroys the held item and clears the hand right after it puts
                    // the plant down (Mouse.TryToSetPlantByCard +0x7EA). A room placement gets here from
                    // the host's answer instead, so that tail is finished here; a card of another plant
                    // is the player's next pick and stays.
                    bool stillCarried = ApiBase.Present(held) ? heldCardType == plantedType
                        : handType == plantedType;

                    if (stillCarried)
                    {
                        mouse.ClearItemOnMouse(true);
                        hand = "carried type " + plantedType + ", cleared";
                    }
                    else
                    {
                        hand = ApiBase.Present(held) ? "card type " + heldCardType + ", left"
                            : "empty (" + handType + ")";
                    }
                }

                Log.Api("SpendCard -> CD " + card.CD + "/" + card.fullCD + ", hand " + hand
                    + " " + ApiBase.FrameTag());
            }, "input");
        }
    }
}
