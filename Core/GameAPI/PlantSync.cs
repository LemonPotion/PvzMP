using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// What the lawn holds is the host's sentence, and this class repairs the parts no placement frame
    /// covers: plants a level script or a gift box put down, and the plants and zombies the glove moved.
    ///
    /// The host walks its own lawn every second and sends its whole layout - every room id, type and
    /// cell. A client makes its own lawn match that layout, so a tutorial plant, a glove move or a
    /// stale duplicate is corrected by the host instead of surviving as a local difference. That one walk
    /// is also the force sync the tutorial needs, because a tutorial plant is just a plant the room did
    /// not see being planted.
    ///
    /// A gift box is a plant whose type the game rolls after the placement, out of a door no frame of
    /// ours can carry, so this class answers the room's rule for it (`MayRollGift`) and asks for the walk
    /// at once when its own box opened (`CensusNow`) rather than let the layout wait a second.
    ///
    /// The plant glove moves a plant through `Mouse.TryToSetPlantByGlove`. A successful native move sends
    /// one `PlantMove` with the room id and the new cell. The host walks the lawn every second as well,
    /// so a move that the object itself cannot describe is still repaired by the layout.
    /// A zombie moved by the glove keeps its object, so it travels as one `ZombieMove` from the glove's
    /// own `Mouse.MoveZombie`, and the regular zombie state push corrects the rest.
    ///
    /// The cart (`Wheel.Use`) moves a plant the same way in two clicks - it lifts one off a cell and puts
    /// it down on another - so it travels as the same `PlantMove` frame, with the room id of the plant the
    /// lift named. Both tools keep their lawn's own object, which is why neither copies health, level or
    /// stage after it: those are the numbers each lawn already had for that plant.
    /// </summary>
    public static class PlantSync
    {
        public const string Capability = "mp-plant-state";

        private const int CensusMillis = 1000;

        private static MpSession _room;
        private static bool _applying;
        private static int _censusAt;

        /// <summary>The room this lawn answers to, or null when no room decides this lawn at all.</summary>
        internal static MpSession Room
        {
            get { return _room; }
        }

        /// <summary>The lawn before `TryToSetPlantByGlove` ran, or null when that call is not ours to read.</summary>
        private static PlantState[] _gloveBefore;

        /// <summary>
        /// The plant this end's tool is holding and the cell it came from: the glove's hand, named by
        /// `TryToPickPlant`, or the cart's pocket, named by the click that lifted it. One tool is in one
        /// hand at a time, so both write the same three numbers and one move frame settles either of them.
        /// </summary>
        private static int _carriedNetId;
        private static int _carriedInstance;
        private static int _carriedType = Cursor.NoCard;

        /// <summary>What the cart held when the click now finishing started, so the two halves tell apart.</summary>
        private static int _cartCarriedBefore;

        /// <summary>True while the native body of the cart's drop is running, so its creator call can be seen.</summary>
        private static bool _cartDropInFlight;

        /// <summary>Plant types already reported as minted by this end's own dice, so the watch is quiet.</summary>
        private static readonly HashSet<int> _localMints = new HashSet<int>();

        /// <summary>
        /// Room ids of the plants the room says are in a cart. Each lawn keeps its own object and its own
        /// numbers, hidden and out of the layout's reach, until the drop's move frame names the cell the
        /// cart put it on. Proof: ref/EVIDENCE.md E55, E58.
        /// </summary>
        private static readonly HashSet<int> _away = new HashSet<int>();

        public static void Attach(MpSession room)
        {
            _room = room;

            try
            {
                new HarmonyLib.Harmony("PvzMP.plants").PatchAll(typeof(PlantSync));
                Log.Info("hook", "the lawn census and the glove's data sync are installed "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Error("hook", "installing the glove hooks failed: " + e.GetType().Name + " "
                    + e.Message + " - the periodic lawn census still runs on the host");
            }

            GiftGate.Install();
            MintGate.Install();
            PlantDeath.Install();
        }

        /// <summary>Run once per frame: the host's census, and nothing else unless a glove hook fired.</summary>
        public static void Tick()
        {
            if (!TheRoomDecides())
            {
                _gloveBefore = null;
                return;
            }

            if (!_room.IsHost)
            {
                return;
            }

            int now = Environment.TickCount;
            if (!Due(now, _censusAt, CensusMillis))
            {
                return;
            }

            _censusAt = now;
            Census();
        }

        public static void ForgetTheLawn()
        {
            _applying = false;
            _gloveBefore = null;
            _censusAt = 0;
            ForgetCarried();
            _cartCarriedBefore = 0;
            _localMints.Clear();
            _away.Clear();
            PlantDeath.Forget();
        }

        /// <summary>Nothing is in a hand on a lawn that no longer exists.</summary>
        private static void ForgetCarried()
        {
            _carriedNetId = 0;
            _carriedInstance = 0;
            _carriedType = Cursor.NoCard;
        }

        /// <summary>
        /// Asked by the gift door: may this end let the game roll which plant a gift box grows into. The
        /// rule is the room's, and a peer that is answered no keeps its closed box until the plant the
        /// host rolled arrives with the layout.
        /// Proof: ref/EVIDENCE.md E40.
        /// </summary>
        public static bool MayRollGift()
        {
            bool rolls = GiftRules.RollsOwnDice(TheRoomDecides(), LevelSync.TheRoomIsInALevel,
                _room != null && _room.IsHost);

            if (!rolls)
            {
                Log.Info("mp", "the gift box on this lawn rolls nothing: what grows out of it is the "
                    + "host's roll " + ApiBase.FrameTag());
            }

            return rolls;
        }

        /// <summary>
        /// Asked by the two doors that place a gift box rather than open one: the artist sunflower's own
        /// ability and the level's periodic box. Both mint it out of this end's dice, so a peer that let
        /// them run shows a box the host never placed and loses it again within a second, which is the
        /// spam this answers. The boxes the lawn holds are the host's and arrive with the layout.
        /// Proof: ref/EVIDENCE.md E43.
        /// </summary>
        public static bool MayPlaceGift(string what)
        {
            bool places = GiftRules.RollsOwnDice(TheRoomDecides(), LevelSync.TheRoomIsInALevel,
                _room != null && _room.IsHost);

            if (!places)
            {
                Log.Info("mp", "no " + what + " is placed here: the gift boxes on this lawn are the host's "
                    + ApiBase.FrameTag());
            }

            return places;
        }

        /// <summary>
        /// Asked by the mint watch with what the game's own creator just put on this lawn. A peer in a
        /// level that is not carrying out one of the mod's own calls mints a plant no frame described, and
        /// the census takes it down again afterwards; naming its type is how the doors that still roll on
        /// a peer are found rather than guessed at. Once per type per level, because an ability runs on
        /// every one of its countdowns and a line per run is noise instead of a fact.
        /// Proof: ref/EVIDENCE.md E43.
        /// </summary>
        public static void NoteMint(int plantType, int column, int row)
        {
            if (GiftRules.RollsOwnDice(TheRoomDecides(), LevelSync.TheRoomIsInALevel,
                    _room != null && _room.IsHost)
                || ApiBase.ModCallDepth > 0)
            {
                return;
            }

            if (!_localMints.Add(plantType))
            {
                return;
            }

            Log.Info("mp", "this end's own dice put plant type " + plantType + " on cell " + column + ","
                + row + " while the room is in a level, and nothing was told about it "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Do not wait for the next census: the game just changed what this lawn holds in a way no frame
        /// of ours described, and the room is told about it on the next Tick.
        /// </summary>
        public static void CensusNow()
        {
            _censusAt = 0;
        }

        /// <summary>Where the plant and zombie data frames arrive; WorldSync forwards them here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.PlantMove:
                {
                    PlantMovePayload move = Protocol.Payload<PlantMovePayload>(frame);

                    if (_room.IsHost)
                    {
                        AcceptPlantMove(peerId, move);
                    }
                    else if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyMove(move);
                    }

                    break;
                }

                case MsgAction.PlantSnapshot:
                {
                    PlantSnapshotPayload snapshot = Protocol.Payload<PlantSnapshotPayload>(frame);

                    if (_room.IsHost)
                    {
                        ApplySnapshot(peerId, snapshot);
                    }
                    else if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyLayout(snapshot);
                    }

                    break;
                }

                case MsgAction.ZombieMove:
                    if (_room.IsHost)
                    {
                        AcceptZombieMove(peerId, Protocol.Payload<ZombieMovePayload>(frame));
                    }
                    else if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyZombieMove(Protocol.Payload<ZombieMovePayload>(frame));
                    }

                    break;

                case MsgAction.PlantLift:
                {
                    PlantLiftPayload lift = Protocol.Payload<PlantLiftPayload>(frame);

                    if (_room.IsHost)
                    {
                        AcceptPlantLift(peerId, lift);
                    }
                    else if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyLift(lift);
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// The host's walk over its own lawn. Every plant the room has not minted - a tutorial plant, a
        /// level script's plant, a plant the glove rebuilt - is minted here, and the walk then sends the
        /// whole layout: every room id, type and cell. The individual spawn/move/despawn frames stay for
        /// placements, but the layout is what repairs anything they missed.
        /// </summary>
        private static void Census()
        {
            if (!TheRoomDecides() || !_room.IsHost || Entities.Board() == null)
            {
                return;
            }

            PlantState[] plants = Plants.ReadAll();
            if (plants == null)
            {
                return;
            }

            var seen = new HashSet<int>();
            int minted = 0;
            int moved = 0;
            int retyped = 0;

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState now = plants[i];
                if (now.InstanceId == 0)
                {
                    continue;
                }

                seen.Add(now.InstanceId);

                NetEntity record = _room.Entities.ByInstanceId(now.InstanceId);

                if (record == null)
                {
                    record = _room.Entities.Add(NetKind.Plant, _room.LocalPeerId, now.PlantType,
                        now.Column, now.Row);
                    _room.Entities.Bind(record.NetId, now.InstanceId);
                    minted++;

                    Log.Info("mp", "plant #" + record.NetId + " (type " + now.PlantType + ", cell "
                        + now.Column + "," + now.Row + ") was on the host's lawn without a room id, minted"
                        + " " + ApiBase.FrameTag());

                    continue;
                }

                if (record.Kind != NetKind.Plant || record.InstanceId != now.InstanceId)
                {
                    continue;
                }

                if (record.Type != now.PlantType)
                {
                    record.Type = now.PlantType;
                    retyped++;
                }

                if (record.Column == now.Column && record.Row == now.Row)
                {
                    continue;
                }

                record.Column = now.Column;
                record.Row = now.Row;
                moved++;
            }

            List<NetEntity> gone = null;

            foreach (NetEntity record in _room.Entities.Entries)
            {
                if (record.Kind != NetKind.Plant || record.InstanceId == 0)
                {
                    continue;
                }

                if (seen.Contains(record.InstanceId))
                {
                    continue;
                }

                if (_away.Contains(record.NetId))
                {
                    // In a cart: its object may already be gone from the lawn that lifted it, and the
                    // drop is what gives the record its new cell. E58.
                    continue;
                }

                if (gone == null)
                {
                    gone = new List<NetEntity>();
                }

                gone.Add(record);
            }

            if (gone != null)
            {
                for (int i = 0; i < gone.Count; i++)
                {
                    _room.Entities.Remove(gone[i].NetId);
                }
            }

            SendLayout(plants);

            if (minted > 0 || moved > 0 || retyped > 0 || (gone != null && gone.Count > 0))
            {
                Log.Info("mp", "lawn census: " + minted + " minted, " + moved + " moved, "
                    + retyped + " retyped, " + (gone == null ? 0 : gone.Count) + " gone "
                    + ApiBase.FrameTag());
            }
        }

        /// <summary>
        /// The host's lawn as one frame: every plant the room can name, with its type and cell. Sending
        /// the whole thing rather than only the changes is deliberate - a client that missed a frame, or
        /// grew a tutorial plant of its own, is brought back to the host's lawn instead of staying different.
        /// </summary>
        private static void SendLayout(PlantState[] plants)
        {
            if (!_room.IsHost || plants == null)
            {
                return;
            }

            var layout = new List<PlantSnap>(plants.Length);

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState plant = plants[i];
                NetEntity record = _room.Entities.ByInstanceId(plant.InstanceId);

                if (record == null || record.Kind != NetKind.Plant)
                {
                    continue;
                }

                layout.Add(PlantSnapRules.Of(record.NetId, plant.PlantType, plant.Column, plant.Row,
                    plant.Facts));
            }

            _room.BroadcastGameplay(MsgAction.PlantSnapshot, new PlantSnapshotPayload
            {
                Plants = layout.ToArray()
            });

            Log.Info("mp", "plant layout: " + layout.Count + " plants from the host " + ApiBase.FrameTag());
        }

        /// <summary>
        /// A client making its lawn look like the host's layout. Every named plant is bound to a local
        /// object of that type on that cell, moved when only the cell differs, replaced when the type
        /// differs, and every local plant the layout does not name is removed. That last step is what
        /// takes out a duplicate or a tutorial plant the host never had. A wanted entry with no object of
        /// its own first takes the plant already standing on its cell, so a plant this lawn grew - by a
        /// fertilizer or by a fusion - is kept and given the room's id instead of being killed and rebuilt.
        /// </summary>
        private static void ApplyLayout(PlantSnapshotPayload layout)
        {
            if (layout == null || layout.Plants == null || !TheRoomDecides() || _applying
                || Entities.Board() == null)
            {
                return;
            }

            var kept = new HashSet<int>();
            var named = new HashSet<int>();
            int moved = 0;
            int spawned = 0;
            int adopted = 0;
            int retyped = 0;
            int aligned = 0;

            for (int i = 0; i < layout.Plants.Length; i++)
            {
                if (layout.Plants[i] != null && layout.Plants[i].NetId > 0)
                {
                    named.Add(layout.Plants[i].NetId);
                }
            }

            _applying = true;

            try
            {
                for (int i = 0; i < layout.Plants.Length; i++)
                {
                    PlantSnap wanted = layout.Plants[i];
                    if (wanted == null || wanted.NetId <= 0)
                    {
                        continue;
                    }

                    NetEntity record = _room.Entities.Find(wanted.NetId);
                    if (record == null || record.Kind != NetKind.Plant)
                    {
                        record = _room.Entities.Adopt(wanted.NetId, NetKind.Plant, _room.LocalPeerId,
                            wanted.PlantType, wanted.Column, wanted.Row);

                        if (record == null)
                        {
                            continue;
                        }
                    }

                    if (_away.Contains(record.NetId))
                    {
                        // In a cart on the room's word: the layout must not show it, move it or grow a
                        // second one on its old cell. The drop's move frame is what brings it back. E58.
                        if (record.InstanceId != 0)
                        {
                            kept.Add(record.InstanceId);
                        }

                        continue;
                    }

                    PlantState here = default(PlantState);
                    bool exists = record.InstanceId != 0
                        && Plants.TryRead(record.InstanceId, out here);

                    if (!exists)
                    {
                        int instanceId = FindUnboundPlant(wanted.PlantType, wanted.Column, wanted.Row);
                        bool tookTheCell = false;

                        if (instanceId == 0)
                        {
                            instanceId = AdoptTheCell(wanted, named);
                            tookTheCell = instanceId != 0;
                        }

                        if (instanceId == 0 && NothingStandsOnTheCell(wanted))
                        {
                            instanceId = Plants.Spawn(wanted.Column, wanted.Row, wanted.PlantType);
                        }

                        if (instanceId != 0)
                        {
                            _room.Entities.Bind(wanted.NetId, instanceId);
                            kept.Add(instanceId);

                            if (tookTheCell)
                            {
                                adopted++;
                            }
                            else
                            {
                                spawned++;
                            }

                            if (Plants.AlignFacts(instanceId, PlantSnapRules.FactsOf(wanted)))
                            {
                                aligned++;
                            }
                        }

                        record.Type = wanted.PlantType;
                        record.Column = wanted.Column;
                        record.Row = wanted.Row;
                        continue;
                    }

                    if (here.PlantType != wanted.PlantType)
                    {
                        // The game's own growth, not a kill and a spawn: the cell is still the old
                        // plant's, and a plain spawn on it returns nothing (E53, E56).
                        int instanceId = Plants.Grow(record.InstanceId, wanted.Column, wanted.Row,
                            wanted.PlantType);

                        if (instanceId != 0)
                        {
                            _room.Entities.Bind(wanted.NetId, instanceId);
                            kept.Add(instanceId);

                            if (Plants.AlignFacts(instanceId, PlantSnapRules.FactsOf(wanted)))
                            {
                                aligned++;
                            }

                            Log.Info("mp", "plant #" + wanted.NetId + " changes type " + here.PlantType
                                + " -> " + wanted.PlantType + " on cell " + wanted.Column + ","
                                + wanted.Row + " as the host says " + ApiBase.FrameTag());
                        }

                        retyped++;
                        record.Type = wanted.PlantType;
                        record.Column = wanted.Column;
                        record.Row = wanted.Row;
                        continue;
                    }

                    if (here.Column != wanted.Column || here.Row != wanted.Row)
                    {
                        Plants.MoveBox(record.InstanceId, wanted.Column, wanted.Row);
                        moved++;
                    }

                    if (Plants.AlignFacts(record.InstanceId, PlantSnapRules.FactsOf(wanted)))
                    {
                        aligned++;
                    }

                    kept.Add(record.InstanceId);
                    record.Type = wanted.PlantType;
                    record.Column = wanted.Column;
                    record.Row = wanted.Row;
                }

                int removed = 0;
                PlantState[] local = Plants.ReadAll();

                if (local != null)
                {
                    for (int i = 0; i < local.Length; i++)
                    {
                        PlantState plant = local[i];
                        if (plant.InstanceId == 0 || kept.Contains(plant.InstanceId))
                        {
                            continue;
                        }

                        NetEntity record = _room.Entities.ByInstanceId(plant.InstanceId);
                        if (record != null && record.Kind == NetKind.Plant)
                        {
                            if (_away.Contains(record.NetId))
                            {
                                kept.Add(plant.InstanceId);
                                continue;
                            }

                            _room.Entities.Remove(record.NetId);
                        }

                        Plants.Kill(plant.InstanceId, 0);
                        removed++;
                    }
                }

                Log.Info("mp", "the host's plant layout arrived: " + layout.Plants.Length + " plants, "
                    + spawned + " spawned, " + adopted + " taken over, " + moved + " moved, " + retyped
                    + " retyped, " + aligned + " aligned, " + removed + " removed here "
                    + ApiBase.FrameTag());
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// A client's lawn after its glove ran. It carries the room id of every plant this end can name;
        /// a plant the glove rebuilt is 0 because its old object is gone. The host matches a 0 to one of
        /// its own minted plants of the same type that the snapshot no longer names, which is the move,
        /// and only spawns when there is no such plant.
        /// </summary>
        private static void ApplySnapshot(int peerId, PlantSnapshotPayload snapshot)
        {
            if (!_room.IsHost || snapshot == null || snapshot.Plants == null || Entities.Board() == null)
            {
                return;
            }

            PlantState[] hostPlants = Plants.ReadAll();
            if (hostPlants == null)
            {
                return;
            }

            var named = new HashSet<int>();
            var cells = new HashSet<long>();

            for (int i = 0; i < snapshot.Plants.Length; i++)
            {
                PlantSnap plant = snapshot.Plants[i];
                if (plant == null)
                {
                    continue;
                }

                if (plant.NetId > 0)
                {
                    named.Add(plant.NetId);
                }

                cells.Add(CellKey(plant.PlantType, plant.Column, plant.Row));
            }

            int moved = 0;
            int spawned = 0;

            for (int i = 0; i < snapshot.Plants.Length; i++)
            {
                PlantSnap plant = snapshot.Plants[i];
                if (plant == null || plant.NetId <= 0)
                {
                    continue;
                }

                NetEntity record = _room.Entities.Find(plant.NetId);
                if (record == null || record.Kind != NetKind.Plant || record.InstanceId == 0)
                {
                    continue;
                }

                PlantState here;
                if (!FindPlant(hostPlants, record.InstanceId, out here))
                {
                    continue;
                }

                record.Type = plant.PlantType;

                if (here.Column == plant.Column && here.Row == plant.Row)
                {
                    continue;
                }

                if (!Plants.MoveBox(record.InstanceId, plant.Column, plant.Row))
                {
                    Log.Warn("mp", "plant #" + record.NetId + " could not be moved to cell "
                        + plant.Column + "," + plant.Row + " on the host's lawn");
                    continue;
                }

                record.Column = plant.Column;
                record.Row = plant.Row;
                moved++;
            }

            for (int i = 0; i < snapshot.Plants.Length; i++)
            {
                PlantSnap plant = snapshot.Plants[i];
                if (plant == null || plant.NetId > 0)
                {
                    continue;
                }

                if (TryMoveUnnamed(hostPlants, named, cells, plant))
                {
                    moved++;
                    continue;
                }

                int instanceId = Plants.Spawn(plant.Column, plant.Row, plant.PlantType);
                if (instanceId == 0)
                {
                    continue;
                }

                NetEntity record = _room.Entities.Add(NetKind.Plant, peerId, plant.PlantType,
                    plant.Column, plant.Row);
                _room.Entities.Bind(record.NetId, instanceId);
                spawned++;

                _room.BroadcastGameplay(MsgAction.SpawnPlant, new SpawnPlantPayload
                {
                    NetId = record.NetId,
                    PlantType = plant.PlantType,
                    Column = plant.Column,
                    Row = plant.Row,
                    OwnerPeer = peerId
                });

                Log.Info("mp", "peer " + peerId + " has plant type " + plant.PlantType + " on cell "
                    + plant.Column + "," + plant.Row + " that the host did not have, spawned as #"
                    + record.NetId + " " + ApiBase.FrameTag());
            }

            if (moved > 0 || spawned > 0)
            {
                Log.Info("mp", "peer " + peerId + "'s glove data: " + moved + " moved, " + spawned
                    + " spawned on the host's lawn " + ApiBase.FrameTag());
            }

            Census();
        }

        /// <summary>
        /// A plant the snapshot could not name is the glove's rebuilt copy. Find the host's own minted
        /// plant of that type that the snapshot does NOT name on its current cell: that is the old copy,
        /// and moving it is the whole data change. A plant the snapshot still names stays where it is.
        /// </summary>
        private static bool TryMoveUnnamed(PlantState[] hostPlants, HashSet<int> named, HashSet<long> cells,
            PlantSnap wanted)
        {
            NetEntity candidate = null;
            PlantState candidateState = default(PlantState);

            foreach (NetEntity record in _room.Entities.Entries)
            {
                if (record.Kind != NetKind.Plant || record.InstanceId == 0)
                {
                    continue;
                }

                if (named.Contains(record.NetId) || record.Type != wanted.PlantType)
                {
                    continue;
                }

                PlantState here;
                if (!FindPlant(hostPlants, record.InstanceId, out here))
                {
                    continue;
                }

                if (cells.Contains(CellKey(record.Type, here.Column, here.Row)))
                {
                    continue;
                }

                candidate = record;
                candidateState = here;
                break;
            }

            if (candidate == null)
            {
                return false;
            }

            if (!Plants.MoveBox(candidate.InstanceId, wanted.Column, wanted.Row))
            {
                return false;
            }

            candidate.Column = wanted.Column;
            candidate.Row = wanted.Row;
            candidate.Type = wanted.PlantType;
            named.Add(candidate.NetId);
            cells.Add(CellKey(wanted.PlantType, wanted.Column, wanted.Row));

            _room.BroadcastGameplay(MsgAction.PlantMove, new PlantMovePayload
            {
                NetId = candidate.NetId,
                Column = wanted.Column,
                Row = wanted.Row
            });

            Log.Info("mp", "plant #" + candidate.NetId + " (type " + candidate.Type + ") moved from cell "
                + candidateState.Column + "," + candidateState.Row + " to " + wanted.Column + ","
                + wanted.Row + " by a peer's glove, the room is told " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>
        /// A peer's glove put a plant on a cell. The host repeats the game's own move on its copy and
        /// tells every lawn; this is the direct path, so the move does not depend on the slower census.
        /// </summary>
        private static void AcceptPlantMove(int peerId, PlantMovePayload move)
        {
            if (move == null || !TheRoomDecides())
            {
                return;
            }

            NetEntity record = _room.Entities.Find(move.NetId);
            if (record == null || record.Kind != NetKind.Plant)
            {
                Log.Warn("mp", "peer " + peerId + " moved plant #" + move.NetId
                    + ", but the host has no such plant");
                return;
            }

            ShowAgain(record);

            int instanceId = record.InstanceId;
            if (instanceId == 0 || !ApiBase.Present(Entities.FindPlant(instanceId)))
            {
                instanceId = FindUnboundPlantAt(move.Column, move.Row);
                if (instanceId == 0)
                {
                    instanceId = FindUnboundPlant(record.Type, move.Column, move.Row);
                }

                if (instanceId != 0)
                {
                    _room.Entities.Bind(record.NetId, instanceId);
                }
            }

            if (instanceId != 0)
            {
                _applying = true;

                try
                {
                    Plants.MoveBox(instanceId, move.Column, move.Row);
                }
                finally
                {
                    _applying = false;
                }
            }

            record.Column = move.Column;
            record.Row = move.Row;

            _room.BroadcastGameplay(MsgAction.PlantMove, move);
            Census();

            Log.Info("mp", "peer " + peerId + " moved plant #" + move.NetId + " to cell "
                + move.Column + "," + move.Row + "; the room is told " + ApiBase.FrameTag());
        }

        /// <summary>The host's move frame: put this end's copy of that plant on the named cell.</summary>
        private static void ApplyMove(PlantMovePayload move)
        {
            if (move == null || !TheRoomDecides() || _applying)
            {
                return;
            }

            NetEntity record = _room.Entities.Find(move.NetId);
            if (record == null || record.Kind != NetKind.Plant)
            {
                return;
            }

            ShowAgain(record);

            if (record.InstanceId == 0 || !ApiBase.Present(Entities.FindPlant(record.InstanceId)))
            {
                int replacement = FindUnboundPlantAt(move.Column, move.Row);
                if (replacement == 0)
                {
                    replacement = FindUnboundPlant(record.Type, move.Column, move.Row);
                }

                if (replacement != 0)
                {
                    _room.Entities.Bind(record.NetId, replacement);
                    Log.Info("mp", "plant #" + record.NetId + " is the glove's rebuilt object on this lawn,"
                        + " rebound to obj " + replacement + " " + ApiBase.FrameTag());
                }
            }

            if (record.InstanceId != 0)
            {
                _applying = true;

                try
                {
                    Plants.MoveBox(record.InstanceId, move.Column, move.Row);
                }
                finally
                {
                    _applying = false;
                }
            }

            record.Column = move.Column;
            record.Row = move.Row;

            Log.Info("mp", "plant #" + move.NetId + " is on cell " + move.Column + "," + move.Row
                + " as the host said " + ApiBase.FrameTag());
        }

        /// <summary>
        /// A peer's cart lifted a plant. The host hides its own copy the same way every other lawn does and
        /// says so to the rest; the plant keeps its room id, its cell and its numbers here, so the drop's
        /// move frame finds the same plant it lifted. Proof: ref/EVIDENCE.md E55, E58.
        /// </summary>
        private static void AcceptPlantLift(int peerId, PlantLiftPayload lift)
        {
            if (lift == null || !TheRoomDecides())
            {
                return;
            }

            NetEntity record = _room.Entities.Find(lift.NetId);
            if (record == null || record.Kind != NetKind.Plant)
            {
                Log.Warn("mp", "peer " + peerId + " lifted plant #" + lift.NetId
                    + ", but the host has no such plant");
                return;
            }

            TakeAway(record, "peer " + peerId + "'s cart");

            _room.BroadcastGameplay(MsgAction.PlantLift, new PlantLiftPayload
            {
                NetId = record.NetId,
                Column = record.Column,
                Row = record.Row
            });

            Log.Info("mp", "peer " + peerId + " lifted plant #" + record.NetId + " off cell "
                + record.Column + "," + record.Row + "; every other lawn hides its copy "
                + ApiBase.FrameTag());
        }

        /// <summary>The host's lift: this end's own copy goes out of sight, not out of the ledger.</summary>
        private static void ApplyLift(PlantLiftPayload lift)
        {
            if (lift == null || !TheRoomDecides() || _applying)
            {
                return;
            }

            NetEntity record = _room.Entities.Find(lift.NetId);
            if (record == null || record.Kind != NetKind.Plant)
            {
                return;
            }

            TakeAway(record, "the host's cart");
        }

        /// <summary>
        /// Hides this lawn's copy of a plant the room put in a cart. The object stays in the lawn's list and
        /// keeps its health, level and cell, so the drop moves the very plant this seat already had; only
        /// the picture and the scripts go quiet. A lawn whose own game already destroyed the object - the
        /// seat that clicked - has nothing to hide and only remembers the id.
        /// </summary>
        private static void TakeAway(NetEntity record, string by)
        {
            _away.Add(record.NetId);

            if (record.InstanceId == 0 || !ApiBase.Present(Entities.FindPlant(record.InstanceId)))
            {
                return;
            }

            Plants.SetShown(record.InstanceId, false);

            Log.Info("mp", "plant #" + record.NetId + " (obj " + record.InstanceId
                + ") is in a cart by " + by + ", this lawn hides its copy " + ApiBase.FrameTag());
        }

        /// <summary>The drop or the layout brought the plant back: show this lawn's own object again.</summary>
        private static void ShowAgain(NetEntity record)
        {
            if (record == null || !_away.Remove(record.NetId))
            {
                return;
            }

            if (record.InstanceId == 0 || !ApiBase.Present(Entities.FindPlant(record.InstanceId)))
            {
                return;
            }

            Plants.SetShown(record.InstanceId, true);

            Log.Info("mp", "plant #" + record.NetId + " (obj " + record.InstanceId
                + ") is out of the cart, this lawn shows its copy again " + ApiBase.FrameTag());
        }

        /// <summary>A client's glove moved a zombie: the host repeats it and tells the room.</summary>
        private static void AcceptZombieMove(int peerId, ZombieMovePayload move)
        {
            if (move == null || !TheRoomDecides())
            {
                return;
            }

            NetEntity record = _room.Entities.Find(move.NetId);
            if (record == null || record.Kind != NetKind.Zombie || record.InstanceId == 0)
            {
                Log.Warn("mp", "peer " + peerId + " moved zombie #" + move.NetId
                    + ", but the host has no such zombie");
                return;
            }

            if (!Zombies.MoveGlove(record.InstanceId, move.X, move.Row))
            {
                Log.Warn("mp", "the host could not repeat peer " + peerId + "'s move of zombie #"
                    + move.NetId);
                return;
            }

            record.Row = move.Row;
            _room.BroadcastGameplay(MsgAction.ZombieMove, move);
            ZombieSync.ForcePush();

            Log.Info("mp", "peer " + peerId + " moved zombie #" + move.NetId + " to x " + move.X
                + ", row " + move.Row + "; the room is told " + ApiBase.FrameTag());
        }

        /// <summary>The host's zombie move, played with the game's own glove move on this end's object.</summary>
        private static void ApplyZombieMove(ZombieMovePayload move)
        {
            if (move == null || !TheRoomDecides() || _applying)
            {
                return;
            }

            NetEntity record = _room.Entities.Find(move.NetId);
            if (record == null || record.Kind != NetKind.Zombie || record.InstanceId == 0)
            {
                return;
            }

            _applying = true;

            try
            {
                Zombies.MoveGlove(record.InstanceId, move.X, move.Row);
                record.Row = move.Row;

                Log.Info("mp", "zombie #" + move.NetId + " is on x " + move.X + ", row " + move.Row
                    + " as the host said " + ApiBase.FrameTag());
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// Asked after the glove's native placement. A carried plant gets a direct move frame first,
        /// because the glove can rebuild the object and a plain snapshot would look like a spawn next to
        /// the old plant. The snapshot/census still follows as the fallback and as the force sync.
        /// </summary>
        private static void AfterGlove()
        {
            PlantState[] before = _gloveBefore;
            _gloveBefore = null;

            if (before == null || !TheRoomDecides() || _applying || Entities.Board() == null)
            {
                ForgetCarried();
                return;
            }

            bool moved = TryMoveCarriedPlant("glove");
            ForgetCarried();

            if (_room.IsHost)
            {
                Census();
                return;
            }

            // A direct PlantMove is authoritative here. Sending the whole lawn as well made the
            // host apply the same glove move twice, and a rebuilt object could be spawned again.
            // The snapshot is only the fallback when the direct move could not name its plant.
            if (!moved)
            {
                SendSnapshot("glove");
            }
        }

        /// <summary>
        /// Asked after one cart click. `Wheel.Use` runs both of its halves on the lawn that clicked, and
        /// the room cannot read which one ran (the cart keeps its pocket in private fields), so the click
        /// is told apart by what this end remembered carrying: empty before means a lift, carrying means
        /// the drop. A lift travels nothing - the plant is in the cart here and on nobody else's lawn, and
        /// the host's layout would only put it back under the cart. The drop is the glove's sentence: one
        /// room plant stands on one other cell now, and every lawn answers by moving its own object there,
        /// which is why the cart's carried plant needs no numbers copied after it.
        /// The lift line says what the click did to this lawn's object, which is the one thing the dump
        /// cannot say: whether the cart destroys it or only takes it out of the lawn's list.
        /// Proof: ref/EVIDENCE.md E55.
        /// </summary>
        private static void AfterCart()
        {
            if (!TheRoomDecides() || _applying || Entities.Board() == null)
            {
                return;
            }

            int carriedBefore = _cartCarriedBefore;
            bool isDrop = CartRules.IsDrop(carriedBefore);

            bool landed = isDrop && TryMoveCarriedPlant("cart", true);

            if (isDrop)
            {
                if (!landed)
                {
                    Log.Info("mp", "the cart's drop named no plant of this room on cell "
                        + Cursor.ReadColumn() + "," + Cursor.ReadRow() + "; this lawn keeps carrying plant #"
                        + carriedBefore + " " + ApiBase.FrameTag());
                }
            }
            else
            {
                SayWhatTheLiftTook();
            }

            if (CartRules.HeldAfterClick(carriedBefore, isDrop ? 0 : _carriedNetId, landed) == 0)
            {
                ForgetCarried();
            }

            if (_room.IsHost)
            {
                Census();
                return;
            }

            // The same rule the glove follows: the move frame is the sentence, and the whole lawn is
            // offered only when the move could not name its plant.
            if (isDrop && !landed)
            {
                SendSnapshot("cart");
            }
        }

        /// <summary>
        /// The measuring door and the sentence at once: the plant the lift named, whether the game's own
        /// body left its object on this lawn, and the room's frame that hides the plant on every other
        /// lawn. A cell can hold two plants, so the plant named is the click's answer and not a guess.
        /// The lift is not a move: the cart has not chosen a cell yet, and the host's layout would only
        /// put the plant back under the cart. Proof: ref/EVIDENCE.md E55, E58.
        /// </summary>
        private static void SayWhatTheLiftTook()
        {
            if (_carriedNetId == 0)
            {
                return;
            }

            NetEntity record = _room.Entities.Find(_carriedNetId);
            PlantState here;
            bool stands = _carriedInstance != 0 && Plants.TryRead(_carriedInstance, out here);

            if (stands)
            {
                Plants.SetShown(_carriedInstance, false);
            }

            _away.Add(_carriedNetId);

            PlantLiftPayload lift = new PlantLiftPayload
            {
                NetId = _carriedNetId,
                Column = record == null ? -1 : record.Column,
                Row = record == null ? -1 : record.Row
            };

            if (_room.IsHost)
            {
                _room.BroadcastGameplay(MsgAction.PlantLift, lift);
            }
            else
            {
                _room.SendToHost(MsgAction.PlantLift, lift);
            }

            Log.Info("mp", "the cart lifted plant #" + _carriedNetId + " (type " + _carriedType
                + ") off cell " + (record == null ? "?" : record.Column + "," + record.Row)
                + "; its object #" + _carriedInstance
                + (stands ? " was hidden here" : " is gone from this lawn")
                + (_room.IsHost ? ", every other lawn is told" : ", the host is asked to hide every other copy")
                + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// The plant the tool was carrying, moved with one explicit frame. If the native tool rebuilt the
        /// plant, the old object is gone and the new one on the target cell is still unbound; bind that
        /// object to the old room id before sending, so every lawn moves its own copy instead of growing
        /// a second plant. A cart drop that built nothing on a clear cell is the fused kind the native
        /// body cannot preview, and that is the one case where this end asks the game's creator itself.
        /// Proof: ref/EVIDENCE.md E55, E58, E63.
        /// </summary>
        private static bool TryMoveCarriedPlant(string tool, bool rebuild = false)
        {
            int netId = _carriedNetId;
            int type = _carriedType;
            int oldInstance = _carriedInstance;

            if (netId <= 0 || type == Cursor.NoCard)
            {
                return false;
            }

            int column = Cursor.ReadColumn();
            int row = Cursor.ReadRow();

            if (column < 0 || row < 0)
            {
                return false;
            }

            NetEntity record = _room.Entities.Find(netId);
            if (record == null || record.Kind != NetKind.Plant)
            {
                return false;
            }

            PlantState here;
            int targetInstance = 0;

            if (oldInstance != 0 && Plants.TryRead(oldInstance, out here)
                && here.Column == column && here.Row == row)
            {
                targetInstance = oldInstance;
            }

            if (targetInstance == 0)
            {
                targetInstance = FindUnboundPlant(type, column, row);
            }

            if (targetInstance == 0 && oldInstance != 0
                && ApiBase.Present(Entities.FindPlant(oldInstance)))
            {
                // The remembered object is still standing somewhere else. That is a stale carry,
                // not a rebuilt plant; let the snapshot/census decide instead of binding the wrong plant.
                Log.Info("mp", "the " + tool + "'s remembered plant #" + netId
                    + " is still on the lawn but not on cell " + column + "," + row
                    + "; falling back to the lawn snapshot " + ApiBase.FrameTag());
                return false;
            }

            if (targetInstance == 0)
            {
                // The native tool can rebuild the plant with a fused/custom type. The new object is
                // still the one on the target cell, so fall back to the cell instead of the old type.
                targetInstance = FindUnboundPlantAt(column, row);
            }

            if (targetInstance != 0 && targetInstance != oldInstance)
            {
                PlantState rebuilt;
                if (Plants.TryRead(targetInstance, out rebuilt))
                {
                    Log.Info("mp", "the " + tool + " rebuilt plant #" + netId + " as obj " + targetInstance
                        + " type " + rebuilt.PlantType + " on cell " + column + "," + row
                        + " " + ApiBase.FrameTag());
                }
            }

            if (targetInstance == 0 && record.InstanceId != 0
                && Plants.TryRead(record.InstanceId, out here)
                && here.Column == column && here.Row == row)
            {
                targetInstance = record.InstanceId;
            }

            if (targetInstance == 0 && rebuild)
            {
                targetInstance = RebuildTheCarriedPlant(tool, netId, type, column, row);
            }

            if (targetInstance == 0)
            {
                Log.Info("mp", "the " + tool + "'s plant #" + netId + " is not on cell " + column + ","
                    + row + " after the native call, falling back to the lawn snapshot "
                    + ApiBase.FrameTag());
                return false;
            }

            if (record.InstanceId != targetInstance)
            {
                _room.Entities.Bind(netId, targetInstance);
            }

            if (oldInstance != 0 && oldInstance != targetInstance
                && ApiBase.Present(Entities.FindPlant(oldInstance)))
            {
                // The tool rebuilt the plant and left the old object behind. Remove it here, or the
                // fallback snapshot would send it as a second plant on the old cell.
                Plants.Kill(oldInstance, 0);
            }

            record.Column = column;
            record.Row = row;
            record.Type = type;

            ShowAgain(record);

            PlantMovePayload move = new PlantMovePayload
            {
                NetId = netId,
                Column = column,
                Row = row
            };

            if (_room.IsHost)
            {
                _room.BroadcastGameplay(MsgAction.PlantMove, move);
            }
            else
            {
                _room.SendToHost(MsgAction.PlantMove, move);
            }

            Log.Info("mp", "the " + tool + " moved plant #" + netId + " (type " + type + ") to cell "
                + column + "," + row + ( _room.IsHost ? " on the host" : " asked of the host")
                + " " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>
        /// The cart's drop body builds from the preview it carries, and a fused kind (measured: type
        /// 1027) has no preview: every drop click left the cell empty while the lift had already destroyed
        /// this lawn's object. The cell being clear is the proof that the native body refused by not
        /// knowing the kind rather than by finding the cell taken, and only then is the game's growth
        /// creator asked for the kind (E56). A cell that already holds something is a drop the player can
        /// still place elsewhere, and the game still carries that plant. Proof: ref/EVIDENCE.md E63.
        /// </summary>
        private static int RebuildTheCarriedPlant(string tool, int netId, int type, int column, int row)
        {
            if (!NothingStandsOnTheCell(new PlantSnap { Column = column, Row = row }))
            {
                Log.Info("mp", "the " + tool + "'s drop of plant #" + netId + " (type " + type
                    + ") created nothing on cell " + column + "," + row
                    + ", which already holds a plant; the drop is refused, not lost "
                    + ApiBase.FrameTag());
                return 0;
            }

            int built = Plants.SpawnFree(column, row, type);

            if (built == 0)
            {
                Log.Error("mp", "the " + tool + "'s drop of plant #" + netId + " (type " + type
                    + ") on cell " + column + "," + row + " created nothing and the game's own creator"
                    + " refused to rebuild it - this lawn has lost the plant " + ApiBase.FrameTag());
                return 0;
            }

            Log.Info("mp", "the " + tool + "'s drop of plant #" + netId + " (type " + type
                + ") created nothing on cell " + column + "," + row + ", so the cell was rebuilt with the"
                + " game's own creator as obj " + built + " (the fused kind has no drop preview) "
                + ApiBase.FrameTag());

            return built;
        }

        /// <summary>The plant the game put in the glove's hand, remembered until placement.</summary>
        private static void RememberGlovePlant()
        {
            if (!TheRoomDecides() || _applying)
            {
                return;
            }

            Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
            if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.thePlantOnGlove))
            {
                return;
            }

            _carriedInstance = mouse.thePlantOnGlove.GetInstanceID();
            _carriedType = (int)mouse.thePlantOnGlove.thePlantType;

            NetEntity record = _room.Entities.ByInstanceId(_carriedInstance);
            _carriedNetId = record != null && record.Kind == NetKind.Plant ? record.NetId : 0;
        }

        private static void SendSnapshot(string tool)
        {
            PlantState[] plants = Plants.ReadAll();
            if (plants == null)
            {
                return;
            }

            var list = new List<PlantSnap>(plants.Length);

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState plant = plants[i];
                NetEntity record = _room.Entities.ByInstanceId(plant.InstanceId);

                list.Add(new PlantSnap
                {
                    NetId = record == null || record.Kind != NetKind.Plant ? 0 : record.NetId,
                    PlantType = plant.PlantType,
                    Column = plant.Column,
                    Row = plant.Row
                });
            }

            _room.SendToHost(MsgAction.PlantSnapshot, new PlantSnapshotPayload
            {
                Plants = list.ToArray()
            });

            Log.Info("mp", "the " + tool + " changed this lawn; " + list.Count
                + " plants are sent to the host as data " + ApiBase.FrameTag());
        }

        /// <summary>True while this end is running the glove, read from the game's own tool state.</summary>
        private static bool GloveActive()
        {
            return ApiBase.Query("GloveActive", () =>
            {
                Il2Cpp.Glove glove = Il2Cpp.Glove.Instance;
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;

                if (!ApiBase.Present(glove))
                {
                    return false;
                }

                return glove.isPickUp
                    || (ApiBase.Present(mouse)
                        && (ApiBase.Present(mouse.thePlantOnGlove)
                            || ApiBase.Present(mouse.theZombieOnGlove)));
            }, "input");
        }

        /// <summary>The unbound plant standing on a cell, whatever type the game has given it now.</summary>
        private static int FindUnboundPlantAt(int column, int row)
        {
            PlantState[] plants = Plants.ReadAll();
            if (plants == null)
            {
                return 0;
            }

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState plant = plants[i];
                if (plant.Column != column || plant.Row != row)
                {
                    continue;
                }

                if (_room.Entities.ByInstanceId(plant.InstanceId) == null)
                {
                    return plant.InstanceId;
                }
            }

            return 0;
        }

        /// <summary>
        /// The plant this end may answer a wanted entry with, out of the plants standing on that entry's cell:
        /// one of the wanted type that no id carries, one the wanted id itself carries when the object that id
        /// names is gone, or one an id the layout no longer names carries - that id is dropped first, so the
        /// same plant is not claimed twice. Anything else on that cell is not this entry's to take, and 0 comes
        /// back. Nothing here kills a plant: what stands on the cell with another type is the room's own
        /// disagreement, and rebuilding it every frame is the loop this answers (ref/EVIDENCE.md E54).
        /// </summary>
        private static int AdoptTheCell(PlantSnap wanted, HashSet<int> named)
        {
            PlantState[] local = Plants.ReadAll();
            if (local == null)
            {
                return 0;
            }

            for (int i = 0; i < local.Length; i++)
            {
                PlantState plant = local[i];
                if (plant.InstanceId == 0 || plant.PlantType != wanted.PlantType
                    || plant.Column != wanted.Column || plant.Row != wanted.Row)
                {
                    continue;
                }

                NetEntity holder = _room.Entities.ByInstanceId(plant.InstanceId);
                if (holder == null || holder.NetId == wanted.NetId)
                {
                    return plant.InstanceId;
                }

                if (named.Contains(holder.NetId))
                {
                    continue;
                }

                _room.Entities.Remove(holder.NetId);

                Log.Info("mp", "plant #" + wanted.NetId + " takes over the plant #" + plant.InstanceId
                    + " standing on cell " + wanted.Column + "," + wanted.Row + ", which the id #"
                    + holder.NetId + " carried and the room no longer names " + ApiBase.FrameTag());

                return plant.InstanceId;
            }

            return 0;
        }

        /// <summary>Whether the cell is clear, so the game's own creator is worth asking at all.</summary>
        private static bool NothingStandsOnTheCell(PlantSnap wanted)
        {
            PlantState[] local = Plants.ReadAll();
            if (local == null)
            {
                return false;
            }

            for (int i = 0; i < local.Length; i++)
            {
                if (local[i].InstanceId != 0 && local[i].Column == wanted.Column && local[i].Row == wanted.Row)
                {
                    return false;
                }
            }

            return true;
        }

        private static int FindUnboundPlant(int plantType, int column, int row)
        {
            PlantState[] plants = Plants.ReadAll();
            if (plants == null)
            {
                return 0;
            }

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState plant = plants[i];
                if (plant.PlantType != plantType || plant.Column != column || plant.Row != row)
                {
                    continue;
                }

                if (_room.Entities.ByInstanceId(plant.InstanceId) == null)
                {
                    return plant.InstanceId;
                }
            }

            return 0;
        }

        private static bool FindPlant(PlantState[] plants, int instanceId, out PlantState found)
        {
            for (int i = 0; i < plants.Length; i++)
            {
                if (plants[i].InstanceId == instanceId)
                {
                    found = plants[i];
                    return true;
                }
            }

            found = default(PlantState);
            return false;
        }

        private static long CellKey(int plantType, int column, int row)
        {
            return ((long)plantType << 32) | ((long)(column & 0xFFFF) << 16) | (uint)(row & 0xFFFF);
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides;
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action
                + ": only the host says where the lawn's plants stand");

            return false;
        }

        private static bool Due(int now, int since, int millis)
        {
            return unchecked((uint)(now - since)) >= (uint)millis;
        }

        [HarmonyPatch(typeof(Mouse), "TryToSetPlantByGlove")]
        public static class GloveDoor
        {
            private static void Prefix()
            {
                try
                {
                    _gloveBefore = null;

                    if (!TheRoomDecides() || _applying)
                    {
                        return;
                    }

                    _gloveBefore = Plants.ReadAll();
                    RememberGlovePlant();
                }
                catch (Exception e)
                {
                    _gloveBefore = null;
                    Log.Warn("hook", "reading the lawn before the glove threw: " + e.GetType().Name
                        + " " + e.Message);
                }
            }

            private static void Postfix()
            {
                try
                {
                    AfterGlove();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the glove's data sync threw: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        /// <summary>
        /// The cart, whose one native body holds both halves of a move. Nothing here stops it: the cart is
        /// the only thing that knows how to take its plant into the pocket and how to put it down, and the
        /// ghost the player drags is its own work. This remembers only what the lift took, so the click
        /// that puts it down can tell the room where the plant stands now.
        ///
        /// The plant is named by the hit test the body itself asks for - `Mouse.GetPlantsOnMouse` is read
        /// at +0x158 of `Wheel.Use` and is the same answer the shovel aims with - and the hovered cell is
        /// the fallback for the frames that answer empty.
        /// Proof: ref/EVIDENCE.md E55.
        /// </summary>
        [HarmonyPatch(typeof(Wheel), "Use")]
        public static class CartDoor
        {
            private static void Prefix()
            {
                _cartCarriedBefore = _carriedNetId;
                _cartDropInFlight = false;

                try
                {
                    bool drop = CartRules.IsDrop(_cartCarriedBefore);

                    if (!TheRoomDecides() || _applying)
                    {
                        return;
                    }

                    _cartDropInFlight = drop;

                    int[] under = Cursor.ReadPlantInstanceIdsUnderCursor();

                    Log.Info("mp", "the cart's click " + (drop
                            ? "is a drop of plant #" + _cartCarriedBefore
                            : "is a lift")
                        + ", the pointer is on cell " + Cursor.ReadColumn() + "," + Cursor.ReadRow()
                        + " and names " + (under == null ? 0 : under.Length) + " plant(s) "
                        + ApiBase.FrameTag());

                    if (drop)
                    {
                        return;
                    }

                    NetEntity taken = ShovelRules.TrackedPlant(_room.Entities, under);

                    if (taken == null)
                    {
                        taken = ShovelRules.TrackedPlantAt(_room.Entities, Cursor.ReadColumn(),
                            Cursor.ReadRow());
                    }

                    if (taken == null)
                    {
                        ForgetCarried();

                        Log.Info("mp", "the cart's lift named no plant this room numbers, so this lawn"
                            + " carries nothing it would have to put down " + ApiBase.FrameTag());

                        return;
                    }

                    _carriedNetId = taken.NetId;
                    _carriedInstance = taken.InstanceId;
                    _carriedType = taken.Type;
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "reading what the cart lifted threw: " + e.GetType().Name + " "
                        + e.Message);
                }
            }

            private static void Postfix(Wheel __instance)
            {
                try
                {
                    AfterCart();

                    Log.Info("mp", "the cart after the click holds type " + (int)__instance.thePlantType
                        + ", its preview is " + (ApiBase.Present(__instance.plant) ? "up" : "gone")
                        + ", its cooldown reads " + __instance.CD + " of " + __instance.fullCD
                        + " " + ApiBase.FrameTag());
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the cart's data sync threw: " + e.GetType().Name + " "
                        + e.Message);
                }
                finally
                {
                    _cartDropInFlight = false;
                }
            }
        }

        /// <summary>
        /// The one creator call the cart's drop makes, and the game's own answer when its checkbox will not
        /// take the carried kind. `Wheel.Use` asks `CreatePlant.SetPlant` with the free flag off and, when
        /// nothing comes back, returns before it destroys its preview, forgets `thePlantType` and starts the
        /// cooldown: the picture stays in the cart and the cart never recharges (E65). The growth body (E56)
        /// is the game's own way to put a kind on a cell its checkbox refuses - the same creator call with
        /// the free flag on - so the flag is lent to that call only, and only for this end's own drop of the
        /// carried type on a cell the room says holds no other plant.
        /// </summary>
        [HarmonyPatch(typeof(CreatePlant), "SetPlant")]
        public static class CartDropDoor
        {
            private static void Prefix(int newColumn, int newRow, PlantType theSeedType,
                                       ref bool isFreeSet)
            {
                try
                {
                    if (!_cartDropInFlight || _applying || isFreeSet || !TheRoomDecides())
                    {
                        return;
                    }

                    if ((int)theSeedType != _carriedType)
                    {
                        return;
                    }

                    NetEntity busy = _room.Entities.PlantAt(newColumn, newRow);
                    if (busy != null && busy.NetId != _carriedNetId)
                    {
                        return;
                    }

                    Il2Cpp.CreatePlant creator = Il2Cpp.CreatePlant.Instance;
                    if (!ApiBase.Present(creator) || creator.CheckBox(newColumn, newRow, theSeedType))
                    {
                        return;
                    }

                    isFreeSet = true;

                    Log.Info("mp", "the cart's drop of plant #" + _carriedNetId + " (type " + _carriedType
                        + ") found the game's checkbox refusing cell " + newColumn + "," + newRow
                        + " while the room has no other plant there; the creator runs with the free flag,"
                        + " as the growth body does " + ApiBase.FrameTag());
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the cart's drop could not ask the game's checkbox: "
                        + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        [HarmonyPatch(typeof(Mouse), "TryToPickPlant")]
        public static class PickDoor
        {
            private static void Postfix()
            {
                try
                {
                    RememberGlovePlant();
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "remembering the glove's plant threw: " + e.GetType().Name
                        + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Mouse), "MoveZombie")]
        public static class ZombieDoor
        {
            private static void Postfix(object[] __args)
            {
                try
                {
                    if (__args == null || __args.Length < 3 || _room == null || !_room.RoomDecides
                        || _applying || Entities.Board() == null || !GloveActive())
                    {
                        return;
                    }

                    Il2Cpp.Zombie zombie = __args[0] as Il2Cpp.Zombie;
                    if (!ApiBase.Present(zombie))
                    {
                        return;
                    }

                    float x = (float)__args[1];
                    int row = (int)__args[2];

                    NetEntity record = _room.Entities.ByInstanceId(zombie.GetInstanceID());
                    if (record == null || record.Kind != NetKind.Zombie)
                    {
                        return;
                    }

                    ZombieMovePayload move = new ZombieMovePayload
                    {
                        NetId = record.NetId,
                        X = x,
                        Row = row
                    };

                    if (_room.IsHost)
                    {
                        _room.BroadcastGameplay(MsgAction.ZombieMove, move);
                        ZombieSync.ForcePush();
                        Log.Info("mp", "the host's glove moved zombie #" + record.NetId + " to x " + x
                            + ", row " + row + " " + ApiBase.FrameTag());
                    }
                    else
                    {
                        _room.SendToHost(MsgAction.ZombieMove, move);
                        Log.Info("mp", "the glove moved zombie #" + record.NetId + " to x " + x
                            + ", row " + row + "; the host is told " + ApiBase.FrameTag());
                    }
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the glove's zombie sync threw: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }
    }
}
