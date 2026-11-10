using System;
using System.Collections.Concurrent;

namespace PvzMP.Core
{
    /// <summary>
    /// Only path from worker threads into il2cpp. Anything that touches the game has to be
    /// posted here and runs on the Unity main thread once per frame.
    /// </summary>
    public static class MainThreadPump
    {
        public const int CriticalBatch = 64;
        public const int CosmeticBatch = 4;
        public const int CosmeticBacklogLimit = 8;

        private static readonly ConcurrentQueue<Action> Critical = new ConcurrentQueue<Action>();
        private static readonly ConcurrentQueue<Action> Cosmetic = new ConcurrentQueue<Action>();
        private static long _droppedCosmetic;

        public static int PendingCritical { get { return Critical.Count; } }
        public static int PendingCosmetic { get { return Cosmetic.Count; } }
        public static long DroppedCosmetic { get { return System.Threading.Interlocked.Read(ref _droppedCosmetic); } }

        /// <summary>Gameplay events: never discarded, the remainder simply runs next frame.</summary>
        public static void Post(Action work)
        {
            if (work != null)
            {
                Critical.Enqueue(work);
            }
        }

        /// <summary>
        /// Cursor and preview state. Only the freshest values matter, so a backlog is thrown
        /// away instead of replaying stale positions.
        /// </summary>
        public static void PostCosmetic(Action work)
        {
            if (work == null)
            {
                return;
            }

            Cosmetic.Enqueue(work);
            int excess = Cosmetic.Count - CosmeticBacklogLimit;
            Action stale;
            for (int i = 0; i < excess; i++)
            {
                if (!Cosmetic.TryDequeue(out stale))
                {
                    break;
                }

                System.Threading.Interlocked.Increment(ref _droppedCosmetic);
            }
        }

        public static void Run()
        {
            int budget = CriticalBatch;
            Action work;
            while (budget-- > 0 && Critical.TryDequeue(out work))
            {
                Execute(work, "critical");
            }

            budget = CosmeticBatch;
            while (budget-- > 0 && Cosmetic.TryDequeue(out work))
            {
                Execute(work, "cosmetic");
            }
        }

        private static void Execute(Action work, string lane)
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                Log.Error("pump", lane + " item threw " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
