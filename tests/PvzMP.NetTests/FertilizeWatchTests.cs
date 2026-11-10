using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The two texts the fertilizer watch builds: what a lawn gained and lost between two reads of itself,
    /// and which cells changed kind between them, plus the one seat either frame leaves alone.
    /// The watch decides nothing about the world, so the whole rule is how a difference is said - and it is
    /// the difference between an in-place change and a replacement that this feature is designed on.
    /// Proof of the native side: ref/EVIDENCE.md E41, E48 and E53.
    /// </summary>
    public class FertilizeWatchTests
    {
        private const string Frozen = "obj #12 type 3 cell 2,1 hp 300/300";
        private const string Ripened = "obj #12 type 42 cell 2,1 hp 300/300";
        private const string Grown = "obj #99 type 42 cell 2,1 hp 300/300";

        [Fact]
        public void APlantThatKeptItsObjectAndChangedItsNumbersReadsAsOneLineOutAndOneBack()
        {
            string text = FertilizeRules.Changed(new[] { Frozen }, new[] { Ripened });

            Assert.Equal("- " + Frozen + " + " + Ripened, text);
        }

        [Fact]
        public void APlantThatLeftAndAnotherThatArrivedAreNamedByTheirObjects()
        {
            string text = FertilizeRules.Changed(new[] { Frozen }, new[] { Grown });

            Assert.Contains("- obj #12", text);
            Assert.Contains("+ obj #99", text);
        }

        [Fact]
        public void ALawnThatReadsExactlyTheSameSaysNothingChanged()
        {
            string text = FertilizeRules.Changed(new[] { Frozen, Ripened }, new[] { Ripened, Frozen });

            Assert.Equal("the lawn reads the same", text);
        }

        /// <summary>
        /// A plant that is only healed keeps its object and its cell, so the one thing that differs is its
        /// health - and that has to show, because nothing the room sends today describes a health number
        /// on a plant that stayed where it was.
        /// </summary>
        [Fact]
        public void AHealingIsADifferenceAndNotAnUnchangedLawn()
        {
            string[] before = { "obj #12 type 3 cell 2,1 hp 100/300" };
            string[] after = { "obj #12 type 3 cell 2,1 hp 300/300" };

            Assert.NotEqual("the lawn reads the same", FertilizeRules.Changed(before, after));
        }

        [Fact]
        public void NothingOnEitherSideOfTheLawnIsNotADifference()
        {
            Assert.Equal("the lawn reads the same", FertilizeRules.Changed(new string[0], new string[0]));
            Assert.Equal("the lawn reads the same", FertilizeRules.Changed(null, null));
        }

        [Fact]
        public void AWholeLawnThatAppearsIsSaidLineByLine()
        {
            string text = FertilizeRules.Changed(null, new[] { Frozen, Ripened });

            Assert.Equal("+ " + Frozen + " + " + Ripened, text);
        }

        /// <summary>
        /// A fertilized cell travels as a cell, and the seat whose hand dropped the fertilizer has already
        /// been changed by the game's own body. Every other seat - a peer the host told, and the host a peer
        /// asked - heals what stands on that cell at home.
        /// </summary>
        [Fact]
        public void TheLawnThatHeldTheFertilizerIsTheOneThatHealsNothing()
        {
            Assert.False(FertilizeRules.HealsHere(7, 7));
            Assert.True(FertilizeRules.HealsHere(2, 7));
            Assert.True(FertilizeRules.HealsHere(0, 7));
        }

        /// <summary>
        /// The growth the live logs of 2026-11-13 measured (E53): a walnut at cell 1,0 left and a tallnut
        /// arrived on the same cell, so the two numbers the rest of the room needs are the cell and the two
        /// kinds - and the kinds, not the objects, because every lawn has its own.
        /// </summary>
        [Fact]
        public void ANutThatBecameAnotherNutIsSaidWithTheCellAndBothKinds()
        {
            Growth[] grown = FertilizeRules.Grown(
                new[] { Plant(-81638, 3, 1, 0) },
                new[] { Plant(-83076, 1027, 1, 0) });

            Growth one = Assert.Single(grown);

            Assert.Equal(1, one.Column);
            Assert.Equal(0, one.Row);
            Assert.Equal(3, one.OldType);
            Assert.Equal(1027, one.NewType);
        }

        [Fact]
        public void APlantThatKeptItsKindGrewNothingEvenWithAnotherObjectUnderIt()
        {
            Assert.Empty(FertilizeRules.Grown(
                new[] { Plant(-81638, 3, 1, 0) },
                new[] { Plant(-90000, 3, 1, 0) }));
        }

        /// <summary>
        /// A fertilizer grows the plant it found and does not bring one, so a cell that was empty before is
        /// not a growth - that is a plant the layout has to answer, and it is not this frame's to say.
        /// </summary>
        [Fact]
        public void ACellThatHeldNothingBeforeGrewNothing()
        {
            Assert.Empty(FertilizeRules.Grown(
                new LawnPlant[0],
                new[] { Plant(-83076, 1027, 1, 0) }));
        }

        /// <summary>
        /// The reason both kinds travel: the same cell holds a pumpkin through the whole growth, and only
        /// the nut left it. A frame that said only the new kind would have every lawn dig up the pumpkin.
        /// </summary>
        [Fact]
        public void ACellHoldingTwoPlantsMovesOnlyTheKindThatLeft()
        {
            Growth[] grown = FertilizeRules.Grown(
                new[] { Plant(-81638, 3, 1, 0), Plant(-700, 8, 1, 0) },
                new[] { Plant(-83076, 1027, 1, 0), Plant(-700, 8, 1, 0) });

            Growth one = Assert.Single(grown);

            Assert.Equal(3, one.OldType);
            Assert.Equal(1027, one.NewType);
        }

        [Fact]
        public void ALawnThisEndCouldNotReadOnOneSideGrewNothing()
        {
            Assert.Empty(FertilizeRules.Grown(null, new[] { Plant(-83076, 1027, 1, 0) }));
            Assert.Empty(FertilizeRules.Grown(new[] { Plant(-81638, 3, 1, 0) }, null));
        }

        [Fact]
        public void TheLawnThatHeldTheFertilizerIsTheOneThatGrowsNothing()
        {
            Assert.False(FertilizeRules.GrowsHere(7, 7));
            Assert.True(FertilizeRules.GrowsHere(2, 7));
            Assert.True(FertilizeRules.GrowsHere(0, 7));
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
