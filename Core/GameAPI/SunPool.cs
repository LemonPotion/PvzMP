using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The shared sun pool. Names mirror the native methods on purpose: their bodies were not
    /// readable, so the mod must not claim a semantic it has not seen in a log line yet.
    /// </summary>
    public static class SunPool
    {
        public static int ReadSun()
        {
            return ApiBase.Query("ReadSun", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board == null ? -1 : board.theSun;
            }, "core-board");
        }

        public static int ReadMaxSun()
        {
            return ApiBase.Query("ReadMaxSun", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board == null ? -1 : board.maxSun;
            }, "sun");
        }

        public static bool SetSun(int value)
        {
            return ApiBase.TryCall("SetSun(" + value + ")", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    Log.Warn("api", "SetSun -> no board on this lawn");
                    return;
                }

                int before = board.theSun;
                board.SetSun(value);
                Log.Api("SetSun -> " + before + " => " + board.theSun);
            }, "core-board");
        }

        public static bool UseSun(int amount)
        {
            return ApiBase.TryCall("UseSun(" + amount + ")", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    Log.Warn("api", "UseSun -> no board on this lawn");
                    return;
                }

                int before = board.theSun;
                board.UseSun(amount);
                Log.Api("UseSun -> " + before + " => " + board.theSun);
            }, "core-board");
        }

        /// <summary>Native GetSun(float, bool save). What "save" changes is not verified.</summary>
        public static bool GetSun(int amount, bool save)
        {
            return ApiBase.TryCall("GetSun(" + amount + ",save=" + save + ")", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    Log.Warn("api", "GetSun -> no board on this lawn");
                    return;
                }

                int before = board.theSun;
                board.GetSun(amount, save);
                Log.Api("GetSun -> " + before + " => " + board.theSun);
            }, "core-board");
        }

        public static bool ClearTheBoard()
        {
            return ApiBase.TryCall("ClearTheBoard", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    Log.Warn("api", "ClearTheBoard -> no board on this lawn");
                    return;
                }

                board.ClearTheBoard();
            }, "core-board");
        }
    }
}
