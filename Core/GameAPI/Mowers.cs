using System;
using System.Collections.Generic;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The row mowers of this lawn. A mower starts when its own trigger sees a zombie, and its
    /// `StartMove` plays the whole start on the object: the bookkeeping in `BoardSpawner.MowerStart`,
    /// the `started` flag, the animation and the velocity. A peer therefore replays that one method on
    /// the same row instead of moving its copy by hand.
    /// </summary>
    public static class Mowers
    {
        /// <summary>This machine's object for the row, or 0 when the lawn has no mower there.</summary>
        public static int FindInstanceForRow(int row)
        {
            return ApiBase.Query("FindMower(row=" + row + ")", () =>
            {
                Il2CppSystem.Collections.Generic.List<Il2Cpp.Mower> mowers = MowerList();
                if (mowers == null)
                {
                    return 0;
                }

                for (int i = 0; i < mowers.Count; i++)
                {
                    Il2Cpp.Mower mower = mowers[i];
                    if (ApiBase.Present(mower) && mower.theMowerRow == row)
                    {
                        return mower.GetInstanceID();
                    }
                }

                return 0;
            }, "core-board");
        }

        /// <summary>The rows this lawn actually holds, so a mismatch is named rather than guessed.</summary>
        public static int[] Rows()
        {
            return ApiBase.Query("MowerRows", () =>
            {
                var rows = new List<int>();
                Il2CppSystem.Collections.Generic.List<Il2Cpp.Mower> mowers = MowerList();
                if (mowers == null)
                {
                    return rows.ToArray();
                }

                for (int i = 0; i < mowers.Count; i++)
                {
                    Il2Cpp.Mower mower = mowers[i];
                    if (ApiBase.Present(mower))
                    {
                        rows.Add(mower.theMowerRow);
                    }
                }

                return rows.ToArray();
            }, "core-board");
        }

        /// <summary>Whether the row's mower has already run; a duplicate host event must not start it twice.</summary>
        public static bool AlreadyStarted(int instanceId)
        {
            return ApiBase.Query("MowerStarted(#" + instanceId + ")", () =>
            {
                Il2Cpp.Mower mower = Find(instanceId);
                return mower == null || mower.started;
            }, "core-board");
        }

        /// <summary>The game's own start on this machine's object. The caller owns the gate flag that lets it run.</summary>
        public static bool StartMoving(int instanceId)
        {
            return ApiBase.TryCall("Mower.StartMove(#" + instanceId + ")", () =>
            {
                Il2Cpp.Mower mower = Find(instanceId);
                if (mower == null)
                {
                    Log.Warn("api", "Mower.StartMove -> no mower #" + instanceId + " on the lawn");
                    return;
                }

                mower.StartMove();
                Log.Api("Mower.StartMove -> row " + mower.theMowerRow + " started=" + mower.started);
            }, "core-mower");
        }

        private static Il2CppSystem.Collections.Generic.List<Il2Cpp.Mower> MowerList()
        {
            Il2Cpp.Board board = Entities.Board();
            return board == null || !ApiBase.Present(board.mowerArray) ? null : board.mowerArray;
        }

        private static Il2Cpp.Mower Find(int instanceId)
        {
            Il2CppSystem.Collections.Generic.List<Il2Cpp.Mower> mowers = MowerList();
            if (mowers == null)
            {
                return null;
            }

            for (int i = 0; i < mowers.Count; i++)
            {
                Il2Cpp.Mower mower = mowers[i];
                if (ApiBase.Present(mower) && mower.GetInstanceID() == instanceId)
                {
                    return mower;
                }
            }

            return null;
        }
    }
}
