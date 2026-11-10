using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Wave progress. Reading is allowed on every peer; the only mutation the mod performs is the
    /// host asking the game to run its own next wave. Clients never get this call; suppression of
    /// client-side waves happens in the patch on BoardSpawner.SummonZombies, not here.
    /// </summary>
    public static class Waves
    {
        public static int ReadWave()
        {
            return ApiBase.Query("ReadWave", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board == null ? -1 : board.theWave;
            }, "sun");
        }

        public static int ReadMaxWave()
        {
            return ApiBase.Query("ReadMaxWave", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board == null ? -1 : board.theMaxWave;
            }, "sun");
        }

        public static float ReadTimeUntilNextWave()
        {
            return ApiBase.Query("ReadTimeUntilNextWave", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board == null ? -1f : board.timeUntilNextWave;
            }, "sun");
        }

        /// <summary>
        /// The whole clock in one pass, so a push cannot carry a wave from one frame and a countdown from
        /// another. False when this end has no lawn to read.
        /// </summary>
        public static bool ReadWaveClock(out int wave, out int maxWave, out float timeUntilNextWave)
        {
            int readWave = -1;
            int readMax = -1;
            float readNext = -1f;

            bool found = ApiBase.Query("ReadWaveClock", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    return false;
                }

                readWave = board.theWave;
                readMax = board.theMaxWave;
                readNext = board.timeUntilNextWave;
                return true;
            }, "sun");

            wave = readWave;
            maxWave = readMax;
            timeUntilNextWave = readNext;
            return found;
        }

        /// <summary>
        /// Makes this lawn's countdown the host's. The three numbers are plain fields the game's own update
        /// writes; there is no native setter to call, so a peer stores them directly. The caller is the
        /// session tick on the game's main thread, which is why this write needs no queue of its own.
        /// </summary>
        public static bool ApplyWaveClock(int wave, int maxWave, float timeUntilNextWave)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "ApplyWaveClock was called off the game's main thread");
                return false;
            }

            Il2Cpp.Board board = Entities.Board();
            if (board == null)
            {
                return false;
            }

            board.theWave = wave;
            board.theMaxWave = maxWave;
            board.timeUntilNextWave = timeUntilNextWave;
            return true;
        }

        public static bool CanStartNextWave()
        {
            return ApiBase.Query("CanStartNextWave", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                return board != null && board.CanStartNextWave;
            }, "sun");
        }

        public static bool StartNextWave()
        {
            return ApiBase.TryCall("StartNextWave", () =>
            {
                Il2Cpp.Board board = Entities.Board();
                if (board == null)
                {
                    Log.Warn("api", "StartNextWave -> no board on this lawn");
                    return;
                }

                int before = board.theWave;
                board.StartNextWave();
                Log.Api("StartNextWave -> wave " + before + " => " + board.theWave
                    + " of " + board.theMaxWave);
            }, "wave-host");
        }
    }
}
