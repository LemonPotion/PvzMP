using System;
using System.Collections.Generic;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// What the game itself put into a coin object. The wire cannot invent these numbers: SetCoin takes
    /// bare ints for item and move type, their meaning is not in the dump, and a CoinSun carries the
    /// values it was created with, so a peer recreates a pickup from what the host observed rather than
    /// from a guessed constant. Proof: ref/EVIDENCE.md E7, E8.
    /// </summary>
    public struct CoinKind
    {
        public int CoinType;
        public int MoveType;
        public int SunPrice;
    }

    /// <summary>
    /// Sun and the other pickups. The board keeps no collection of coins, so the handles have to live
    /// behind this boundary.
    ///
    /// A coin is named by its `CoinSun` component and not by its `GameObject`, because that is the object
    /// the game itself hands to the code that ends a coin: `CoinSun.Die` is a method of the component, and
    /// the gate that watches a coin being collected is told the component. One name for both doors, and no
    /// lookup between them that could fail. Proof: ref/EVIDENCE.md E9, E10, E11.
    /// </summary>
    public static class Coins
    {
        private static readonly Dictionary<int, Il2Cpp.CoinSun> Live =
            new Dictionary<int, Il2Cpp.CoinSun>();

        /// <summary>
        /// The name of a coin the game just made, or 0 when the object is not one the mod can hold: not
        /// every object the creator returns carries the component that makes it a coin, and a coin the mod
        /// cannot name is a coin the room leaves to the game.
        /// </summary>
        public static int Adopt(UnityEngine.GameObject coin)
        {
            return ApiBase.Query("AdoptCoin", () =>
            {
                if (!ApiBase.Present(coin))
                {
                    return 0;
                }

                Il2Cpp.CoinSun sun = Sun(coin);
                if (!ApiBase.Present(sun))
                {
                    return 0;
                }

                int instanceId = sun.GetInstanceID();
                Live[instanceId] = sun;
                Prune();

                return instanceId;
            }, "core-item");
        }

        /// <summary>
        /// The game's own creator for a pickup, asked with the numbers the host read off its own coin, so
        /// the coin that appears here is made the same way as the one that appeared there.
        /// </summary>
        public static int Spawn(int column, int row, CoinKind kind)
        {
            return ApiBase.Call("SpawnCoin(cell=" + column + "," + row + ",coin=" + kind.CoinType
                + ",move=" + kind.MoveType + ")", () =>
            {
                Il2Cpp.CreateItem creator = Il2Cpp.CreateItem.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "SpawnCoin -> no CreateItem instance");
                    return 0;
                }

                UnityEngine.GameObject coin = creator.SetCoin(column, row, kind.CoinType, kind.MoveType);
                if (!ApiBase.Present(coin))
                {
                    Log.Warn("api", "SpawnCoin -> native creator returned no object");
                    return 0;
                }

                int instanceId = Adopt(coin);

                Log.Api("SpawnCoin -> " + (instanceId == 0 ? "no CoinSun on the object the game made"
                    : Describe(instanceId)));

                return instanceId;
            }, "core-item");
        }

        /// <summary>The game's own end of a coin: the component's own death, which is what a collection walks.</summary>
        public static bool Despawn(int instanceId)
        {
            return ApiBase.TryCall("DespawnCoin(#" + instanceId + ")", () =>
            {
                Il2Cpp.CoinSun sun = Handle(instanceId);
                if (sun == null)
                {
                    Log.Warn("api", "DespawnCoin -> no coin #" + instanceId + " here");
                    return;
                }

                sun.Die();
                Live.Remove(instanceId);
            }, "core-item");
        }

        /// <summary>
        /// What a coin this end knows is worth, read off the game's own component. A coin this end has no
        /// handle for reads as no price at all, which is a coin the room charges nothing for.
        /// </summary>
        public static CoinKind KindOf(int instanceId)
        {
            return ApiBase.Query("ReadCoinKind(#" + instanceId + ")", () =>
            {
                Il2Cpp.CoinSun sun = Handle(instanceId);

                return sun == null ? default(CoinKind) : Kind(sun);
            }, "core-item");
        }

        /// <summary>A coin in the words a log line needs, including the number the game itself pays for it.</summary>
        public static string Describe(int instanceId)
        {
            Il2Cpp.CoinSun sun = Handle(instanceId);

            return sun == null ? "Coin#" + instanceId + " this end has no handle for"
                : "Coin#" + instanceId + " coin=" + sun.theCoinType + " move=" + sun.theMoveType
                  + " sun=" + sun.sunPrice + " cost=" + sun.realCost;
        }

        /// <summary>
        /// The coins this end still holds a living handle for: the only answer to "what is lying on this
        /// lawn that the room has no name for".
        /// </summary>
        public static List<int> Held()
        {
            var ids = new List<int>();

            foreach (KeyValuePair<int, Il2Cpp.CoinSun> entry in Live)
            {
                if (ApiBase.Present(entry.Value))
                {
                    ids.Add(entry.Key);
                }
            }

            return ids;
        }

        private static Il2Cpp.CoinSun Handle(int instanceId)
        {
            Il2Cpp.CoinSun sun;
            if (!Live.TryGetValue(instanceId, out sun) || !ApiBase.Present(sun))
            {
                return null;
            }

            return sun;
        }

        private static Il2Cpp.CoinSun Sun(UnityEngine.GameObject coin)
        {
            return coin.GetComponent<Il2Cpp.CoinSun>();
        }

        private static CoinKind Kind(Il2Cpp.CoinSun sun)
        {
            return new CoinKind
            {
                CoinType = sun.theCoinType,
                MoveType = sun.theMoveType,
                SunPrice = sun.sunPrice
            };
        }

        private static void Prune()
        {
            if (Live.Count < 32)
            {
                return;
            }

            var dead = new List<int>();
            foreach (KeyValuePair<int, Il2Cpp.CoinSun> entry in Live)
            {
                if (!ApiBase.Present(entry.Value))
                {
                    dead.Add(entry.Key);
                }
            }

            foreach (int id in dead)
            {
                Live.Remove(id);
            }
        }
    }
}
