using System;
using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The arbiter's decision matrix, tested against tables built here rather than against the shipped
    /// one, so that the answers a wrong table must give are proved even though the shipped table is,
    /// hopefully, never wrong. Four properties carry the reliability of the whole design and each of them
    /// is checked from several directions: nothing outside a room is ever consulted, an unknown method is
    /// refused instead of guessed, a per-frame body cannot be made sendable by any row, a body that rolls
    /// cannot be replayed, and the reason of an answer is always printable.
    ///
    /// Shared collection with CoverageTableTests: both read the log, and xunit runs classes in parallel by
    /// default, so a "nothing was printed" assertion in one class would be decided by another class's
    /// timing. Other tests log too, which is why the checks below only ever look for this area's own tag.
    /// </summary>
    [Collection("coverage")]
    public class ArbiterDecisionTests
    {
        private const string CoveredClass = "Board";
        private const string CoveredMethod = "SetSun";
        private const int CoveredArity = 1;

        private static CoverageTable Table(Authority authority, Verdict verdict, string effect,
                                          long rva = 0x481250, int arity = CoveredArity,
                                          bool rolls = false)
        {
            string key = Arbiter.Key(CoveredClass, CoveredMethod, arity);
            CoverageRule rule = new CoverageRule(CoveredClass, CoveredMethod, arity, authority, verdict);
            CoverageFact fact = new CoverageFact(key, rva, 7, 0, false, "void T(int)", "i", effect, rolls);
            return new CoverageTable(new[] { rule }, new[] { fact });
        }

        // ---- nothing outside a room is ever consulted -------------------------

        [Fact]
        public void OutsideARoomTheAnswerIsLocalBeforeTheTableIsRead()
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, "world"),
                                        CoveredClass, CoveredMethod, CoveredArity, false, true);

            Assert.Equal(Verdict.Local, d.Verdict);
            Assert.Contains("no room", d.Reason);
        }

        [Fact]
        public void OutsideARoomANullTableStillAnswersLocal()
        {
            // The single player guarantee cannot depend on the table being present, complete or sane.
            Decision d = Arbiter.Decide(null, CoveredClass, CoveredMethod, CoveredArity, false, false);

            Assert.Equal(Verdict.Local, d.Verdict);
            Assert.Null(d.Rule);
            Assert.Null(d.Fact);
        }

        [Fact]
        public void OutsideARoomAnEmptyTableStillAnswersLocal()
        {
            CoverageTable empty = new CoverageTable(new CoverageRule[0], new CoverageFact[0]);
            Decision d = Arbiter.Decide(empty, "Zombie", "TakeDamage", 5, false, false);

            Assert.Equal(Verdict.Local, d.Verdict);
        }

        [Fact]
        public void OutsideARoomGarbageNamesAreStillLocalBecauseNothingIsAsked()
        {
            CoverageTable table = Table(Authority.HostOnly, Verdict.Replicate, "world");
            string[][] garbage =
            {
                new string[] { null, null }, new[] { "", "" }, new[] { "Board", null },
                new string[] { null, "SetSun" }, new[] { "   ", "   " },
                new[] { "Board$$", "SetSun" }, new[] { "_il2cpp", "GetSun" }
            };

            foreach (string[] names in garbage)
            {
                Decision d = Arbiter.Decide(table, names[0], names[1], 1, false, true);
                Assert.Equal(Verdict.Local, d.Verdict);
            }
        }

        [Fact]
        public void OutsideARoomTheVerdictIsLocalForEveryShapeOfCaller()
        {
            CoverageTable table = Table(Authority.HostOnly, Verdict.Replicate, "world");

            foreach (bool isHost in new[] { false, true })
            {
                Assert.Equal(Verdict.Local,
                    Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity,
                                   false, isHost).Verdict);
            }
        }

        [Fact]
        public void TheArbiterItselfWritesNoLogLine()
        {
            // A decision is not an action. Only the audit and the wire are allowed to print, so a table
            // asked a thousand times a frame cannot be the reason a log stops being readable.
            TestLog.Clear();
            CoverageTable table = Table(Authority.AnyPeer, Verdict.Replicate, "world");

            for (int i = 0; i < 1000; i++)
            {
                Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, i % 2 == 0);
                Arbiter.Decide(table, "Nope", "Nada", 0, true, true);
                Arbiter.Decide(null, CoveredClass, CoveredMethod, CoveredArity, true, true);
            }

            Assert.False(TestLog.Has("[cov]"), "a decision logged: " + TestLog.Describe());
        }

        // ---- an unknown method is refused, never guessed ---------------------

        [Theory]
        [InlineData("Board", "SetSun", 0)]
        [InlineData("Board", "SetSun", 2)]
        [InlineData("Board", "SetSun", 7)]
        [InlineData("Board", "SetSun", -1)]
        [InlineData("Board", "GetSun", 1)]
        [InlineData("Board", "TakeDamage", 5)]
        [InlineData("Zombie", "TakeDamage", 5)]
        [InlineData("Mouse", "TryToSetPlantByCard", 1)]
        [InlineData("Core", "Lawnf", 0)]
        [InlineData("board", "setsun", 1)]
        [InlineData("BOARD", "SetSun", 1)]
        [InlineData(" Board", "SetSun", 1)]
        [InlineData("Board", " SetSun", 1)]
        [InlineData("Board", "SetSun ", 1)]
        [InlineData("System.Collections.Generic.List`1", "Add", 1)]
        public void InARoomAnythingTheTableDoesNotNameIsRefused(string cls, string method, int arity)
        {
            CoverageTable table = Table(Authority.AnyPeer, Verdict.Replicate, "world");
            Decision d = Arbiter.Decide(table, cls, method, arity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("not in the coverage table", d.Reason);
            Assert.Null(d.Rule);
            Assert.Equal(Arbiter.Key(cls, method, arity), d.Key);
        }

        [Fact]
        public void ARefusalOfAnUnknownMethodCarriesTheKeyItWasAskedFor()
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, "world"),
                                        "Board", "SunUpdate", 0, true, true);

            Assert.Equal("Board.SunUpdate/0", d.Key);
            Assert.Contains("Board.SunUpdate/0", d.ToString());
        }

        [Fact]
        public void ANullNameIsRefusedInsideARoomInsteadOfThrowing()
        {
            // The name arrives from a socket. Throwing there would be the old bug with a new spelling.
            CoverageTable table = Table(Authority.AnyPeer, Verdict.Replicate, "world");

            Decision d = Arbiter.Decide(table, null, null, int.MinValue, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Equal("./-2147483648", d.Key);
        }

        [Fact]
        public void ARowWithoutAMeasuredFactIsRefusedAndSaysHowToFixIt()
        {
            CoverageRule rule = new CoverageRule(CoveredClass, CoveredMethod, CoveredArity,
                                                 Authority.AnyPeer, Verdict.Replicate);
            CoverageTable table = new CoverageTable(new[] { rule }, new CoverageFact[0]);

            Decision d = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("pvz-index.py --arbiter", d.Reason);
        }

        [Fact]
        public void ANullTableInsideARoomRefusesEverything()
        {
            Decision d = Arbiter.Decide(null, CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("no coverage table", d.Reason);
        }

        // ---- a per-frame body cannot be made sendable by any row -------------

        [Theory]
        [InlineData("frame")]
        [InlineData("gen")]
        [InlineData("blocked")]
        public void AnUnsendableBodyIsRefusedEvenWhenTheRowAsksForIt(string effect)
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, effect),
                                        CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains(effect, d.Reason);
        }

        [Theory]
        [InlineData("frame")]
        [InlineData("gen")]
        [InlineData("blocked")]
        public void AnUnsendableBodyIsRefusedForEveryVerdictTheRowCouldClaim(string effect)
        {
            foreach (Verdict verdict in new[]
                     { Verdict.Mirror, Verdict.Local, Verdict.Replicate, Verdict.Never })
            {
                Decision d = Arbiter.Decide(Table(Authority.HostOnly, verdict, effect),
                                            CoveredClass, CoveredMethod, CoveredArity, true, true);

                Assert.Equal(Verdict.Never, d.Verdict);
            }
        }

        [Theory]
        [InlineData("read")]
        [InlineData("pure")]
        public void AReplicateRowOnABodyThatChangesNothingIsRefused(string effect)
        {
            // Announcing a getter is traffic with no fact behind it, and the reason has to say that.
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, effect),
                                        CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("carries no change", d.Reason);
        }

        [Theory]
        [InlineData("read")]
        [InlineData("pure")]
        [InlineData("world")]
        [InlineData("config")]
        [InlineData("side")]
        public void ASideOrLocalRowIsNotJudgedByTheEffectOfItsBody(string effect)
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Local, effect),
                                        CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Local, d.Verdict);
        }

        // ---- a body that rolls cannot be replayed ----------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AReplicateRowOnABodyThatRollsIsRefusedEitherWayRound(bool callerIsHost)
        {
            // The merge door is the real case: Board.SetEvePlants rolls which plant grows, so the host
            // and the peer would each grow a different one from the same packet (E35).
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, "world",
                                              0x47C6F0, 3, true),
                                        CoveredClass, CoveredMethod, 3, true, callerIsHost);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("rolls", d.Reason);
        }

        [Theory]
        [InlineData(Verdict.Replicate)]
        [InlineData(Verdict.Mirror)]
        public void ARollingBodyRefusesEveryRowThatTravels(Verdict verdict)
        {
            // Both travelling rows end with this machine code running on every lawn, so a body that rolls
            // gives two lawns two different answers whichever lane the frame came in on (E35).
            CoverageTable table = Table(Authority.HostOnly, verdict, "world", 0x47C6F0, 3, true);

            Decision d = Arbiter.Decide(table, CoveredClass, CoveredMethod, 3, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("rolls", d.Reason);
        }

        [Theory]
        [InlineData(Verdict.Local)]
        [InlineData(Verdict.Never)]
        public void ARollingBodyChangesNothingForARowThatStaysHome(Verdict verdict)
        {
            CoverageTable table = Table(Authority.HostOnly, verdict, "world", 0x47C6F0, 3, true);

            Decision d = Arbiter.Decide(table, CoveredClass, CoveredMethod, 3, true, true);

            Assert.Equal(verdict, d.Verdict);
        }

        [Fact]
        public void OutsideARoomABodyThatRollsIsStillTheGamesOwnBusiness()
        {
            // Single player rolled a plant this way long before the mod existed, and the answer cannot
            // change because a row was written badly: the roll check lives behind the room check.
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, "world",
                                              0x47C6F0, 3, true),
                                        CoveredClass, CoveredMethod, 3, false, true);

            Assert.Equal(Verdict.Local, d.Verdict);
            Assert.Contains("no room", d.Reason);
        }

        // ---- authority -------------------------------------------------------

        [Fact]
        public void AHostOnlyDoorAnnouncesNothingFromAPeer()
        {
            CoverageTable table = Table(Authority.HostOnly, Verdict.Replicate, "world");

            Decision host = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, true);
            Decision peer = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, false);

            Assert.Equal(Verdict.Replicate, host.Verdict);
            Assert.Equal(Verdict.Never, peer.Verdict);
            Assert.Contains("only the host", peer.Reason);
        }

        [Theory]
        [InlineData(Verdict.Replicate, true)]
        [InlineData(Verdict.Replicate, false)]
        [InlineData(Verdict.Mirror, true)]
        [InlineData(Verdict.Mirror, false)]
        public void AnAnyPeerDoorTravelsWhateverSeatOpenedIt(Verdict verdict, bool isHost)
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, verdict, "world"),
                                        CoveredClass, CoveredMethod, CoveredArity, true, isHost);

            Assert.Equal(verdict, d.Verdict);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AuthorityIsOnlyReadForARowThatTravels(bool isHost)
        {
            // A row that stays on this lawn has no sender to distrust, so the authority column is not
            // asked about it at all.
            foreach (Verdict verdict in new[] { Verdict.Local, Verdict.Never })
            {
                Decision d = Arbiter.Decide(Table(Authority.HostOnly, verdict, "world"),
                                            CoveredClass, CoveredMethod, CoveredArity, true, isHost);

                Assert.Equal(verdict, d.Verdict);
            }
        }

        [Fact]
        public void AHostOnlyMirrorRowIsBelievedOnlyFromTheHostSeat()
        {
            // A latest value is the room's state, so the seat question is the same one an ordered row
            // asks: travelling is what puts a row under an authority, not the lane it travels on.
            CoverageTable table = Table(Authority.HostOnly, Verdict.Mirror, "world");

            Decision host = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, true);
            Decision peer = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, false);

            Assert.Equal(Verdict.Mirror, host.Verdict);
            Assert.Equal(Verdict.Never, peer.Verdict);
            Assert.Contains("only the host", peer.Reason);
        }

        [Fact]
        public void TheHostAnswerForANeverRowIsStillNever()
        {
            Decision d = Arbiter.Decide(Table(Authority.HostOnly, Verdict.Never, "world"),
                                        CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.DoesNotContain("not in the coverage table", d.Reason);
        }

        // ---- the answer is always printable and always the same --------------

        [Fact]
        public void EveryAnswerCarriesANonEmptyPrintableReason()
        {
            CoverageTable table = Table(Authority.AnyPeer, Verdict.Replicate, "world");
            object[][] asks =
            {
                new object[] { CoveredClass, CoveredMethod, CoveredArity, true, true },
                new object[] { CoveredClass, CoveredMethod, CoveredArity, true, false },
                new object[] { CoveredClass, CoveredMethod, CoveredArity, false, true },
                new object[] { "Nope", "Nada", 0, true, true },
                new object[] { null, null, 3, true, false },
                new object[] { CoveredClass, CoveredMethod, 9, false, false }
            };

            foreach (object[] ask in asks)
            {
                Decision d = Arbiter.Decide(table, (string)ask[0], (string)ask[1], (int)ask[2],
                                            (bool)ask[3], (bool)ask[4]);

                Assert.False(string.IsNullOrWhiteSpace(d.Reason), d.Key);
                Assert.True(IsAscii(d.Reason), "non-ASCII reason: " + d.Reason);
                Assert.True(IsAscii(d.ToString()), "non-ASCII answer: " + d);
                Assert.NotNull(d.Key);
            }
        }

        [Fact]
        public void TheSameQuestionTwiceGivesTheSameAnswer()
        {
            CoverageTable table = Table(Authority.HostOnly, Verdict.Replicate, "world");

            Decision first = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, false);
            Decision second = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, false);

            Assert.Equal(first.Verdict, second.Verdict);
            Assert.Equal(first.Reason, second.Reason);
            Assert.Equal(first.Key, second.Key);
        }

        [Fact]
        public void TheAnswerHandsBackTheRowAndTheFactItUsed()
        {
            CoverageTable table = Table(Authority.AnyPeer, Verdict.Replicate, "world");
            Decision d = Arbiter.Decide(table, CoveredClass, CoveredMethod, CoveredArity, true, true);

            Assert.Same(table.FindRule(d.Key), d.Rule);
            Assert.Same(table.FindFact(d.Key), d.Fact);
            Assert.Equal(0x481250L, d.Fact.Rva);
        }

        [Fact]
        public void AnUnknownAnswerHandsBackNeitherRowNorFact()
        {
            Decision d = Arbiter.Decide(Table(Authority.AnyPeer, Verdict.Replicate, "world"),
                                        "Ghost", "Method", 1, true, true);

            Assert.Null(d.Rule);
            Assert.Null(d.Fact);
        }

        private static bool IsAscii(string text)
        {
            foreach (char c in text)
            {
                if (c < ' ' || c > '~')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
