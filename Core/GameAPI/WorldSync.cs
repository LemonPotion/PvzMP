using System;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The gameplay half of a room, on the game's side: the local player's intent leaves as a request,
    /// and the only thing that puts anything on the lawn is the host saying so. The host runs the game's
    /// own creator once and tells every peer about it; a peer runs the same creator when that frame
    /// arrives. Nothing here decides a rule the native API can decide - validity is the game's
    /// `CheckBox`, the plant type is the game's card, and the sun that pays for it is the game's own
    /// counter, pushed as an absolute value so a peer's copy of the number heals itself.
    ///
    /// This class calls the facade directly and knows no abstraction over the world: what it cannot do
    /// without the game is exactly what the facade already wraps.
    /// </summary>
    public static class WorldSync
    {
        public const string Capability = "mp-placement";

        /// <summary>Off only when the shovel itself could not be gated; the dig then stays with the game.</summary>
        public const string ShovelCapability = "mp-shovel";

        /// <summary>The int value of Plant.DieReason.ByShovel in this build (dump.cs:29928).</summary>
        public const int DiedByShovel = 7;

        /// <summary>
        /// The int value of Plant.DieReason.ByLevelUp in this build, which is how the game's own fusion
        /// takes a plant away. A peer repeats the fusion by dying with the reason the host's lawn used.
        /// Proof: ref/EVIDENCE.md E12.
        /// </summary>
        public const int FusedAway = 4;

        private static MpSession _room;

        /// <summary>The placement this end asked for and has not seen answered; one request per cell at most.</summary>
        private static PlacePlantRequest _pending;

        private static int _pendingSince;

        /// <summary>
        /// The card the outstanding request was clicked with. It is spent only once the host's spawn
        /// for that click is on this lawn, because a refused click must not start a recharge the game
        /// would not have started either. Proof: ref/EVIDENCE.md E15.
        /// </summary>
        private static Il2Cpp.CardUI _pendingCard;

        /// <summary>The plant this end asked to dig and has not seen leave; 0 is nothing asked.</summary>
        private static int _digAsked;

        private static int _digAskedSince;

        /// <summary>
        /// The host's own lawn before its shovel was let through. The tool cuts its own lawn, so what it
        /// took is measured after it ran and never guessed before: a cell can hold two plants and the
        /// game's hit test, not the mod, decides which one a tool takes.
        /// </summary>
        private static LawnPlant[] _shovelBefore;

        /// <summary>
        /// How long an identical repeat is treated as the same wish rather than a new one. Long enough to
        /// cover a round trip, short enough that a lost answer never leaves the player unable to plant.
        /// </summary>
        public const int RepeatAskingMillis = 2000;

        /// <summary>A price above any card in this build is a broken peer, not a planting.</summary>
        public const int MaxClaimedCost = 100000;

        /// <summary>Told by the boot: which room to talk to, and that the native placement is now gated.</summary>
        public static void Attach(MpSession room)
        {
            _room = room;
            room.GameplayHandler = HandleFrame;
            MouseGate.Install();
            ShovelGate.Install();
            MergeGate.Install();
            CoinGate.Install();
            FertilizeGate.Install();

            Log.Info("mp", "the world is gated on the room: placement and shovel ask the host from now on"
                + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the merge watch. The room now copies a fusion by measuring the lawn around the game's
        /// own creator call, but the dump still cannot say which of the game's click paths a player walked,
        /// so this records that - and the cell and card a click used - while the room decides.
        /// Proof: ref/EVIDENCE.md E12.
        /// </summary>
        public static void DescribeMergeCall(string what)
        {
            if (_room == null || !_room.RoomDecides)
            {
                return;
            }

            Log.Info("mp", what + " while the room decides: cell " + Cursor.ReadColumn() + ","
                + Cursor.ReadRow() + " held type " + Cursor.ReadHeldCardPlantType() + ", plants under the"
                + " pointer " + Cursor.ReadPlantInstanceIdsUnderCursor().Length + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the gate before the game places anything. True means the local click was turned into
        /// a request and the native placement must not run - which is the whole point of the detour.
        /// </summary>
        public static bool TryCapturePlacement()
        {
            if (_room == null || !_room.RoomDecides || ApiBase.IsBroken(Capability))
            {
                return false;
            }

            int plantType = Cursor.ReadHeldCardPlantType();
            if (plantType == Cursor.NoCard)
            {
                Log.Info("mp", "placement asked with nothing held, the game carries on " + ApiBase.FrameTag());
                return false;
            }

            Il2Cpp.CardUI card = CardUse.Held();

            if (!ApiBase.Present(card))
            {
                // A type without a card behind it is what a broken cursor read looks like, and placing it
                // would grow a plant at cell 0,0 that nobody clicked: the click is let through as the
                // game's own nothing (E61).
                Log.Info("mp", "placement asked for type " + plantType + " cell " + Cursor.ReadColumn()
                    + "," + Cursor.ReadRow() + " with no card behind it, the game carries on "
                    + ApiBase.FrameTag());

                return false;
            }

            if (CardUse.Recharging(card))
            {
                // The native click refuses this card at its own recharge check and changes nothing, so
                // the click is let through rather than swallowed: the game is the one saying no.
                Log.Info("mp", "the card of type " + plantType + " is still recharging (CD " + card.CD + "/"
                    + card.fullCD + "), the game refuses this click " + ApiBase.FrameTag());

                return false;
            }

            PlacePlantRequest request = new PlacePlantRequest
            {
                PlantType = plantType,
                Column = Cursor.ReadColumn(),
                Row = Cursor.ReadRow(),

                // The card on the mouse belongs to this end's own bank; the host may not have that card
                // at all, so its price has to travel with the click or nothing can be charged. The held
                // card is the one being spent, so it is also the one whose price is right: a duplicate
                // slot costs more than the first, and the bank's first card would undercharge it.
                Cost = card.theSeedCost
            };

            Log.Info("mp", "placement asked for type " + plantType + " cell " + Cell(request)
                + " cost " + request.Cost + " " + ApiBase.FrameTag());

            if (Claimed(card))
            {
                Log.Info("mp", "the card on the mouse is already being spent on cell " + Cell(_pending)
                    + ", not asking for another one");

                return true;
            }

            if (_pending != null && Same(request, _pending)
                && Environment.TickCount - _pendingSince < RepeatAskingMillis)
            {
                Log.Info("mp", "still waiting for the host on cell " + Cell(request) + ", not asking twice");
                return true;
            }

            if (_room.IsHost)
            {
                Grant(_room.LocalPeerId, request, card);
                return true;
            }

            _pending = request;
            _pendingSince = Environment.TickCount;
            _pendingCard = card;

            if (!_room.SendToHost(MsgAction.PlacePlantRequest, request))
            {
                _pending = null;
                _pendingCard = null;
                Log.Warn("mp", "the placement request for cell " + Cell(request) + " could not leave this end");
            }

            return true;
        }

        /// <summary>
        /// A level change takes the lawn - and with it every cell and plant this end asked about - away, so
        /// a wish is dropped rather than answered into a board that no longer exists.
        /// </summary>
        public static void ForgetWishes()
        {
            if (_pending != null)
            {
                Log.Info("mp", "the placement asked for cell " + Cell(_pending) + " is forgotten with the lawn");
                _pending = null;
                _pendingSince = 0;
                _pendingCard = null;
            }

            if (_digAsked != 0)
            {
                Log.Info("mp", "the dig asked for plant #" + _digAsked + " is forgotten with the lawn");
                _digAsked = 0;
                _digAskedSince = 0;
            }
        }

        /// <summary>
        /// Asked by the shovel's dig before the game cuts. True means the room is deciding about this plant
        /// and the native tool must not cut the hole - the peer path, where the host has to answer first.
        /// The host's own shovel is always let through, because its cut is measured right afterwards.
        /// The selection that picks the tool up is not gated: the game keeps it (ref/EVIDENCE.md E14).
        /// </summary>
        public static bool TryCaptureShovel()
        {
            if (_room == null || !_room.RoomDecides || ApiBase.IsBroken(ShovelCapability))
            {
                return false;
            }

            if (_room.IsHost)
            {
                // The host's own shovel is the game's to run, and the lawn is read right before it so its
                // postfix can only name what it did. Nothing here picks the plant: that is the tool's own
                // hit test, the only answer every lawn will agree on.
                _shovelBefore = LawnSnapshot();

                return false;
            }

            if (!DecideShovel())
            {
                return false;
            }

            ReturnShovel();

            return true;
        }

        /// <summary>
        /// The stopped dig never reaches its own first two calls, which put the tool back in its place and
        /// clear it from the mouse (ref/EVIDENCE.md E14b). They are the game's own methods, so a peer's
        /// shovel ends up exactly where it would after a dig it was allowed to make.
        /// </summary>
        private static void ReturnShovel()
        {
            ApiBase.TryCall("Shovel.PutDown", () =>
            {
                Il2Cpp.Shovel shovel = Il2Cpp.Shovel.Instance;
                if (ApiBase.Present(shovel))
                {
                    shovel.PutDown();
                }
            });

            ApiBase.TryCall("Mouse.ClearItemOnMouse", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (ApiBase.Present(mouse))
                {
                    mouse.ClearItemOnMouse(false);
                }
            });
        }

        /// <summary>
        /// Asked by the host's own dig. Only that dig cuts the lawn that owns the room, and the snapshot is
        /// from just before it, so the hole is named from the measured difference. A peer has no snapshot
        /// here: its request is answered by the host, and the answer arrives as a despawn.
        /// </summary>
        public static void AfterShovel()
        {
            LawnPlant[] before = _shovelBefore;
            _shovelBefore = null;

            if (before == null || _room == null || !_room.RoomDecides)
            {
                return;
            }

            LawnChange change = Measure(before);
            if (change == null)
            {
                Log.Warn("mp", "the shovel ran but this lawn could not be read afterwards");
                return;
            }

            if (change.IsEmpty)
            {
                return;
            }

            Publish("shovel", DiedByShovel, _room.LocalPeerId, change);
            PushSun();
        }

        /// <summary>
        /// A peer's shovel: the plant the room grew under the tool, in the game's own hit-test order,
        /// with the hovered cell as the fallback for the frames where the hit test names nothing.
        /// </summary>
        private static bool DecideShovel()
        {
            int[] under = Cursor.ReadPlantInstanceIdsUnderCursor();
            NetEntity target = ShovelRules.TrackedPlant(_room.Entities, under);

            if (target == null && (under == null || under.Length == 0))
            {
                // The game's own hit test answers with no plant on some frames while a tool points at a
                // busy cell. Falling back to the cell keeps that click in the room instead of cutting a
                // hole on this lawn only; the host still names the plant it takes away on every lawn.
                int column = Cursor.ReadColumn();
                int row = Cursor.ReadRow();
                target = ShovelRules.TrackedPlantAt(_room.Entities, column, row);

                if (target != null)
                {
                    Log.Info("mp", "the shovel's hit test named no plant, but cell " + column + "," + row
                        + " holds #" + target.NetId + ", so the room is asked about that plant"
                        + " " + ApiBase.FrameTag());
                }
            }

            if (target == null)
            {
                Log.Info("mp", "the shovel is aimed at something the room did not grow, the game digs it alone"
                    + " " + ApiBase.FrameTag());

                return false;
            }

            return AskToDig(target);
        }

        /// <summary>A peer's wish to dig: the host answers it, and until it does the hole stays undug.</summary>
        private static bool AskToDig(NetEntity target)
        {
            if (_digAsked == target.NetId
                && Environment.TickCount - _digAskedSince < RepeatAskingMillis)
            {
                Log.Info("mp", "still waiting for the host on plant #" + target.NetId + ", not asking twice");
                return true;
            }

            _digAsked = target.NetId;
            _digAskedSince = Environment.TickCount;

            RemovePlantRequest request = new RemovePlantRequest { NetId = target.NetId };

            if (!_room.SendToHost(MsgAction.RemovePlantRequest, request))
            {
                _digAsked = 0;
                Log.Warn("mp", "the request to dig plant #" + target.NetId + " could not leave this end");
            }

            return true;
        }

        private static void HandleFrame(int peerId, DecodedFrame frame)
        {
            try
            {
                switch (frame.Action)
                {
                    case MsgAction.PlacePlantRequest:
                        if (IAmTheHost(peerId, frame))
                        {
                            Grant(peerId, Protocol.Payload<PlacePlantRequest>(frame), null);
                        }

                        break;

                    case MsgAction.RemovePlantRequest:
                        if (IAmTheHost(peerId, frame))
                        {
                            ApplyDigRequest(peerId, Protocol.Payload<RemovePlantRequest>(frame));
                        }

                        break;

                    case MsgAction.SpawnPlant:
                        if (TheHostSaidIt(peerId, frame))
                        {
                            ApplySpawn(Protocol.Payload<SpawnPlantPayload>(frame));
                        }

                        break;

                    case MsgAction.DespawnPlant:
                        if (TheHostSaidIt(peerId, frame))
                        {
                            ApplyDespawn(Protocol.Payload<DespawnPlantPayload>(frame));
                        }

                        break;

                    case MsgAction.RequestDenied:
                        if (TheHostSaidIt(peerId, frame))
                        {
                            ApplyDenial(Protocol.Payload<DenyPayload>(frame));
                        }

                        break;

                    case MsgAction.PlantMove:
                    case MsgAction.PlantSnapshot:
                    case MsgAction.ZombieMove:
                    case MsgAction.PlantLift:
                        PlantSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.TrophySpawn:
                    case MsgAction.TrophyCollect:
                        TrophySync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.GraveLayout:
                        GraveSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.Fertilized:
                    case MsgAction.FertilizeGrew:
                        FertilizeSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.PauseRequest:
                    case MsgAction.PauseState:
                        PauseSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.SpawnZombie:
                    case MsgAction.DespawnZombie:
                    case MsgAction.ZombieState:
                    case MsgAction.WaveState:
                    case MsgAction.MowerStart:
                        ZombieSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.PlantPickRequest:
                    case MsgAction.PlantPick:
                    case MsgAction.PlantFree:
                    case MsgAction.PlantDenied:
                        PlantSelect.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.SlowRequest:
                    case MsgAction.SlowState:
                        SlowSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.RewardClaim:
                        RewardSync.HandleFrame(peerId, frame);
                        break;

                    case MsgAction.Effect:
                        EffectSync.HandleFrame(peerId, frame);
                        break;

                    default:
                        LevelSync.HandleFrame(peerId, frame);
                        break;
                }
            }
            catch (ProtocolException e)
            {
                Log.Warn("mp", "dropping " + frame.Name + " from peer " + peerId + ": " + e.Message);
            }
        }

        /// <summary>
        /// A request about the lawn only means something to the end that owns the decision. A peer hearing
        /// one has a host that is confused, and answering it would be two ends planting the same cell.
        /// </summary>
        private static bool IAmTheHost(int peerId, DecodedFrame frame)
        {
            if (_room.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action + " to this end: only the host"
                + " decides what is on the lawn");

            return false;
        }

        /// <summary>
        /// What exists on the lawn is the host's sentence and nobody else's. A peer that reports a plant
        /// appearing or gone is a peer that has forgotten who decides, and listening to it is how two ends
        /// end up with two different lawns.
        /// </summary>
        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action + ": only the host says what is on"
                + " the lawn");

            return false;
        }

        /// <summary>
        /// The host's authority in one call: check with the game, run the game's own creator, then tell
        /// the room what the lawn looks like now. The creator is not asked what it did - CreatePlant.SetPlant
        /// runs the fusion itself and its return value is not a report - so the lawn is measured before and
        /// after and the difference is what travels. A busy cell is therefore not a refusal any more: the
        /// game decides whether two plants fuse, stack or refuse, and the room only repeats that answer.
        /// The host's own click pays its card here as well; a peer's card is spent on the peer's lawn when
        /// the spawn arrives. Proof: ref/EVIDENCE.md E12, E15.
        /// </summary>
        private static void Grant(int peerId, PlacePlantRequest request, Il2Cpp.CardUI card)
        {
            int bankCost = CardBank.ReadCost(request.PlantType);
            int cost = request.Cost > 0 ? request.Cost : bankCost;

            Log.Info("mp", "place request from peer " + peerId + ": type=" + request.PlantType
                + " cell=" + Cell(request) + " cost=" + cost
                + (request.Cost > 0 ? " (from the clicking end)" : " (from this bank)")
                + " " + ApiBase.FrameTag());

            if (cost > MaxClaimedCost)
            {
                Refuse(peerId, MsgAction.PlacePlantRequest,
                    "the request prices the plant at " + cost + ", the room does not accept that");
                return;
            }

            string reason = Refusal(peerId, request, cost);
            if (reason != null)
            {
                Refuse(peerId, MsgAction.PlacePlantRequest, reason);
                return;
            }

            LawnPlant[] before = LawnSnapshot();
            if (before == null)
            {
                Refuse(peerId, MsgAction.PlacePlantRequest, "this end cannot read its own lawn");
                return;
            }

            bool theClickerIsHere = peerId == _room.LocalPeerId;
            int sunBefore = SunPool.ReadSun();

            int instanceId = theClickerIsHere
                ? Plants.PlantFromCard(request.Column, request.Row, request.PlantType)
                : Plants.Spawn(request.Column, request.Row, request.PlantType);

            LawnChange change = Measure(before);

            if (change == null)
            {
                Refuse(peerId, MsgAction.PlacePlantRequest, "this end cannot read its own lawn");
                return;
            }

            if (change.IsEmpty && theClickerIsHere)
            {
                // The card path gave nothing. The retry runs only because the lawn did not move, so a
                // fusion that returned null is never planted a second time.
                LawnPlant[] retryBefore = LawnSnapshot();

                if (retryBefore == null)
                {
                    Refuse(peerId, MsgAction.PlacePlantRequest, "this end cannot read its own lawn");
                    return;
                }

                Log.Warn("mp", "the game's card path gave nothing for cell " + Cell(request)
                    + ", the plain creator runs instead so this lawn still gets the plant the room agreed to");

                instanceId = Plants.Spawn(request.Column, request.Row, request.PlantType);
                change = Measure(retryBefore);
            }

            if (change == null)
            {
                Refuse(peerId, MsgAction.PlacePlantRequest, "this end cannot read its own lawn");
                return;
            }

            if (change.IsEmpty)
            {
                if (instanceId != 0 && Plants.TryRead(instanceId, out PlantState made))
                {
                    Log.Warn("mp", "the creator made plant #" + made.InstanceId + " on cell "
                        + made.Column + "," + made.Row + " but the lawn snapshot did not see it;"
                        + " the room repeats the creator's own word so the lawns do not differ");

                    change = LawnDiff.OnlyNew(Mark(made));
                }
                else
                {
                    Refuse(peerId, MsgAction.PlacePlantRequest, "the game kept the lawn unchanged");
                    return;
                }
            }

            PayThePool(sunBefore, cost);

            if (theClickerIsHere)
            {
                int plantedType = change.Appeared.Length > 0
                    ? change.Appeared[0].PlantType
                    : request.PlantType;

                CardUse.Spend(card, plantedType);
            }

            Publish("fusion", FusedAway, peerId, change);

            PushSun();
        }

        /// <summary>
        /// Every plant on this lawn, in the types the pure diff reads. Null when the lawn cannot be read,
        /// which the caller has to treat as "nothing may be claimed" rather than "the lawn is empty".
        /// </summary>
        private static LawnPlant[] LawnSnapshot()
        {
            return Marks(Plants.ReadAll());
        }

        /// <summary>
        /// A lawn this end read, in the types the pure rules read: the object, its kind and its cell, with
        /// no number that belongs to this machine. Asked for by every watch that has to say what one call
        /// did to the lawn, so the answer the rules compare is always built the same way.
        /// </summary>
        public static LawnPlant[] Marks(PlantState[] plants)
        {
            if (plants == null)
            {
                return null;
            }

            LawnPlant[] marks = new LawnPlant[plants.Length];

            for (int i = 0; i < plants.Length; i++)
            {
                marks[i] = Mark(plants[i]);
            }

            return marks;
        }

        private static LawnChange Measure(LawnPlant[] before)
        {
            LawnPlant[] after = LawnSnapshot();

            return after == null ? null : LawnDiff.Of(_room.Entities, before, after);
        }

        private static LawnPlant Mark(PlantState plant)
        {
            return new LawnPlant
            {
                InstanceId = plant.InstanceId,
                PlantType = plant.PlantType,
                Column = plant.Column,
                Row = plant.Row
            };
        }

        /// <summary>
        /// The measured change, said to the room: every plant the native call took first, then every
        /// plant it left behind. Net ids are minted here and nowhere else, and the ordering is safe
        /// because the transport keeps one reliable queue per peer: a despawn that empties a cell always
        /// arrives before the spawn that fills it, on every lawn, including the clicker's.
        /// </summary>
        private static void Publish(string cause, int reason, int ownerPeer, LawnChange change)
        {
            foreach (int netId in change.GoneNetIds)
            {
                NetEntity gone = _room.Entities.Remove(netId);
                if (gone == null)
                {
                    Log.Warn("mp", "the " + cause + " names plant #" + netId + " but this lawn has no such"
                        + " record");
                    continue;
                }

                Log.Info("mp", "plant #" + netId + " (type " + gone.Type + ", cell " + gone.Column + ","
                    + gone.Row + ") left this lawn (" + cause + ")");

                _room.BroadcastGameplay(MsgAction.DespawnPlant, new DespawnPlantPayload
                {
                    NetId = netId,
                    Reason = reason
                });
            }

            foreach (LawnPlant plant in change.Appeared)
            {
                NetEntity record = _room.Entities.Add(NetKind.Plant, ownerPeer, plant.PlantType,
                    plant.Column, plant.Row);

                _room.Entities.Bind(record.NetId, plant.InstanceId);

                Log.Info("mp", "plant #" + record.NetId + " (type " + plant.PlantType + ") appeared on cell "
                    + plant.Column + "," + plant.Row + " for peer " + ownerPeer + ", obj " + plant.InstanceId);

                _room.BroadcastGameplay(MsgAction.SpawnPlant, new SpawnPlantPayload
                {
                    NetId = record.NetId,
                    PlantType = plant.PlantType,
                    Column = plant.Column,
                    Row = plant.Row,
                    OwnerPeer = ownerPeer
                });
            }

            if (change.Unnamed > 0)
            {
                Log.Warn("mp", "the game changed " + change.Unnamed + " plant(s) the room never minted on"
                    + " this lawn; they stay a difference every peer will keep, roster: "
                    + _room.Roster.Describe());
            }
        }

        /// <summary>
        /// The shared pool is the host's number, so the host's copy has to end up smaller by exactly what
        /// the game charges. Whether the creator already spent it is not readable from the dump - the bodies
        /// are empty - so it is measured rather than assumed: the planting runs, the difference is compared
        /// with the price written on the card, and only what is still missing is taken through the game's
        /// own `UseSun`. Guessing either way would leave the room with a pool drained twice or untouched.
        /// </summary>
        private static void PayThePool(int sunBefore, int cost)
        {
            int sunAfter = SunPool.ReadSun();
            int owed = PlantingRules.StillOwed(sunBefore, sunAfter, cost);

            if (owed == 0)
            {
                Log.Info("mp", "the pool went " + sunBefore + " -> " + sunAfter + " against a cost of "
                    + cost + ": nothing left for the room to charge");

                return;
            }

            SunPool.UseSun(owed);

            Log.Info("mp", "the pool went " + sunBefore + " -> " + sunAfter + " against a cost of " + cost
                + ", so the room charges the remaining " + owed + " and the pool is " + SunPool.ReadSun());
        }

        /// <summary>Why the host says no, in the host's own words, or null when it agrees.</summary>
        private static string Refusal(int peerId, PlacePlantRequest request, int cost)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.Connected)
            {
                return "peer " + peerId + " is not in the room";
            }

            if (Entities.Board() == null)
            {
                return "this lawn is gone, nothing can be planted";
            }

            bool gameAllows = Plants.CanPlace(request.Column, request.Row, request.PlantType);

            string poor = PlantingRules.Affordance(SunPool.ReadSun(), cost);
            if (poor != null)
            {
                return poor;
            }

            if (cost == PlantingRules.UnknownCost)
            {
                Log.Info("mp", "nothing is charged for type " + request.PlantType + ": "
                    + CardBank.Describe(request.PlantType));
            }

            NetEntity busy = _room.Entities.PlantAt(request.Column, request.Row);
            if (busy != null)
            {
                // A cell that already holds a plant is how the game's own fusion starts, so a busy cell
                // is not refused: the creator runs and the lawn diff names whatever the game decided.
                // CheckBox is not a fusion signal (it also allows a plain same-type overlap), so its
                // answer here is only logged as context. Proof: ref/EVIDENCE.md E12.
                Log.Info("mp", "cell " + Cell(request) + " holds plant #" + busy.NetId + " of type "
                    + busy.Type + ", asked type " + request.PlantType + ", the game's CheckBox calls the"
                    + " cell " + (gameAllows ? "plantable" : "blocked") + ", the creator decides "
                    + ApiBase.FrameTag());

                return null;
            }

            if (!gameAllows)
            {
                return "the game refuses type " + request.PlantType + " in cell " + Cell(request);
            }

            return null;
        }

        private static void Refuse(int peerId, MsgAction requested, string reason)
        {
            Log.Warn("mp", "peer " + peerId + "'s " + requested + " refused: " + reason);

            if (peerId == _room.LocalPeerId)
            {
                // This end made the click, so nobody is going to send it a frame about its own refusal. The
                // answer the player sees is the game's: what the mod refused stays in the log, and a plant
                // the game itself will not take is said by the game's own notice line (E36).
                return;
            }

            _room.SendToPeer(peerId, MsgAction.RequestDenied, new DenyPayload
            {
                Requested = requested,
                Reason = reason
            });
        }

        /// <summary>
        /// The host's answer about one plant. Any seat may ask for any plant the room grew, and the plant
        /// leaves every lawn in the room or none of them. The host's own click is not this path: its tool
        /// cuts the lawn itself and is measured afterwards. Here the host's copy is removed through the
        /// game's own death call, then every lawn is told.
        /// </summary>
        private static bool Dig(int peerId, NetEntity target)
        {
            Log.Info("mp", "dig request from peer " + peerId + ": " + Describe(target)
                + " " + ApiBase.FrameTag());

            string reason = ShovelRules.Refusal(target);
            if (reason != null)
            {
                Refuse(peerId, MsgAction.RemovePlantRequest, reason);
                return false;
            }

            if (target.InstanceId != 0 && !Plants.Kill(target.InstanceId, DiedByShovel))
            {
                Refuse(peerId, MsgAction.RemovePlantRequest,
                    "the game would not take plant #" + target.NetId + " off this lawn");

                return false;
            }

            _room.Entities.Remove(target.NetId);

            Log.Info("mp", "plant #" + target.NetId + " dug up by peer " + peerId + ", roster: "
                + _room.Roster.Describe());

            _room.BroadcastGameplay(MsgAction.DespawnPlant, new DespawnPlantPayload
            {
                NetId = target.NetId,
                Reason = DiedByShovel
            });

            return true;
        }

        private static void ApplyDigRequest(int peerId, RemovePlantRequest request)
        {
            Dig(peerId, _room.Entities.Find(request.NetId));
        }

        /// <summary>The host saying a plant is gone. This end has no vote left to cast about it.</summary>
        private static void ApplyDespawn(DespawnPlantPayload despawn)
        {
            NetEntity record = _room.Entities.Find(despawn.NetId);
            if (record == null)
            {
                Log.Warn("mp", "plant #" + despawn.NetId + " is already gone from this lawn");
                return;
            }

            _room.Entities.Remove(despawn.NetId);

            if (record.InstanceId != 0)
            {
                Plants.Kill(record.InstanceId, despawn.Reason);
            }

            if (despawn.NetId == _digAsked)
            {
                _digAsked = 0;
                _digAskedSince = 0;
            }

            Log.Info("mp", "plant #" + despawn.NetId + " from peer " + record.OwnerPeer + " cell "
                + record.Column + "," + record.Row + " left this lawn " + ApiBase.FrameTag());
        }

        private static void ApplySpawn(SpawnPlantPayload spawn)
        {
            NetEntity record = _room.Entities.Adopt(spawn.NetId, NetKind.Plant, spawn.OwnerPeer,
                spawn.PlantType, spawn.Column, spawn.Row);

            if (record == null)
            {
                Log.Warn("mp", "spawn for #" + spawn.NetId + " already applied on this lawn");
                return;
            }

            bool theClickerIsHere = spawn.OwnerPeer == _room.LocalPeerId;

            // The host naming a plant is answered first by the plant standing on that cell: this end may
            // already have grown one there itself - from the level, from the tutorial, or out of its own
            // cart's pocket - and a second object on one cell is a difference every later layout has to
            // take down. A click this end stopped has nothing standing there, so it still reaches a creator.
            int instanceId = ExistingPlant(spawn.PlantType, spawn.Column, spawn.Row);

            if (instanceId == 0)
            {
                instanceId = theClickerIsHere
                    ? Plants.PlantFromCard(spawn.Column, spawn.Row, spawn.PlantType)
                    : Plants.Spawn(spawn.Column, spawn.Row, spawn.PlantType);
            }

            if (instanceId == 0 && theClickerIsHere)
            {
                Log.Warn("mp", "the game's card path gave nothing for plant #" + spawn.NetId
                    + ", the plain creator runs instead");

                instanceId = Plants.Spawn(spawn.Column, spawn.Row, spawn.PlantType);
            }

            if (instanceId == 0)
            {
                Log.Error("mp", "the host's plant #" + spawn.NetId + " did not appear on this lawn");
                return;
            }

            _room.Entities.Bind(record.NetId, instanceId);

            if (_pending != null && spawn.OwnerPeer == _room.LocalPeerId && Answers(_pending, spawn))
            {
                Il2Cpp.CardUI card = _pendingCard;
                _pending = null;
                _pendingCard = null;

                CardUse.Spend(card, spawn.PlantType);
            }

            Log.Info("mp", "plant #" + spawn.NetId + " from peer " + spawn.OwnerPeer + " cell "
                + spawn.Column + "," + spawn.Row + " -> obj " + instanceId + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// A plant on that cell with that type that the room has not bound yet, or 0. This is what lets a
        /// tutorial plant the client already grew answer the host's census spawn instead of duplicating it.
        /// </summary>
        private static int ExistingPlant(int plantType, int column, int row)
        {
            return ApiBase.Query("FindUnboundPlant(cell=" + column + "," + row + ",type=" + plantType + ")",
                () =>
                {
                    int found = 0;

                    Entities.ForEachPlant(plant =>
                    {
                        if (found != 0 || (int)plant.thePlantType != plantType
                            || plant.thePlantColumn != column || plant.thePlantRow != row)
                        {
                            return;
                        }

                        int instanceId = plant.GetInstanceID();
                        if (_room.Entities.ByInstanceId(instanceId) == null)
                        {
                            found = instanceId;
                        }
                    });

                    return found;
                }, Capability);
        }

        /// <summary>
        /// Asked by the sun gate before the game credits a collection. True everywhere except a peer that is
        /// in a room: that end may collect a coin for the picture, but only the host's pool is the room's
        /// number, so a peer credit would be a second counter. Outside a room it is always true, which is
        /// why a single player lawn credits exactly as the game does. Proof: ref/EVIDENCE.md E10.
        /// </summary>
        public static bool MayCreditSun()
        {
            if (_room == null || !_room.RoomDecides)
            {
                return true;
            }

            return _room.IsHost;
        }

        /// <summary>
        /// Asked by the sun gate after the game credited this lawn. Only the host's number is the room's
        /// number, so only a host credit travels: it pushes the absolute value a collection just changed,
        /// because a grant is not the only event that moves the pool.
        /// </summary>
        public static void NoteSunCredited()
        {
            if (_room == null || !_room.RoomDecides || !_room.IsHost)
            {
                return;
            }

            PushSun();
        }

        /// <summary>
        /// Every grant moves money, and the host's number is the shared one, so it travels as the game's
        /// own assign: one covered door, one frame, and a peer that ends up with the host's number rather
        /// than a number the mod computed for it. Proof: ref/EVIDENCE.md E27 - SetSun writes theSun and
        /// reads nothing, which is why an absolute value heals a drifted lawn instead of widening it.
        /// </summary>
        private static void PushSun()
        {
            int sun = SunPool.ReadSun();
            if (sun < 0)
            {
                return;
            }

            NativeCalls.Announce("Board.SetSun/1", CallArgs.None, sun);
            Log.Info("mp", "sun announced as " + sun + "/" + SunPool.ReadMaxSun());
        }

        private static void ApplyDenial(DenyPayload denial)
        {
            Log.Warn("mp", "the host refused my " + denial.Requested + ": " + denial.Reason
                + " " + ApiBase.FrameTag());

            if (denial.Requested == MsgAction.PlacePlantRequest)
            {
                _pending = null;
                _pendingCard = null;
            }

            if (denial.Requested == MsgAction.RemovePlantRequest)
            {
                _digAsked = 0;
                _digAskedSince = 0;
            }
        }

        /// <summary>
        /// Whether this card is already committed to the request in flight. A second click with the same
        /// card while its answer is on the way would otherwise buy a second plant on one recharge.
        /// </summary>
        private static bool Claimed(Il2Cpp.CardUI card)
        {
            return _pending != null
                && ApiBase.Present(card)
                && ApiBase.Present(_pendingCard)
                && _pendingCard.GetInstanceID() == card.GetInstanceID();
        }

        private static bool Same(PlacePlantRequest left, PlacePlantRequest right)
        {
            return left.PlantType == right.PlantType && left.Column == right.Column && left.Row == right.Row;
        }

        /// <summary>
        /// Whether this spawn answers the wish this end made. The cell is what has to match: a fusion
        /// usually hands back a plant of another type on the same cell, and that answer still ends the
        /// wait. A spawn the host minted for another reason on the same cell cannot happen, because a
        /// wish is only sent when its click was captured and the host answers one wish at a time.
        /// </summary>
        private static bool Answers(PlacePlantRequest request, SpawnPlantPayload spawn)
        {
            return request.Column == spawn.Column && request.Row == spawn.Row;
        }

        private static string Cell(PlacePlantRequest request)
        {
            return request.Column + "," + request.Row;
        }

        /// <summary>
        /// A plant the room knows, in the words the room uses for it. A null here is a real answer a peer
        /// can get - it named a number this lawn never grew - so the phrase has to stand on its own.
        /// </summary>
        private static string Describe(NetEntity target)
        {
            if (target == null)
            {
                return "a plant the room does not have";
            }

            return "plant #" + target.NetId + " (" + target.Kind + ") cell " + target.Column + ","
                + target.Row + " grown by peer " + target.OwnerPeer;
        }
    }
}
