using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    public class SessionRulesTests
    {
        [Fact]
        public void TypedNickSurvivesOnlyWithLetterDigitUnderscore()
        {
            Assert.True(NickRules.IsAllowed("Lemon_9"));
            Assert.False(NickRules.IsAllowed("Lemon-9"));
            Assert.False(NickRules.IsAllowed("Лemon"));
            Assert.False(NickRules.IsAllowed("ab"));
            Assert.False(NickRules.IsAllowed(new string('a', NickRules.MaxLength + 1)));
        }

        [Fact]
        public void DisallowedNickCharactersAreDroppedNotTheWholeName()
        {
            Assert.Equal("BigBob", NickRules.Clean("Big Bob!"));
            Assert.Equal("LeMon", NickRules.Clean("  Le\tMon\r\n"));
            Assert.Equal(string.Empty, NickRules.Clean("!!!"));
            Assert.Equal(string.Empty, NickRules.Clean(null));
        }

        [Fact]
        public void TypedNickFallsToProfileNameAndThenToTheConstant()
        {
            Assert.Equal("BigBob", NickRules.Resolve("Big Bob!", "Player"));
            Assert.Equal("Sunny", NickRules.Resolve("", "Sunny"));
            Assert.Equal("Sunny", NickRules.Resolve("!!!", "Sunny"));

            // The native profile really is 'Player' for every install, and a cyrillic profile name is
            // empty once cleaned, so the constant is the only thing left that can go into a roster.
            Assert.Equal("Player", NickRules.Resolve("Привет мир", "!!!"));
            Assert.Equal("Player", NickRules.Resolve(null, null));
        }

        [Fact]
        public void LongTypedNickIsCutToTheCapRatherThanRefused()
        {
            string cleaned = NickRules.Clean(new string('a', 40));
            Assert.Equal(NickRules.MaxLength, cleaned.Length);
            Assert.True(NickRules.IsAllowed(cleaned));
        }

        [Fact]
        public void SuffixSharesTheLengthCapWithTheName()
        {
            Assert.Equal("Player#2", NickRules.WithSuffix("Player", 2));

            string full = new string('a', NickRules.MaxLength);
            string suffixed = NickRules.WithSuffix(full, 12);
            Assert.Equal(NickRules.MaxLength, suffixed.Length);
            Assert.EndsWith("#12", suffixed);
        }

        [Fact]
        public void HostNumbersDuplicatesUntilASlotIsFree()
        {
            var taken = new HashSet<string> { "Player", "Player#2" };
            Assert.Equal("Player#3", NickRules.FirstFree("Player", taken));
            Assert.Equal("Lemon", NickRules.FirstFree("Lemon", taken));
        }

        [Fact]
        public void DuplicateNumberingGivesUpInsteadOfRenamingForever()
        {
            var taken = new HashSet<string> { "Player" };
            for (int number = 2; number <= NickRules.MaxSuffixNumber; number++)
            {
                taken.Add(NickRules.WithSuffix("Player", number));
            }

            Assert.Null(NickRules.FirstFree("Player", taken));

            taken.Remove("Player#4");
            Assert.Equal("Player#4", NickRules.FirstFree("Player", taken));
        }

        [Fact]
        public void PortAcceptsOnlyABoundedWholeNumber()
        {
            int port;
            Assert.True(AddressRules.TryPort("7777", out port));
            Assert.Equal(7777, port);

            Assert.True(AddressRules.TryPort(" 80 ", out port));
            Assert.Equal(80, port);

            Assert.False(AddressRules.TryPort("", out port));
            Assert.False(AddressRules.TryPort(null, out port));
            Assert.False(AddressRules.TryPort("0", out port));
            Assert.False(AddressRules.TryPort("70000", out port));
            Assert.False(AddressRules.TryPort("abc", out port));
            Assert.False(AddressRules.TryPort("1.5", out port));
            Assert.False(AddressRules.TryPort("-2", out port));
        }

        [Fact]
        public void HostKeepsWhatAnAddressCanBeMadeOf()
        {
            Assert.Equal("192.168.1.10", AddressRules.CleanHost(" 192.168.1.10 "));
            Assert.Equal("myhost-example", AddressRules.CleanHost("my host-example"));

            // Dropping the characters an address cannot contain leaves the rest exactly as typed: an
            // accidental "http://" paste ends up as junk in the host slot, which cannot resolve and says
            // so in the connect log. Parsing URLs here would only reject valid addresses.
            Assert.Equal("httphost", AddressRules.CleanHost("http://host"));
            Assert.Equal(string.Empty, AddressRules.CleanHost(null));
        }

        [Fact]
        public void HostIsCappedAtTheWireLimitAndEmptyIsNotUsable()
        {
            Assert.Equal(AddressRules.MaxHostLength, AddressRules.CleanHost(new string('a', 200)).Length);
            Assert.False(AddressRules.IsUsableHost(AddressRules.CleanHost("   ")));
            Assert.True(AddressRules.IsUsableHost(AddressRules.CleanHost("a")));
        }
    }
}
