using System;
using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The graves of this lawn. A grave is not a plant and not a zombie: it is a `GridItem`, kept in the
    /// board's own `griditemArray`, and the single door that puts one there is the static
    /// `GridItem.SetGridItem(column, row, type, graveType)` - the same door craters, ladders, ice blocks and
    /// scary pots go through. Nothing here decides a rule: it reads what the lawn holds and repeats the
    /// game's own creator with the numbers the host rolled.
    ///
    /// Proof of the collection, the door and the four arguments: ref/EVIDENCE.md E42.
    /// </summary>
    public static class Graves
    {
        /// <summary>The `GridItemType` value this build calls a grave.</summary>
        private const int GraveItem = 7;

        /// <summary>Every grave this lawn holds, in the order the board's list walks.</summary>
        public static LawnGrave[] ReadAll()
        {
            return ApiBase.Query("read the graves on this lawn", () =>
            {
                Il2CppSystem.Collections.Generic.List<Il2Cpp.GridItem> items = Items();
                if (items == null)
                {
                    return null;
                }

                var graves = new List<LawnGrave>();

                for (int i = 0; i < items.Count; i++)
                {
                    LawnGrave grave;
                    if (Describe(items[i], out grave))
                    {
                        graves.Add(grave);
                    }
                }

                return graves.ToArray();
            }, "core-grave");
        }

        /// <summary>
        /// The game's own creator, with the kind the host rolled. Zero is no grave: the door answers null
        /// when the cell will not take one, and a peer that could not build the host's grave has to say so
        /// in the log instead of pretending the layout landed.
        /// </summary>
        public static int Build(int column, int row, int graveType)
        {
            return ApiBase.Call("build the host's grave on cell " + column + "," + row, () =>
            {
                Il2Cpp.GridItem item = Il2Cpp.GridItem.SetGridItem(column, row,
                    Il2Cpp.GridItemType.Grave, (Il2Cpp.GraveType)graveType);

                if (!ApiBase.Present(item))
                {
                    Log.Warn("api", "SetGridItem -> no grave on cell " + column + "," + row);
                    return 0;
                }

                Log.Api("SetGridItem -> grave type " + graveType + " on cell " + column + "," + row
                    + ", obj " + item.GetInstanceID());

                return item.GetInstanceID();
            }, "core-grave");
        }

        /// <summary>
        /// Taking one grave down with the object's own `Die`, which is the door the game uses for a grave
        /// that emptied: it destroys the object and drops what the grave was holding. Writing the object off
        /// the list by hand would leave the sprite on the lawn.
        /// </summary>
        public static bool TakeDown(int instanceId)
        {
            return ApiBase.TryCall("take down grave obj " + instanceId, () =>
            {
                Il2Cpp.GridItem item = Find(instanceId);
                if (item == null)
                {
                    Log.Warn("api", "grave obj " + instanceId + " is not on this lawn any more");
                    return;
                }

                item.Die();
            }, "core-grave");
        }

        /// <summary>
        /// Whether an object in the board's list is a grave, and what this lawn would have to know about it.
        /// The kind of grave lives on the `Grave` half of the object, so an item that is not one reads as no
        /// kind at all, which is the `Default` the game's own callers pass.
        /// </summary>
        private static bool Describe(Il2Cpp.GridItem item, out LawnGrave grave)
        {
            grave = default(LawnGrave);

            if (!ApiBase.Present(item) || (int)item.theItemType != GraveItem)
            {
                return false;
            }

            grave.Column = item.theItemColumn;
            grave.Row = item.theItemRow;
            grave.InstanceId = item.GetInstanceID();
            grave.GraveType = Kind(item);

            return true;
        }

        private static int Kind(Il2Cpp.GridItem item)
        {
            try
            {
                Il2Cpp.Grave grave = item.TryCast<Il2Cpp.Grave>();

                return ApiBase.Present(grave) ? (int)grave.graveType : 0;
            }
            catch (Exception e)
            {
                Log.Warn("api", "the kind of grave obj " + item.GetInstanceID() + " cannot be read: "
                    + e.GetType().Name);

                return 0;
            }
        }

        private static Il2Cpp.GridItem Find(int instanceId)
        {
            Il2CppSystem.Collections.Generic.List<Il2Cpp.GridItem> items = Items();
            if (items == null)
            {
                return null;
            }

            for (int i = 0; i < items.Count; i++)
            {
                Il2Cpp.GridItem item = items[i];
                if (ApiBase.Present(item) && item.GetInstanceID() == instanceId)
                {
                    return item;
                }
            }

            return null;
        }

        private static Il2CppSystem.Collections.Generic.List<Il2Cpp.GridItem> Items()
        {
            Il2Cpp.Board board = Entities.Board();
            if (board == null)
            {
                return null;
            }

            if (!ApiBase.Present(board.griditemArray))
            {
                Log.Warn("api", "this lawn has no grid-item list to read");
                return null;
            }

            return board.griditemArray;
        }
    }
}
