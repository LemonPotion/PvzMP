using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The rules behind the hammer. The blow itself is the game's: its crush body asks the engine which
    /// zombies are around the tool and writes a `Zombie.TakeDamage` onto each. All the room needs from that
    /// is the damage steps, and they are only worth trusting when they are the game's own numbers - so
    /// these tests are about who may say them, what may be said, and the zombies this end cannot name.
    ///
    /// Proof of the doors: ref/EVIDENCE.md E54, E60.
    /// </summary>
    public class HammerBlowTests
    {
        [Fact]
        public void TheDamageStepsOneBlowTookAreOrderedOnceEachInTheOrderTheGameTookThem()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);
            Bind(ledger, NetKind.Zombie, 102);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[]
            {
                Blow(102, 100), Blow(101, 200), Blow(102, 100)
            });

            Assert.Equal(new[] { 2, 1 }, NetIds(named));
        }

        [Fact]
        public void TheNumbersTheGameComputedTravelWithTheStep()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            HammerBlow heard = Blow(101, 1800);
            heard.Maker = "Hammer";
            heard.DamageType = 2;
            heard.ReportType = 270;
            heard.Fix = true;

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { heard });

            Assert.Equal(1, named.Length);
            Assert.Equal(1800, named[0].Damage);
            Assert.Equal("Hammer", named[0].Maker);
            Assert.Equal(2, named[0].DamageType);
            Assert.Equal(270, named[0].ReportType);
            Assert.True(named[0].Fix);
        }

        [Fact]
        public void AZombieTheRoomHasNoNumberForIsLeftOutOfTheOrder()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { Blow(101, 50), Blow(60606, 50) });

            Assert.Equal(new[] { 1 }, NetIds(named));
        }

        [Fact]
        public void AnObjectTheLedgerKeepsAsSomethingElseIsNotADamageStep()
        {
            var ledger = new EntityLedger();

            // A plant record with this machine's id is a real object, but a hammer blow is only ever said
            // about a zombie: naming a plant here would have the room run the damage on the wrong type.
            Bind(ledger, NetKind.Plant, 707);
            Bind(ledger, NetKind.Sun, 708);

            Assert.Empty(HammerRules.NamedBlows(ledger, new[] { Blow(707, 50), Blow(708, 50) }));
        }

        [Fact]
        public void ABlowThisEndCannotReadOrdersNothingInsteadOfThrowing()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            Assert.Empty(HammerRules.NamedBlows(null, new[] { Blow(101, 50) }));
            Assert.Empty(HammerRules.NamedBlows(ledger, null));
            Assert.Empty(HammerRules.NamedBlows(ledger, new HammerBlow[0]));
        }

        [Fact]
        public void AnObjectWithNoMachineIdIsNotLookedUpAtAll()
        {
            var ledger = new EntityLedger();

            // 0 is what "nothing" looks like on this machine, and a record that has not bound its own copy
            // yet still carries it, so asking the ledger about it would name someone else's zombie.
            ledger.Add(NetKind.Zombie, 0, 3, 0, 0);

            Assert.Empty(HammerRules.NamedBlows(ledger, new[] { Blow(0, 50) }));
        }

        [Fact]
        public void AStepTheBlowHeardTwiceIsNamedBothTimesButKeepsTheFirstNumbers()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            HammerBlow first = Blow(101, 100);
            HammerBlow again = Blow(101, 999);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { first, again });

            Assert.Single(named);
            Assert.Equal(100, named[0].Damage);

            // The number is written onto the copy that did not make the order too: the caller prints a
            // step as "left out" only when this is zero, and a duplicate did travel.
            Assert.Equal(1, first.NetId);
            Assert.Equal(1, again.NetId);
        }

        [Fact]
        public void TheOrderFollowsTheLawnAndNotTheOrderTheRoomNumberedTheZombies()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 500);
            Bind(ledger, NetKind.Zombie, 400);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { Blow(400, 10), Blow(500, 20) });

            Assert.Equal(new[] { 2, 1 }, NetIds(named));
            Assert.Equal(10, named[0].Damage);
            Assert.Equal(20, named[1].Damage);
        }

        [Fact]
        public void AStepWithANullMakerAndZeroNumbersStillTravels()
        {
            // A zero is a value, not an omission: only NetId decides whether a step reached the room, so a
            // hit the game computed as zero damage with no maker and no roll still has to be repeated.
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { Blow(101, 0) });

            Assert.Single(named);
            Assert.Equal(0, named[0].Damage);
            Assert.Null(named[0].Maker);
            Assert.Equal(0, named[0].DamageType);
            Assert.Equal(0, named[0].ReportType);
            Assert.False(named[0].Fix);
        }

        [Fact]
        public void AStepTheRoomCannotNameIsLeftOutBesideOneItCan()
        {
            var ledger = new EntityLedger();
            Bind(ledger, NetKind.Zombie, 101);

            HammerBlow nameless = Blow(0, 50);
            HammerBlow known = Blow(101, 60);

            HammerBlow[] named = HammerRules.NamedBlows(ledger, new[] { nameless, known });

            Assert.Equal(new[] { 1 }, NetIds(named));
            Assert.Equal(0, nameless.NetId);
            Assert.Equal(1, known.NetId);
        }

        [Fact]
        public void OnlyTheEndWhoseOwnClickRanTheBodySaysTheBlow()
        {
            Assert.True(HammerRules.Witnesses(true, true, false),
                "the lawn that clicked has a blow to report");
            Assert.False(HammerRules.Witnesses(true, false, false),
                "a damage step outside the tool body is somebody else's business");
            Assert.False(HammerRules.Witnesses(false, true, false),
                "outside a room a hammer is not the mod's business at all");
        }

        [Fact]
        public void AnEndReplayingTheRoomsOrderDoesNotSayTheBlowAgain()
        {
            // The room's order runs the game's own blow body, which writes the same damage step. Without
            // this the room would answer itself and every seat would hit the same zombie once more.
            Assert.False(HammerRules.Witnesses(true, true, true));
        }

        private static HammerBlow Blow(int instanceId, int damage)
        {
            return new HammerBlow { InstanceId = instanceId, Damage = damage };
        }

        private static int[] NetIds(HammerBlow[] named)
        {
            int[] ids = new int[named.Length];
            for (int i = 0; i < named.Length; i++)
            {
                ids[i] = named[i].NetId;
            }

            return ids;
        }

        private static void Bind(EntityLedger ledger, NetKind kind, int instanceId)
        {
            ledger.Bind(ledger.Add(kind, 0, 0, 0, 0).NetId, instanceId);
        }
    }
}
