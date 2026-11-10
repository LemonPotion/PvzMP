using System;
using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Identity and state of a plant, in types the rest of the mod may hold. An id is a Unity
    /// instance id: unique for the whole process and never reused, unlike an il2cpp object pointer,
    /// which the game's own entity pools hand out again after a recycle.
    /// </summary>
    public struct PlantState
    {
        public int InstanceId;
        public int PlantType;
        public int Row;
        public int Column;

        /// <summary>Everything the room shares about a plant besides its kind and cell (E72).</summary>
        public PlantFacts Facts;
    }

    /// <summary>Zombie health is long in this build while plant health is int; see ref/API-VERIFIED.md.</summary>
    public struct ZombieState
    {
        public int InstanceId;
        public int ZombieType;
        public int Row;
        public long Health;
        public long MaxHealth;
        public int FirstArmorHealth;
        public int SecondArmorHealth;
        public bool Alive;

        /// <summary>Hypnosis is a side, not a status: the host's flag is the room's answer (E73).</summary>
        public bool MindControlled;

        /// <summary>The base speed and the eight factors `Zombie.UpdateSpeed` multiplies it by.</summary>
        public float OriginSpeed;
        public float FreezeSpeed;
        public float ColdSpeed;
        public float ButterSpeed;
        public float KelpSpeed;
        public float GarlicSpeed;
        public float UniqueSpeed;
        public float HoleSpeed;
        public float MaLiangSpeed;

        /// <summary>The game's anchor position (`Entity.axis`), read only when a position push is due.</summary>
        public float X;
        public float Y;
    }

    /// <summary>
    /// Raw access to the two entity collections of the current lawn. Every method here must be
    /// called from inside an ApiBase.Call/Query lambda by a facade class; nothing in this file
    /// guards or logs on its own.
    /// </summary>
    internal static class Entities
    {
        internal static Il2Cpp.Board Board()
        {
            Il2Cpp.Board board = Il2Cpp.Board.Instance;
            return ApiBase.Present(board) ? board : null;
        }

        private static Il2CppSystem.Collections.Generic.List<Il2Cpp.Plant> Plants(Il2Cpp.Board board)
        {
            if (!ApiBase.Present(board.boardEntity))
            {
                return null;
            }

            return ApiBase.Present(board.boardEntity.plantArray) ? board.boardEntity.plantArray : null;
        }

        private static Il2CppSystem.Collections.Generic.List<Il2Cpp.Zombie> Zombies(Il2Cpp.Board board)
        {
            return ApiBase.Present(board.zombieArray) ? board.zombieArray : null;
        }

        internal static Il2Cpp.Plant FindPlant(int instanceId)
        {
            Il2Cpp.Board board = Board();
            if (board == null)
            {
                return null;
            }

            Il2CppSystem.Collections.Generic.List<Il2Cpp.Plant> plants = Plants(board);
            if (plants == null)
            {
                return null;
            }

            for (int i = 0; i < plants.Count; i++)
            {
                Il2Cpp.Plant plant = plants[i];
                if (ApiBase.Present(plant) && plant.GetInstanceID() == instanceId)
                {
                    return plant;
                }
            }

            return null;
        }

        internal static Il2Cpp.Zombie FindZombie(int instanceId)
        {
            Il2Cpp.Board board = Board();
            if (board == null)
            {
                return null;
            }

            Il2CppSystem.Collections.Generic.List<Il2Cpp.Zombie> zombies = Zombies(board);
            if (zombies == null)
            {
                return null;
            }

            for (int i = 0; i < zombies.Count; i++)
            {
                Il2Cpp.Zombie zombie = zombies[i];
                if (ApiBase.Present(zombie) && zombie.GetInstanceID() == instanceId)
                {
                    return zombie;
                }
            }

            return null;
        }

        internal static void ForEachPlant(Action<Il2Cpp.Plant> body)
        {
            Il2Cpp.Board board = Board();
            Il2CppSystem.Collections.Generic.List<Il2Cpp.Plant> plants = board == null ? null : Plants(board);
            if (plants == null)
            {
                return;
            }

            for (int i = 0; i < plants.Count; i++)
            {
                Il2Cpp.Plant plant = plants[i];
                if (ApiBase.Present(plant))
                {
                    body(plant);
                }
            }
        }

        internal static void ForEachZombie(Action<Il2Cpp.Zombie> body)
        {
            Il2Cpp.Board board = Board();
            Il2CppSystem.Collections.Generic.List<Il2Cpp.Zombie> zombies = board == null ? null : Zombies(board);
            if (zombies == null)
            {
                return;
            }

            for (int i = 0; i < zombies.Count; i++)
            {
                Il2Cpp.Zombie zombie = zombies[i];
                if (ApiBase.Present(zombie))
                {
                    body(zombie);
                }
            }
        }

        internal static string Describe(Il2Cpp.Plant plant)
        {
            return "Plant#" + plant.GetInstanceID() + " type=" + (int)plant.thePlantType
                + " cell=" + plant.thePlantColumn + "," + plant.thePlantRow
                + " hp=" + plant.thePlantHealth + "/" + plant.thePlantMaxHealth;
        }

        internal static string Describe(Il2Cpp.Zombie zombie)
        {
            return "Zombie#" + zombie.GetInstanceID() + " type=" + (int)zombie.theZombieType
                + " row=" + zombie.theZombieRow + " hp=" + zombie.theHealth
                + "/" + zombie.theMaxHealth + " alive=" + zombie.Alive;
        }
    }

    /// <summary>
    /// The collection half of the native surface: who is on the lawn right now. Used by the host to
    /// validate requests and by the reconcile loop to compare ids without touching il2cpp types.
    /// </summary>
    public static class BoardCollections
    {
        public static int[] PlantInstanceIds()
        {
            return ApiBase.Query("PlantInstanceIds", Collect, "core-board");
        }

        public static int[] ZombieInstanceIds()
        {
            return ApiBase.Query("ZombieInstanceIds", CollectZombies, "core-board");
        }

        private static int[] Collect()
        {
            var ids = new List<int>();
            Entities.ForEachPlant(plant => ids.Add(plant.GetInstanceID()));
            return ids.ToArray();
        }

        private static int[] CollectZombies()
        {
            var ids = new List<int>();
            Entities.ForEachZombie(zombie => ids.Add(zombie.GetInstanceID()));
            return ids.ToArray();
        }
    }
}
