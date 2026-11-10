using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// What a planting costs the room, in the game's own numbers. Two rules and nothing else: whether the
    /// pool the host owns covers the card's price, and how much of that price the host still has to charge
    /// after the game made the plant. The price itself is never here - it is read off the card, and what
    /// the game's creator does with it is measured in a live run rather than assumed.
    /// </summary>
    public class PlantingRulesTests
    {
        [Fact]
        public void APoolSmallerThanTheCardIsRefusedInItsOwnWords()
        {
            string reason = PlantingRules.Affordance(25, 100);

            Assert.Contains("25", reason);
            Assert.Contains("100", reason);
        }

        [Fact]
        public void APoolThatExactlyCoversTheCardIsNotARefusal()
        {
            Assert.Null(PlantingRules.Affordance(100, 100));
            Assert.Null(PlantingRules.Affordance(150, 100));
        }

        /// <summary>
        /// A bank with no card of this type has no price, and the mod does not answer that with a number of
        /// its own - neither a free planting nor a refusal. The caller is expected to say so out loud.
        /// </summary>
        [Fact]
        public void ACardTheBankDoesNotPriceIsNeitherFreeNorRefused()
        {
            Assert.Null(PlantingRules.Affordance(0, PlantingRules.UnknownCost));
            Assert.Equal(0, PlantingRules.StillOwed(500, 500, PlantingRules.UnknownCost));
        }

        [Fact]
        public void APoolThatCannotBeReadIsNotSpent()
        {
            Assert.Null(PlantingRules.Affordance(-1, 50));
            Assert.Equal(0, PlantingRules.StillOwed(-1, 500, 50));
            Assert.Equal(0, PlantingRules.StillOwed(500, -1, 50));
        }

        [Fact]
        public void WhatTheGameChargedItselfIsNotChargedTwice()
        {
            Assert.Equal(0, PlantingRules.StillOwed(100, 50, 50));
        }

        [Fact]
        public void WhatTheGameLeftUnpaidIsTakenThroughTheGame()
        {
            Assert.Equal(50, PlantingRules.StillOwed(100, 100, 50));
            Assert.Equal(30, PlantingRules.StillOwed(100, 80, 50));
        }

        /// <summary>
        /// The game is allowed to take more than the card says - snow and night cards do exactly that, and
        /// the mod has no right to hand the difference back. Only what is missing is charged.
        /// </summary>
        [Fact]
        public void TheRoomNeverRefundsWhatTheGameTookExtra()
        {
            Assert.Equal(0, PlantingRules.StillOwed(100, 40, 50));
            Assert.Equal(0, PlantingRules.StillOwed(100, 0, 50));
        }

        [Fact]
        public void AFreeCardLeavesThePoolAlone()
        {
            Assert.Null(PlantingRules.Affordance(0, 0));
            Assert.Equal(0, PlantingRules.StillOwed(100, 100, 0));
        }
    }
}
