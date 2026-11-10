namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the room does when this native method runs. The four answers are exhaustive on purpose: a
    /// method the table does not name is not a fifth verdict, it is refused, so the coverage table is an
    /// allow-list and never a deny-list.
    /// </summary>
    public enum Verdict
    {
        /// <summary>
        /// The call travels as a method call and the native method runs on every end. The shared world
        /// stays identical because every end executes the same machine code with the same arguments,
        /// not because one end sends field values to the others. That only holds while the arguments
        /// decide the answer: a body that reaches UnityEngine.Random is refused, because replaying a roll
        /// gives every end its own.
        /// </summary>
        Replicate,

        /// <summary>
        /// The value travels, not the call: the newest one wins and nothing is ordered. On the wire that
        /// is the cosmetic lane, and in the inbox it is no sequence and no retry queue, because a value
        /// that arrives late describes a world that has moved on. A door earns this answer by being a
        /// plain write of an absolute field (E27); the room keeps the last value of every door it answers
        /// this way and hands it to a seat that joins in the middle of a level. Cursors, nicknames and the
        /// phases of a level load are the room's presentation, and a late one is worthless.
        /// </summary>
        Mirror,

        /// <summary>
        /// Only the end where it happened runs it. A sound, a particle, a refund animation. The other ends
        /// see the state the door already changed, so announcing this would repeat a side effect.
        /// </summary>
        Local,

        /// <summary>
        /// Refused. A frame naming this method is dropped on receipt with a log line. Used for the doors
        /// that must not travel (a per-frame roll of Random) and for the ones the room cannot answer for
        /// yet, so that the gap is a row in the table instead of a silent difference between two lawns.
        /// </summary>
        Never
    }

    /// <summary>
    /// Whose local execution of a travelling door may be announced. Read only for the verdicts that put a
    /// frame on the wire, Replicate and Mirror: a Local or Never row never sends anything, so its
    /// authority answers nothing.
    /// </summary>
    public enum Authority
    {
        /// <summary>A click on any seat is the room's business.</summary>
        AnyPeer,

        /// <summary>
        /// Only the host's own game code may announce it. This also keeps a peer quiet while it applies a
        /// door the host sent: the peer is not the host, so a nested call inside that replay is refused
        /// before it can travel back.
        /// </summary>
        HostOnly
    }

    /// <summary>
    /// One row of the coverage table: one native method, one answer. The triple (class, method, arity) is
    /// the key and it is a claim about the native build, so tools/re/pvz-index.py --arbiter proves it
    /// against GameAssembly.dll and writes the measured facts next to it. An overload is a different row,
    /// because a different parameter list is a different adapter on the wire.
    /// </summary>
    public sealed class CoverageRule
    {
        public CoverageRule(string classToken, string method, int arity, Authority authority, Verdict verdict)
        {
            ClassToken = classToken;
            Method = method;
            Arity = arity;
            Authority = authority;
            Verdict = verdict;
        }

        public string ClassToken { get; private set; }

        public string Method { get; private set; }

        /// <summary>Declared parameters, the instance pointer not counted.</summary>
        public int Arity { get; private set; }

        public Authority Authority { get; private set; }

        public Verdict Verdict { get; private set; }

        /// <summary>The key the wire and the fact table both use: Board.SetSun/1.</summary>
        public string Key
        {
            get { return Arbiter.Key(ClassToken, Method, Arity); }
        }

        public override string ToString()
        {
            return Key + " " + Verdict + " " + Authority;
        }
    }

    /// <summary>
    /// The allow-list: which native methods the room covers, one row per synced feature. Adding a feature
    /// means adding one row here and rerunning the generator; it never means a new payload class, a new
    /// gate or a hand-written field list.
    ///
    /// Every name and arity below is copied from tools/re/pvz-index.py, never remembered, and the why of a
    /// row is a measured fact with its evidence id. Outside a room none of this is consulted at all: the
    /// arbiter answers Local before it looks anything up, so single player cannot be reached by a wrong
    /// row.
    /// </summary>
    public static class Coverage
    {
        private static CoverageRule Rule(string classToken, string method, int arity,
                                         Authority authority, Verdict verdict)
        {
            return new CoverageRule(classToken, method, arity, authority, verdict);
        }

        public static readonly CoverageRule[] Rules =
        {
            // The sun bank. GetSun credits it and never calls SetSun (E28), so crediting and assigning
            // are two different doors and only the host's own code may open either one.
            Rule("Board", "GetSun", 2, Authority.HostOnly, Verdict.Replicate),

            // The assignment is the first row the room calls Mirror, and the measurement is why: 7 bytes,
            // no calls, one write of its own field (E27). Nothing about that door is ordered - the number
            // is absolute, so the newest one is the answer and a bank read out of a frame the room sent
            // thirty seconds ago is worse than the next frame. Crediting stays Replicate because an
            // amount only means something added in order.
            Rule("Board", "SetSun", 1, Authority.HostOnly, Verdict.Mirror),

            // Spending is not a door of its own: every end spends as part of the planting door it is
            // told to run, so the same machine code leaves the same bank on all four lawns.
            Rule("Board", "UseSun", 1, Authority.HostOnly, Verdict.Local),

            // SunUpdate rolls the sky itself (E30: Random.Range then eight CreateItem.SetCoin). Two ends
            // would roll two different skies, so it runs on the host and the coins are what travels.
            Rule("Board", "SunUpdate", 0, Authority.HostOnly, Verdict.Never),

            // One coin on the lawn, minted by the host, or taken away by whoever clicked it (E29).
            Rule("CreateItem", "SetCoin", 6, Authority.HostOnly, Verdict.Replicate),
            Rule("CoinSun", "Die", 0, Authority.HostOnly, Verdict.Replicate),

            // The flight to the bank is presentation. The bank is the fact, and it arrives by GetSun.
            Rule("CoinSun", "MoveToPosition", 0, Authority.HostOnly, Verdict.Local),

            // A Unity callback that mints coins. It cannot be covered and naming it proves the gate
            // refuses a frame method even when a row asks.
            Rule("CoinSun", "Update", 0, Authority.HostOnly, Verdict.Never),

            // Planting. The parametric door is the intent; the click handler that reaches it is not,
            // because it takes nothing and everything it needs sits in Mouse fields (E32).
            Rule("Mouse", "SetPlantFromCard", 3, Authority.AnyPeer, Verdict.Replicate),
            Rule("Mouse", "TryToSetPlantByCard", 0, Authority.AnyPeer, Verdict.Never),
            Rule("Mouse", "TryToSetPlantByGlove", 0, Authority.AnyPeer, Verdict.Never),

            // Digging. Any seat may dig any plant the room grew, and the door pays the sun back itself
            // through two CreateItem.SetCoin calls (E33), so the refund must not travel again.
            Rule("Shovel", "Use", 1, Authority.AnyPeer, Verdict.Replicate),
            Rule("Shovel", "PayBackSun", 1, Authority.HostOnly, Verdict.Local),
            Rule("Shovel", "ReturnPlantToGarden", 1, Authority.HostOnly, Verdict.Local),

            // The hammer. Its click body cannot travel: it reads this machine's pointer and asks the
            // engine which zombies are around it. What it decided does travel - one `Zombie.TakeDamage`
            // per zombie the crush touched, carrying the damage, its kind and the report the game's own
            // blow computed, with the lawn's own hammer as the maker. `Hammer.EffectZombie` looks like a
            // blow and is not one: its body draws bounds, an explosion and a freeze level, and its call
            // list contains no damage step at all, which is why a room that repeated it saw a peer's
            // zombie survive on the host (E60). Any seat may start a blow, and the host takes a peer's
            // blows onto its own lawn before passing them on, so the health every seat is told is the
            // host's. Proof: ref/EVIDENCE.md E54, E57, E60.
            Rule("Zombie", "TakeDamage", 5, Authority.AnyPeer, Verdict.Replicate),

            // Wheat to new plants. The door rolls which plant grows (E35), so replaying it would not give
            // every end the same lawn: the decided value has to travel, not the call. The latest-wins lane
            // exists now, but a Mirror row still runs the method it names, so this row waits for a door
            // whose arguments are the plant the host rolled. Until one is measured the merge stays on the
            // lawn diff.
            Rule("Board", "SetEvePlants", 3, Authority.HostOnly, Verdict.Never),

            // The gift box. The plant it grows is rolled shortly after the placement, inside the game's own
            // animation callback, and that callback takes no arguments: like the by-card door above, there
            // is nothing a frame could carry. So the door stays off the wire and a peer does not run it at
            // all while the room stands in a level - the host's box opens, and its plant is the layout's
            // business (E40).
            Rule("Present", "AnimEvent", 0, Authority.HostOnly, Verdict.Never),
            Rule("SnowPresent", "AnimEvent", 0, Authority.HostOnly, Verdict.Never),

            // The boxes a lawn would place by itself: the artist sunflower's periodic ability and the
            // level's own minute box. Each reaches a roll in the body it calls - the cell it puts the box
            // on and what the box turns out to be - so a peer that ran them showed a box the host never
            // placed and lost it to the next census. Both bodies give nothing back to their caller, which
            // is what makes them safe to skip and what the mint door below is not.
            Rule("Paintingflower", "AttributeEvent", 0, Authority.HostOnly, Verdict.Never),
            Rule("Board", "CreateRandomPresent", 0, Authority.HostOnly, Verdict.Never),

            // The plant creator. Its parameters carry a plant, a vector and a save object no frame can
            // name, its body rolls, and it hands its caller the object it made - 181 of the 399 call sites
            // the build has read through that pointer or pass it on before testing it, so a skipped body is
            // a crash in the game's own code rather than a refusal. A mint the room agrees on therefore
            // travels as the layout that holds it, never as a call, and the rule that stops a peer minting
            // on its own sits on the doors above, which give nothing back (E43).
            Rule("CreatePlant", "SetPlant", 9, Authority.HostOnly, Verdict.Never),

            // The lawn going down. Every end has to see it, and only the host may say so.
            Rule("Board", "ClearTheBoard", 0, Authority.HostOnly, Verdict.Replicate),

            // The graves. All three of the game's grave doors roll: `SetGrave` the level's cells,
            // `SupplyGrave` the cell of the grave a wave delivers, and `SetGridItem` - the door both of
            // them end in, and the only way anything gets onto a lawn cell - the kind of grave when the
            // caller left it at `Default`. The grave's zombie and its preview are rolled a second later,
            // inside the animation the opening starts. So nothing here can be replayed; the host's cells
            // and kinds travel as its layout and a peer repeats the creator with the host's numbers,
            // which is the same answer the merge above waits for (E42).
            Rule("Board", "SetGrave", 0, Authority.HostOnly, Verdict.Never),
            Rule("Board", "SupplyGrave", 0, Authority.HostOnly, Verdict.Never),
            Rule("GridItem", "SetGridItem", 4, Authority.HostOnly, Verdict.Never),
            Rule("Grave", "SetZombie", 0, Authority.HostOnly, Verdict.Never),

            // A plant dying of its own accord is not covered yet: with zombie damage still running on
            // each end, announcing a death would let two ends kill the same plant.
            Rule("Plant", "Die", 1, Authority.HostOnly, Verdict.Never)
        };
    }
}
