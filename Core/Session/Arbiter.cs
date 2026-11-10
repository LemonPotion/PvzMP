using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// One coverage row as the native build measured it. Nothing here is remembered by hand: the generator
    /// in tools/re/pvz-index.py writes the file these rows live in, so a row cannot survive a game update
    /// that moved the method, changed its parameter list or gave it a different body.
    /// </summary>
    public sealed class CoverageFact
    {
        public CoverageFact(string key, long rva, int size, int calls, bool isStatic,
                            string format, string cats, string effect, bool rolls)
        {
            Key = key;
            Rva = rva;
            Size = size;
            Calls = calls;
            IsStatic = isStatic;
            Format = format;
            Cats = cats;
            Effect = effect;
            Rolls = rolls;
        }

        public string Key { get; private set; }

        /// <summary>Address of the body in GameAssembly.dll, 0 when the build has none.</summary>
        public long Rva { get; private set; }

        /// <summary>Code length, cut at the int3 padding the compiler leaves after every body (E26).</summary>
        public int Size { get; private set; }

        /// <summary>Static calls out of the body that resolve to a known method.</summary>
        public int Calls { get; private set; }

        public bool IsStatic { get; private set; }

        /// <summary>The signature shape the wire adapter is chosen by, e.g. void T(int).</summary>
        public string Format { get; private set; }

        /// <summary>One category letter per declared parameter, instance pointer not counted.</summary>
        public string Cats { get; private set; }

        /// <summary>What the body does: world, config, side, read, pure, frame, gen or blocked.</summary>
        public string Effect { get; private set; }

        /// <summary>
        /// True when the body resolves a call into UnityEngine.Random. A replay of such a method runs
        /// the dice again on every end, so the ends do not agree; what has to travel is the outcome.
        /// </summary>
        public bool Rolls { get; private set; }

        public override string ToString()
        {
            return Key + " 0x" + Rva.ToString("X") + " " + Format + " " + Effect;
        }
    }

    /// <summary>
    /// The answer to one question: may this native method be the shared world's business right now.
    /// Reason is never empty, because a refusal that cannot say why is the bug this whole table exists
    /// to remove.
    /// </summary>
    public sealed class Decision
    {
        internal Decision(Verdict verdict, string reason, CoverageRule rule, CoverageFact fact)
        {
            Verdict = verdict;
            Reason = reason;
            Rule = rule;
            Fact = fact;
        }

        public Verdict Verdict { get; private set; }

        public string Reason { get; private set; }

        /// <summary>The row that decided, null when nothing in the table matched.</summary>
        public CoverageRule Rule { get; private set; }

        /// <summary>What the build says about the method, null when the row was never measured.</summary>
        public CoverageFact Fact { get; private set; }

        /// <summary>The key as it was asked, for the log line that refuses it.</summary>
        public string Key { get; internal set; }

        public override string ToString()
        {
            return Verdict + " " + Key + " (" + Reason + ")";
        }
    }

    /// <summary>
    /// The rows and the facts together. Held in an object rather than read from the two static arrays so
    /// that the decision matrix can be tested with a table of its own; the mod uses the single Default.
    /// </summary>
    public sealed class CoverageTable
    {
        private readonly Dictionary<string, CoverageRule> _rules;
        private readonly Dictionary<string, CoverageFact> _facts;
        private readonly List<CoverageRule> _ruleList;
        private readonly List<CoverageFact> _factList;

        public CoverageTable(IEnumerable<CoverageRule> rules, IEnumerable<CoverageFact> facts)
        {
            _rules = new Dictionary<string, CoverageRule>(StringComparer.Ordinal);
            _facts = new Dictionary<string, CoverageFact>(StringComparer.Ordinal);
            _ruleList = new List<CoverageRule>();
            _factList = new List<CoverageFact>();

            if (rules != null)
            {
                foreach (CoverageRule rule in rules)
                {
                    if (rule == null)
                    {
                        continue;
                    }

                    // The first row for a key wins, and Problems reports the collision, so a duplicated
                    // row cannot change what the room does - it only fails the audit.
                    string key = rule.Key;
                    if (!_rules.ContainsKey(key))
                    {
                        _rules[key] = rule;
                    }

                    _ruleList.Add(rule);
                }
            }

            if (facts != null)
            {
                foreach (CoverageFact fact in facts)
                {
                    if (fact == null || string.IsNullOrEmpty(fact.Key))
                    {
                        continue;
                    }

                    if (!_facts.ContainsKey(fact.Key))
                    {
                        _facts[fact.Key] = fact;
                    }

                    _factList.Add(fact);
                }
            }
        }

        public IReadOnlyList<CoverageRule> RuleList
        {
            get { return _ruleList; }
        }

        public IReadOnlyList<CoverageFact> FactList
        {
            get { return _factList; }
        }

        public CoverageRule FindRule(string key)
        {
            CoverageRule rule;
            return _rules.TryGetValue(key, out rule) ? rule : null;
        }

        public CoverageFact FindFact(string key)
        {
            CoverageFact fact;
            return _facts.TryGetValue(key, out fact) ? fact : null;
        }
    }

    /// <summary>
    /// The arbiter: the table plus the two questions the room asks it. Whether this end is in a room at
    /// all, and whether the caller is the host.
    ///
    /// Three properties hold by construction and every one of them is tested. Nothing is consulted before
    /// the room question, so an out-of-room call cannot be affected by any row. An unknown method is
    /// refused rather than guessed at, so the table is an allow-list. And a method whose measured body is
    /// per-frame, generated or unsendable is refused even when a row asks for it, so no row can put a
    /// frame callback on the wire - nor one that rolls, because a replay of a roll is not the same world.
    /// </summary>
    public static class Arbiter
    {
        /// <summary>Effects that cannot be a call on the wire: not a door, or not a decidable one.</summary>
        private static readonly HashSet<string> Unsendable = new HashSet<string>(StringComparer.Ordinal)
        {
            "frame", "gen", "blocked"
        };

        /// <summary>Effects that change nothing, so announcing one is traffic without a fact behind it.</summary>
        private static readonly HashSet<string> Immutable = new HashSet<string>(StringComparer.Ordinal)
        {
            "read", "pure"
        };

        public static string Key(string classToken, string method, int arity)
        {
            return (classToken ?? string.Empty) + "." + (method ?? string.Empty) + "/" + arity;
        }

        /// <summary>
        /// The default table: the hand-written rows and the generated facts that prove them. Built once;
        /// both sources are fixed arrays.
        /// </summary>
        public static CoverageTable Default
        {
            get { return _default ?? (_default = Build(Coverage.Rules, CoverageFacts.Rows)); }
        }

        private static CoverageTable _default;

        private static CoverageTable Build(CoverageRule[] rules, CoverageFact[] facts)
        {
            return new CoverageTable(rules, facts);
        }

        public static Decision Decide(string classToken, string method, int arity,
                                      bool inRoom, bool callerIsHost)
        {
            return Decide(Default, classToken, method, arity, inRoom, callerIsHost);
        }

        /// <summary>
        /// The key grammar the whole design uses: class.method/arity compared Ordinal, with the arity in
        /// decimal and nothing else around it. Both the receiver's question and the engine's lookup go
        /// through here, so a key that does not parse cannot be applied on either side.
        /// </summary>
        public static bool TrySplitKey(string key, out string classToken, out string method, out int arity)
        {
            classToken = null;
            method = null;
            arity = 0;

            if (key == null)
            {
                return false;
            }

            int slash = key.LastIndexOf('/');
            int dot = slash < 0 ? -1 : key.LastIndexOf('.');

            if (dot <= 0 || slash <= dot + 1)
            {
                return false;
            }

            if (!int.TryParse(key.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                              out arity))
            {
                return false;
            }

            classToken = key.Substring(0, dot);
            method = key.Substring(dot + 1, slash - dot - 1);

            return method.Length > 0;
        }

        /// <summary>
        /// The same question asked with the key the wire and the table already use, so a receiver never
        /// has to take a class name apart to ask it.
        /// </summary>
        public static Decision Decide(CoverageTable table, string key, bool inRoom, bool callerIsHost)
        {
            string classToken;
            string method;
            int arity;

            if (!TrySplitKey(key, out classToken, out method, out arity))
            {
                // A key that is not class.method/arity cannot be in the table, and the room refuses what
                // is not in the table instead of guessing how the sender meant it.
                return new Decision(Verdict.Never, "the frame names '" + key
                    + "', which is not a class.method/arity key", null, null)
                {
                    Key = key ?? string.Empty
                };
            }

            return Decide(table, classToken, method, arity, inRoom, callerIsHost);
        }

        /// <summary>
        /// What the room says about one native method called on this end. inRoom false is the single
        /// player answer and is returned before the table is read: outside a room the mod has no opinion
        /// about anything, so no row - wrong, stale or missing - can be reached.
        /// </summary>
        public static Decision Decide(CoverageTable table, string classToken, string method, int arity,
                                      bool inRoom, bool callerIsHost)
        {
            string key = Key(classToken, method, arity);

            if (!inRoom)
            {
                return Answer(table, key, Verdict.Local, "no room: the game decides alone");
            }

            if (table == null)
            {
                return Answer(null, key, Verdict.Never, "no coverage table");
            }

            CoverageRule rule = table.FindRule(key);
            if (rule == null)
            {
                return Answer(table, key, Verdict.Never, "not in the coverage table");
            }

            CoverageFact fact = table.FindFact(key);
            if (fact == null)
            {
                return Answer(table, key, Verdict.Never,
                    "row has no measured fact: rerun py -3 tools/re/pvz-index.py --arbiter");
            }

            if (Unsendable.Contains(fact.Effect))
            {
                return Answer(table, key, Verdict.Never,
                    "body is " + fact.Effect + ", a " + fact.Effect + " method is never a call");
            }

            if (Travels(rule.Verdict) && Immutable.Contains(fact.Effect))
            {
                return Answer(table, key, Verdict.Never,
                    "row says " + rule.Verdict + " but the body only " + fact.Effect +
                    "s, so announcing it carries no change");
            }

            if (Travels(rule.Verdict) && fact.Rolls)
            {
                return Answer(table, key, Verdict.Never,
                    "row says " + rule.Verdict + " but the body rolls, so replaying it gives every end "
                    + "a different answer");
            }

            if (Travels(rule.Verdict) && rule.Authority == Authority.HostOnly
                && !callerIsHost)
            {
                return Answer(table, key, Verdict.Never,
                    "only the host may announce " + key + "; this end is a peer");
            }

            return Answer(table, key, rule.Verdict, Describe(rule, fact));
        }

        /// <summary>
        /// The two answers that put a frame on the wire, so the checks a body has to pass before it may
        /// travel are written once instead of per verdict: a rolling body is two different lawns whether
        /// the frame was ordered or latest-wins, and a peer is no more the host on the cosmetic lane.
        /// </summary>
        public static bool Travels(Verdict verdict)
        {
            return verdict == Verdict.Replicate || verdict == Verdict.Mirror;
        }

        /// <summary>
        /// Which lane a verdict's frames belong on. Mirror is a value whose newest one is the whole
        /// message, so it travels where a late frame is thrown away; everything else the room says about
        /// the world travels with the world, in order.
        /// </summary>
        public static MsgCategory LaneFor(Verdict verdict)
        {
            return verdict == Verdict.Mirror ? MsgCategory.Cosmetic : MsgCategory.Gameplay;
        }

        /// <summary>
        /// Everything wrong with a table, one line per problem. Empty means the rows and the build agree.
        /// </summary>
        public static List<string> Problems(CoverageTable table)
        {
            List<string> problems = new List<string>();
            if (table == null)
            {
                problems.Add("no coverage table");
                return problems;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CoverageRule rule in table.RuleList)
            {
                string key = rule.Key;
                if (!seen.Add(key))
                {
                    problems.Add("duplicate coverage row " + key);
                }

                if (rule.Arity < 0)
                {
                    problems.Add(key + " has a negative arity");
                }

                if (!IsAscii(key + rule.Verdict + rule.Authority))
                {
                    problems.Add(key + " is not ASCII");
                }

                CoverageFact fact = table.FindFact(key);
                if (fact == null)
                {
                    problems.Add(key + " has no measured fact: rerun py -3 tools/re/pvz-index.py --arbiter");
                    continue;
                }

                if (fact.Rva <= 0)
                {
                    problems.Add(key + " has no body in this build");
                }

                if (Unsendable.Contains(fact.Effect) && rule.Verdict != Verdict.Never)
                {
                    problems.Add(key + " is " + fact.Effect +
                        ", so the row cannot be " + rule.Verdict + " - only Never");
                }

                if (Travels(rule.Verdict) && Immutable.Contains(fact.Effect))
                {
                    problems.Add(key + " is a " + rule.Verdict + " row on a body that only "
                        + fact.Effect + "s");
                }

                if (Travels(rule.Verdict) && fact.Rolls)
                {
                    problems.Add(key + " rolls: replaying it gives every end a different answer, "
                        + "so the row has to be Local or Never and the outcome has to travel");
                }
            }

            HashSet<string> factKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CoverageFact fact in table.FactList)
            {
                if (!factKeys.Add(fact.Key))
                {
                    problems.Add("duplicate measured fact " + fact.Key);
                }

                if (table.FindRule(fact.Key) == null)
                {
                    problems.Add(fact.Key + " is measured but no row names it - delete the row or add it");
                }
            }

            return problems;
        }

        /// <summary>
        /// The audit the room prints when it opens. A problem is an error line, because a coverage row
        /// that no longer matches the build is exactly the kind of silence that made the old gates
        /// unreliable: the mod kept sending a call the native method no longer answered.
        /// </summary>
        public static string Audit(CoverageTable table)
        {
            List<string> problems = Problems(table);
            foreach (string problem in problems)
            {
                Log.Error("cov", problem);
            }

            string summary = "coverage: " + table.RuleList.Count + " rows, " + table.FactList.Count
                + " measured, " + problems.Count + " problems";
            Log.Info("cov", summary);
            return summary;
        }

        private static Decision Answer(CoverageTable table, string key, Verdict verdict, string reason)
        {
            return new Decision(verdict, reason,
                table == null ? null : table.FindRule(key),
                table == null ? null : table.FindFact(key))
            {
                Key = key
            };
        }

        private static string Describe(CoverageRule rule, CoverageFact fact)
        {
            if (rule.Verdict != Verdict.Replicate)
            {
                return rule.Verdict + ": body is " + fact.Effect + ", " + fact.Calls
                    + " calls at 0x" + fact.Rva.ToString("X");
            }

            return "the same machine code runs on every end, body is " + fact.Effect
                + ", " + fact.Calls + " calls at 0x" + fact.Rva.ToString("X");
        }

        private static bool IsAscii(string text)
        {
            if (text == null)
            {
                return true;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < ' ' || text[i] > 126)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
