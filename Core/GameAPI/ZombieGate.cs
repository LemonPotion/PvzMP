using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The hooks behind ZombieSync. Two of them stop the game on a peer and two only listen on the host:
    ///
    /// - `BoardSpawner.SummonZombies` is the wave roll. A peer skips it, the host runs it with the flag
    ///   that makes every zombie the roll creates travel.
    /// - `CreateZombie.SetZombie` is the one creator the roll goes through. Its postfix only acts while
    ///   that flag is up, so the host's other creators stay local.
    /// - `Mower.OnTriggerStay2D`, `Mower.StartMove` and `BoardSpawner.MowerStart` are the mower doors:
    ///   a peer skips all three, and only a replay of the host's event is let through by the flag.
    /// - `BoardSpawner.MowerStart` on the host is where both mower doors meet, so its postfix is the
    ///   single place a started mower is sent from.
    ///
    /// Every prefix answers through ZombieSync and returns true on any throw, so a broken hook leaves the
    /// game doing what it did before instead of freezing it.
    /// </summary>
    public static class ZombieGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.zombies").PatchAll(typeof(ZombieGate));
                Log.Info("hook", "waves, wave zombies and mowers are mirrored from the host "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(ZombieSync.Capability, "the wave and zombie hooks could not be installed");
                ApiBase.Disable(ZombieSync.MowerCapability, "the mower hooks could not be installed");
                Log.Error("hook", "gating waves, zombies and mowers failed: " + e.GetType().Name + " "
                    + e.Message + " - every end runs its own waves again and the lawns drift apart");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.BoardSpawner), "SummonZombies")]
        public static class SummonZombies
        {
            private static bool Prefix(int wave)
            {
                try
                {
                    return !ZombieSync.TryCaptureWaveSpawn(wave);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the wave gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game summon this wave itself");
                    return true;
                }
            }

            private static void Postfix()
            {
                try
                {
                    ZombieSync.EndWaveSpawn();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the wave flag could not be cleared: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.CreateZombie), "SetZombie")]
        public static class SetZombie
        {
            // Row is the call's own argument, not the object's field: the peer repeats the same call on
            // terrain the host already adjusted, so it must receive the same number the host passed.
            private static void Postfix(Il2Cpp.Zombie __result, int __0, float __2)
            {
                try
                {
                    if (!ApiBase.Present(__result))
                    {
                        return;
                    }

                    ZombieSync.NoteZombieCreated(__result, __0, (int)__result.theZombieType, __2);
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the zombie watch threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mower), "OnTriggerStay2D")]
        public static class MowerTouch
        {
            private static bool Prefix(Il2Cpp.Mower __instance)
            {
                try
                {
                    return !ZombieSync.TryCaptureMowerStart(__instance);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the mower trigger gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the mower react itself");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Mower), "StartMove")]
        public static class MowerStartMove
        {
            private static bool Prefix(Il2Cpp.Mower __instance)
            {
                try
                {
                    return !ZombieSync.TryCaptureMowerStart(__instance);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the mower start gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the mower start itself");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.BoardSpawner), "MowerStart")]
        public static class BoardSpawnerMowerStart
        {
            private static bool Prefix(int row)
            {
                try
                {
                    return !ZombieSync.TryCaptureMowerStart(row);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the mower bookkeeping gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game record the start");
                    return true;
                }
            }

            private static void Postfix(int row)
            {
                try
                {
                    ZombieSync.NoteMowerStarted(row);
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the mower watch threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
