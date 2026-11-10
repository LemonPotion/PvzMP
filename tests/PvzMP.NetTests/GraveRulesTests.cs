using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The grave rules, away from the game: whose dice a grave may come from, and what one lawn has to do
    /// to look like the host's. The native side - that every grave door rolls, and that the zombie inside a
    /// grave is rolled by the body that opens it - is proved in ref/EVIDENCE.md E42.
    /// </summary>
    public class GraveRulesTests
    {
        private static LawnGrave Grave(int column, int row, int kind = 1, int instance = 0)
        {
            return new LawnGrave
            {
                Column = column,
                Row = row,
                GraveType = kind,
                InstanceId = instance
            };
        }

        [Fact]
        public void AHostCellThisLawnDoesNotHoldIsGrownWithTheHostsKind()
        {
            GraveChange change = GraveRules.Reconcile(
                new[] { Grave(3, 1, 2) }, new LawnGrave[0]);

            Assert.Equal(1, change.ToBuild.Length);
            Assert.Equal(3, change.ToBuild[0].Column);
            Assert.Equal(2, change.ToBuild[0].GraveType);
            Assert.Equal(0, change.ToRemove.Length);
        }

        [Fact]
        public void AGraveThisLawnHoldsAndTheHostDoesNotNameIsTakenDown()
        {
            GraveChange change = GraveRules.Reconcile(
                new LawnGrave[0], new[] { Grave(3, 1, 2, 501) });

            Assert.Equal(0, change.ToBuild.Length);
            Assert.Equal(1, change.ToRemove.Length);
            Assert.Equal(501, change.ToRemove[0].InstanceId);
        }

        /// <summary>
        /// A cell both lawns hold with a different kind is the level's own disagreement: the game's creator
        /// makes the kind it is told, and nothing turns one grave into another, so the object leaves and a
        /// new one is built on the same cell.
        /// </summary>
        [Fact]
        public void ACellHoldingADifferentKindLeavesAndIsGrownAgain()
        {
            GraveChange change = GraveRules.Reconcile(
                new[] { Grave(2, 4, 1) }, new[] { Grave(2, 4, 3, 777) });

            Assert.Equal(1, change.ToRemove.Length);
            Assert.Equal(777, change.ToRemove[0].InstanceId);
            Assert.Equal(1, change.ToBuild.Length);
            Assert.Equal(1, change.ToBuild[0].GraveType);
        }

        [Fact]
        public void TwoLawnsThatNameTheSameCellsAndKindsChangeNothing()
        {
            GraveChange change = GraveRules.Reconcile(
                new[] { Grave(1, 0, 1), Grave(5, 2, 2) },
                new[] { Grave(5, 2, 2, 12), Grave(1, 0, 1, 9) });

            Assert.True(change.IsEmpty);
        }

        [Fact]
        public void ALawnThatIsToldNothingAboutGravesBuildsNothingAndKeepsNothing()
        {
            GraveChange change = GraveRules.Reconcile(null, new[] { Grave(1, 1, 1, 4) });

            Assert.Equal(0, change.ToBuild.Length);
            Assert.Equal(1, change.ToRemove.Length);

            Assert.True(GraveRules.Reconcile(null, null).IsEmpty);
            Assert.True(GraveRules.Reconcile(new LawnGrave[0], new LawnGrave[0]).IsEmpty);
        }

        /// <summary>
        /// The game can put two objects on one cell, and the layout names a cell once. The grave the host
        /// does not have is taken down, so the cell ends holding exactly the one the host described.
        /// </summary>
        [Fact]
        public void ASecondGraveOnACellTheHostAlreadyNamedLeavesToo()
        {
            GraveChange change = GraveRules.Reconcile(
                new[] { Grave(4, 3, 1) }, new[] { Grave(4, 3, 1, 20), Grave(4, 3, 2, 21) });

            Assert.Equal(1, change.ToRemove.Length);
            Assert.Equal(21, change.ToRemove[0].InstanceId);
            Assert.Equal(0, change.ToBuild.Length);
        }

        [Fact]
        public void TheHostRollsItsOwnGravesInsideALevelAndNobodyElseDoes()
        {
            Assert.True(GraveRules.RollsOwnGraves(true, true, true));
            Assert.False(GraveRules.RollsOwnGraves(true, true, false));
        }

        /// <summary>
        /// Outside a room, and inside one while no level is up, the graves are not the room's business at
        /// all: the garden, the menu and the loading lawn keep rolling exactly as they did before this rule
        /// existed.
        /// </summary>
        [Fact]
        public void AGraveRolledAwayFromTheRoomsLevelStaysWithTheGame()
        {
            Assert.True(GraveRules.RollsOwnGraves(false, true, false));
            Assert.True(GraveRules.RollsOwnGraves(true, false, false));
            Assert.True(GraveRules.RollsOwnGraves(false, false, true));
        }
    }
}
