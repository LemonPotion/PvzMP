using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests.Scenario
{
    /// <summary>
    /// Whole rounds of a room, not one rule at a time: a host lawn, one or two seats, the real
    /// `PlantSnapshot` frame over loopback sockets, and the model lawn each seat ends up holding.
    /// The mapping under test is the shipped `PlantSnapRules`; the health and death rules are the
    /// shipped `PlantFactsRules`.
    /// </summary>
    public class PlantScenarioTests
    {
        [Fact]
        public void ALateSeatGetsTheWholeLawnWithEveryFact()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                PlantFacts sunflower = Facts(280, 300);
                PlantFacts fusion = Facts(900, 1200);
                PlantFacts tower = Facts(480, 500);

                room.HostLawn.Put(1, 18, 0, 0, sunflower);
                room.HostLawn.Put(2, 1005, 3, 2, fusion);
                room.HostLawn.Put(3, 352, 5, 4, tower);

                // The seat joins after the lawn already stood, which is the late-join case.
                room.Join();

                new Timeline()
                    .Step("the host walks its lawn once", room.SendLayout)
                    .Step("the late seat holds all three plants", () =>
                    {
                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(3, report.Spawned);
                        Assert.Equal(3, report.Kept);
                        Assert.Equal(0, report.Removed);

                        LawnModel seat = room.ClientLawns[0];

                        Assert.True(sunflower.SameAs(seat.Find(1).Facts));
                        Assert.True(fusion.SameAs(seat.Find(2).Facts));
                        Assert.True(tower.SameAs(seat.Find(3).Facts));

                        Assert.Equal(3, seat.Find(2).Column);
                        Assert.Equal(2, seat.Find(2).Row);
                        Assert.Equal(1005, seat.Find(2).PlantType);
                        Assert.Equal(6, seat.Find(2).Facts.Level);
                        Assert.Equal(11, seat.Find(2).Facts.Kills);
                    })
                    .Run();
            }
        }

        [Fact]
        public void EverySeatConvergesOnTheHostsNumbersWithinOneLayout()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                LawnPlant host = room.HostLawn.Put(1, 18, 1, 1, Facts(300, 300));

                room.Join();
                room.Join();

                room.SendLayout();
                room.TakeLayout(0);
                room.TakeLayout(1);

                new Timeline()
                    .Step("the host's plant is hurt, grown and given kills", () =>
                    {
                        host.Facts.Health = 120;
                        host.Facts.Level = 8;
                        host.Facts.Kills = 9;
                        room.SendLayout();
                    })
                    .Step("both seats take the damage, the level and the kills", () =>
                    {
                        for (int seat = 0; seat < 2; seat++)
                        {
                            LayoutReport report = room.TakeLayout(seat);

                            Assert.Equal(1, report.Aligned);
                            Assert.Equal(1, report.Damaged);
                            Assert.Equal(1, report.LevelUps);
                            Assert.True(host.Facts.SameAs(room.ClientLawns[seat].Find(1).Facts));
                            Assert.Equal(120, room.ClientLawns[seat].Find(1).Facts.Health);
                        }
                    })
                    .Run();
            }
        }

        [Fact]
        public void AGrowthInPlaceArrivesAsANewObjectAtTheHostsLevel()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                LawnPlant host = room.HostLawn.Put(1, 18, 2, 2, Facts(300, 300));

                room.Join();
                room.SendLayout();
                room.TakeLayout(0);

                LawnModel seat = room.ClientLawns[0];
                int before = seat.Find(1).ObjectId;

                new Timeline()
                    .Step("the host's plant fuses in place and grows", () =>
                    {
                        host.PlantType = 1005;
                        host.Facts = Facts(900, 1200);
                        host.Facts.Level = 5;
                        room.SendLayout();
                    })
                    .Step("the seat rebuilds it instead of keeping the old object", () =>
                    {
                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(1, report.Retyped);
                        Assert.Equal(1, report.Aligned);
                        Assert.Equal(1, report.LevelUps);

                        LawnPlant grown = seat.Find(1);
                        Assert.NotEqual(before, grown.ObjectId);
                        Assert.Equal(1005, grown.PlantType);
                        Assert.Equal(2, grown.Column);
                        Assert.Equal(2, grown.Row);
                        Assert.True(host.Facts.SameAs(grown.Facts));
                    })
                    .Run();
            }
        }

        [Fact]
        public void APlantTheHostStopsNamingLeavesEverySeat()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                LawnPlant first = room.HostLawn.Put(1, 18, 0, 0, Facts(300, 300));
                room.HostLawn.Put(2, 22, 1, 0, Facts(300, 300));

                room.Join();
                room.Join();
                room.SendLayout();
                room.TakeLayout(0);
                room.TakeLayout(1);

                new Timeline()
                    .Step("the host's plant dies and the lawn stops naming it", () =>
                    {
                        room.HostLawn.Plants.Remove(room.HostLawn.Find(2));
                        room.SendLayout();
                    })
                    .Step("both seats remove exactly that plant", () =>
                    {
                        for (int seat = 0; seat < 2; seat++)
                        {
                            LayoutReport report = room.TakeLayout(seat);

                            Assert.Equal(1, report.Removed);
                            Assert.Null(room.ClientLawns[seat].Find(2));
                            Assert.True(first.Facts.SameAs(room.ClientLawns[seat].Find(1).Facts));
                        }
                    })
                    .Run();
            }
        }

        [Fact]
        public void TwoPlantsOnOneCellBothSurviveTheLayout()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                PlantFacts lower = Facts(300, 300);
                PlantFacts upper = Facts(650, 900);

                LawnPlant onion = room.HostLawn.Put(1, 18, 4, 3, lower);
                LawnPlant fuse = room.HostLawn.Put(2, 1005, 4, 3, upper);

                room.Join();
                room.SendLayout();
                room.TakeLayout(0);

                LawnModel seat = room.ClientLawns[0];

                Assert.Equal(2, seat.CountOn(4, 3));
                Assert.True(lower.SameAs(seat.Find(1).Facts));
                Assert.True(upper.SameAs(seat.Find(2).Facts));

                new Timeline()
                    .Step("only the upper plant is hurt on the host", () =>
                    {
                        fuse.Facts.Health = 300;
                        room.SendLayout();
                    })
                    .Step("one entry changes and the other stays exactly as it was", () =>
                    {
                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(1, report.Aligned);
                        Assert.Equal(1, report.Damaged);
                        Assert.Equal(2, seat.CountOn(4, 3));

                        Assert.True(lower.SameAs(seat.Find(1).Facts));
                        Assert.Equal(300, seat.Find(2).Facts.Health);
                        Assert.True(onion.Facts.SameAs(seat.Find(1).Facts));
                    })
                    .Run();
            }
        }

        [Fact]
        public void ACartPlantWaitsForTheDropInsteadOfTheLayout()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                LawnPlant host = room.HostLawn.Put(9, 18, 2, 2, Facts(300, 300));

                room.Join();
                room.SendLayout();
                room.TakeLayout(0);

                LawnModel seat = room.ClientLawns[0];

                new Timeline()
                    .Step("the cart lifts the plant on this seat", () =>
                    {
                        seat.Find(9).Away = true;
                        host.Column = 6;
                        host.Row = 1;
                        room.SendLayout();
                    })
                    .Step("the layout neither moves nor removes the carried plant", () =>
                    {
                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(0, report.Removed);
                        Assert.Equal(0, report.Moved);
                        Assert.Equal(2, seat.Find(9).Column);
                        Assert.Equal(2, seat.Find(9).Row);
                    })
                    .Step("the drop names the new cell", () =>
                    {
                        seat.Find(9).Away = false;
                        room.SendLayout();

                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(1, report.Moved);
                        Assert.Equal(6, seat.Find(9).Column);
                        Assert.Equal(1, seat.Find(9).Row);
                    })
                    .Run();
            }
        }

        [Fact]
        public void APeerHealsAZeroHealthPlantBackToTheHostsNumber()
        {
            using (RoomHarness room = RoomHarness.Start())
            {
                LawnPlant host = room.HostLawn.Put(1, 18, 0, 0, Facts(300, 300));

                room.Join();
                room.SendLayout();
                room.TakeLayout(0);

                LawnModel seat = room.ClientLawns[0];

                new Timeline()
                    .Step("this seat's own game brought the plant to zero", () =>
                    {
                        seat.Find(1).Facts.Health = 0;

                        // The death is held on this seat: the plant is a room plant and the host has
                        // not stopped naming it, so the local roll is not the sentence.
                        Assert.False(PlantFactsRules.MayDie(true, true, false, false, true));
                    })
                    .Step("the host still names it alive and whole", () =>
                    {
                        Assert.True(host.Facts.Health > 0);
                        room.SendLayout();

                        LayoutReport report = room.TakeLayout(0);

                        Assert.Equal(1, report.Aligned);
                        Assert.Equal(1, report.Healed);
                        Assert.Equal(300, seat.Find(1).Facts.Health);
                    })
                    .Run();
            }
        }

        /// <summary>Every fact the room keeps, filled with a value that is not the zero of any other.</summary>
        private static PlantFacts Facts(int health, int maxHealth)
        {
            return new PlantFacts
            {
                Health = health,
                MaxHealth = maxHealth,
                Shield = 40,
                Level = 6,
                Stage = 2,
                Skin = 9,
                Magnet = 3,
                Wheat = 4,
                ShootLevel = 2,
                ShootCurse = 5,
                Kills = 11,
                Puff = 2,
                Butter = 7,
                Attribute = 8,
                Disable = 1,
                Freeze = 3,
                FirstParent = 18,
                SecondParent = 22,
                LilyType = 30,
                Flags = PlantFactsRules.Pack(true, false, true, false, true, false, true)
            };
        }
    }
}
