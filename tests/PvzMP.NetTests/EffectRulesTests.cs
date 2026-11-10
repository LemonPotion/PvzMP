using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The two rules the effect relay needs away from the game: which seat plays an effect it was told
    /// about, and which of the game's own sounds the room carries. Everything else about an effect is the
    /// game's - the room only repeats the same native call with the same numbers.
    /// Proof of the native side: ref/EVIDENCE.md E50.
    /// </summary>
    public class EffectRulesTests
    {
        [Fact]
        public void TheSeatWhoseLawnMadeAnEffectDoesNotPlayItAgain()
        {
            Assert.False(EffectRules.PlaysHere(2, 2));
        }

        /// <summary>
        /// The case the mower complaint is about: the host's lawn made the sound, and the seat that hears
        /// about it afterwards is a different seat, so it plays it.
        /// </summary>
        [Fact]
        public void EveryOtherSeatPlaysWhatTheHostToldIt()
        {
            Assert.True(EffectRules.PlaysHere(0, 3));
            Assert.True(EffectRules.PlaysHere(4, 0));
        }

        /// <summary>
        /// A peer's effect is carried on by the host, which stands on a lawn that did not make it, so the
        /// host hears its own room too.
        /// </summary>
        [Fact]
        public void TheHostPlaysAnEffectAPeerAskedItToCarryOn()
        {
            Assert.True(EffectRules.PlaysHere(3, 0));
        }

        [Fact]
        public void ASoundMadeOutsideAKnownEventStaysOnItsOwnLawn()
        {
            Assert.False(EffectRules.CarriesSound(0));
        }

        /// <summary>
        /// A death inside a mower's attack nests two events, and a sound made under either of them is the
        /// room's business; only the count falling back to nothing says the event is over.
        /// </summary>
        [Fact]
        public void ASoundMadeWhileAnEventIsOpenTravels()
        {
            Assert.True(EffectRules.CarriesSound(1));
            Assert.True(EffectRules.CarriesSound(2));
        }
    }
}
