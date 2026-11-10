using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The draft orders a seat hears before its own seed chooser exists. They have to arrive in the order
    /// they were said, be carried out once, and belong to the chooser that is coming rather than the one
    /// that has gone - which is the difference between a late player seeing another's plant greyed out and
    /// being offered it twice.
    /// </summary>
    public class DraftHoldTests
    {
        [Fact]
        public void OrdersComeBackInTheOrderTheyWereSaid()
        {
            DraftHold hold = new DraftHold();
            hold.Add(true, 7, 1);
            hold.Add(true, 9, 2);
            hold.Add(false, 7, 1);

            Assert.Equal(3, hold.Count);

            List<DraftHold.Order> orders = hold.Take();

            Assert.Equal(3, orders.Count);
            Assert.True(orders[0].Taken);
            Assert.Equal(7, orders[0].PlantType);
            Assert.Equal(1, orders[0].OwnerPeer);
            Assert.Equal(9, orders[1].PlantType);
            Assert.False(orders[2].Taken);
            Assert.Equal(7, orders[2].PlantType);
        }

        [Fact]
        public void APlantTakenAndPutBackLeavesNobodyHoldingIt()
        {
            DraftHold hold = new DraftHold();
            hold.Add(true, 7, 1);
            hold.Add(false, 7, 1);
            hold.Add(true, 7, 2);

            PlantDraft draft = new PlantDraft();

            foreach (DraftHold.Order order in hold.Take())
            {
                if (order.Taken)
                {
                    draft.TryTake(order.PlantType, order.OwnerPeer);
                }
                else
                {
                    draft.Release(order.PlantType, order.OwnerPeer);
                }
            }

            Assert.Equal(2, draft.OwnerOf(7));
        }

        [Fact]
        public void TakingLeavesNothingBehindSoNoOrderIsCarriedOutTwice()
        {
            DraftHold hold = new DraftHold();
            hold.Add(true, 7, 1);

            Assert.Single(hold.Take());
            Assert.Empty(hold.Take());
            Assert.Equal(0, hold.Count);
        }

        [Fact]
        public void AnOrderKeptForAChooserThatWentAwayIsNotForTheNextOne()
        {
            DraftHold hold = new DraftHold();
            hold.Add(true, 7, 1);
            hold.Add(true, 9, 1);

            hold.GiveUp();

            Assert.Equal(0, hold.Count);
            Assert.Empty(hold.Take());
        }

        [Fact]
        public void ASeatThatTakesTwoPlantsBeforeTheChooserOpensLosesNeither()
        {
            DraftHold hold = new DraftHold();
            hold.Add(true, 3, 4);
            hold.Add(true, 4, 4);

            PlantDraft draft = new PlantDraft();

            foreach (DraftHold.Order order in hold.Take())
            {
                draft.TryTake(order.PlantType, order.OwnerPeer);
            }

            Assert.Equal(4, draft.OwnerOf(3));
            Assert.Equal(4, draft.OwnerOf(4));
        }
    }
}
