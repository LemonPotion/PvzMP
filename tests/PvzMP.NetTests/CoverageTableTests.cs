using System;
using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The shipped table: the rows the mod decided, the facts GameAssembly.dll measured, and the audit
    /// that keeps them from drifting apart. This is the file that fails when somebody adds a row and
    /// forgets to rerun the generator, which is the failure that would otherwise surface as a peer
    /// silently not doing what the host asked.
    /// </summary>
    [Collection("coverage")]
    public class CoverageTableTests
    {
        private static CoverageTable Table
        {
            get { return Arbiter.Default; }
        }

        // ---- the table and the build agree ----------------------------------

        [Fact]
        public void EveryRowIsMeasuredAndEveryMeasurementIsARow()
        {
            List<string> problems = Arbiter.Problems(Table);

            Assert.True(problems.Count == 0, "coverage problems: " + string.Join(" | ", problems.ToArray()));
        }

        [Fact]
        public void EveryRowOfTheShippedTableHasAFact()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                Assert.NotNull(rule.Key);
                CoverageFact fact = Table.FindFact(rule.Key);
                Assert.NotNull(fact);
                Assert.Equal(rule.Arity, fact.Cats.Length);
            }

            Assert.Equal(Coverage.Rules.Length, Table.FactList.Count);
        }

        [Fact]
        public void TheGeneratedFactsAreSortedSoTheFileIsByteStable()
        {
            string previous = null;

            foreach (CoverageFact fact in CoverageFacts.Rows)
            {
                if (previous != null)
                {
                    Assert.True(string.CompareOrdinal(previous, fact.Key) < 0,
                        "generated facts out of order: " + previous + " then " + fact.Key);
                }

                previous = fact.Key;
            }
        }

        [Fact]
        public void NoRowIsMeasuredAtAnAddressTheBuildDoesNotHave()
        {
            foreach (CoverageFact fact in CoverageFacts.Rows)
            {
                Assert.True(fact.Rva > 0, fact.Key + " has no body");
                Assert.True(fact.Size > 0, fact.Key + " has an empty body");
            }
        }

        [Theory]
        [InlineData("Board", "SetSun", 1, 7, 0)]
        [InlineData("Board", "GetSun", 2, 899, 10)]
        [InlineData("Board", "SunUpdate", 0, 1244, 12)]
        [InlineData("CreateItem", "SetCoin", 6, 1169, 14)]
        [InlineData("CoinSun", "Die", 0, 86, 2)]
        [InlineData("Plant", "Die", 1, 2476, 46)]
        [InlineData("Board", "ClearTheBoard", 0, 400, 3)]
        [InlineData("Shovel", "Use", 1, 1732, 21)]
        [InlineData("Mouse", "TryToSetPlantByCard", 0, 4385, 84)]
        public void TheFactsCarryTheSizesAndCallCountsEvidenceRecords(string cls, string method,
                                                                      int arity, int size, int calls)
        {
            // EVIDENCE.md E27-E31 and E32 measured these bodies by hand, with the int3 cut of E26. A
            // generated file that disagrees with them means the index, not the evidence, moved.
            CoverageFact fact = Table.FindFact(Arbiter.Key(cls, method, arity));

            Assert.NotNull(fact);
            Assert.Equal(size, fact.Size);
            Assert.Equal(calls, fact.Calls);
        }

        [Fact]
        public void TheEffectVocabularyIsTheOneTheAtlasUses()
        {
            HashSet<string> known = new HashSet<string>(StringComparer.Ordinal)
            {
                "gen", "frame", "blocked", "world", "config", "side", "pure", "read"
            };

            foreach (CoverageFact fact in CoverageFacts.Rows)
            {
                Assert.True(known.Contains(fact.Effect), fact.Key + " effect " + fact.Effect);
            }
        }

        [Fact]
        public void EveryReplicateRowNamesADoorThatChangesTheWorld()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                if (rule.Verdict != Verdict.Replicate)
                {
                    continue;
                }

                Assert.Equal("world", Table.FindFact(rule.Key).Effect);
            }
        }

        [Fact]
        public void TheKeyIsSpelledTheWayTheWireSpellsIt()
        {
            Assert.Equal("Board.SetSun/1", Arbiter.Key("Board", "SetSun", 1));
            Assert.Equal("Board.SunUpdate/0", Arbiter.Key("Board", "SunUpdate", 0));
            // A name that is missing still forms a key: the separator is always there, because a key
            // builder with special cases is one more thing that can disagree with the wire.
            Assert.Equal("./0", Arbiter.Key(null, null, 0));
            Assert.Equal("a.b/-1", Arbiter.Key("a", "b", -1));
        }

        [Fact]
        public void RowsAndFactsAreLookedUpByOrdinalIdentityNotByFuzzyName()
        {
            Assert.Null(Table.FindFact("board.setsun/1"));
            Assert.Null(Table.FindFact("Board.SetSun/0"));
            Assert.Null(Table.FindFact("SetSun/1"));
            Assert.Null(Table.FindFact("Board.SetSun"));
            Assert.NotNull(Table.FindFact("Board.SetSun/1"));
        }

        // ---- what the shipped table answers ---------------------------------

        [Fact]
        public void OutsideARoomEveryRowOfTheTableIsLocal()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                foreach (bool isHost in new[] { false, true })
                {
                    Decision d = Arbiter.Decide(Table, rule.ClassToken, rule.Method, rule.Arity,
                                                false, isHost);

                    Assert.Equal(Verdict.Local, d.Verdict);
                    Assert.Contains("no room", d.Reason);
                }
            }
        }

        [Fact]
        public void InsideARoomTheHostGetsExactlyTheVerdictTheRowDeclares()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                Decision d = Arbiter.Decide(Table, rule.ClassToken, rule.Method, rule.Arity, true, true);

                Assert.Equal(rule.Verdict, d.Verdict);
            }
        }

        [Fact]
        public void InsideARoomAPeerKeepsOnlyTheDoorsThatBelongToIt()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                Decision d = Arbiter.Decide(Table, rule.ClassToken, rule.Method, rule.Arity, true, false);
                Verdict expected = Arbiter.Travels(rule.Verdict)
                                   && rule.Authority == Authority.HostOnly
                    ? Verdict.Never
                    : rule.Verdict;

                Assert.Equal(expected, d.Verdict);
            }
        }

        [Theory]
        // The bank. Only the host's own code may change it, and a peer that says otherwise is refused.
        // Setting it is one absolute field write (E27), so its frame is a value on the latest-wins lane;
        // adding an amount to it only means something in order, so GetSun keeps the ordered lane.
        [InlineData("Board", "SetSun", 1, true, Verdict.Mirror)]
        [InlineData("Board", "SetSun", 1, false, Verdict.Never)]
        [InlineData("Board", "GetSun", 2, true, Verdict.Replicate)]
        [InlineData("Board", "GetSun", 2, false, Verdict.Never)]
        [InlineData("Board", "GetSun", 1, true, Verdict.Never)]
        [InlineData("Board", "UseSun", 1, true, Verdict.Local)]
        [InlineData("Board", "UseSun", 1, false, Verdict.Local)]
        // The sky: SunUpdate rolls Random eight times (E30), so it never travels and never gets sent.
        [InlineData("Board", "SunUpdate", 0, true, Verdict.Never)]
        [InlineData("Board", "SunUpdate", 0, false, Verdict.Never)]
        [InlineData("CreateItem", "SetCoin", 6, true, Verdict.Replicate)]
        [InlineData("CreateItem", "SetCoin", 6, false, Verdict.Never)]
        [InlineData("CoinSun", "Die", 0, true, Verdict.Replicate)]
        [InlineData("CoinSun", "MoveToPosition", 0, true, Verdict.Local)]
        [InlineData("CoinSun", "Update", 0, true, Verdict.Never)]
        // Planting and digging: any seat's click is the room's business.
        [InlineData("Mouse", "SetPlantFromCard", 3, true, Verdict.Replicate)]
        [InlineData("Mouse", "SetPlantFromCard", 3, false, Verdict.Replicate)]
        [InlineData("Mouse", "SetPlantFromCard", 2, false, Verdict.Never)]
        [InlineData("Mouse", "TryToSetPlantByCard", 0, false, Verdict.Never)]
        [InlineData("Mouse", "TryToSetPlantByGlove", 0, true, Verdict.Never)]
        [InlineData("Shovel", "Use", 1, false, Verdict.Replicate)]
        [InlineData("Shovel", "PayBackSun", 1, true, Verdict.Local)]
        [InlineData("Shovel", "ReturnPlantToGarden", 1, true, Verdict.Local)]
        // The hammer: the click cannot travel, the crush's own damage step can, and either end may start
        // it. `Hammer.EffectZombie` is not a blow at all: its body writes no damage (E60).
        [InlineData("Zombie", "TakeDamage", 5, true, Verdict.Replicate)]
        [InlineData("Zombie", "TakeDamage", 5, false, Verdict.Replicate)]
        [InlineData("Zombie", "TakeDamage", 4, true, Verdict.Never)]
        [InlineData("Hammer", "EffectZombie", 1, true, Verdict.Never)]
        [InlineData("Hammer", "Use", 1, false, Verdict.Never)]
        [InlineData("Board", "SetEvePlants", 3, true, Verdict.Never)]
        [InlineData("Board", "SetEvePlants", 3, false, Verdict.Never)]
        [InlineData("Board", "ClearTheBoard", 0, true, Verdict.Replicate)]
        // Not covered yet: a method the build has but the table does not name.
        [InlineData("Plant", "Die", 1, true, Verdict.Never)]
        [InlineData("Board", "Update", 0, true, Verdict.Never)]
        public void TheShippedTableAnswersTheseDoorsThisWay(string cls, string method, int arity,
                                                            bool isHost, Verdict expected)
        {
            Decision d = Arbiter.Decide(Table, cls, method, arity, true, isHost);

            Assert.Equal(expected, d.Verdict);
        }

        [Fact]
        public void ADoorThatIsNotCoveredSaysSoInsteadOfPretending()
        {
            Decision d = Arbiter.Decide(Table, "Hammer", "EffectZombie", 1, true, true);

            Assert.Equal(Verdict.Never, d.Verdict);
            Assert.Contains("not in the coverage table", d.Reason);
        }

        [Fact]
        public void TheHammersDamageDoorCarriesTheShapeTheBuildMeasured()
        {
            // E60: Zombie.TakeDamage/5 at rva 0x712210, 1761 bytes, cats ioeib, effect world, no roll.
            CoverageRule rule = Table.FindRule("Zombie.TakeDamage/5");
            CoverageFact fact = Table.FindFact("Zombie.TakeDamage/5");

            Assert.NotNull(rule);
            Assert.NotNull(fact);
            Assert.Equal(Authority.AnyPeer, rule.Authority);
            Assert.Equal("ioeib", fact.Cats);
            Assert.Equal("world", fact.Effect);
            Assert.False(fact.Rolls);
            Assert.Equal(1761, fact.Size);
        }

        [Fact]
        public void TheDoorThatOnlyDrawsTheCrushIsNotARow()
        {
            // Hammer.EffectZombie draws bounds, an explosion and a freeze and writes no damage (E60). Naming
            // it as the blow left the host's zombie standing after every peer's kill, so it must not come
            // back as a row; neither may the click, which aims with this machine's pointer.
            Assert.Null(Table.FindRule("Hammer.EffectZombie/1"));
            Assert.Null(Table.FindRule("Hammer.Use/1"));
        }

        // ---- the audit -------------------------------------------------------

        [Fact]
        public void TheAuditPrintsOneSummaryLineAndNothingElseForAGoodTable()
        {
            TestLog.Clear();

            string summary = Arbiter.Audit(Table);

            Assert.Equal("coverage: " + Coverage.Rules.Length + " rows, " + Coverage.Rules.Length
                         + " measured, 0 problems", summary);
            Assert.True(TestLog.Has(summary), TestLog.Describe());
            Assert.False(TestLog.Has("Error [cov]"), "a good table must not raise an error line");
        }

        [Fact]
        public void TheAuditIsTheSameEveryTimeItIsAsked()
        {
            Assert.Equal(Arbiter.Audit(Table), Arbiter.Audit(Table));
        }

        [Fact]
        public void TheAuditNamesARowThatDriftedFromTheBuild()
        {
            CoverageRule rule = new CoverageRule("Board", "SetSun", 1, Authority.AnyPeer,
                                                 Verdict.Replicate);
            CoverageTable broken = new CoverageTable(new[] { rule }, new CoverageFact[0]);

            TestLog.Clear();
            string summary = Arbiter.Audit(broken);

            Assert.Equal("coverage: 1 rows, 0 measured, 1 problems", summary);
            Assert.True(TestLog.Has("has no measured fact"), TestLog.Describe());
            Assert.True(TestLog.Has("pvz-index.py --arbiter"), TestLog.Describe());
        }

        [Fact]
        public void TheAuditNamesAFactNobodyRowsFor()
        {
            CoverageFact orphan = new CoverageFact("Board.Orphan/0", 0x1000, 8, 0, false,
                                                   "void T()", "", "world", false);
            CoverageTable broken = new CoverageTable(new CoverageRule[0], new[] { orphan });

            Assert.Contains("no row names it", string.Join(" | ",
                Arbiter.Problems(broken).ToArray()));
        }

        [Fact]
        public void TheAuditRefusesAFrameRowThatWantedToTravel()
        {
            CoverageRule rule = new CoverageRule("Board", "Tick", 0, Authority.AnyPeer, Verdict.Mirror);
            CoverageFact fact = new CoverageFact(rule.Key, 0x1000, 40, 1, false,
                                                 "void T()", "", "frame", false);
            CoverageTable broken = new CoverageTable(new[] { rule }, new[] { fact });

            List<string> problems = Arbiter.Problems(broken);

            Assert.Contains("Board.Tick/0", string.Join(" | ", problems.ToArray()));
            Assert.Contains("only Never", string.Join(" | ", problems.ToArray()));
        }

        [Fact]
        public void TheAuditRefusesAReplicateRowOnABodyThatRolls()
        {
            CoverageRule rule = new CoverageRule("Board", "SetEvePlants", 3, Authority.HostOnly,
                                                 Verdict.Replicate);
            CoverageFact fact = new CoverageFact(rule.Key, 0x47C6F0, 1291, 22, false,
                                                 "obj T(int,int,bool)", "iib", "world", true);
            CoverageTable broken = new CoverageTable(new[] { rule }, new[] { fact });

            Assert.Contains("rolls", string.Join(" | ", Arbiter.Problems(broken).ToArray()));
        }

        [Fact]
        public void NoReplicateRowInTheShippedTableReplaysARoll()
        {
            // The one property the merge feature was wrong about: the host rolled a plant, sent the
            // call, and every peer rolled a different one. That cannot be a row any more.
            foreach (CoverageRule rule in Table.RuleList)
            {
                if (rule.Verdict != Verdict.Replicate)
                {
                    continue;
                }

                CoverageFact fact = Table.FindFact(rule.Key);
                Assert.False(fact.Rolls, rule.Key + " would replay a roll onto every end");
            }
        }

        [Theory]
        [InlineData("Board", "SetEvePlants", 3)]
        [InlineData("Board", "SunUpdate", 0)]
        public void TheTwoRollingDoorsAreMeasuredAndRefused(string cls, string method, int arity)
        {
            string key = Arbiter.Key(cls, method, arity);

            Assert.True(Table.FindFact(key).Rolls, key + " stopped measuring the roll (E35)");
            Assert.Equal(Verdict.Never, Table.FindRule(key).Verdict);
            Assert.Equal(Verdict.Never, Arbiter.Decide(Table, cls, method, arity, true, true).Verdict);
        }

        [Fact]
        public void TheAuditCatchesADuplicateRowAndANegativeArity()
        {
            CoverageRule first = new CoverageRule("Board", "SetSun", 1, Authority.AnyPeer, Verdict.Local);
            CoverageRule again = new CoverageRule("Board", "SetSun", 1, Authority.HostOnly,
                                                  Verdict.Replicate);
            CoverageRule odd = new CoverageRule("Board", "SetSun", -2, Authority.AnyPeer, Verdict.Local);
            CoverageFact fact = new CoverageFact(first.Key, 0x481250, 7, 0, false,
                                                 "void T(int)", "i", "world", false);
            CoverageTable broken = new CoverageTable(new[] { first, again, odd }, new[] { fact });

            List<string> problems = Arbiter.Problems(broken);
            string all = string.Join(" | ", problems.ToArray());

            Assert.Contains("duplicate coverage row", all);
            Assert.Contains("negative arity", all);

            // The first row wins the key, so a collision cannot quietly change what the room does.
            Assert.Same(first, broken.FindRule(first.Key));
        }

        [Fact]
        public void TheAuditIgnoresNullRowsInsteadOfThrowingOnThem()
        {
            CoverageTable broken = new CoverageTable(
                new[] { null, new CoverageRule("Board", "SetSun", 1, Authority.AnyPeer, Verdict.Local) },
                new[] { null, new CoverageFact("", 0, 0, 0, false, "", "", "", false) });

            Assert.Single(broken.RuleList);
            Assert.Empty(broken.FactList);
            Assert.Contains("has no measured fact",
                string.Join(" | ", Arbiter.Problems(broken).ToArray()));
        }

        [Fact]
        public void TheProblemsOfANullTableSayThereIsNoTable()
        {
            List<string> problems = Arbiter.Problems(null);

            Assert.Single(problems);
            Assert.Equal("no coverage table", problems[0]);
        }

        // ---- shape of the table itself --------------------------------------

        [Fact]
        public void TheRowsAreSpelledInAsciiAndShortEnoughToRead()
        {
            foreach (CoverageRule rule in Coverage.Rules)
            {
                Assert.False(string.IsNullOrEmpty(rule.ClassToken), "a row without a class");
                Assert.False(string.IsNullOrEmpty(rule.Method), "a row without a method");
                Assert.True(rule.Arity >= 0, rule.Key);
                Assert.True(rule.Key.Length <= 40, "unreadably long key: " + rule.Key);

                foreach (char c in rule.Key)
                {
                    Assert.True(c >= ' ' && c <= '~', "non-ASCII key: " + rule.Key);
                }
            }
        }

        [Fact]
        public void TheTableNamesRealGameClassesAndNotALibraryThatHappensToMatch()
        {
            HashSet<string> classes = new HashSet<string>(StringComparer.Ordinal);
            foreach (CoverageRule rule in Coverage.Rules)
            {
                classes.Add(rule.ClassToken);
            }

            Assert.Contains("Board", classes);
            Assert.Contains("Mouse", classes);
            Assert.Contains("Shovel", classes);
            Assert.Contains("CreateItem", classes);
            Assert.Contains("CoinSun", classes);

            // Core.Lawnf and Lawnf are both real (E24), and no door of the room lives in a helper.
            Assert.False(classes.Contains("Lawnf"), "a static helper is not a door");
            Assert.False(classes.Contains("Core.Lawnf"), "a static helper is not a door");
        }

        [Fact]
        public void TheShippedTableIsBigEnoughToCoverTheFirstBatch()
        {
            int ordered = 0;
            int latest = 0;
            foreach (CoverageRule rule in Coverage.Rules)
            {
                if (rule.Verdict == Verdict.Replicate)
                {
                    ordered++;
                }
                else if (rule.Verdict == Verdict.Mirror)
                {
                    latest++;
                }
            }

            // Seven is the honest number of doors that travel: the merge door left this list when the
            // measurement showed it rolls (E35), and a door that rolls cannot travel as a call. One of
            // the seven is a value rather than a change, which is what the latest-wins lane is for.
            Assert.True(ordered + latest >= 7, "only " + (ordered + latest) + " doors travel");
            Assert.True(latest >= 1, "no row uses the lane a value travels on");
            Assert.True(Coverage.Rules.Length >= 17, "rows were lost from the table");
        }
    }
}
