using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The seed bank of this lawn, read through the game's own card objects. The mod never prices a plant:
    /// what it costs is the number written on the card standing in this bank, and how long the player waits
    /// before the same card can be used again is the cooldown that card carries. Both are read here so the
    /// room can say what the game charges, and so a live run can be shown the difference between a planting
    /// the game paid for and one the room had to pay for itself.
    /// </summary>
    public static class CardBank
    {
        public const int UnknownCost = PvzMP.Core.Session.PlantingRules.UnknownCost;

        /// <summary>The card of this plant type that is lying in this bank, or null when the bank has none.</summary>
        public static Il2Cpp.CardUI Find(int plantType)
        {
            return ApiBase.Query("FindCard(type=" + plantType + ")", () =>
            {
                Il2Cpp.InGameUI ui = Il2Cpp.InGameUI.Instance;
                if (!ApiBase.Present(ui) || !ApiBase.Present(ui.Cards))
                {
                    return null;
                }

                Il2CppSystem.Collections.Generic.List<Il2Cpp.CardUI> cards = ui.Cards;

                for (int i = 0; i < cards.Count; i++)
                {
                    Il2Cpp.CardUI card = cards[i];
                    if (!ApiBase.Present(card) || !card.onCardBank)
                    {
                        continue;
                    }

                    if ((int)card.thePlantType == plantType)
                    {
                        return card;
                    }
                }

                return null;
            }, "seed-bank");
        }

        /// <summary>
        /// What this lawn says the plant costs: the bank's card first, then any card of that type that is
        /// still in the scene (a held or dropped card). UnknownCost only when the type has no card here.
        /// </summary>
        public static int ReadCost(int plantType)
        {
            Il2Cpp.CardUI card = Find(plantType);
            if (ApiBase.Present(card))
            {
                return card.theSeedCost;
            }

            return ApiBase.Query("FindAnyCardCost(type=" + plantType + ")", () =>
            {
                Il2Cpp.InGameUI ui = Il2Cpp.InGameUI.Instance;
                if (!ApiBase.Present(ui) || !ApiBase.Present(ui.Cards))
                {
                    return UnknownCost;
                }

                Il2CppSystem.Collections.Generic.List<Il2Cpp.CardUI> cards = ui.Cards;

                for (int i = 0; i < cards.Count; i++)
                {
                    Il2Cpp.CardUI other = cards[i];
                    if (ApiBase.Present(other) && (int)other.thePlantType == plantType
                        && other.theSeedCost > 0)
                    {
                        return other.theSeedCost;
                    }
                }

                return UnknownCost;
            }, "seed-bank");
        }

        /// <summary>
        /// The card in the words a log line needs: the price the game charges and the cooldown it is in
        /// right now. Read after a planting, this pair is the evidence of whether the game's own card path
        /// did either of them, which is what the room is not allowed to guess about.
        /// </summary>
        public static string Describe(int plantType)
        {
            Il2Cpp.CardUI card = Find(plantType);
            if (!ApiBase.Present(card))
            {
                return "no card of type " + plantType + " is on this bank";
            }

            return "cost " + card.theSeedCost + ", CD " + card.CD + "/" + card.fullCD;
        }
    }
}
