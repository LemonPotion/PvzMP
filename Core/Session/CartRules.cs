namespace PvzMP.Core.Session
{
    /// <summary>
    /// The cart's two clicks, away from the game. `Wheel.Use` is one body holding both halves: with the
    /// cart empty it takes the plant the pointer is over, and with a plant in the cart it puts that plant
    /// on the cell the pointer is over. Which half ran is written in the cart's own fields, and those are
    /// private, so the room does not read them - it asks the question the body itself answers, which is
    /// whether this end is already carrying one.
    ///
    /// The net effect is the glove's effect: one plant the room numbers stands on another cell afterwards.
    /// That is why the cart travels as a move and not as a death and a birth: every lawn keeps its own
    /// object, so its own health, level and stage are the ones the room agrees on without being carried.
    /// </summary>
    public static class CartRules
    {
        /// <summary>
        /// What this end carries after one cart click. A click while carrying is the drop, and only a drop
        /// that landed takes the carry away - a cell the game refused still holds the cart's plant, and the
        /// player may aim it somewhere else. A click while empty is the lift and remembers what the pointer
        /// named, which is nothing when the pointer named no plant the room numbers.
        /// </summary>
        public static int HeldAfterClick(int carriedBefore, int liftedNetId, bool dropLanded)
        {
            if (carriedBefore == 0)
            {
                return liftedNetId;
            }

            return dropLanded ? 0 : carriedBefore;
        }

        /// <summary>
        /// True when this click is the drop: the cart already carries a plant, so the pointer names a cell
        /// and not a plant to lift. Naming the pointer's plant on a drop would remember the plant standing
        /// where the cart means to put its own down.
        /// </summary>
        public static bool IsDrop(int carriedBefore)
        {
            return carriedBefore != 0;
        }
    }
}
