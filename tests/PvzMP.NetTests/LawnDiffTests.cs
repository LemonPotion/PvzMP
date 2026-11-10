using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// What the host says after it ran the game's own creator. The reduction is pure: two snapshots of
    /// one lawn in, the net ids that have to travel out. The game's fusion itself is CreatePlant.SetPlant
    /// running CheckMix, which no test can prove here - the live run does that; these tests prove that
    /// whatever the game left behind is named correctly and in an order a peer can replay.
    /// </summary>
    public class LawnDiffTests
    {
        [Fact]
        public void AnUntouchedLawnSaysNothingHappened()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity pea = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            ledger.Bind(pea.NetId, 100);

            LawnPlant[] before = { Plant(100, 0, 3, 1) };

            LawnChange change = LawnDiff.Of(ledger, before, before);

            Assert.True(change.IsEmpty);
            Assert.Empty(change.GoneNetIds);
            Assert.Empty(change.Appeared);
        }

        [Fact]
        public void ANewPlantIsTheOnlyThingReported()
        {
            EntityLedger ledger = new EntityLedger();

            LawnChange change = LawnDiff.Of(ledger, new LawnPlant[0], new[] { Plant(100, 4, 3, 1) });

            Assert.Empty(change.GoneNetIds);
            Assert.Single(change.Appeared);
            Assert.Equal(100, change.Appeared[0].InstanceId);
            Assert.Equal(4, change.Appeared[0].PlantType);
            Assert.Equal(3, change.Appeared[0].Column);
            Assert.Equal(1, change.Appeared[0].Row);
            Assert.False(change.IsEmpty);
        }

        [Fact]
        public void AFusionIsBothTakenPlantsGoneAndTheResultAppeared()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity first = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            NetEntity second = ledger.Add(NetKind.Plant, 1, 0, 3, 1);
            ledger.Bind(first.NetId, 100);
            ledger.Bind(second.NetId, 200);

            LawnPlant[] before = { Plant(100, 0, 3, 1), Plant(200, 0, 3, 1) };
            LawnPlant[] after = { Plant(300, 5, 3, 1) };

            LawnChange change = LawnDiff.Of(ledger, before, after);

            // Every despawn travels before every spawn, so a peer can empty the cell and then fill it.
            Assert.Equal(new[] { first.NetId, second.NetId }, change.GoneNetIds);
            Assert.Single(change.Appeared);
            Assert.Equal(300, change.Appeared[0].InstanceId);
            Assert.Equal(5, change.Appeared[0].PlantType);
        }

        [Fact]
        public void AFusionInPlaceDiesUnderTheOldIdAndIsNamedAgain()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity pea = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            ledger.Bind(pea.NetId, 100);

            LawnPlant[] before = { Plant(100, 0, 3, 1) };
            LawnPlant[] after = { Plant(100, 7, 3, 1) };

            LawnChange change = LawnDiff.Of(ledger, before, after);

            // A peer can only repeat a creation, not a mutation, so the same object is a death and a birth.
            Assert.Equal(new[] { pea.NetId }, change.GoneNetIds);
            Assert.Single(change.Appeared);
            Assert.Equal(100, change.Appeared[0].InstanceId);
            Assert.Equal(7, change.Appeared[0].PlantType);
        }

        [Fact]
        public void UnchangedPlantsAreNotRepeatedWhenAnotherOneDies()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity stays = ledger.Add(NetKind.Plant, 0, 0, 1, 0);
            NetEntity dies = ledger.Add(NetKind.Plant, 0, 0, 2, 0);
            ledger.Bind(stays.NetId, 10);
            ledger.Bind(dies.NetId, 20);

            LawnChange change = LawnDiff.Of(ledger,
                new[] { Plant(10, 0, 1, 0), Plant(20, 0, 2, 0) },
                new[] { Plant(10, 0, 1, 0) });

            Assert.Equal(new[] { dies.NetId }, change.GoneNetIds);
            Assert.Empty(change.Appeared);
        }

        [Fact]
        public void APlantTheRoomNeverMintedCannotBeNamed()
        {
            EntityLedger ledger = new EntityLedger();

            // A level can start with plants; they are on every lawn but have no net id to despawn by.
            LawnChange change = LawnDiff.Of(ledger,
                new[] { Plant(900, 0, 4, 2) },
                new LawnPlant[0]);

            Assert.Empty(change.GoneNetIds);
            Assert.Empty(change.Appeared);
            Assert.Equal(1, change.Unnamed);
            Assert.False(change.IsEmpty);
        }

        [Fact]
        public void AMutationOfAPlantTheRoomNeverMintedStaysUnnamed()
        {
            EntityLedger ledger = new EntityLedger();

            LawnChange change = LawnDiff.Of(ledger,
                new[] { Plant(900, 0, 4, 2) },
                new[] { Plant(900, 3, 4, 2) });

            // Naming it again would create a second plant on every peer that already has the first.
            Assert.Empty(change.GoneNetIds);
            Assert.Empty(change.Appeared);
            Assert.Equal(1, change.Unnamed);
        }

        [Fact]
        public void ARecordOfAnotherKindIsNotAPlantTheDiffCanName()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity sun = ledger.Add(NetKind.Sun, 0, 42, 3, 1);
            ledger.Bind(sun.NetId, 500);

            LawnChange change = LawnDiff.Of(ledger, new[] { Plant(500, 0, 3, 1) }, new LawnPlant[0]);

            Assert.Empty(change.GoneNetIds);
            Assert.Equal(1, change.Unnamed);
        }

        [Fact]
        public void ZeroInstanceIdsAreNotPlantsAndAreIgnored()
        {
            LawnChange change = LawnDiff.Of(new EntityLedger(),
                new[] { Plant(0, 0, 1, 1) },
                new[] { Plant(0, 0, 1, 1) });

            Assert.True(change.IsEmpty);
        }

        [Fact]
        public void TheCreatorAloneCanSayAPlantAppeared()
        {
            LawnChange change = LawnDiff.OnlyNew(Plant(77, 2, 5, 3));

            Assert.Empty(change.GoneNetIds);
            Assert.Single(change.Appeared);
            Assert.Equal(77, change.Appeared[0].InstanceId);
            Assert.Equal(2, change.Appeared[0].PlantType);
            Assert.Equal(5, change.Appeared[0].Column);
            Assert.Equal(3, change.Appeared[0].Row);
        }

        [Fact]
        public void TheShovelFallbackNamesThePlantOnTheHoveredCell()
        {
            EntityLedger ledger = new EntityLedger();
            NetEntity pea = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            ledger.Bind(pea.NetId, 100);
            ledger.Add(NetKind.Sun, 0, 42, 4, 1);

            Assert.Same(pea, ShovelRules.TrackedPlantAt(ledger, 3, 1));
            Assert.Null(ShovelRules.TrackedPlantAt(ledger, 4, 1));
            Assert.Null(ShovelRules.TrackedPlantAt(ledger, 9, 9));

            // A cell outside the grid answers nothing rather than asking the ledger with a broken key.
            Assert.Null(ShovelRules.TrackedPlantAt(ledger, -1, 1));
            Assert.Null(ShovelRules.TrackedPlantAt(ledger, 3, -1));
            Assert.Null(ShovelRules.TrackedPlantAt(null, 3, 1));
        }

        private static LawnPlant Plant(int instanceId, int type, int column, int row)
        {
            return new LawnPlant
            {
                InstanceId = instanceId,
                PlantType = type,
                Column = column,
                Row = row
            };
        }
    }
}
