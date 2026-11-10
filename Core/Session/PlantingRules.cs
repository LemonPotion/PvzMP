namespace PvzMP.Core.Session
{
    /// <summary>
    /// The two money questions the room has to answer about a planting, in the game's own numbers.
    ///
    /// What a plant costs is not the mod's business: it is `theSeedCost` written on the card standing in
    /// this bank, and the mod only reads it. What the room decides is whether the pool the host owns
    /// covers that number before it lets the plant appear, and - since the grant is answered on a lawn
    /// that is not the lawn of the one who clicked - how much of the cost the host still has to charge
    /// after the game made the plant on its own.
    /// </summary>
    public static class PlantingRules
    {
        /// <summary>No card of this type in this bank, so no price is known and nothing may be claimed.</summary>
        public const int UnknownCost = -1;

        /// <summary>
        /// Why the pool cannot pay for this plant, or null when it can. An unknown cost is not a free
        /// planting and not a refusal either: the price is the game's number, and where the game has no
        /// card of this type the mod has no right to invent one - the caller logs that nothing was charged.
        /// </summary>
        public static string Affordance(int sun, int cost)
        {
            if (cost == UnknownCost || cost <= 0 || sun < 0 || sun >= cost)
            {
                return null;
            }

            return "the pool holds " + sun + ", the card costs " + cost;
        }

        /// <summary>
        /// How much the host still has to charge after the game made the plant. The creator is measured
        /// rather than trusted: its body is empty in the dump, so whether it pays for the plant out of
        /// `theSun` is something only a live run can say, and the answer must not end up as the pool
        /// charged twice or not at all. Whatever the game already took is left alone - if it took more
        /// than the card says, that extra is the game's own business, not a refund the mod hands back.
        /// </summary>
        public static int StillOwed(int sunBefore, int sunAfter, int cost)
        {
            if (cost == UnknownCost || cost <= 0 || sunBefore < 0 || sunAfter < 0)
            {
                return 0;
            }

            int paid = sunBefore - sunAfter;

            return paid >= cost ? 0 : cost - paid;
        }
    }
}
