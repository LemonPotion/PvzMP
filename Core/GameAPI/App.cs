using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Where the game itself says it is. These are the only predicates the patches are allowed to
    /// gate on, which is what keeps single player untouched: outside a session nothing calls in here,
    /// and inside one a hook still has to match the game's own state.
    /// </summary>
    public static class App
    {
        /// <summary>Raw int value of the native GameStatus enum; -1 is OutGame in this build.</summary>
        public static int ReadGameStatus()
        {
            return ApiBase.Query("ReadGameStatus",
                () => (int)Il2Cpp.GameAPP.theGameStatus, "app-state");
        }

        /// <summary>
        /// The status as the game itself names it, for log lines only. The measured run showed that
        /// this field says nothing about whether a battle is running - the main menu reads 0/InGame -
        /// so no decision may be made from it.
        /// </summary>
        public static string DescribeStatus()
        {
            int status = ReadGameStatus();
            string name = ApiBase.Query("GameStatus.ToString(" + status + ")", () =>
                ((Il2Cpp.GameStatus)status).ToString(), "app-state");

            return status + (name == null ? string.Empty : "/" + name);
        }

        /// <summary>Native profile name, used as the default nickname. Empty when the game has none.</summary>
        public static string ReadPlayerName()
        {
            string name = ApiBase.Query("ReadPlayerName", () =>
            {
                string native = Il2Cpp.GameAPP.playerName;
                return native == null ? string.Empty : native;
            }, "app-state");

            return name ?? string.Empty;
        }

        /// <summary>
        /// Which level the game thinks it is on. These two fields are what a peer compares against the
        /// host's `EnterGame` arguments to know the level it was told to load is really the one loaded -
        /// the lawn alone says a battle exists, not which one.
        ///
        /// Both are plain reads, so they stay quiet: a level check runs every frame until it passes.
        /// </summary>
        public static int ReadBoardLevel()
        {
            return ApiBase.Query("GameAPP.theBoardLevel", () => Il2Cpp.GameAPP.theBoardLevel, "app-state");
        }

        public static int ReadBoardType()
        {
            return ApiBase.Query("GameAPP.theBoardType", () => (int)Il2Cpp.GameAPP.theBoardType, "app-state");
        }
    }
}
