using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The two clicks of `Wheel.Use` and what each one leaves in the cart. The game's body keeps its
    /// pocket in private fields, so this is the whole of what the mod can know about a cart: whether the
    /// click it just watched was the lift or the drop, and what it carries afterwards.
    /// </summary>
    public class CartRulesTests
    {
        [Fact]
        public void AClickWhileTheCartCarriesIsTheDropAndNotASecondLift()
        {
            Assert.False(CartRules.IsDrop(0));
            Assert.True(CartRules.IsDrop(7));
        }

        [Fact]
        public void TheLiftRemembersThePlantThePointerNamed()
        {
            Assert.Equal(7, CartRules.HeldAfterClick(0, 7, false));
        }

        [Fact]
        public void ALiftThatNamedNoPlantOfTheRoomCarriesNothing()
        {
            Assert.Equal(0, CartRules.HeldAfterClick(0, 0, false));
        }

        [Fact]
        public void ADropThatReachedItsCellTakesTheCarryAway()
        {
            Assert.Equal(0, CartRules.HeldAfterClick(7, 0, true));
        }

        [Fact]
        public void ACellTheGameRefusedLeavesTheCartStillCarryingTheSamePlant()
        {
            // The player may aim that cell again, and the plant is nowhere on this lawn until it does.
            Assert.Equal(7, CartRules.HeldAfterClick(7, 0, false));
        }

        [Fact]
        public void TheDropNeverStartsCarryingThePlantStandingWhereItMeantToPutDown()
        {
            // A drop aims a cell that already holds another of the room's plants. Reading the pointer as
            // the lift would swap the cart's own plant for that one and move the wrong plant onto the cell.
            Assert.Equal(0, CartRules.HeldAfterClick(7, 9, true));
        }
    }
}
