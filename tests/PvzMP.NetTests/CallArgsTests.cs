using System;
using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The adapter registry: one measured category string in, one argument vector out, for every shape the
    /// atlas can name. This is the file that fails when a category is added to the generator and forgotten
    /// here, which is the failure that would otherwise arrive as a native method being handed a number
    /// where it wanted a function pointer.
    ///
    /// Shared collection with the coverage tests: both read the log.
    /// </summary>
    [Collection("coverage")]
    public class CallArgsTests
    {
        private static object[] Round(string cats, params object[] values)
        {
            return CallArgs.Decode(cats, CallArgs.Encode(cats, values));
        }

        // ---- the categories the room can write -------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        [InlineData(-50)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void AnIntSurvivesTheRoundTrip(long value)
        {
            Assert.Equal(value, Round("i", value)[0]);
        }

        [Fact]
        public void AnEnumIsWrittenAsTheInt32TheNativeBodyWants()
        {
            // dump.cs names the enum type, the body reads int32_t: the wire carries the number, and the
            // call site is allowed to hand a boxed int or an enum, because both are the same value.
            Assert.Equal(7L, Round("e", 7)[0]);
            Assert.Equal(-3L, Round("e", -3)[0]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ABoolSurvivesTheRoundTrip(bool value)
        {
            Assert.Equal(value, Round("b", value)[0]);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(-0.125f)]
        [InlineData(1f / 3f)]
        [InlineData(float.MaxValue)]
        [InlineData(float.MinValue)]
        [InlineData(float.Epsilon)]
        public void AFloatComesBackBitForBit(float value)
        {
            // The wire is a double, which holds every float exactly. Anything less would make a peer's
            // UseSun(float) a different number of sun than the host spent.
            object decoded = Round("f", value)[0];

            Assert.Equal(value, Convert.ToSingle(decoded));
        }

        [Theory]
        [InlineData("Peashooter")]
        [InlineData("")]
        [InlineData("a b c")]
        [InlineData("quote \" inside")]
        [InlineData("back\\slash")]
        [InlineData("line\nbreak")]
        [InlineData("tab\there")]
        [InlineData("\u0441\u043e\u043b\u043d\u0446\u0435")]
        public void AStringComesBackExactly(string sample)
        {
            Assert.Equal(sample, Round("s", sample)[0]);
        }

        [Fact]
        public void ALongNameIsRefusedBeforeItReachesTheLog()
        {
            string tooLong = new string('x', Protocol.MaxTextChars + 1);

            Assert.Throws<FormatException>(() => CallArgs.Encode("s", tooLong));
        }

        [Fact]
        public void SixteenArgumentsOfOneShapeRoundTrip()
        {
            object[] values = new object[16];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = i;
            }

            object[] decoded = CallArgs.Decode(new string('i', 16),
                                               CallArgs.Encode(new string('i', 16), values));

            Assert.Equal(16, decoded.Length);
            Assert.Equal(15L, decoded[15]);
        }

        [Fact]
        public void AValueTypeTravelsAsItsNumbers()
        {
            // CreateItem.SetCoin's fifth parameter is a Vector3 (E13): the atlas calls the category t and
            // the body wants three floats, so three floats is what travels.
            object[] decoded = Round("t", new object[] { new object[] { 1f, 2.5f, -3f } });

            object[] numbers = (object[])decoded[0];
            Assert.Equal(3, numbers.Length);
            Assert.Equal(2.5, Convert.ToDouble(numbers[1]));
        }

        [Fact]
        public void AnArrayTravelsAsReferencesAndNothingElse()
        {
            // The collection case the architecture asks for: elementwise, and every element is a token,
            // because an address from the sending machine means nothing here.
            object[] decoded = Round("a", new object[]
                                     {
                                         new object[]
                                         {
                                             CallArgs.Entity(1), CallArgs.Entity(2), CallArgs.None
                                         }
                                     });

            object[] items = (object[])decoded[0];
            Assert.Equal(3, items.Length);
            Assert.Equal(2, CallArgs.NetIdOf((string)items[1]));
            Assert.Equal(CallArgs.None, items[2]);
        }

        [Fact]
        public void ACallWithNoArgumentsIsStillAValidCall()
        {
            Assert.Empty(CallArgs.Decode("", CallArgs.Encode("")));
            Assert.Equal("[]", CallArgs.Encode(""));
            Assert.Equal(0, CallArgs.Count(null));
        }

        [Fact]
        public void TheWholeShapeOfAMeasuredRowRoundTrips()
        {
            // iiiitb is CreateItem.SetCoin/6 as the build measures it: the row that mints every coin.
            object[] decoded = Round("iiiitb", 3, 4, 0, 1, new object[] { 0f, 120f, 0f }, true);

            Assert.Equal(6, decoded.Length);
            Assert.Equal(3L, decoded[0]);
            Assert.Equal(true, decoded[5]);
        }

        // ---- the categories that cannot be a value ---------------------------

        [Theory]
        [InlineData("d")]
        [InlineData("p")]
        [InlineData("g")]
        [InlineData("k")]
        [InlineData("?")]
        public void AHardCategoryHasNoWireShapeAndSaysWhich(string cat)
        {
            Assert.Equal(cat, CallArgs.UnsendableIn(cat));
            Assert.False(CallArgs.IsSendable("ii" + cat + "b"));

            Assert.Throws<FormatException>(() => CallArgs.Encode(cat, 1));
            Assert.Throws<FormatException>(() => CallArgs.Decode(cat, "[1]"));
        }

        [Fact]
        public void TheUnsendableLetterIsNamedNotGuessedAt()
        {
            Assert.Equal("k", CallArgs.UnsendableIn("iik"));
            Assert.Null(CallArgs.UnsendableIn("iibf"));
            Assert.Null(CallArgs.UnsendableIn(null));
            Assert.True(CallArgs.IsSendable(null));
        }

        [Fact]
        public void ACategoryTheRegistryDoesNotKnowIsRefusedOnBothSides()
        {
            // The generator owns the letter set; if it ever writes a new one, the codec has to stop rather
            // than decode an argument as the wrong type.
            Assert.Throws<FormatException>(() => CallArgs.Encode("z", 1));
            Assert.Throws<FormatException>(() => CallArgs.Decode("z", "[1]"));
        }

        // ---- the count has to agree with the row -----------------------------

        [Theory]
        [InlineData("ii", 1)]
        [InlineData("i", 2)]
        [InlineData("", 1)]
        public void AnArgumentVectorOfTheWrongLengthIsRefused(string cats, int given)
        {
            object[] values = new object[given];
            for (int i = 0; i < given; i++)
            {
                values[i] = 1;
            }

            FormatException error = Assert.Throws<FormatException>(
                () => CallArgs.Encode(cats, values));

            Assert.Contains("parameter(s)", error.Message);
        }

        [Fact]
        public void AnArgumentVectorWrittenTooShortIsRefusedByTheReaderToo()
        {
            FormatException error = Assert.Throws<FormatException>(() => CallArgs.Decode("ii", "[1]"));

            Assert.Contains("1 argument(s) for 2 parameter(s)", error.Message);
        }

        [Fact]
        public void NullArgumentsAreReadAsNoArguments()
        {
            Assert.Equal("[]", CallArgs.Encode(""));
            Assert.Throws<FormatException>(() => CallArgs.Decode("i", null));
        }

        // ---- hostile input --------------------------------------------------

        [Theory]
        [InlineData("not json at all")]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("[1,")]
        [InlineData("[1,]")]
        [InlineData("{\"0\":1}")]
        [InlineData("[\"x\",\"y\",\"z\",\"w\",\"v\"]")]
        public void AFrameThatIsNotAnArgumentVectorIsRefusedWithAReason(string json)
        {
            // A peer is not trusted to have written valid input: the shape is checked here, so the
            // applier never sees a half-read call.
            Assert.Throws<FormatException>(() => CallArgs.Decode("ii", json));
        }

        [Theory]
        [InlineData("i", "[\"50\"]")]
        [InlineData("i", "[1.5]")]
        [InlineData("i", "[[]]")]
        [InlineData("i", "[true]")]
        [InlineData("b", "[1]")]
        [InlineData("b", "[\"true\"]")]
        [InlineData("f", "[\"x\"]")]
        [InlineData("f", "[[1]]")]
        [InlineData("s", "[1]")]
        [InlineData("s", "[null]")]
        [InlineData("o", "[\"0x7ff7a1b2\"]")]
        [InlineData("o", "[12]")]
        [InlineData("t", "[1]")]
        [InlineData("t", "[\"a\"]")]
        [InlineData("a", "\"#1\"")]
        [InlineData("a", "[1]")]
        public void AValueWrittenInTheWrongShapeIsRefused(string cat, string json)
        {
            Assert.Throws<FormatException>(() => CallArgs.Decode(cat, json));
        }

        [Fact]
        public void AFrameHoldingMoreArgumentsThanAnyRowIsRefused()
        {
            List<string> numbers = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                numbers.Add(i.ToString());
            }

            string json = "[" + string.Join(",", numbers.ToArray()) + "]";

            FormatException error = Assert.Throws<FormatException>(() => CallArgs.Decode(
                new string('i', 200), json));

            Assert.Contains("more than", error.Message);
        }

        [Fact]
        public void AFrameHoldingAMonsterArrayIsRefused()
        {
            List<string> items = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                items.Add("\"#1\"");
            }

            Assert.Throws<FormatException>(() => CallArgs.Decode("a", "[" + string.Join(",",
                items.ToArray()) + "]"));
        }

        [Fact]
        public void AHostileStringIsRefusedBeforeItIsPrinted()
        {
            string huge = "\"" + new string('x', 500) + "\"";

            FormatException error = Assert.Throws<FormatException>(() => CallArgs.Decode("s",
                "[" + huge + "]"));

            Assert.Contains("cap", error.Message);
        }

        // ---- reference tokens ----------------------------------------------

        [Theory]
        [InlineData("#0", RefKind.Entity)]
        [InlineData("#12", RefKind.Entity)]
        [InlineData("/Board/Sun", RefKind.ScenePath)]
        [InlineData("@Peashooter", RefKind.Asset)]
        [InlineData("!Hammer", RefKind.Singleton)]
        [InlineData("~", RefKind.None)]
        [InlineData("", RefKind.None)]
        public void EveryReferenceSaysWhichLookupItWants(string token, RefKind expected)
        {
            Assert.Equal(expected, CallArgs.KindOf(token));
        }

        [Theory]
        [InlineData("0x7ff7a1b2")]
        [InlineData("Board.Sun")]
        [InlineData(" ")]
        public void AnythingWithoutAPrefixIsNotAReference(string token)
        {
            // This is the whole point of the token: a pointer from the sending machine cannot be
            // mistaken for something this machine can dereference.
            Assert.Throws<FormatException>(() => CallArgs.KindOf(token));
        }

        [Fact]
        public void AnEntityTokenCarriesTheRoomIdAndNothingElse()
        {
            Assert.Equal(7, CallArgs.NetIdOf(CallArgs.Entity(7)));
            Assert.Equal("7", CallArgs.ValueOf(CallArgs.Entity(7)));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("/Board"));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("#x"));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("#-1"));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("# 1"));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-2147483648)]
        public void ANegativeRoomIdIsNotAnEntity(int netId)
        {
            Assert.Throws<FormatException>(() => CallArgs.Entity(netId));
        }

        [Fact]
        public void TheLargestEntityIdStillRoundTrips()
        {
            Assert.Equal(int.MaxValue, CallArgs.NetIdOf(CallArgs.Entity(int.MaxValue)));
        }

        [Fact]
        public void APathOrNameOverTheTextCapIsRefused()
        {
            Assert.Throws<FormatException>(() => CallArgs.ScenePath(new string('a', 65)));
            Assert.Throws<FormatException>(() => CallArgs.Asset(new string('a', 65)));
            Assert.Throws<FormatException>(() => CallArgs.Singleton(new string('a', 65)));
            Assert.Equal("/" + new string('a', 64), CallArgs.ScenePath(new string('a', 64)));
        }

        [Fact]
        public void ASingletonTokenCarriesTheClassThatKeepsTheTool()
        {
            // The hammer is neither an entity the room numbers nor a scene path: every lawn builds its
            // own tool, and `Hammer.Instance` is the only name both lawns can spell (E60).
            Assert.Equal("!Hammer", CallArgs.Singleton("Hammer"));
            Assert.Equal(RefKind.Singleton, CallArgs.KindOf("!Hammer"));
            Assert.Equal("Hammer", CallArgs.ValueOf("!Hammer"));
            Assert.Equal("[\"!Hammer\"]", CallArgs.Encode("o", CallArgs.Singleton("Hammer")));
            Assert.Equal("!Hammer", CallArgs.Decode("o", "[\"!Hammer\"]")[0]);
        }

        [Theory]
        [InlineData("!")]
        [InlineData("!1Hammer")]
        [InlineData("!Ham mer")]
        [InlineData("!!Hammer")]
        [InlineData("!Ham-mer")]
        public void ASingletonTokenWithoutAClassNameIsNotAReference(string token)
        {
            Assert.Throws<FormatException>(() => CallArgs.KindOf(token));
        }

        [Theory]
        [InlineData("Hammer")]
        [InlineData("_privateTool")]
        [InlineData("Tool2")]
        [InlineData("A")]
        public void AClassNameTheBuildCouldHaveKeepsItsToken(string name)
        {
            Assert.Equal("!" + name, CallArgs.Singleton(name));
            Assert.Equal(RefKind.Singleton, CallArgs.KindOf("!" + name));
            Assert.Equal(name, CallArgs.ValueOf("!" + name));
        }

        [Fact]
        public void AClassNameAtTheTextCapIsStillAToken()
        {
            string longest = new string('a', 64);

            Assert.Equal("!" + longest, CallArgs.Singleton(longest));
            Assert.Equal(RefKind.Singleton, CallArgs.KindOf("!" + longest));
        }

        [Theory]
        [InlineData("!\u0421\u043e\u043b\u043d\u0446\u0435")]
        [InlineData("!Hamm\u00e9r")]
        public void AClassNameThatIsNotAsciiIsNotAReference(string token)
        {
            // A letter this machine's `char.IsLetter` accepts is not a name any class of the build has,
            // and the frame such a token would reach is a lookup the other lawn cannot answer.
            Assert.Throws<FormatException>(() => CallArgs.KindOf(token));
        }

        [Fact]
        public void ASingletonWithoutANameIsWrittenButTheReaderRefusesIt()
        {
            // The writer has nothing to put in place of a missing name, so a call site with no maker uses
            // `None`; the reader is what keeps a bare "!" from ever reaching a lookup.
            Assert.Equal("!", CallArgs.Singleton(null));
            Assert.Equal("!", CallArgs.Singleton(""));
            Assert.Throws<FormatException>(() => CallArgs.KindOf("!"));
        }

        [Fact]
        public void ASingletonTokenIsNotAnEntity()
        {
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf(CallArgs.Singleton("Hammer")));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("/Board"));
            Assert.Throws<FormatException>(() => CallArgs.NetIdOf("~"));
        }

        [Fact]
        public void TheHammersDamageStepTravelsWhole()
        {
            // Zombie.TakeDamage/5 as the build measures it (E60): the damage the crush computed, the maker
            // by class, its kind, the plant type it reports as, and the keep-alive flag.
            object[] decoded = Round("ioeib", 1800, CallArgs.Singleton("Hammer"), 2, 270, false);

            Assert.Equal(1800L, decoded[0]);
            Assert.Equal("!Hammer", decoded[1]);
            Assert.Equal(2L, decoded[2]);
            Assert.Equal(270L, decoded[3]);
            Assert.Equal(false, decoded[4]);
        }

        [Fact]
        public void ANullPathAndANullNameBecomeTheBarePrefixes()
        {
            // A call site that has nothing to name must not be able to write an untyped token by accident.
            Assert.Equal("/", CallArgs.ScenePath(null));
            Assert.Equal("@", CallArgs.Asset(null));
            Assert.Equal(RefKind.ScenePath, CallArgs.KindOf(CallArgs.ScenePath(null)));
        }

        [Fact]
        public void AReferenceArgumentAcceptsATokenAndRefusesAnObject()
        {
            Assert.Throws<FormatException>(() => CallArgs.Encode("o", new object()));
            Assert.Equal("[\"#3\"]", CallArgs.Encode("o", CallArgs.Entity(3)));
            Assert.Equal("#3", CallArgs.Decode("o", "[\"#3\"]")[0]);
        }

        [Fact]
        public void ANullReferenceIsWrittenAsTheTokenForNothing()
        {
            Assert.Equal("[\"~\"]", CallArgs.Encode("o", new object[] { null }));
            Assert.Equal("~", CallArgs.Decode("o", "[null]")[0]);
            Assert.Equal("~", CallArgs.Decode("o", "[\"~\"]")[0]);
        }

        [Fact]
        public void AnIntPassedWhereAnEntityBelongsToIsTakenAsARoomId()
        {
            // The call site hands the ledger id it has; the adapter is what turns it into a token, so a
            // gate never has to remember the prefix.
            Assert.Equal("#5", CallArgs.Decode("o", CallArgs.Encode("o", 5))[0]);
        }

        // ---- the log line --------------------------------------------------

        [Fact]
        public void DescribeNamesEveryCategoryAndStaysShort()
        {
            Assert.Equal("(i=50)", CallArgs.Describe("i", new object[] { 50L }));
            Assert.Equal("(i=1, b=True)", CallArgs.Describe("ib", new object[] { 1L, true }));
            Assert.Equal("(o=-)", CallArgs.Describe("o", new object[0]));
            Assert.Equal("(a=[3])", CallArgs.Describe("a", new object[]
                                     {
                                         new object[] { "#1", "#2", "~" }
                                     }));
            Assert.Equal("()", CallArgs.Describe(null, null));

            string longText = CallArgs.Describe("s", new object[] { new string('a', 40) });
            Assert.Equal("(s=" + new string('a', 24) + "...)", longText);
        }

        [Fact]
        public void DescribeReplacesWhatTheLogCannotHold()
        {
            // A peer owns the bytes of a string argument; the log line is the mod's witness, so a
            // newline must not be able to forge a second one.
            Assert.Equal("(s=two ? lines)",
                         CallArgs.Describe("s", new object[] { "two \n lines" }));
            Assert.DoesNotContain("\n", CallArgs.Describe("s", new object[] { "a\nb" }));
            Assert.DoesNotContain("\u0441", CallArgs.Describe("s", new object[] { "\u0441" }));
        }

        [Fact]
        public void DescribeNeverWritesANonAsciiCharacter()
        {
            string text = CallArgs.Describe("s", new object[] { "sun \u0441\u043e\u043b\u043d\u0446\u0435" });

            foreach (char c in text)
            {
                Assert.True(c <= 126 || c == '?', "Describe wrote a non-ASCII char");
            }
        }

        [Fact]
        public void TheCodecPrintsNothingByItself()
        {
            TestLog.Clear();

            for (int i = 0; i < 200; i++)
            {
                Round("ifb", i, 0.5f, i % 2 == 0);
            }

            Assert.False(TestLog.Has("[call]"), "encoding an argument vector logged something");
        }
    }
}
