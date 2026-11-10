using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The cart's whole two-click cycle over a lawn the room numbers, not one rule at a time. Each click
    /// is answered by the same two questions the door asks - is this the drop, and what does this end carry
    /// afterwards - and the game's own answer about the aimed cell decides whether the drop landed.
    ///
    /// The cycle the fixes were measured on is here in full: a fused plant is lifted, the native creator
    /// refuses the drop and the cart must still carry that same plant, the player aims again and the drop
    /// lands, and only then is the carry gone. A model that lets the refused drop take the carry away would
    /// make the second click a lift of whatever stands on the new aim cell - the desync E63 and E65
    /// describe from the other side.
    /// </summary>
    public class CartCycleTests
    {
        private const int FusedType = 1027;

        /// <summary>The lift click: the cart names the plant under the pointer, nothing else changes.</summary>
        private static int Lift(EntityLedger lawn, int carriedBefore, int column, int row)
        {
            Assert.False(CartRules.IsDrop(carriedBefore));

            NetEntity taken = lawn.PlantAt(column, row);

            return CartRules.HeldAfterClick(carriedBefore, taken == null ? 0 : taken.NetId, false);
        }

        /// <summary>
        /// The drop click. The native body either puts the plant on the cell or refuses; only a landed drop
        /// moves the room's record and takes the carry away.
        /// </summary>
        private static int Drop(EntityLedger lawn, int carriedBefore, int column, int row,
                                bool creatorTookIt)
        {
            Assert.True(CartRules.IsDrop(carriedBefore));

            NetEntity record = lawn.Find(carriedBefore);
            Assert.NotNull(record);

            bool landed = creatorTookIt;

            if (landed)
            {
                record.Column = column;
                record.Row = row;
            }

            return CartRules.HeldAfterClick(carriedBefore, 0, landed);
        }

        [Fact]
        public void AFusedPlantSurvivesARefusedDropAndLandsOnTheNextAim()
        {
            EntityLedger lawn = new EntityLedger();
            NetEntity fused = lawn.Add(NetKind.Plant, 1, FusedType, 5, 0);

            int carried = Lift(lawn, 0, 5, 0);
            Assert.Equal(fused.NetId, carried);

            // The creator refused the fused kind on a clear cell: the cart still holds the same plant.
            carried = Drop(lawn, carried, 4, 1, false);
            Assert.Equal(fused.NetId, carried);
            Assert.Equal(5, lawn.Find(fused.NetId).Column);
            Assert.Equal(0, lawn.Find(fused.NetId).Row);

            // The free flag lets the game's own creator take it, and the drop lands once.
            carried = Drop(lawn, carried, 1, 1, true);

            Assert.Equal(0, carried);
            Assert.Equal(1, lawn.Find(fused.NetId).Column);
            Assert.Equal(1, lawn.Find(fused.NetId).Row);
        }

        [Fact]
        public void ARefusedDropStaysADropEvenOverAnotherRoomPlant()
        {
            // The second click after a refusal must not be read as a lift of the plant standing there:
            // that would swap the cart's plant for that one and move the wrong plant afterwards.
            EntityLedger lawn = new EntityLedger();
            NetEntity carriedPlant = lawn.Add(NetKind.Plant, 1, FusedType, 5, 0);
            NetEntity bystander = lawn.Add(NetKind.Plant, 1, 1, 4, 1);

            int carried = Lift(lawn, 0, 5, 0);
            carried = Drop(lawn, carried, 4, 1, false);

            Assert.Equal(carriedPlant.NetId, carried);
            Assert.NotEqual(bystander.NetId, carried);
            Assert.Equal(4, lawn.Find(bystander.NetId).Column);
            Assert.Equal(1, lawn.Find(bystander.NetId).Row);
        }

        [Fact]
        public void ADropOntoTheCellsThePlantLeftStillEndsTheCarry()
        {
            EntityLedger lawn = new EntityLedger();
            NetEntity plant = lawn.Add(NetKind.Plant, 1, FusedType, 3, 2);

            int carried = Lift(lawn, 0, 3, 2);
            carried = Drop(lawn, carried, 3, 2, true);

            Assert.Equal(0, carried);
            Assert.Equal(3, lawn.Find(plant.NetId).Column);
            Assert.Equal(2, lawn.Find(plant.NetId).Row);
            Assert.Equal(plant.NetId, lawn.PlantAt(3, 2).NetId);
        }

        [Fact]
        public void EveryClickOfAWholeCycleKeepsTheLawnAgreeing()
        {
            // Lift, refuse, aim elsewhere, land, lift that same plant again and land it back: the record
            // is the only plant on its cell at every stop, and the cart carries it or nothing.
            EntityLedger lawn = new EntityLedger();
            NetEntity plant = lawn.Add(NetKind.Plant, 1, FusedType, 5, 0);

            int carried = Lift(lawn, 0, 5, 0);
            Assert.Equal(plant.NetId, carried);
            Assert.Null(lawn.PlantAt(4, 1));

            carried = Drop(lawn, carried, 4, 1, false);
            Assert.Equal(plant.NetId, carried);
            Assert.Null(lawn.PlantAt(4, 1));

            carried = Drop(lawn, carried, 0, 0, true);
            Assert.Equal(0, carried);
            Assert.Equal(plant.NetId, lawn.PlantAt(0, 0).NetId);

            carried = Lift(lawn, 0, 0, 0);
            Assert.Equal(plant.NetId, carried);

            carried = Drop(lawn, carried, 5, 0, true);
            Assert.Equal(0, carried);
            Assert.Equal(plant.NetId, lawn.PlantAt(5, 0).NetId);
            Assert.Single(lawn.Entries);
        }

        [Fact]
        public void ALiftThatNamesNoRoomPlantLeavesTheCartEmptyAndTheFirstDropIsStillALift()
        {
            // A plant the room does not number (a tutorial one, or one the host has not announced yet)
            // is not something this end can carry into a move frame, so the lift remembers nothing and
            // the next click is a lift again rather than a drop of an unnamed plant.
            EntityLedger lawn = new EntityLedger();

            int carried = Lift(lawn, 0, 8, 4);
            Assert.Equal(0, carried);
            Assert.False(CartRules.IsDrop(carried));

            carried = Lift(lawn, carried, 8, 4);
            Assert.Equal(0, carried);
        }
    }
}
