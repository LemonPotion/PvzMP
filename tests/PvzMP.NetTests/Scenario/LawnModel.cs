using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.NetTests.Scenario
{
    /// <summary>One plant of a model lawn, with the local object identity a rebuilt plant would get.</summary>
    internal sealed class LawnPlant
    {
        public int ObjectId;
        public int NetId;
        public int PlantType;
        public int Column;
        public int Row;
        public PlantFacts Facts;

        /// <summary>In a cart on the room's word: the layout neither shows nor moves it until the drop.</summary>
        public bool Away;
    }

    /// <summary>What one layout entry did to a model lawn, in the counts the game's own log line uses.</summary>
    internal sealed class LayoutReport
    {
        public int Spawned;
        public int Moved;
        public int Retyped;
        public int Aligned;
        public int Healed;
        public int Damaged;
        public int LevelUps;
        public int Removed;
        public int Kept;
    }

    /// <summary>
    /// The plant list of one lawn as the layout sees it: a room id, a kind, a cell, the facts and the
    /// local object. It answers a layout entry the way `PlantSync.ApplyLayout` does - spawn what is not
    /// here, grow what changed kind, move what changed cell, take the host's facts through the shipped
    /// mapping and health rules, and remove what the host stops naming - so a scenario over real sockets
    /// proves what a seat actually holds after the frame, not only what was sent.
    /// </summary>
    internal sealed class LawnModel
    {
        private int _nextObject = 100;

        public readonly List<LawnPlant> Plants = new List<LawnPlant>();

        public LawnPlant Find(int netId)
        {
            for (int i = 0; i < Plants.Count; i++)
            {
                if (Plants[i].NetId == netId)
                {
                    return Plants[i];
                }
            }

            return null;
        }

        public LawnPlant On(int column, int row)
        {
            for (int i = 0; i < Plants.Count; i++)
            {
                if (Plants[i].Column == column && Plants[i].Row == row)
                {
                    return Plants[i];
                }
            }

            return null;
        }

        public int CountOn(int column, int row)
        {
            int found = 0;

            for (int i = 0; i < Plants.Count; i++)
            {
                if (Plants[i].Column == column && Plants[i].Row == row)
                {
                    found++;
                }
            }

            return found;
        }

        public LawnPlant Put(int netId, int plantType, int column, int row, PlantFacts facts)
        {
            var plant = new LawnPlant
            {
                ObjectId = _nextObject++,
                NetId = netId,
                PlantType = plantType,
                Column = column,
                Row = row,
                Facts = facts
            };

            Plants.Add(plant);
            return plant;
        }

        public LawnPlant Put(int netId, int plantType, int column, int row, int health, int maxHealth)
        {
            return Put(netId, plantType, column, row, new PlantFacts
            {
                Health = health,
                MaxHealth = maxHealth,
                Level = 1
            });
        }

        /// <summary>The host's side of a walk: every plant this lawn can name, as one frame.</summary>
        public PlantSnap[] Layout()
        {
            var snaps = new List<PlantSnap>(Plants.Count);

            for (int i = 0; i < Plants.Count; i++)
            {
                LawnPlant plant = Plants[i];

                if (plant.Away || plant.ObjectId == 0)
                {
                    continue;
                }

                snaps.Add(PlantSnapRules.Of(plant.NetId, plant.PlantType, plant.Column, plant.Row,
                    plant.Facts));
            }

            return snaps.ToArray();
        }

        public LayoutReport ApplyLayout(PlantSnap[] wanted)
        {
            var report = new LayoutReport();
            var named = new HashSet<int>();

            for (int i = 0; i < wanted.Length; i++)
            {
                if (wanted[i] != null && wanted[i].NetId > 0)
                {
                    named.Add(wanted[i].NetId);
                }
            }

            for (int i = 0; i < wanted.Length; i++)
            {
                PlantSnap snap = wanted[i];

                if (snap == null || snap.NetId <= 0)
                {
                    continue;
                }

                LawnPlant plant = Find(snap.NetId);

                if (plant != null && plant.Away)
                {
                    report.Kept++;
                    continue;
                }

                bool fresh = plant == null;

                if (fresh)
                {
                    plant = new LawnPlant { ObjectId = _nextObject++, NetId = snap.NetId };
                    Plants.Add(plant);
                    report.Spawned++;
                }
                else if (plant.PlantType != snap.PlantType)
                {
                    // The game's growth: the old object is destroyed and the new one starts empty, so
                    // everything but the room id is the host's to say again.
                    plant.ObjectId = _nextObject++;
                    plant.Facts = default(PlantFacts);
                    report.Retyped++;
                }

                plant.PlantType = snap.PlantType;

                if (plant.Column != snap.Column || plant.Row != snap.Row)
                {
                    plant.Column = snap.Column;
                    plant.Row = snap.Row;

                    if (!fresh)
                    {
                        report.Moved++;
                    }
                }

                PlantFacts host = PlantSnapRules.FactsOf(snap);

                if (!plant.Facts.SameAs(host))
                {
                    Take(plant, host, report);
                    report.Aligned++;
                }

                report.Kept++;
            }

            for (int i = Plants.Count - 1; i >= 0; i--)
            {
                LawnPlant plant = Plants[i];

                if (named.Contains(plant.NetId) || plant.Away)
                {
                    continue;
                }

                Plants.RemoveAt(i);
                report.Removed++;
            }

            return report;
        }

        /// <summary>
        /// The host's numbers, through the same health rule the game's doors are chosen by: a gap in
        /// health is a heal or a damage of exactly that amount, and the rest are the fields the host's
        /// copy already carries.
        /// </summary>
        private static void Take(LawnPlant plant, PlantFacts host, LayoutReport report)
        {
            int amount;

            switch (PlantFactsRules.Between(plant.Facts.Health, host.Health, out amount))
            {
                case HealthStep.Heal:
                    report.Healed++;
                    break;

                case HealthStep.Damage:
                    report.Damaged++;
                    break;
            }

            if (host.Level > plant.Facts.Level)
            {
                report.LevelUps++;
            }

            plant.Facts = host;
        }
    }
}
