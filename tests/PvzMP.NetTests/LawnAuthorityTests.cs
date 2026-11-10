using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// Who may let the game change what a lawn holds. The cup and the gift are the two doors the game
    /// opens itself, out of bodies that read this lawn's own state or roll `UnityEngine.Random`, so the
    /// answer cannot come from "is there a board here": measured live, that question let a cup through on
    /// a peer during a level change (E39) and let two ends grow two plants out of one gift box (E40).
    /// </summary>
    public class LawnAuthorityTests
    {
        [Fact]
        public void ASinglePlayerRunsEveryCupDoorItself()
        {
            // Outside a room the mod has nothing to say: the garden, the menu and a whole level belong to
            // the game, and no cup of the room's is on the lawn to disagree with.
            Assert.True(TrophyRules.RunsOwnCup(false, false, false, false));
            Assert.True(TrophyRules.RunsOwnCup(false, true, false, false));
            Assert.True(TrophyRules.RunsOwnCup(false, true, true, false));
        }

        [Fact]
        public void TheHostsOwnCupAlwaysRuns()
        {
            Assert.True(TrophyRules.RunsOwnCup(true, true, true, false));
            Assert.True(TrophyRules.RunsOwnCup(true, false, true, false));
        }

        [Fact]
        public void APeerInLevelsLawnBuildsNoCupOfItsOwn()
        {
            Assert.False(TrophyRules.RunsOwnCup(true, true, false, false));
        }

        [Fact]
        public void APeerBuildsItsCupWhereTheRoomHasNoLevelUp()
        {
            // The garden and the menu are not the room's lawn. A cup built there is the game's business,
            // and refusing it would be a bug the room did not have before.
            Assert.True(TrophyRules.RunsOwnCup(true, false, false, false));
        }

        [Fact]
        public void TheHostsAnnouncedCupGetsThroughOnAPeer()
        {
            // Replaying the host's spawn runs the same native door on this end, so the gate cannot be the
            // only question the door asks.
            Assert.True(TrophyRules.RunsOwnCup(true, true, false, true));
        }

        [Fact]
        public void OnlyOneEndRollsWhatAGiftBoxGrows()
        {
            Assert.True(GiftRules.RollsOwnDice(false, true, false));
            Assert.True(GiftRules.RollsOwnDice(false, false, false));
            Assert.True(GiftRules.RollsOwnDice(true, true, true));
            Assert.False(GiftRules.RollsOwnDice(true, true, false));

            // A box on a lawn the room is not standing in - the garden, a menu - is nobody else's gift.
            Assert.True(GiftRules.RollsOwnDice(true, false, false));
        }

        [Fact]
        public void TheSweepKeyTakesDownOnlyWhatTheRoomNeverAnnounced()
        {
            Assert.False(SweepRules.MayTakeDown(false, false));
            Assert.False(SweepRules.MayTakeDown(false, true));
            Assert.False(SweepRules.MayTakeDown(true, true));
            Assert.True(SweepRules.MayTakeDown(true, false));
        }

        [Fact]
        public void TheCupTheHostLiftedKeepsItsAnimation()
        {
            Assert.True(TrophyRules.IsLifting(101, 101));

            // A sweep with no lift on this lawn (E39: 0 is the id of nothing) keeps no cup, and a cup
            // beside the one that is rising is a leftover nobody can lift.
            Assert.False(TrophyRules.IsLifting(0, 0));
            Assert.False(TrophyRules.IsLifting(0, 101));
            Assert.False(TrophyRules.IsLifting(101, 202));
        }
    }
}
