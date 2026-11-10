using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// A room plant dies on the host's word. The host's own lawn runs `Plant.Die` as it always did; a
    /// peer holds the death of a plant the ledger numbers until the host's layout stops naming it, and
    /// the mod's own removal - that layout, the host's shovel, the host's despawn - sets
    /// `Plants.ForTheRoom` and is allowed through. A plant the ledger does not number is not a room plant
    /// and dies exactly as the game says, which is also the answer outside a level and outside a room.
    ///
    /// `Die` is virtual and four kinds in this build replace it - GoldSquash, Leigon, PortalNut and
    /// UltimatePortalNut - so every declaration in the plant family is a door here, found by walking the
    /// game's types rather than named one by one. Proof: ref/EVIDENCE.md E73.
    /// </summary>
    [HarmonyPatch]
    public static class PlantDeath
    {
        /// <summary>Room ids already told about a held death, so a plant that keeps asking logs once.</summary>
        private static readonly HashSet<int> _held = new HashSet<int>();

        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.plant-death").PatchAll(typeof(PlantDeath));
                Log.Info("hook", "a room plant dies on the host's word " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                // The cost of no gate is a plant the host keeps alive being destroyed here and rebuilt
                // by the next layout, which loses this lawn's animation and its cart state with it.
                Log.Error("hook", "gating the plant death doors failed: " + e.GetType().Name + " "
                    + e.Message + " - a plant brought down here may die before the host says so");
            }
        }

        public static void Forget()
        {
            _held.Clear();
        }

        /// <summary>
        /// Every declaration of `Die` in the game's plant family. The interop assembly carries one proxy
        /// per game type, so a subclass that replaces the death body is its own target; patching only
        /// `Plant` itself would let four kinds die on a peer's local roll.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var doors = new List<MethodBase>();
            Type plant = typeof(Il2Cpp.Plant);

            try
            {
                Type[] types = plant.Assembly.GetTypes();

                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];

                    if (!plant.IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo die = type.GetMethod("Die",
                        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(Il2Cpp.Plant.DieReason) }, null);

                    if (die != null && !die.IsAbstract)
                    {
                        doors.Add(die);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("hook", "listing the plant death doors failed: " + e.GetType().Name + " "
                    + e.Message + " - only the doors named before the failure are gated");
            }

            Log.Api("the plant death doors are: " + doors.Count + " declaration(s) of Die");

            return doors;
        }

        private static bool Prefix(Il2Cpp.Plant __instance)
        {
            try
            {
                MpSession room = PlantSync.Room;
                if (__instance == null || room == null)
                {
                    return true;
                }

                NetEntity record = room.Entities.ByInstanceId(__instance.GetInstanceID());
                bool roomBound = record != null && record.Kind == NetKind.Plant;

                if (PlantFactsRules.MayDie(room.RoomDecides, LevelSync.TheRoomIsInALevel, room.IsHost,
                        Plants.ForTheRoom, roomBound))
                {
                    return true;
                }

                if (_held.Add(record.NetId))
                {
                    Log.Info("mp", "plant #" + record.NetId + " was brought down on this lawn, but a room "
                        + "plant dies on the host's word: it stays up until the host's layout stops naming"
                        + " it " + ApiBase.FrameTag());
                }

                return false;
            }
            catch (Exception e)
            {
                Log.Warn("hook", "the death gate threw: " + e.GetType().Name + " " + e.Message
                    + ", letting the game run its own death");
                return true;
            }
        }
    }
}
