using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The mod's own logging entry point is redirected into memory before any test runs, so the
    /// transport can assert that a networked action produced a log line - the acceptance rule is the
    /// same here as in the game. Without this the first MelonLogger call would try to load MelonLoader.
    /// </summary>
    internal static class TestLog
    {
        private static readonly object Sync = new object();
        private static readonly List<string> Lines = new List<string>();

        [ModuleInitializer]
        internal static void RedirectModLogging()
        {
            Log.Sink = (level, text) =>
            {
                lock (Sync)
                {
                    Lines.Add(level + " " + text);
                }
            };
        }

        internal static void Clear()
        {
            lock (Sync)
            {
                Lines.Clear();
            }
        }

        internal static bool Has(string fragment)
        {
            lock (Sync)
            {
                for (int i = 0; i < Lines.Count; i++)
                {
                    if (Lines[i].Contains(fragment))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string Describe()
        {
            lock (Sync)
            {
                return string.Join(" | ", Lines.ToArray());
            }
        }
    }

    internal static class Wait
    {
        internal static bool Until(Func<bool> condition, int timeoutMillis = 3000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMillis)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return condition();
        }
    }
}
