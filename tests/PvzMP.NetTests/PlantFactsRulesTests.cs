using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The shipped plant-fact rules, away from any game: the flags, the heal/damage gap, the host's word
    /// on death, and the one mapping between a lawn's facts and the layout entry that carries them.
    /// </summary>
    public class PlantFactsRulesTests
    {
        [Fact]
        public void PackAndHasAgreeOnEveryBit()
        {
            int[] bits =
            {
                PlantFactsRules.Invincible, PlantFactsRules.Undead, PlantFactsRules.IsLily,
                PlantFactsRules.Imitatless, PlantFactsRules.KeepShooting,
                PlantFactsRules.Uncrashable, PlantFactsRules.MelonSputter
            };

            for (int i = 0; i < bits.Length; i++)
            {
                int flags = PlantFactsRules.Pack(i == 0, i == 1, i == 2, i == 3, i == 4, i == 5,
                    i == 6);

                for (int j = 0; j < bits.Length; j++)
                {
                    Assert.Equal(i == j, PlantFactsRules.Has(flags, bits[j]));
                }
            }

            Assert.Equal(0, PlantFactsRules.Pack(false, false, false, false, false, false, false));
            Assert.Equal(127, PlantFactsRules.Pack(true, true, true, true, true, true, true));
        }

        [Fact]
        public void BetweenHealsDamagesOrDoesNothing()
        {
            int amount;

            Assert.Equal(HealthStep.Heal, PlantFactsRules.Between(100, 300, out amount));
            Assert.Equal(200, amount);

            Assert.Equal(HealthStep.Damage, PlantFactsRules.Between(300, 100, out amount));
            Assert.Equal(200, amount);

            Assert.Equal(HealthStep.None, PlantFactsRules.Between(300, 300, out amount));
            Assert.Equal(0, amount);

            // A host number of zero or below is not a fact: a dead plant is an entry the layout stops
            // naming, and its removal is the death.
            Assert.Equal(HealthStep.None, PlantFactsRules.Between(300, 0, out amount));
            Assert.Equal(0, amount);
            Assert.Equal(HealthStep.None, PlantFactsRules.Between(300, -5, out amount));
            Assert.Equal(0, amount);
        }

        [Fact]
        public void MayDieAllowsTheHostAndTheModAndHoldsAPeersRoomPlant()
        {
            // Outside a room, outside a level, or on a plant the ledger does not number: the game's
            // own death runs exactly as it did before the mod.
            Assert.True(PlantFactsRules.MayDie(false, true, false, false, true));
            Assert.True(PlantFactsRules.MayDie(true, false, false, false, true));
            Assert.True(PlantFactsRules.MayDie(true, true, false, false, false));

            // The host's word is the sentence on the host's own lawn.
            Assert.True(PlantFactsRules.MayDie(true, true, true, false, true));

            // The mod carrying out the host's removal is allowed through on a peer.
            Assert.True(PlantFactsRules.MayDie(true, true, false, true, true));

            // Everything else about a room plant on a peer waits for the layout.
            Assert.False(PlantFactsRules.MayDie(true, true, false, false, true));
        }

        [Fact]
        public void SameAsNoticesAnySingleField()
        {
            PlantFacts mine = Sample();
            PlantFacts other = Sample();

            Assert.True(mine.SameAs(other));

            other.Freeze = mine.Freeze + 1;
            Assert.False(mine.SameAs(other));

            other = Sample();
            other.FirstParent = mine.FirstParent + 1;
            Assert.False(mine.SameAs(other));

            other = Sample();
            other.Flags = 0;
            Assert.False(mine.SameAs(other));
        }

        [Fact]
        public void ALayoutEntryCarriesEveryFactThroughTheMapping()
        {
            PlantFacts facts = Sample();
            PlantSnap snap = PlantSnapRules.Of(44, 1005, 3, 2, facts);

            Assert.Equal(44, snap.NetId);
            Assert.Equal(1005, snap.PlantType);
            Assert.Equal(3, snap.Column);
            Assert.Equal(2, snap.Row);

            Assert.Equal(facts.Health, snap.Health);
            Assert.Equal(facts.MaxHealth, snap.MaxHealth);
            Assert.Equal(facts.Shield, snap.Shield);
            Assert.Equal(facts.Level, snap.Level);
            Assert.Equal(facts.Stage, snap.Stage);
            Assert.Equal(facts.Skin, snap.Skin);
            Assert.Equal(facts.Magnet, snap.Magnet);
            Assert.Equal(facts.Wheat, snap.Wheat);
            Assert.Equal(facts.ShootLevel, snap.ShootLevel);
            Assert.Equal(facts.ShootCurse, snap.ShootCurse);
            Assert.Equal(facts.Kills, snap.Kills);
            Assert.Equal(facts.Puff, snap.Puff);
            Assert.Equal(facts.Butter, snap.Butter);
            Assert.Equal(facts.Attribute, snap.Attribute);
            Assert.Equal(facts.Disable, snap.Disable);
            Assert.Equal(facts.Freeze, snap.Freeze);
            Assert.Equal(facts.FirstParent, snap.FirstParent);
            Assert.Equal(facts.SecondParent, snap.SecondParent);
            Assert.Equal(facts.LilyType, snap.LilyType);
            Assert.Equal(facts.Flags, snap.Flags);

            Assert.True(facts.SameAs(PlantSnapRules.FactsOf(snap)));
        }

        [Fact]
        public void AMissingEntryIsAllZeroes()
        {
            PlantFacts facts = PlantSnapRules.FactsOf(null);

            Assert.True(default(PlantFacts).SameAs(facts));
        }

        private static PlantFacts Sample()
        {
            return new PlantFacts
            {
                Health = 280,
                MaxHealth = 300,
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
