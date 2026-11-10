using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The colour a seat is drawn in, in one place. A ghost on the lawn, a name written on a card and a
    /// line of the roster should say the same thing about the same player, and four seats is a short
    /// enough list to keep that honest. The colour follows the seat's number, and the host always has
    /// number zero, so the first colour is the host's on every screen.
    /// </summary>
    public static class SeatTint
    {
        /// <summary>The seat's colour as a ghost wears it: see-through, because it stands over the lawn.</summary>
        public static Color Of(int peerId)
        {
            switch (((peerId % 4) + 4) % 4)
            {
                case 0:
                    return new Color(0.35f, 0.85f, 1f, 0.8f);
                case 1:
                    return new Color(1f, 0.75f, 0.25f, 0.8f);
                case 2:
                    return new Color(0.55f, 1f, 0.45f, 0.8f);
                default:
                    return new Color(0.85f, 0.55f, 1f, 0.8f);
            }
        }

        /// <summary>The same colour for text and pictures that sit on top of something, fully opaque.</summary>
        public static Color Label(int peerId)
        {
            Color tint = Of(peerId);
            return new Color(tint.r, tint.g, tint.b, 1f);
        }
    }
}
