using System;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace PvzMP
{
    public enum MsgCategory : byte
    {
        Control = 1,
        Gameplay = 2,
        Cosmetic = 3
    }

    /// <summary>
    /// One byte on the wire, so the numbers are unique across categories even though an action is only
    /// ever read inside its own category: two members sharing a value would make the name of a decoded
    /// action ambiguous, which is exactly what the logs and the session switch depend on.
    /// </summary>
    public enum MsgAction : byte
    {
        Hello = 1,
        Welcome = 2,
        Roster = 3,
        Reject = 4,
        Heartbeat = 5,
        Bye = 6,

        /// <summary>
        /// A seat saying it is ready for the level to begin. It carries nothing: the answer is which seat
        /// said it, and the host writes it into its own copy of the roster and broadcasts that, so every
        /// screen sees the same list. Proof: ref/EVIDENCE.md E17b.
        /// </summary>
        SeatReady = 7,

        PlacePlantRequest = 11,
        SpawnPlant = 12,
        RemovePlantRequest = 13,
        DespawnPlant = 14,
        SpawnZombie = 15,
        DespawnZombie = 16,
        CollectSunRequest = 17,
        DespawnSun = 18,
        SunState = 19,
        WaveState = 20,
        LevelStart = 21,
        GameOver = 22,
        RequestDenied = 23,
        LevelReady = 24,
        BattleStart = 25,
        SpawnSun = 26,
        PauseRequest = 27,
        PauseState = 28,
        ZombieState = 29,
        MowerStart = 30,

        CursorIntent = 31,

        Ping = 32,
        Pong = 33,
        PlantPickRequest = 34,
        PlantPick = 35,
        PlantFree = 36,
        LevelLeave = 37,
        Effect = 38,
        SlowRequest = 39,
        SlowState = 40,
        RewardClaim = 41,
        PlantDenied = 42,
        PlantMove = 43,
        PlantSnapshot = 44,
        ZombieMove = 45,
        TrophySpawn = 46,
        TrophyCollect = 47,

        /// <summary>
        /// The universal call: one native method from the coverage table, named by its key, with its
        /// arguments written by the adapter registry. This is the only frame a new synced feature needs,
        /// so a feature is a row instead of a message type. It travels twice, and the row says which way:
        /// Replicate on the ordered gameplay lane, where a missing frame means a lawn that changed, and
        /// Mirror on the cosmetic lane, where a value that arrived late is worth less than the next one.
        /// </summary>
        MethodCall = 48,

        /// <summary>
        /// The host's graves: which cells hold one and which kind of grave the host's roll made each of
        /// them. Not a call, because none of the doors that lay a grave can be replayed - all three roll
        /// their own answer, and a replay of a roll gives every end its own (E42). So the decided numbers
        /// travel and the peer repeats the game's own creator, exactly as the plant layout already does.
        /// </summary>
        GraveLayout = 49,

        /// <summary>
        /// One lawn's fertilizer went onto one cell. The cell travels and not the result, because the body
        /// that fertilizes writes no health and calls no recover door of its own - what it does to the plant
        /// is the game's, on the lawn that held the fertilizer (E48). Every other lawn answers by putting its
        /// own plant of that cell back whole through the game's own <c>Plant.Recover</c>, with its own numbers.
        /// Host-decided, the way the glove's moves are. Where that body also changed a plant into another
        /// kind, the kind it changed to is said by <see cref="FertilizeGrew"/>.
        /// </summary>
        Fertilized = 50,

        /// <summary>
        /// The kind a cell's plant became on the lawn that held the fertilizer, and the cell it happened on.
        /// Its own frame because the body that changes a plant is reached by an animation event a third of a
        /// second after the drop that asked for it, so the cell and its result are two moments on the lawn
        /// that made them (E53). Every other lawn answers by replacing the plant of that cell that holds the
        /// kind which left, through the game's own creator with the kind that arrived.
        /// </summary>
        FertilizeGrew = 51,

        /// <summary>
        /// One room plant went into a cart on one lawn. The plant keeps its object and its numbers on every
        /// other lawn and is only hidden there, because the cart's own body takes it out of the lawn that
        /// clicked and the game destroys or reparents that object (E55). The drop is the usual
        /// <see cref="PlantMove"/>: it names the same room id and the cell the cart put it on, and every
        /// lawn shows its own object again and moves it there.
        /// </summary>
        PlantLift = 52
    }

    public enum RejectCode
    {
        None = 0,
        VersionMismatch = 1,
        RoomFull = 2,
        AlreadyStarted = 3,
        BadHandshake = 4,
        TooManySuffixes = 5
    }

    public sealed class RosterEntry
    {
        public int PeerId { get; set; }
        public string Nick { get; set; }
        public bool IsHost { get; set; }
        public bool Connected { get; set; }

        /// <summary>
        /// Whether this seat has pressed the chooser's LET'S ROCK and is waiting for the room. Only the
        /// host's copy is written; a client's list is what the host broadcast, so a seat cannot mark
        /// itself ready on a screen the room is not looking at.
        /// </summary>
        public bool Ready { get; set; }

        /// <summary>Round trip to the host in milliseconds, -1 until the first ping answers.</summary>
        public int PingMillis { get; set; }
    }

    public sealed class HelloPayload
    {
        public string Version { get; set; }
        public string Nick { get; set; }
    }

    public sealed class WelcomePayload
    {
        public int PeerId { get; set; }
        public string Version { get; set; }
    }

    public sealed class RosterPayload
    {
        public RosterEntry[] Entries { get; set; }
    }

    public sealed class RejectPayload
    {
        public RejectCode Code { get; set; }
        public string Reason { get; set; }
    }

    public sealed class PlacePlantRequest
    {
        public int PlantType { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }

        /// <summary>
        /// The price written on the clicking end's own card. The host's bank does not necessarily hold
        /// that card - each seat picks its own - so without this number the host has nothing to charge.
        /// </summary>
        public int Cost { get; set; }
    }

    public sealed class SpawnPlantPayload
    {
        public int NetId { get; set; }
        public int PlantType { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public int OwnerPeer { get; set; }
    }

    public sealed class RemovePlantRequest
    {
        public int NetId { get; set; }
    }

    public sealed class DespawnPlantPayload
    {
        public int NetId { get; set; }
        public int Reason { get; set; }
    }

    public sealed class SpawnZombiePayload
    {
        public int NetId { get; set; }
        public int ZombieType { get; set; }
        public int Row { get; set; }
        public float SpawnX { get; set; }
        public int Wave { get; set; }

        /// <summary>
        /// The base walking speed the host's own creator rolled for this zombie. `CreateZombie.SetZombie`
        /// rolls it out of `UnityEngine.Random`, so every lawn that spawns its own copy rolls a different
        /// walk; carrying the host's roll in the same frame that announces the zombie is what makes the
        /// copies start walking alike instead of only after the next state push (E59).
        /// </summary>
        public float OriginSpeed { get; set; }
    }

    public sealed class DespawnZombiePayload
    {
        public int NetId { get; set; }
        public int Reason { get; set; }
    }

    public sealed class CollectSunRequest
    {
        public int NetId { get; set; }
    }

    /// <summary>
    /// A coin the host minted. The three numbers are the arguments the game's own creator takes, so a peer
    /// that applies this asks the same method and gets the same coin: the price is read from the item table
    /// by the game, not carried over the wire.
    /// </summary>
    public sealed class SpawnSunPayload
    {
        public int NetId { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public int CoinType { get; set; }
        public int MoveType { get; set; }
    }

    /// <summary>
    /// The host's no to a gameplay request. The request itself stays unacknowledged on purpose: a
    /// granted placement answers itself by the plant appearing, so only a refusal has to travel.
    /// </summary>
    public sealed class DenyPayload
    {
        public MsgAction Requested { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DespawnSunPayload
    {
        public int NetId { get; set; }
        public int CollectedBy { get; set; }
    }

    /// <summary>Absolute value only, never a delta: small simulation drift then heals itself.</summary>
    public sealed class SunStatePayload
    {
        public int Sun { get; set; }
        public int MaxSun { get; set; }
    }

    /// <summary>
    /// The host's wave clock. The time is the same absolute field the game's own update decrements, so a
    /// peer that writes it keeps the countdown the host shows instead of running its own.
    /// </summary>
    public sealed class WaveStatePayload
    {
        public int Wave { get; set; }
        public int MaxWave { get; set; }
        public float TimeUntilNextWave { get; set; }

        /// <summary>The host's pause-menu difficulty and game speed, the same two fields its sliders write.</summary>
        public bool HasSettings { get; set; }
        public int Difficulty { get; set; }
        public float GameSpeed { get; set; }
    }

    /// <summary>
    /// One zombie the host still has on its lawn and the numbers its own body carries. Only zombies the
    /// room minted are listed: a peer repeats them through the game's own creator, and the health, the
    /// speed factors and (every position push) the place on the lawn are what a creator cannot be asked
    /// to redo.
    /// </summary>
    public sealed class ZombieHealth
    {
        public int NetId { get; set; }
        public long Health { get; set; }

        /// <summary>False for a body the host already counts as dead; the peer kills its own copy.</summary>
        public bool Alive { get; set; }

        public int FirstArmor { get; set; }
        public int SecondArmor { get; set; }

        /// <summary>
        /// A hypnotized body fights for its new side and the whole lawn must agree on which side that is;
        /// the host's flag is the room's answer (E73).
        /// </summary>
        public bool MindControlled { get; set; }

        /// <summary>The base speed and the eight multipliers `Zombie.UpdateSpeed` applies to it.</summary>
        public float OriginSpeed { get; set; }
        public float FreezeSpeed { get; set; }
        public float ColdSpeed { get; set; }
        public float ButterSpeed { get; set; }
        public float KelpSpeed { get; set; }
        public float GarlicSpeed { get; set; }
        public float UniqueSpeed { get; set; }
        public float HoleSpeed { get; set; }
        public float MaLiangSpeed { get; set; }

        /// <summary>
        /// True on the slower position push; X/Y are the host's `Entity.axis` position, the point
        /// `Zombie.AdjustPosition` aligns. The peer corrects x only and keeps its own y, which the game
        /// eases onto the row's ground.
        /// </summary>
        public bool HasPosition { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Row { get; set; }
    }

    public sealed class ZombieStatePayload
    {
        public ZombieHealth[] Zombies { get; set; }
    }

    /// <summary>Which row's mower the host's lawn started; the peer replays the same object's StartMove.</summary>
    public sealed class MowerStartPayload
    {
        public int Row { get; set; }
    }

    /// <summary>The four arguments of the game's own UIMgr.EnterGame, as the host saw them.</summary>
    public sealed class LevelStartPayload
    {
        public int LevelType { get; set; }
        public int LevelNumber { get; set; }
        public int LevelId { get; set; }
        public string LevelName { get; set; }
    }

    /// <summary>The peer saying the host's level is up on its lawn, so the host knows who to wait for.</summary>
    public sealed class LevelReadyPayload
    {
        public int LevelType { get; set; }
        public int LevelNumber { get; set; }
    }

    /// <summary>
    /// A seat's own word about the chooser's LET'S ROCK. The word is a state and not an event: the first
    /// press says ready, and a seat that changes its mind says the other word with the same frame. The host
    /// owns the roster, so this only asks it to remember; every screen is answered by the roster broadcast
    /// that follows.
    /// </summary>
    public sealed class SeatReadyPayload
    {
        public bool Ready { get; set; }
    }

    public sealed class GameOverPayload
    {
        public bool Won { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The host's word on the room's pause: true when every lawn stops, false when it moves again.</summary>
    public sealed class PauseStatePayload
    {
        public bool Paused { get; set; }
    }

    public sealed class CursorIntentPayload
    {
        public int PeerId { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public int PlantType { get; set; }
        public bool Shovel { get; set; }

        /// <summary>True while the glove carries a plant or a zombie; the ghost is that object.</summary>
        public bool Glove { get; set; }

        /// <summary>The zombie type on the glove, or -1 when the glove carries a plant or nothing.</summary>
        public int GloveZombieType { get; set; } = -1;

        /// <summary>True while this seat holds the hammer up.</summary>
        public bool Hammer { get; set; }

        /// <summary>True while this seat holds a cart up.</summary>
        public bool Wheel { get; set; }

        /// <summary>True while this seat holds the fertilizer up - a bucket, not a tool of the strip.</summary>
        public bool Fertilizer { get; set; }
    }

    public sealed class PlantPickRequestPayload
    {
        public int PlantType { get; set; }
    }

    public sealed class PlantPickPayload
    {
        public int PlantType { get; set; }
        public int OwnerPeer { get; set; }
    }

    public sealed class PlantFreePayload
    {
        public int PlantType { get; set; }
        public int OwnerPeer { get; set; }
    }

    public sealed class PlantDeniedPayload
    {
        public int PlantType { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// One presentation a lawn made and the room has to make too. Kind 0 is `ParticleManager.SetParticle`
    /// (Vector2), kind 1 is `CreateParticle.SetParticle` (Vector3), kind 2 is `GameAPP.PlaySound`. Every
    /// other seat calls the same native method with the same numbers; the seat whose lawn made it does not,
    /// because the game already played it there.
    /// </summary>
    public sealed class EffectPayload
    {
        public int Kind { get; set; }

        /// <summary>The seat whose lawn made the effect. The transport's sender wins over this number.</summary>
        public int OriginPeer { get; set; }
        public int ParticleType { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int Row { get; set; }
        public float Scale { get; set; }

        /// <summary>The `lim` flag of `ParticleManager.SetParticle`; ignored by the other kinds.</summary>
        public bool Lim { get; set; }

        /// <summary>The `setLayer` flag of `CreateParticle.SetParticle`; ignored by the other kinds.</summary>
        public bool SetLayer { get; set; }

        /// <summary>The sound id of kind 2, as the game's own `PlaySound` was handed it.</summary>
        public int Sound { get; set; }

        /// <summary>The volume of kind 2; ignored by the particles.</summary>
        public float Volume { get; set; }

        /// <summary>The pitch of kind 2; ignored by the particles.</summary>
        public float Pitch { get; set; }
    }

    public sealed class SlowStatePayload
    {
        public bool Slow { get; set; }
    }

    public sealed class RewardClaimPayload
    {
        public int PlantType { get; set; }
    }

    /// <summary>One plant's cell after the glove moved it, named by the room's id.</summary>
    public sealed class PlantMovePayload
    {
        public int NetId { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
    }

    /// <summary>One plant a cart took off its cell, named by the room's id.</summary>
    public sealed class PlantLiftPayload
    {
        public int NetId { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
    }

    /// <summary>One plant on the sending end's lawn, with 0 when that end cannot name its room id.</summary>
    public sealed class PlantSnap
    {
        public int NetId { get; set; }
        public int PlantType { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }

        /// <summary>
        /// What the host's plant has become since it was placed: its health and shield, the growth and
        /// skin the game shows, the counters its behaviour reads, the flags that decide how it reacts.
        /// A peer matches these with its own numbers every second; the numbers never travel the other
        /// way, because the host's lawn is the room's truth (E71, E72).
        /// </summary>
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public int Shield { get; set; }
        public int Level { get; set; }
        public int Stage { get; set; }
        public int Skin { get; set; }
        public int Magnet { get; set; }
        public int Wheat { get; set; }
        public int ShootLevel { get; set; }
        public int ShootCurse { get; set; }
        public int Kills { get; set; }
        public int Puff { get; set; }
        public int Butter { get; set; }
        public int Attribute { get; set; }
        public int Disable { get; set; }
        public int Freeze { get; set; }
        public int FirstParent { get; set; }
        public int SecondParent { get; set; }
        public int LilyType { get; set; }

        /// <summary>The persistent booleans, packed by <see cref="PvzMP.Core.Session.PlantFactsRules"/>.</summary>
        public int Flags { get; set; }
    }

    /// <summary>A client's lawn after its glove ran; the host turns it into the room's usual frames.</summary>
    public sealed class PlantSnapshotPayload
    {
        public PlantSnap[] Plants { get; set; }
    }

    /// <summary>One grave on the sending lawn: where it stands and which kind the game rolled for it.</summary>
    public sealed class GraveSnap
    {
        public int Column { get; set; }
        public int Row { get; set; }
        public int GraveType { get; set; }
    }

    /// <summary>The whole of one lawn's graves, as the end that holds them reads them.</summary>
    public sealed class GraveLayoutPayload
    {
        public GraveSnap[] Graves { get; set; }
    }

    /// <summary>One cell a fertilizer was dropped on, and the seat whose hand dropped it.</summary>
    public sealed class FertilizedPayload
    {
        public int OriginPeer { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
    }

    /// <summary>
    /// The kind the plant of one cell became on the seat that used the fertilizer there. A healing without a
    /// new kind is carried by <see cref="FertilizedPayload"/> alone. Both kinds are said because a cell can
    /// hold more than one plant and only the one the fertilizer left is replaced - a lawn that rebuilt every
    /// plant of the cell would dig up a pumpkin the fertilizer never touched.
    /// </summary>
    public sealed class FertilizeGrewPayload
    {
        public int OriginPeer { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public int OldType { get; set; }
        public int NewType { get; set; }
    }

    /// <summary>A zombie the glove moved, named by the room's id and the glove's own target.</summary>
    public sealed class ZombieMovePayload
    {
        public int NetId { get; set; }
        public float X { get; set; }
        public int Row { get; set; }
    }

    /// <summary>The four arguments of the game's own `Lawnf.SetAward`, as the host saw them.</summary>
    public sealed class TrophySpawnPayload
    {
        public float X { get; set; }
        public float Y { get; set; }
        public bool KillZombie { get; set; }
        public bool Fake { get; set; }
    }

    /// <summary>Where the host's cup stood, so a client lifts the same cup when a lawn has several.</summary>
    public sealed class TrophyCollectPayload
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    /// <summary>
    /// One call of one covered native method. Nothing here names a game type: the key is the coverage
    /// table's own, the target and the arguments are the tokens and shapes the adapter registry writes,
    /// and the sequence is what lets the receiving end notice a frame it already applied or a frame that
    /// never arrived.
    /// </summary>
    public sealed class MethodCallPayload
    {
        /// <summary>The coverage key: Board.SetSun/1. Never a bare name.</summary>
        public string Key { get; set; }

        /// <summary>The instance as a reference token, ~ when the method takes no instance on the wire.</summary>
        public string Target { get; set; }

        /// <summary>The argument vector as JSON, written against the row's measured categories.</summary>
        public string Args { get; set; }

        /// <summary>Monotonic per sending end, so a replay of the same frame is recognizable.</summary>
        public long Seq { get; set; }

        /// <summary>The end whose game asked for the call. The host owns the stream, so it stamps this.</summary>
        public int OriginPeer { get; set; }
    }

    public sealed class DecodedFrame
    {
        public MsgCategory Category { get; private set; }
        public MsgAction Action { get; private set; }
        public string Json { get; private set; }

        internal DecodedFrame(MsgCategory category, MsgAction action, string json)
        {
            Category = category;
            Action = action;
            Json = json;
        }

        public string Name
        {
            get { return Category + "/" + Action; }
        }
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Every message type and the whole wire format in one place:
    /// [4-byte big-endian body length][1 category][1 action][UTF-8 JSON].
    /// A frame is rejected before allocation when the length is impossible, so a broken or hostile
    /// stream cannot make the mod reserve memory.
    /// </summary>
    public static class Protocol
    {
        /// <summary>
        /// The wire version. Raised whenever a message number or a payload shape changes, so a stale end
        /// is refused at the handshake with a reason instead of dropping the connection on the first
        /// new frame.
        /// </summary>
        public const string Version = "0.1";
        public const int BodyOverhead = 2;
        public const int MaxFrameBytes = 64 * 1024;
        public const int MaxJsonChars = MaxFrameBytes - BodyOverhead;
        public const int MaxTextChars = 64;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = false
        };

        public static byte[] Encode(MsgCategory category, MsgAction action, object payload)
        {
            string json = payload == null ? "{}" : JsonSerializer.Serialize(payload, JsonOptions);
            if (json.Length > MaxJsonChars)
            {
                throw new ProtocolException("refusing to send " + category + "/" + action
                    + " with " + json.Length + " chars, the cap is " + MaxJsonChars);
            }

            byte[] jsonBytes = Utf8.GetBytes(json);
            int body = BodyOverhead + jsonBytes.Length;
            if (body > MaxFrameBytes)
            {
                throw new ProtocolException("refusing to send " + category + "/" + action
                    + ": " + body + " bytes exceeds the " + MaxFrameBytes + " byte frame cap");
            }

            byte[] frame = new byte[4 + body];
            frame[0] = (byte)(body >> 24);
            frame[1] = (byte)(body >> 16);
            frame[2] = (byte)(body >> 8);
            frame[3] = (byte)body;
            frame[4] = (byte)category;
            frame[5] = (byte)action;
            Buffer.BlockCopy(jsonBytes, 0, frame, 6, jsonBytes.Length);
            return frame;
        }

        /// <summary>Decodes the body only - the 4 length bytes are consumed by the framing layer.</summary>
        public static DecodedFrame Decode(byte[] body, int offset, int length)
        {
            if (length < BodyOverhead)
            {
                throw new ProtocolException("frame body of " + length + " bytes cannot hold a category and action");
            }

            byte rawCategory = body[offset];
            byte rawAction = body[offset + 1];

            // Casts to an enum never throw, so both bytes are validated against the definitions instead.
            MsgCategory category = (MsgCategory)rawCategory;
            if (!Enum.IsDefined(typeof(MsgCategory), category))
            {
                throw new ProtocolException("unknown category " + rawCategory);
            }

            MsgAction action = (MsgAction)rawAction;
            if (!Belongs(category, action))
            {
                throw new ProtocolException("action " + rawAction + " is not a " + category + " action ("
                    + Range(category) + ")");
            }

            string json = length == BodyOverhead
                ? "{}"
                : Utf8.GetString(body, offset + BodyOverhead, length - BodyOverhead);

            if (json.Length > MaxJsonChars)
            {
                throw new ProtocolException("decoded json of " + json.Length + " chars exceeds the cap");
            }

            return new DecodedFrame(category, action, json);
        }

        public static T Payload<T>(DecodedFrame frame)
        {
            try
            {
                T value = JsonSerializer.Deserialize<T>(frame.Json, JsonOptions);
                if (value == null)
                {
                    throw new ProtocolException(frame.Name + " carried no " + typeof(T).Name + " payload");
                }

                CheckText(frame, value);
                return value;
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ProtocolException(frame.Name + " payload is not a readable "
                    + typeof(T).Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Anything the mod later prints or builds UI from arrives as a string, so the length is
        /// bounded here rather than trusting a peer. Nicks live inside roster entries, so the walk has
        /// to descend into nested payloads, not stop at the top object.
        /// </summary>
        private static void CheckText(DecodedFrame frame, object payload)
        {
            CheckText(frame, payload, 0);
        }

        private const int MaxTextDepth = 3;

        private static void CheckText(DecodedFrame frame, object value, int depth)
        {
            if (value == null || depth > MaxTextDepth)
            {
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                return;
            }

            if (value is string text)
            {
                Bound(frame, "text", text);
                return;
            }

            IEnumerable items = value as IEnumerable;
            if (items != null)
            {
                foreach (object item in items)
                {
                    CheckText(frame, item, depth + 1);
                }

                return;
            }

            foreach (System.Reflection.PropertyInfo property in type.GetProperties())
            {
                object child = property.GetValue(value, null);
                if (child is string childText)
                {
                    Bound(frame, "field " + property.Name, childText);
                    continue;
                }

                CheckText(frame, child, depth + 1);
            }
        }

        private static void Bound(DecodedFrame frame, string name, string text)
        {
            if (text.Length > MaxTextChars)
            {
                throw new ProtocolException(frame.Name + " " + name + " is " + text.Length
                    + " chars, the cap is " + MaxTextChars);
            }
        }

        /// <summary>Which category owns an action: a byte borrowed from another category is a bug or an attack.</summary>
        private static bool Belongs(MsgCategory category, MsgAction action)
        {
            switch (category)
            {
                case MsgCategory.Control:
                    return (action >= MsgAction.Hello && action <= MsgAction.SeatReady)
                        || (action >= MsgAction.Ping && action <= MsgAction.Pong);
                case MsgCategory.Gameplay:
                    return (action >= MsgAction.PlacePlantRequest && action <= MsgAction.MowerStart)
                        || (action >= MsgAction.PlantPickRequest && action <= MsgAction.PlantLift);
                case MsgCategory.Cosmetic:
                    return action == MsgAction.CursorIntent || action == MsgAction.MethodCall;
                default:
                    return false;
            }
        }

        private static string Range(MsgCategory category)
        {
            switch (category)
            {
                case MsgCategory.Control:
                    return (byte)MsgAction.Hello + ".." + (byte)MsgAction.SeatReady + ", "
                        + (byte)MsgAction.Ping + ".." + (byte)MsgAction.Pong;
                case MsgCategory.Gameplay:
                    return (byte)MsgAction.PlacePlantRequest + ".." + (byte)MsgAction.MowerStart + ", "
                        + (byte)MsgAction.PlantPickRequest + ".." + (byte)MsgAction.PlantLift;
                case MsgCategory.Cosmetic:
                    return (byte)MsgAction.CursorIntent + ".." + (byte)MsgAction.CursorIntent + ", "
                        + (byte)MsgAction.MethodCall + ".." + (byte)MsgAction.MethodCall;
                default:
                    return (byte)MsgAction.CursorIntent + ".." + (byte)MsgAction.CursorIntent;
            }
        }
    }
}
