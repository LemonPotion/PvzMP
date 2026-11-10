using System;
using System.Collections.Generic;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Guards shared by every native call: main-thread affinity, failure accounting and
    /// liveness checks on il2cpp object handles. Contains no gameplay rules.
    /// </summary>
    public static class ApiBase
    {
        public const int BreakerThreshold = 5;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, int> Streak = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> OpenBreakers = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> SaidBroken = new HashSet<string>(StringComparer.Ordinal);
        private static int _ownerThreadId = -1;
        private static int _totalFailures;

        public static int FailureCount { get { lock (Sync) { return _totalFailures; } } }

        /// <summary>
        /// How many native calls the mod is carrying out on the game's main thread right now. Above zero,
        /// whatever the game makes is something the mod asked for: a placement the room confirmed, an
        /// object a layout replay put down, the level the room is loading. At zero, this end's own game
        /// code made it, and no other end knows about it yet.
        ///
        /// Only `Call` and `TryCall` raise it, because those are the two doors the mod asks the game to do
        /// something; `Query` reads, and reads make nothing. A plain counter is enough because the guard
        /// already refuses off-thread calls: one thread, one number.
        /// </summary>
        public static int ModCallDepth { get { return _modCallDepth; } }

        private static int _modCallDepth;

        public static void AttachMainThread()
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            Log.Info("api", "main thread captured: id=" + _ownerThreadId + " " + FrameTag());
        }

        public static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _ownerThreadId;

        public static string FrameTag()
        {
            try
            {
                return "frame=" + UnityEngine.Time.frameCount;
            }
            catch
            {
                return "frame=?";
            }
        }

        public static bool IsBroken(string capability)
        {
            lock (Sync)
            {
                return OpenBreakers.Contains(capability ?? string.Empty);
            }
        }

        /// <summary>Used by NativeProbe to switch a whole capability off before it ever gets called.</summary>
        public static void Disable(string capability, string reason)
        {
            lock (Sync)
            {
                OpenBreakers.Add(capability);
            }
            Log.Warn("api", "capability disabled: " + capability + " (" + reason + ")");
        }

        /// <summary>
        /// An il2cpp handle is usable only when the pointer is non-zero, the native object was not
        /// collected, and the Unity object behind it is still alive. A destroyed Unity object keeps both
        /// the managed handle and a live-looking pointer, and every call into it throws: only Unity's own
        /// alive check can tell it from one still in the scene (E61).
        /// </summary>
        public static bool Present(Il2CppObjectBase handle)
        {
            if (handle == null)
            {
                return false;
            }

            try
            {
                if (handle.Pointer == IntPtr.Zero || handle.WasCollected)
                {
                    return false;
                }

                UnityEngine.Object unity = handle as UnityEngine.Object;
                return unity == null || (bool)unity;
            }
            catch (Exception e)
            {
                Log.Warn("api", "liveness check threw: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static T Call<T>(string method, Func<T> nativeCall, string capability = null)
        {
            if (!Guard(method, capability))
            {
                return default(T);
            }

            try
            {
                _modCallDepth++;

                T result = nativeCall();
                Succeeded(method, result);
                return result;
            }
            catch (Exception e)
            {
                Failed(method, capability, e);
                return default(T);
            }
            finally
            {
                _modCallDepth--;
            }
        }

        /// <summary>
        /// Same guard and failure accounting as Call, but silent on success. Reads that run every
        /// poll (board state, entity lists) must not push a line per call into the log, because the
        /// rule the project runs on is that a log line means a networked or mutating action happened.
        /// </summary>
        public static T Query<T>(string method, Func<T> nativeCall, string capability = null)
        {
            if (!Guard(method, capability))
            {
                return default(T);
            }

            try
            {
                T result = nativeCall();
                Succeeded(method);
                return result;
            }
            catch (Exception e)
            {
                Failed(method, capability, e);
                return default(T);
            }
        }

        public static bool TryCall(string method, Action nativeCall, string capability = null)
        {
            if (!Guard(method, capability))
            {
                return false;
            }

            try
            {
                _modCallDepth++;

                nativeCall();
                Succeeded(method, "ok");
                return true;
            }
            catch (Exception e)
            {
                Failed(method, capability, e);
                return false;
            }
            finally
            {
                _modCallDepth--;
            }
        }

        private static bool Guard(string method, string capability)
        {
            if (!OnMainThread)
            {
                Log.Error("api", "off-thread call blocked: " + method
                    + " (thread=" + Thread.CurrentThread.ManagedThreadId + ") " + FrameTag());
                return false;
            }

            if (capability != null && IsBroken(capability))
            {
                // Said once, because the answer this guard gives is now the same forever: every later
                // read of that door answers with nothing, and that nothing must not look like a value.
                bool first;
                lock (Sync)
                {
                    first = SaidBroken.Add(capability);
                }

                if (first)
                {
                    Log.Info("api", "the " + capability + " reads are disabled on this lawn: every call "
                        + "of that door answers with nothing for the rest of the session " + FrameTag());
                }

                return false;
            }

            return true;
        }

        private static void Succeeded(string method)
        {
            lock (Sync)
            {
                Streak.Remove(method);
            }
        }

        private static void Succeeded(string method, object result)
        {
            Succeeded(method);
            Log.Api(method + " -> " + Describe(result) + " " + FrameTag());
        }

        private static void Failed(string method, string capability, Exception e)
        {
            int streak;
            lock (Sync)
            {
                _totalFailures++;
                Streak.TryGetValue(method, out streak);
                streak++;
                Streak[method] = streak;
            }

            Log.Error("api", method + " -> FAIL " + e.GetType().Name + ": " + e.Message
                + " " + FrameTag() + " (total failures=" + FailureCount + ")");

            if (streak >= BreakerThreshold && capability != null && !IsBroken(capability))
            {
                Disable(capability, streak + " consecutive failures in " + method);
            }
        }

        private static string Describe(object result)
        {
            if (result == null)
            {
                return "null";
            }

            if (result is Il2CppObjectBase handle)
            {
                return handle.GetType().Name + "@0x" + handle.Pointer.ToInt64().ToString("X");
            }

            return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
