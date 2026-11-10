using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// One grave as either lawn can read it: where it stands, which kind of grave the game rolled for it,
    /// and - only on the end that read it - the object it is. The instance id never goes on the wire,
    /// because a second end's objects mean nothing to this one.
    /// </summary>
    public struct LawnGrave
    {
        public int Column;
        public int Row;
        public int GraveType;
        public int InstanceId;
    }

    /// <summary>
    /// What this lawn has to do to look like the host's graves: build these cells, take down those objects.
    /// Both halves are needed, because a lawn that only ever adds ends the level holding graves nobody
    /// planted and a zombie nobody rolled.
    /// </summary>
    public sealed class GraveChange
    {
        public static readonly GraveChange None = new GraveChange(
            new LawnGrave[0], new LawnGrave[0]);

        public GraveChange(LawnGrave[] toBuild, LawnGrave[] toRemove)
        {
            ToBuild = toBuild;
            ToRemove = toRemove;
        }

        /// <summary>Cells the host names and this lawn does not hold, as the host described them.</summary>
        public LawnGrave[] ToBuild { get; private set; }

        /// <summary>The objects this lawn holds on cells the host's layout does not name.</summary>
        public LawnGrave[] ToRemove { get; private set; }

        public bool IsEmpty
        {
            get { return ToBuild.Length == 0 && ToRemove.Length == 0; }
        }
    }

    /// <summary>
    /// The two grave rules, away from the game: whose dice a grave may come from, and what one lawn has to
    /// do to look like the other.
    ///
    /// Every grave on a lawn is rolled. `Board.SetGrave` rolls the cells of the level's graves three times
    /// over, `Board.SupplyGrave` rolls the cell of the grave a wave delivers, and the one door both of them
    /// end in - `GridItem.SetGridItem` - rolls again when the caller left the kind at `Default`. The grave's
    /// zombie is rolled a second later, inside the animation callback that opens it. Two ends therefore grow
    /// two different lawns of graves and two different zombies out of them, which is the defect this rule
    /// answers. Proof: ref/EVIDENCE.md E42.
    ///
    /// The values the rolls leave behind are sendable - a cell and a kind are two numbers, and a zombie type
    /// is one - so the host's layout travels and a peer repeats the game's own creator with the host's
    /// arguments. The call itself cannot travel: a replay of a rolling body gives every end its own answer,
    /// which is the reason the arbiter refuses it (E35), and `SetGridItem` rolls in its own body even though
    /// it takes the kind as a parameter.
    /// </summary>
    public static class GraveRules
    {
        /// <summary>
        /// True means the game's own dice may run on this end. The host always rolls, because the host's
        /// lawn is the lawn the room is told about. A peer rolls nothing while the room stands in a level:
        /// its cells come from the layout and its grave opens are the host's sentence too, so a peer that
        /// let its own grave open would also let its own zombie type out of the same roll. Outside a room,
        /// and inside one while no level is up, every end rolls - the garden and the menu are not the
        /// room's lawn.
        /// </summary>
        public static bool RollsOwnGraves(bool roomDecides, bool theRoomIsInALevel, bool isHost)
        {
            if (!roomDecides || !theRoomIsInALevel)
            {
                return true;
            }

            return isHost;
        }

        /// <summary>
        /// Where a grave stands is the only name it has: the game puts at most one object of this kind in a
        /// cell, and the layout the host sends is a list of cells. A host cell this lawn does not hold is
        /// built, a held object on a cell the host does not name is taken down, and a cell both lawns hold
        /// with a different kind is taken down and built again, because a peer cannot turn one grave into
        /// another - only the game's creator makes a grave, and it makes the kind it is told to.
        /// </summary>
        public static GraveChange Reconcile(LawnGrave[] host, LawnGrave[] local)
        {
            var build = new List<LawnGrave>();
            var remove = new List<LawnGrave>();

            var held = new HashSet<int>();
            var named = new HashSet<int>();

            if (local != null)
            {
                for (int i = 0; i < local.Length; i++)
                {
                    int cell = Cell(local[i].Column, local[i].Row);

                    // Two objects on one cell are the game's own difference; the first is the one the
                    // layout is compared with and every later one is a grave the host does not hold.
                    if (!held.Add(cell))
                    {
                        remove.Add(local[i]);
                    }
                }
            }

            if (host != null)
            {
                for (int i = 0; i < host.Length; i++)
                {
                    LawnGrave wanted = host[i];
                    int cell = Cell(wanted.Column, wanted.Row);

                    if (!named.Add(cell))
                    {
                        continue;
                    }

                    LawnGrave here;
                    if (held.Contains(cell))
                    {
                        here = Find(local, wanted.Column, wanted.Row);

                        if (here.GraveType == wanted.GraveType)
                        {
                            continue;
                        }

                        remove.Add(here);
                    }

                    build.Add(wanted);
                }
            }

            if (local != null)
            {
                for (int i = 0; i < local.Length; i++)
                {
                    if (!named.Contains(Cell(local[i].Column, local[i].Row)))
                    {
                        remove.Add(local[i]);
                    }
                }
            }

            if (build.Count == 0 && remove.Count == 0)
            {
                return GraveChange.None;
            }

            return new GraveChange(build.ToArray(), remove.ToArray());
        }

        private static LawnGrave Find(LawnGrave[] local, int column, int row)
        {
            for (int i = 0; local != null && i < local.Length; i++)
            {
                if (local[i].Column == column && local[i].Row == row)
                {
                    return local[i];
                }
            }

            return default(LawnGrave);
        }

        private static int Cell(int column, int row)
        {
            return row * 100 + column;
        }
    }
}
