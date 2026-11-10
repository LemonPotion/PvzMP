namespace PvzMP.Core.Session
{
    /// <summary>
    /// The shape of the bands of preview cards a seat's name gets.
    ///
    /// Two numbers decide whether a seat's whole hand is readable: how wide one card is and how many stand
    /// beside it. The band holds five, because that is what the player asked to see, so the width of a card
    /// is capped at the share of the line a band of them can take - which is what keeps the last card of a
    /// band on the screen instead of past its edge. A card narrower than that share keeps the shape of the
    /// game's own card, because a preview that does not have a seed card's shape does not read as one.
    ///
    /// Everything is a distance from the line and not a point on the screen, so the answer can be checked
    /// without a running game, and the HUD keeps the one job of turning a distance into an anchor.
    /// </summary>
    public static class PreviewBands
    {
        /// <summary>
        /// The width of one preview card: what the height asks for at the game's own card shape, never so
        /// wide that a band of them reaches past the line they hang under.
        /// </summary>
        public static float CardWidth(float lineWidth, int slots, float gap, float height, float aspect)
        {
            float share = (lineWidth - (slots - 1) * gap) / slots;

            return height * aspect < share ? height * aspect : share;
        }

        /// <summary>How far from a line's left edge the centre of one card of a band stands.</summary>
        public static float CenterX(int slot, float width, float gap)
        {
            return width / 2f + slot * (width + gap);
        }

        /// <summary>
        /// How far below a line's bottom edge the centre of one of its bands stands. The first band hangs a
        /// gap under the name and every band after it a gap under the band before, so a hand longer than one
        /// band is read downward and never across the names above it.
        /// </summary>
        public static float CenterY(int band, float height, float gap)
        {
            return gap + band * (height + gap) + height / 2f;
        }

        /// <summary>
        /// How many bands a hand of this many cards needs: one for the first band and one more for every
        /// band after it, so eleven plants of a five-wide band are said in three bands.
        /// </summary>
        public static int BandCount(int cards, int slots)
        {
            if (cards <= 0)
            {
                return 0;
            }

            return (cards - 1) / slots + 1;
        }
    }
}
