using System;
using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The room's zombies, waves and mowers. Every one of those is a roll the game makes on the end that
    /// owns it: `BoardSpawner.SummonZombies` picks a random row and the game's own `Random`, so two ends
    /// that run it roll different waves; a mower starts from its own trigger; and health is simulated
    /// locally from whatever moment each end's plants fired. The host therefore runs the wave and the
    /// mower, and this class carries the result:
    ///
    /// - A peer blocks `BoardSpawner.SummonZombies`; the host records every zombie its summon created
    ///   (through a flag around that call and a postfix on `CreateZombie.SetZombie`) and sends its net id,
    ///   type, row and spawn x. The peer repeats the game's own creator, so the zombie walks by itself.
    /// - The host pushes the wave number, the maximum and the countdown as absolute values. A peer has no
    ///   native setter for those fields, so it stores the host's numbers; the countdown then reads the
    ///   same on every screen.
    /// - A peer's mowers never start from their own triggers; the host's `BoardSpawner.MowerStart`, the
    ///   one call both mower doors make, is sent by row, and the peer replays `Mower.StartMove`.
    /// - A periodic snapshot carries the current health, both armor values and the base speed with its
    ///   eight factors of every zombie the room minted; on the slower position push it also carries the
    ///   row and the position, which the peer follows through the game's own `Zombie.AdjustPosition`. The
    ///   host's own list is the death report: a bound zombie missing from it is sent as a despawn. No
    ///   death hook is installed because `Zombie.Die` is virtual and overridden by several zombie types,
    ///   where the base patch would not see them.
    /// - The same wave frame carries the host's pause-menu difficulty and game speed
    ///   (`GameAPP.config.difficulty/gameSpeed`, the fields its own sliders write), so every screen runs
    ///   the level at the host's pace.
    ///
    /// Zombies born outside a wave reach the room through the same creator postfix: whatever the host's lawn
    /// gained while the room stands in a level is minted and sent once. A grave is the case that matters -
    /// its zombie type is rolled inside the animation body that opens it, and a peer refuses that body, so
    /// the host's answer is the only one the room has (E42).
    /// </summary>
    public static class ZombieSync
    {
        public const string Capability = "mp-zombies";

        /// <summary>Off when the mower hooks could not be installed; mowers then start on every end.</summary>
        public const string MowerCapability = "mp-mowers";

        private const int WavePushMillis = 500;
        private const int ZombiePushMillis = 500;

        /// <summary>
        /// How often the host sends where a zombie really is. The gaps are bridged by the client's own
        /// smooth correction; one second keeps the residual small without sending every health frame.
        /// </summary>
        private const int PositionPushMillis = 500;
        private const int ReportMillis = 5000;
        private const int WaveReportMillis = 10000;

        /// <summary>How long a zombie the host's list stops naming may stay on this lawn before it is taken down.</summary>
        private const int HostSilenceMillis = 3000;

        /// <summary>How long an object with no room record may stay on a peer's lawn before it is taken down.</summary>
        private const int UnboundGraceMillis = 3000;

        private const int SweepMillis = 1000;

        private static MpSession _room;

        /// <summary>True only while the host's own `BoardSpawner.SummonZombies` runs.</summary>
        private static bool _hostSummoning;

        /// <summary>True only while this peer replays a mower the host started.</summary>
        private static bool _mowerApplying;

        private static int _wavePushedAt;
        private static int _zombiesPushedAt;
        private static int _positionsPushedAt;
        private static int _hostReportedAt;
        private static int _clientReportedAt;
        private static int _clientWaveReportedAt;
        private static int _lastWave = -1;
        private static int _lastClientWave = -1;
        private static int _lastDifficulty = -1;
        private static int _lastClientDifficulty = -1;
        private static float _lastGameSpeed = -1f;
        private static float _lastClientGameSpeed = -1f;
        private static bool _settingsKnown;

        /// <summary>Rows whose local mower start was refused, so the refusal is printed once per row.</summary>
        private static readonly HashSet<int> BlockedRows = new HashSet<int>();

        /// <summary>When each room zombie was last named by the host's list.</summary>
        private static readonly Dictionary<int, int> HostNamedAt = new Dictionary<int, int>();

        /// <summary>Objects on this lawn with no room record, and when each was first seen here.</summary>
        private static readonly Dictionary<int, int> UnboundSince = new Dictionary<int, int>();

        private static int _emptyListPushedAt;
        private static int _sweptAt;

        public static void Attach(MpSession room)
        {
            _room = room;
            ZombieGate.Install();

            Log.Info("mp", "the waves, the zombies and the mowers are the host's from now on "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the summon prefix. True means the native wave must not run - the peer path. The host's
        /// own call is let through with the flag that makes every zombie it creates travel.
        /// </summary>
        public static bool TryCaptureWaveSpawn(int wave)
        {
            if (!RoomDecides())
            {
                return false;
            }

            if (_room.IsHost)
            {
                _hostSummoning = true;
                return false;
            }

            Log.Info("mp", "the host summons wave " + wave + "; no zombie is rolled here "
                + ApiBase.FrameTag());

            return true;
        }

        /// <summary>Asked by the summon postfix. Always false afterwards, so a thrown body cannot stay open.</summary>
        public static void EndWaveSpawn()
        {
            _hostSummoning = false;
        }

        /// <summary>
        /// Asked by the creator's postfix. A zombie the host's wave summon just made is the room's, and so is
        /// one made by any other door on the host's lawn while the room stands in a level: the grave that
        /// delivers a wave zombie rolls its type inside an animation body no frame can name, so the only way
        /// the room learns which zombie it got is to ask the creator what came back (E42). A zombie this lawn
        /// already bound is not minted twice - the wave summon and the periodic layout both pass through here.
        /// </summary>
        public static void NoteZombieCreated(Il2Cpp.Zombie zombie, int row, int zombieType, float spawnX)
        {
            if (_room == null || !_room.IsHost || !_room.RoomDecides || ApiBase.IsBroken(Capability))
            {
                return;
            }

            if (!_hostSummoning && !LevelSync.TheRoomIsInALevel)
            {
                return;
            }

            if (!ApiBase.Present(zombie))
            {
                Log.Warn("mp", "a wave summon returned no zombie object, the room is not told about it");
                return;
            }

            if (_room.Entities.ByInstanceId(zombie.GetInstanceID()) != null)
            {
                return;
            }

            NetEntity record = _room.Entities.Add(NetKind.Zombie, _room.LocalPeerId, zombieType, -1, row);
            _room.Entities.Bind(record.NetId, zombie.GetInstanceID());

            Log.Info("mp", "zombie #" + record.NetId + " (type " + zombieType + ", row " + row
                + ", x " + spawnX + ") appeared on this lawn for the room, obj " + zombie.GetInstanceID()
                + " " + ApiBase.FrameTag());

            _room.BroadcastGameplay(MsgAction.SpawnZombie, new SpawnZombiePayload
            {
                NetId = record.NetId,
                ZombieType = zombieType,
                Row = row,
                SpawnX = spawnX,
                Wave = Waves.ReadWave(),
                OriginSpeed = zombie.theOriginSpeed
            });
        }

        /// <summary>
        /// Asked by the mower prefixes. True means this mower must not start here: a peer does not decide
        /// when the room's mowers run. The host's own triggers are let through. A replay is let through by
        /// the flag, so the mower the host named can still start on the peer's lawn.
        /// </summary>
        public static bool TryCaptureMowerStart(Il2Cpp.Mower mower)
        {
            int row = ApiBase.Present(mower) ? mower.theMowerRow : -1;

            return BlockMowerStart(row);
        }

        /// <summary>The same refusal for `BoardSpawner.MowerStart`, which every mower door ends in.</summary>
        public static bool TryCaptureMowerStart(int row)
        {
            return BlockMowerStart(row);
        }

        /// <summary>
        /// True while this peer plays the mower the host started. What its lawn shows while that runs is the
        /// room's own echo, so the effect relay keeps it to itself instead of telling the room about it.
        /// </summary>
        public static bool ReplayingAMower
        {
            get { return _mowerApplying; }
        }

        private static bool BlockMowerStart(int row)
        {
            if (_mowerApplying || !RoomDecides() || _room.IsHost || ApiBase.IsBroken(MowerCapability))
            {
                return false;
            }

            if (BlockedRows.Add(row))
            {
                Log.Info("mp", "the mower of row " + row + " is not started here: the host starts the"
                    + " room's mowers " + ApiBase.FrameTag());
            }

            return true;
        }

        /// <summary>Asked by `BoardSpawner.MowerStart` after the host's mower really started.</summary>
        public static void NoteMowerStarted(int row)
        {
            if (_room == null || !_room.IsHost || !_room.RoomDecides || ApiBase.IsBroken(MowerCapability))
            {
                return;
            }

            Log.Info("mp", "the mower of row " + row + " started on this lawn, the room is told "
                + ApiBase.FrameTag());

            _room.BroadcastGameplay(MsgAction.MowerStart, new MowerStartPayload { Row = row });
        }

        /// <summary>A new level takes the old wave clock and every wish of this class with it.</summary>
        /// <summary>
        /// Asks the next tick to send health and position now. Used after a glove move, where the
        /// regular three-second position push would otherwise leave the moved zombie behind for a while.
        /// </summary>
        public static void ForcePush()
        {
            _zombiesPushedAt = Environment.TickCount - ZombiePushMillis;
            _positionsPushedAt = Environment.TickCount - PositionPushMillis;
        }

        public static void ForgetTheLawn()
        {
            _hostSummoning = false;
            _mowerApplying = false;
            _lastWave = -1;
            _lastClientWave = -1;
            _wavePushedAt = 0;
            _zombiesPushedAt = 0;
            _positionsPushedAt = 0;
            _lastDifficulty = -1;
            _lastClientDifficulty = -1;
            _lastGameSpeed = -1f;
            _lastClientGameSpeed = -1f;
            _settingsKnown = false;
            BlockedRows.Clear();
            HostNamedAt.Clear();
            UnboundSince.Clear();
            _emptyListPushedAt = 0;
            _sweptAt = 0;
            Zombies.ForgetTheLawn();
        }

        /// <summary>Run once per frame: the host pushes the clock, the state and deaths; a peer sweeps its lawn.</summary>
        public static void Tick()
        {
            if (_room == null || !_room.RoomDecides || ApiBase.IsBroken(Capability))
            {
                return;
            }

            int now = Environment.TickCount;

            if (!_room.IsHost)
            {
                SweepThisLawn(now);
                return;
            }

            int wave;
            int maxWave;
            float timeUntilNextWave;

            if (Waves.ReadWaveClock(out wave, out maxWave, out timeUntilNextWave))
            {
                bool changed = wave != _lastWave;

                if (changed || Due(now, _wavePushedAt, WavePushMillis))
                {
                    _wavePushedAt = now;
                    _lastWave = wave;

                    int difficulty;
                    float gameSpeed;

                    bool settingsRead = LevelSettings.Read(out difficulty, out gameSpeed);

                    if (settingsRead)
                    {
                        _settingsKnown = true;
                    }
                    else
                    {
                        difficulty = _lastDifficulty;
                        gameSpeed = _lastGameSpeed;
                    }

                    bool settingsChanged = settingsRead
                        && (difficulty != _lastDifficulty || gameSpeed != _lastGameSpeed);

                    _lastDifficulty = difficulty;
                    _lastGameSpeed = gameSpeed;

                    _room.BroadcastGameplay(MsgAction.WaveState, new WaveStatePayload
                    {
                        Wave = wave,
                        MaxWave = maxWave,
                        TimeUntilNextWave = timeUntilNextWave,
                        HasSettings = _settingsKnown,
                        Difficulty = difficulty,
                        GameSpeed = gameSpeed
                    });

                    if (changed)
                    {
                        Log.Info("mp", "the wave clock is at " + wave + "/" + maxWave + ", next in "
                            + timeUntilNextWave + "s, the room is told " + ApiBase.FrameTag());
                    }

                    if (settingsChanged)
                    {
                        Log.Info("mp", "difficulty " + difficulty + ", game speed x" + gameSpeed
                            + ", the room is told " + ApiBase.FrameTag());
                    }
                }
            }

            if (Due(now, _zombiesPushedAt, ZombiePushMillis))
            {
                _zombiesPushedAt = now;

                bool withPositions = Due(now, _positionsPushedAt, PositionPushMillis);
                if (withPositions)
                {
                    _positionsPushedAt = now;
                }

                PushZombieState(now, withPositions);
            }
        }

        /// <summary>
        /// The client's correction runs after the game's own zombie update, so the position the player
        /// sees this frame is the corrected one and the game's walk cannot overwrite it before the next.
        /// </summary>
        public static void LateTick()
        {
            if (_room == null || !_room.RoomDecides || ApiBase.IsBroken(Capability) || _room.IsHost)
            {
                return;
            }

            Zombies.Correct();
        }

        /// <summary>
        /// Every zombie the room minted, as the host's lawn has it now. A bound zombie the host's list no
        /// longer holds has left the lawn - dead, run over, or taken by the level - and is sent as a despawn
        /// before the state, so a peer removes the object instead of leaving a body the host does not have.
        /// Health and the speed factors ride every push; the position and row ride only the slower position
        /// push, which is what puts a drifted walk back where the host has it.
        /// </summary>
        private static void PushZombieState(int now, bool withPositions)
        {
            ZombieState[] live = Zombies.ReadAll();
            if (live == null)
            {
                return;
            }

            var byInstance = new Dictionary<int, ZombieState>();
            for (int i = 0; i < live.Length; i++)
            {
                byInstance[live[i].InstanceId] = live[i];
            }

            var health = new List<ZombieHealth>();
            var gone = new List<NetEntity>();

            foreach (NetEntity entry in _room.Entities.Entries)
            {
                if (entry.Kind != NetKind.Zombie || entry.InstanceId == 0)
                {
                    continue;
                }

                ZombieState state;
                if (byInstance.TryGetValue(entry.InstanceId, out state))
                {
                    if (!state.Alive || state.Health <= 0)
                    {
                        gone.Add(entry);
                        continue;
                    }

                    health.Add(new ZombieHealth
                    {
                        NetId = entry.NetId,
                        Health = state.Health,
                        Alive = state.Alive,
                        MindControlled = state.MindControlled,
                        FirstArmor = state.FirstArmorHealth,
                        SecondArmor = state.SecondArmorHealth,
                        OriginSpeed = state.OriginSpeed,
                        FreezeSpeed = state.FreezeSpeed,
                        ColdSpeed = state.ColdSpeed,
                        ButterSpeed = state.ButterSpeed,
                        KelpSpeed = state.KelpSpeed,
                        GarlicSpeed = state.GarlicSpeed,
                        UniqueSpeed = state.UniqueSpeed,
                        HoleSpeed = state.HoleSpeed,
                        MaLiangSpeed = state.MaLiangSpeed,
                        HasPosition = withPositions,
                        X = withPositions ? state.X : 0f,
                        Y = withPositions ? state.Y : 0f,
                        Row = state.Row
                    });
                }
                else
                {
                    gone.Add(entry);
                }
            }

            foreach (NetEntity entry in gone)
            {
                _room.Entities.Remove(entry.NetId);

                Log.Info("mp", "zombie #" + entry.NetId + " (type " + entry.Type + ", row " + entry.Row
                    + ") is gone from this lawn, the room is told " + ApiBase.FrameTag());

                _room.BroadcastGameplay(MsgAction.DespawnZombie, new DespawnZombiePayload
                {
                    NetId = entry.NetId,
                    Reason = 0
                });
            }

            if (health.Count == 0 && gone.Count == 0)
            {
                // Nothing to name, but a peer may still hold a copy it was never told to drop: the empty
                // list is the host's word that its lawn has no room zombie, and it repeats that word now
                // and then so an orphaned copy can finish its list against this end's.
                if (LevelSync.TheRoomIsInALevel && Due(now, _emptyListPushedAt, ReportMillis))
                {
                    _emptyListPushedAt = now;

                    _room.BroadcastGameplay(MsgAction.ZombieState, new ZombieStatePayload
                    {
                        Zombies = health.ToArray()
                    });
                }

                return;
            }

            _emptyListPushedAt = 0;

            _room.BroadcastGameplay(MsgAction.ZombieState, new ZombieStatePayload
            {
                Zombies = health.ToArray()
            });

            if (Due(now, _hostReportedAt, ReportMillis))
            {
                _hostReportedAt = now;

                long total = 0;
                for (int i = 0; i < health.Count; i++)
                {
                    total += health[i].Health;
                }

                Log.Info("mp", "zombie state pushed: " + health.Count + " room zombie(s), total hp "
                    + total + " " + ApiBase.FrameTag());
            }
        }

        /// <summary>Where the zombie frames arrive; WorldSync forwards what is neither a plant nor the pool.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.SpawnZombie:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplySpawn(peerId, Protocol.Payload<SpawnZombiePayload>(frame));
                    }

                    break;

                case MsgAction.DespawnZombie:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyDespawn(Protocol.Payload<DespawnZombiePayload>(frame));
                    }

                    break;

                case MsgAction.ZombieState:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyState(Protocol.Payload<ZombieStatePayload>(frame));
                    }

                    break;

                case MsgAction.WaveState:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyWave(Protocol.Payload<WaveStatePayload>(frame));
                    }

                    break;

                case MsgAction.MowerStart:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyMower(Protocol.Payload<MowerStartPayload>(frame));
                    }

                    break;

                default:
                    Log.Warn("mp", "no one handles " + frame.Name + " from peer " + peerId + " yet");
                    break;
            }
        }

        /// <summary>The host's zombie, made by the game's own creator so it walks, animates and shoots.</summary>
        private static void ApplySpawn(int peerId, SpawnZombiePayload spawn)
        {
            NetEntity record = _room.Entities.Adopt(spawn.NetId, NetKind.Zombie, peerId,
                spawn.ZombieType, -1, spawn.Row);

            if (record == null)
            {
                Log.Warn("mp", "zombie #" + spawn.NetId + " is already on this lawn");
                return;
            }

            int instanceId = Zombies.Spawn(spawn.Row, spawn.ZombieType, spawn.SpawnX);

            if (instanceId == 0)
            {
                _room.Entities.Remove(record.NetId);
                Log.Error("mp", "the host's zombie #" + spawn.NetId + " did not appear on this lawn");
                return;
            }

            _room.Entities.Bind(record.NetId, instanceId);

            // This lawn's creator rolled its own walk; the host's roll is the room's. The write and the
            // game's own recompute of the walk happen before this frame is done, so the copy steps out
            // with the host's speed rather than with its own until the next state push (E59).
            ZombieState own;
            float rolled = Zombies.TryRead(instanceId, out own) ? own.OriginSpeed : 0f;
            bool matched = spawn.OriginSpeed > 0f && Zombies.SetBaseSpeed(instanceId, spawn.OriginSpeed);

            Log.Info("mp", "zombie #" + spawn.NetId + " (type " + spawn.ZombieType + ", row " + spawn.Row
                + ", wave " + spawn.Wave + ") from the host -> obj " + instanceId + ", base speed this lawn"
                + " rolled " + rolled + (matched ? " -> " + spawn.OriginSpeed + " (the host's)"
                    : ", the host said no speed")
                + " " + ApiBase.FrameTag());
        }

        /// <summary>The host saying a zombie has left its lawn. This end has no vote left to cast about it.</summary>
        private static void ApplyDespawn(DespawnZombiePayload despawn)
        {
            NetEntity record = _room.Entities.Find(despawn.NetId);
            if (record == null || record.Kind != NetKind.Zombie)
            {
                Log.Warn("mp", "zombie #" + despawn.NetId + " is already gone from this lawn");
                return;
            }

            _room.Entities.Remove(despawn.NetId);
            HostNamedAt.Remove(despawn.NetId);

            if (record.InstanceId != 0)
            {
                Zombies.Kill(record.InstanceId, despawn.Reason);
            }

            Log.Info("mp", "zombie #" + despawn.NetId + " (row " + record.Row + ") left this lawn by the"
                + " host's word " + ApiBase.FrameTag());
        }

        /// <summary>
        /// The host's numbers for every room zombie. The write is the only way to make the copy agree:
        /// the game has setters only through its damage code, which would also run the death and effect
        /// logic again. A position push also repeats the row and moves the object through the game's own
        /// `Zombie.AdjustPosition`. A zombie this end no longer has is counted and left to the despawn frame.
        /// </summary>
        private static void ApplyState(ZombieStatePayload state)
        {
            if (state.Zombies == null)
            {
                return;
            }

            int bound = 0;
            int changed = 0;
            int moved = 0;
            int missing = 0;

            var named = new HashSet<int>();

            for (int i = 0; i < state.Zombies.Length; i++)
            {
                ZombieHealth value = state.Zombies[i];

                named.Add(value.NetId);

                NetEntity record = _room.Entities.Find(value.NetId);
                if (record == null || record.Kind != NetKind.Zombie || record.InstanceId == 0)
                {
                    continue;
                }

                bound++;

                if (!value.Alive || value.Health <= 0)
                {
                    ZombieState here;
                    if (Zombies.TryRead(record.InstanceId, out here) && here.Alive)
                    {
                        Zombies.Kill(record.InstanceId, 0);
                        Log.Info("mp", "zombie #" + value.NetId + " is dead on the host, killed here "
                            + ApiBase.FrameTag());
                    }

                    continue;
                }

                ZombieState host = new ZombieState
                {
                    Row = value.Row,
                    Health = value.Health,
                    FirstArmorHealth = value.FirstArmor,
                    SecondArmorHealth = value.SecondArmor,
                    MindControlled = value.MindControlled,
                    OriginSpeed = value.OriginSpeed,
                    FreezeSpeed = value.FreezeSpeed,
                    ColdSpeed = value.ColdSpeed,
                    ButterSpeed = value.ButterSpeed,
                    KelpSpeed = value.KelpSpeed,
                    GarlicSpeed = value.GarlicSpeed,
                    UniqueSpeed = value.UniqueSpeed,
                    HoleSpeed = value.HoleSpeed,
                    MaLiangSpeed = value.MaLiangSpeed,
                    X = value.X,
                    Y = value.Y
                };

                bool corrected;
                bool positionMoved;

                if (Zombies.ApplyState(record.InstanceId, host, value.HasPosition, out corrected,
                    out positionMoved))
                {
                    if (corrected)
                    {
                        changed++;
                    }

                    if (positionMoved)
                    {
                        moved++;
                    }
                }
                else
                {
                    missing++;
                }
            }

            int now = Environment.TickCount;

            ReconcileTheList(named, now);

            if (bound > 0 && Due(now, _clientReportedAt, ReportMillis))
            {
                _clientReportedAt = now;

                Log.Info("mp", "zombie state from the host: " + bound + " bound, " + changed
                    + " corrected, " + moved + " moved, " + missing + " not on this lawn "
                    + ApiBase.FrameTag());
            }
        }

        /// <summary>
        /// The host's list, finished on this end: a bound zombie the host's frames have stopped naming is a
        /// zombie the host no longer has, so it is taken down here too. A bind this end made after the last
        /// frame is left alone until the next frame can name it.
        /// </summary>
        private static void ReconcileTheList(HashSet<int> named, int now)
        {
            var extra = new List<NetEntity>();

            foreach (NetEntity entry in _room.Entities.Entries)
            {
                if (entry.Kind != NetKind.Zombie || entry.InstanceId == 0)
                {
                    continue;
                }

                if (named.Contains(entry.NetId))
                {
                    HostNamedAt[entry.NetId] = now;
                    continue;
                }

                int seenAt;
                if (HostNamedAt.TryGetValue(entry.NetId, out seenAt) && Due(now, seenAt, HostSilenceMillis))
                {
                    extra.Add(entry);
                }
            }

            for (int i = 0; i < extra.Count; i++)
            {
                NetEntity entry = extra[i];

                HostNamedAt.Remove(entry.NetId);
                _room.Entities.Remove(entry.NetId);
                Zombies.Kill(entry.InstanceId, 0);

                Log.Info("mp", "zombie #" + entry.NetId + " (row " + entry.Row + ") has not been in the"
                    + " host's list for " + HostSilenceMillis / 1000 + "s, it is taken down here "
                    + ApiBase.FrameTag());
            }
        }

        /// <summary>
        /// A peer's lawn holds what the host said and nothing else. An object this end never adopted has no
        /// net id, so no frame can ever correct or despawn it: once it has stood here long enough to prove
        /// no frame is coming for it, it is taken down through the game's own death.
        /// </summary>
        private static void SweepThisLawn(int now)
        {
            if (!LevelSync.TheRoomIsInALevel || !Due(now, _sweptAt, SweepMillis))
            {
                return;
            }

            _sweptAt = now;

            int[] ids = BoardCollections.ZombieInstanceIds();
            if (ids == null || ids.Length == 0)
            {
                UnboundSince.Clear();
                return;
            }

            var present = new HashSet<int>(ids);

            if (UnboundSince.Count > 0)
            {
                var gone = new List<int>();

                foreach (int id in UnboundSince.Keys)
                {
                    if (!present.Contains(id))
                    {
                        gone.Add(id);
                    }
                }

                for (int i = 0; i < gone.Count; i++)
                {
                    UnboundSince.Remove(gone[i]);
                }
            }

            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];

                if (_room.Entities.ByInstanceId(id) != null)
                {
                    UnboundSince.Remove(id);
                    continue;
                }

                int since;
                if (!UnboundSince.TryGetValue(id, out since))
                {
                    UnboundSince[id] = now;
                    continue;
                }

                if (!Due(now, since, UnboundGraceMillis))
                {
                    continue;
                }

                UnboundSince.Remove(id);
                Zombies.Kill(id, 0);

                Log.Info("mp", "zombie obj " + id + " stands on this lawn but the host never named it,"
                    + " it is taken down " + ApiBase.FrameTag());
            }
        }

        /// <summary>
        /// The host's clock, stored in the three fields the game's own wave update reads. A peer has no
        /// native call for this; writing the absolute numbers is what makes its countdown the host's. The
        /// same frame carries the pause menu's difficulty and speed, which are copied the same way.
        /// </summary>
        private static void ApplyWave(WaveStatePayload state)
        {
            if (!Waves.ApplyWaveClock(state.Wave, state.MaxWave, state.TimeUntilNextWave))
            {
                return;
            }

            bool settingsApplied = state.HasSettings
                && LevelSettings.Apply(state.Difficulty, state.GameSpeed);

            if (state.HasSettings)
            {
                LevelSettings.SyncSliders(state.Difficulty, state.GameSpeed);
            }

            int now = Environment.TickCount;
            bool changed = state.Wave != _lastClientWave;
            bool settingsChanged = state.HasSettings
                && (state.Difficulty != _lastClientDifficulty || state.GameSpeed != _lastClientGameSpeed);

            _lastClientWave = state.Wave;

            if (state.HasSettings)
            {
                _lastClientDifficulty = state.Difficulty;
                _lastClientGameSpeed = state.GameSpeed;
            }

            if (changed || Due(now, _clientWaveReportedAt, WaveReportMillis))
            {
                _clientWaveReportedAt = now;

                Log.Info("mp", "the host's wave clock: " + state.Wave + "/" + state.MaxWave + ", next in "
                    + state.TimeUntilNextWave + "s, stored on this lawn " + ApiBase.FrameTag());
            }

            if (settingsChanged)
            {
                Log.Info("mp", "the host's difficulty " + state.Difficulty + " and game speed x"
                    + state.GameSpeed + " are " + (settingsApplied ? "stored here" : "NOT stored here")
                    + " " + ApiBase.FrameTag());
            }
        }

        /// <summary>The host's mower, played with the game's own start on this end's object of that row.</summary>
        private static void ApplyMower(MowerStartPayload start)
        {
            int instanceId = Mowers.FindInstanceForRow(start.Row);

            if (instanceId == 0)
            {
                int[] rows = Mowers.Rows();

                Log.Warn("mp", "the host started the mower of row " + start.Row + ", but this lawn has"
                    + " mowers on " + Join(rows) + " - it does not start here");
                return;
            }

            if (Mowers.AlreadyStarted(instanceId))
            {
                Log.Info("mp", "the mower of row " + start.Row + " had already started here");
                return;
            }

            _mowerApplying = true;

            try
            {
                bool started = Mowers.StartMoving(instanceId);

                Log.Info("mp", "the host's mower of row " + start.Row + " is played here: "
                    + (started ? "the game's own start ran" : "the game threw") + " " + ApiBase.FrameTag());
            }
            finally
            {
                _mowerApplying = false;
            }
        }

        /// <summary>
        /// What exists on this lawn is the host's sentence, the wave included. A peer that reports one of
        /// these frames is a peer that has forgotten who decides, and listening to it is two different lawns.
        /// </summary>
        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action + ": only the host says what the"
                + " waves do");

            return false;
        }

        private static bool RoomDecides()
        {
            return _room != null && !ApiBase.IsBroken(Capability) && _room.RoomDecides;
        }

        /// <summary>Wrap-safe interval check: Environment.TickCount rolls over and is signed.</summary>
        private static bool Due(int now, int since, int millis)
        {
            return unchecked((uint)(now - since)) >= (uint)millis;
        }

        private static string Join(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return "none";
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(values[i]);
            }

            return text.ToString();
        }
    }
}
