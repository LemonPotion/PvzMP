using System.Collections.Generic;
using System.Text;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// The rules the fertilizer watch needs away from the game: what a lawn gained and what it lost between
    /// two reads of itself, and whose lawn answers to the room about it. The watch measures, so nothing here
    /// decides what a difference means beyond what a seat has to do about it. Native proof: E48, E53.
    /// </summary>
    public static class FertilizeRules
    {
        /// <summary>
        /// Whether this lawn puts its own plants of a fertilized cell back whole. The seat whose hand
        /// dropped the fertilizer is changed by the game's own body already, so it heals nothing twice, and
        /// the host that repeats the ask to the rest of the room stands on a lawn no fertilizer touched.
        /// </summary>
        public static bool HealsHere(int originPeer, int localPeer)
        {
            return originPeer != localPeer;
        }

        /// <summary>
        /// Whether this lawn rebuilds the plant of a cell in the kind another lawn grew it into. The seat
        /// that grew it stands on the changed lawn already; every other lawn has to say what its own plant of
        /// that cell became, or the layout it sends back keeps naming the kind the fertilizer left behind.
        /// </summary>
        public static bool GrowsHere(int originPeer, int localPeer)
        {
            return originPeer != localPeer;
        }

        /// <summary>
        /// The cells whose plant changed kind between two readings of one lawn, each said with the kind that
        /// left and the kind that arrived. A cell that held nothing before is not here, because a fertilizer
        /// grows the plant it found and does not bring one; a plant that kept its kind is not here either,
        /// because a healing is carried by the cell's own frame. A lawn this end could not read on one side
        /// answers with nothing.
        /// </summary>
        public static Growth[] Grown(LawnPlant[] before, LawnPlant[] after)
        {
            var grown = new List<Growth>();

            if (before == null || after == null)
            {
                return grown.ToArray();
            }

            for (int i = 0; i < after.Length; i++)
            {
                LawnPlant now = after[i];

                if (HeldOnCell(before, now.Column, now.Row, now.PlantType))
                {
                    continue;
                }

                int left = LeftOnCell(before, after, now.Column, now.Row);

                if (left != 0)
                {
                    grown.Add(new Growth(now.Column, now.Row, left, now.PlantType));
                }
            }

            return grown.ToArray();
        }

        /// <summary>Whether this cell held a plant of this kind in this reading of the lawn.</summary>
        private static bool HeldOnCell(LawnPlant[] plants, int column, int row, int type)
        {
            for (int i = 0; i < plants.Length; i++)
            {
                LawnPlant one = plants[i];

                if (one.Column == column && one.Row == row && one.PlantType == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The kind this cell held before and does not hold now, or 0 when nothing left it. The kind that
        /// left is what a lawn replaces, so a cell holding a pumpkin beside the nut the fertilizer grew says
        /// only the nut: the pumpkin is in both readings and never asked about.
        /// </summary>
        private static int LeftOnCell(LawnPlant[] before, LawnPlant[] after, int column, int row)
        {
            for (int i = 0; i < before.Length; i++)
            {
                LawnPlant one = before[i];

                if (one.Column != column || one.Row != row || HeldOnCell(after, column, row, one.PlantType))
                {
                    continue;
                }

                return one.PlantType;
            }

            return 0;
        }

        /// <summary>
        /// Lines this lawn read before and does not read now, each with a minus, then lines it reads now
        /// and did not before, each with a plus. A line is a whole plant as one text, so a plant that kept
        /// its object and only changed its type or its health shows as one line out and one line back -
        /// which is the answer the watch is after: an object that survives with new numbers is a change in
        /// place, an object that leaves and another that arrives is a replacement.
        /// </summary>
        public static string Changed(string[] before, string[] after)
        {
            var text = new StringBuilder();

            for (int i = 0; before != null && i < before.Length; i++)
            {
                if (!Holds(after, before[i]))
                {
                    Say(text, "-", before[i]);
                }
            }

            for (int i = 0; after != null && i < after.Length; i++)
            {
                if (!Holds(before, after[i]))
                {
                    Say(text, "+", after[i]);
                }
            }

            return text.Length == 0 ? "the lawn reads the same" : text.ToString();
        }

        private static bool Holds(string[] lines, string line)
        {
            if (line == null)
            {
                return true;
            }

            for (int i = 0; lines != null && i < lines.Length; i++)
            {
                if (lines[i] == line)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Say(StringBuilder text, string sign, string line)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(sign).Append(' ').Append(line);
        }
    }
}
