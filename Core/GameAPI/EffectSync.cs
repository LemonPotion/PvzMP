using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// What the room plays when one lawn shows something. A particle or a sound is made by the game on the
    /// lawn that acted, and the other lawns never see it: a peer often loses the body before the host's
    /// despawn frame arrives, so its own `Die` never runs, and a fusion or a mower that ran on one seat's
    /// screen was silent and bare on everyone else's. So every seat tells the room what its own game just
    /// made, and every other seat calls the same native method with the same numbers.
    ///
    /// Particles travel as they are made. Sounds are the door `SoundSync` watches, and they travel only
    /// while one of the events counted below is being played - the audio call is made from hundreds of
    /// places that are not events, and a room that relayed every idle rustle would drown the ones worth
    /// hearing.
    ///
    /// The events are counted by depth. Every declared `Die(int)` override of a zombie type is gated at boot
    /// by reflection, because a Harmony attribute on the base method would never see the overrides the game
    /// actually calls, and the mower's two doors and the fusion's two are gated by attribute. A gated body
    /// that throws leaves the count one too high until the level ends, which is why `ForgetTheLawn` resets
    /// it.
    ///
    /// Proof of the doors and of what each one plays: ref/EVIDENCE.md E50.
    /// </summary>
    public static class EffectSync
    {
        public const string Capability = "mp-effects";

        private static MpSession _room;

        /// <summary>Non-zero while a death, a mower's run or a fusion is being played.</summary>
        private static int _eventDepth;

        /// <summary>True while this end replays an effect, so nothing is sent back.</summary>
        private static bool _applying;

        /// <summary>What one level said and replayed; a particle is counted and not written, a sound is both.</summary>
        private static int _said;
        private static int _played;

        /// <summary>The sounds the game started while a room decided, whether or not they travelled. The one
        /// number that says whether the sounds worth hearing are inside the events that are counted.</summary>
        private static int _heard;

        private static MethodInfo _beforeDie;
        private static MethodInfo _afterDie;

        public static void Attach(MpSession room)
        {
            _room = room;

            try
            {
                new HarmonyLib.Harmony("PvzMP.effects").PatchAll(typeof(EffectSync));
                Log.Info("hook", "the particles of this lawn are said to the room and its events are counted "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the effect gates could not be installed");
                Log.Error("hook", "gating this lawn's particles failed: " + e.GetType().Name + " "
                    + e.Message + " - a seat keeps only the effects its own lawn plays");
            }

            GateDeaths();
        }

        /// <summary>
        /// Every zombie type's own `Die(int)`, found by reflection because a Harmony attribute on the base
        /// method would never see the overrides the game actually calls.
        /// </summary>
        private static void GateDeaths()
        {
            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("PvzMP.effects.die");
                _beforeDie = typeof(EffectSync).GetMethod("BeforeDie",
                    BindingFlags.Public | BindingFlags.Static);
                _afterDie = typeof(EffectSync).GetMethod("AfterDie",
                    BindingFlags.Public | BindingFlags.Static);

                Type[] types;
                try
                {
                    types = typeof(Il2Cpp.Zombie).Assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }

                int gated = 0;

                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null || !typeof(Il2Cpp.Zombie).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo die = type.GetMethod("Die",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly, null, new[] { typeof(int) }, null);

                    if (die == null)
                    {
                        continue;
                    }

                    try
                    {
                        harmony.Patch(die, new HarmonyMethod(_beforeDie), new HarmonyMethod(_afterDie));
                        gated++;
                    }
                    catch (Exception e)
                    {
                        Log.Warn("hook", "the death of " + type.Name + " cannot be watched: "
                            + e.GetType().Name + " " + e.Message);
                    }
                }

                Log.Info("hook", gated + " zombie death method(s) are counted as events "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Error("hook", "watching the zombie deaths failed: " + e.GetType().Name + " "
                    + e.Message + " - the sounds of a death are no longer known as one");
            }
        }

        /// <summary>Run before a zombie's own death body, so what it plays is counted as one event.</summary>
        public static void BeforeDie()
        {
            OpenEvent();
        }

        /// <summary>Run after it, and always takes the count back.</summary>
        public static void AfterDie()
        {
            CloseEvent();
        }

        /// <summary>One of the events that owns a sound has begun on this lawn.</summary>
        public static void OpenEvent()
        {
            _eventDepth++;
        }

        /// <summary>And has ended, so a sound made later is the lawn's own business again.</summary>
        public static void CloseEvent()
        {
            if (_eventDepth > 0)
            {
                _eventDepth--;
            }
        }

        /// <summary>Asked by `SoundSync`: only a sound played while an event is open travels.</summary>
        public static bool EventOpen()
        {
            return EffectRules.CarriesSound(_eventDepth);
        }

        /// <summary>Asked by `ParticleManager.SetParticle` after this lawn made one of its own particles.</summary>
        public static void NoteManagerParticle(int particleType, Vector2 position, int layerRow, bool lim,
            float scale)
        {
            if (!MaySay())
            {
                return;
            }

            Say(new EffectPayload
            {
                Kind = EffectRules.ManagerParticle,
                ParticleType = particleType,
                X = position.x,
                Y = position.y,
                Z = 0f,
                Row = layerRow,
                Scale = scale,
                Lim = lim,
                SetLayer = true
            });
        }

        /// <summary>Asked by `CreateParticle.SetParticle` after this lawn made one of its own objects.</summary>
        public static void NoteCreatedParticle(int particleType, Vector3 position, int row, bool setLayer)
        {
            if (!MaySay())
            {
                return;
            }

            Say(new EffectPayload
            {
                Kind = EffectRules.CreatedParticle,
                ParticleType = particleType,
                X = position.x,
                Y = position.y,
                Z = position.z,
                Row = row,
                Scale = 0f,
                Lim = true,
                SetLayer = setLayer
            });
        }

        /// <summary>
        /// Asked by `SoundSync` after this lawn started one of the game's own sounds. What is heard is counted
        /// before what travels is decided, because the count is the answer to whether the events that are
        /// opened are the ones the sounds of this level live in.
        /// </summary>
        public static void NoteSound(int sound, float volume, float pitch)
        {
            if (!MaySay())
            {
                return;
            }

            _heard++;

            if (!EventOpen())
            {
                return;
            }

            Say(new EffectPayload
            {
                Kind = EffectRules.Sound,
                Sound = sound,
                Volume = volume,
                Pitch = pitch
            });
        }

        /// <summary>
        /// The room's turn to play it. The host tells every seat at once; a seat asks the host, which plays
        /// it on its own lawn and carries it on to the rest. A sound is written both ways, because whether
        /// one arrived is the question the stage was asked about; a particle only raises the count the level
        /// says when it ends, because a lawn throws hundreds of them.
        /// </summary>
        private static void Say(EffectPayload effect)
        {
            effect.OriginPeer = _room.LocalPeerId;

            if (_room.IsHost)
            {
                _room.BroadcastGameplay(MsgAction.Effect, effect);
            }
            else
            {
                _room.SendToHost(MsgAction.Effect, effect);
            }

            if (effect.Kind == EffectRules.Sound)
            {
                Log.Info("mp", "this lawn said " + Describe(effect) + " to the room " + ApiBase.FrameTag());
            }

            _said++;
        }

        private static string Describe(EffectPayload effect)
        {
            return effect.Kind == EffectRules.Sound
                ? "sound " + effect.Sound
                : "particle " + effect.ParticleType + " of kind " + effect.Kind;
        }

        /// <summary>Where the effect frames arrive; WorldSync forwards them here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Action != MsgAction.Effect || _room == null)
            {
                return;
            }

            EffectPayload effect = Protocol.Payload<EffectPayload>(frame);

            if (_room.IsHost)
            {
                // A seat asking the room. Whose lawn made it is what the transport says and not what the
                // frame claims, and no seat's effect was made on the host's own lawn, so the host plays it.
                effect.OriginPeer = peerId;

                Apply(effect);
                _room.BroadcastGameplay(MsgAction.Effect, effect);

                return;
            }

            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " sent an effect: only the host says what the room played");
                return;
            }

            Apply(effect);
        }

        /// <summary>The level is over, so the count of one lawn's effects is said and started clean again.</summary>
        public static void ForgetTheLawn()
        {
            if (_said > 0 || _played > 0 || _heard > 0)
            {
                Log.Info("mp", "this lawn heard " + _heard + " sound(s), said " + _said
                    + " effect(s) and played " + _played + " of the room's " + ApiBase.FrameTag());
            }

            _said = 0;
            _played = 0;
            _heard = 0;
            _eventDepth = 0;
            _applying = false;
        }

        /// <summary>
        /// The same native call the origin's lawn made. A seat often has no zombie left by the time this
        /// arrives, which is exactly why the effect is replayed directly instead of through the event that
        /// made it.
        /// </summary>
        private static void Apply(EffectPayload effect)
        {
            if (!EffectRules.PlaysHere(effect.OriginPeer, _room.LocalPeerId))
            {
                return;
            }

            if (effect.Kind == EffectRules.Sound)
            {
                Log.Info("mp", "this lawn played " + Describe(effect) + " of seat " + effect.OriginPeer + " "
                    + ApiBase.FrameTag());
            }

            _played++;
            _applying = true;

            try
            {
                if (effect.Kind == EffectRules.ManagerParticle)
                {
                    ParticleManager manager = ApiBase.Query("ParticleManager.Instance",
                        () => ParticleManager.Instance, Capability);

                    if (!ApiBase.Present(manager))
                    {
                        return;
                    }

                    ApiBase.Query("ParticleManager.SetParticle", () => manager.SetParticle(
                        (ParticleType)effect.ParticleType,
                        new Vector2(effect.X, effect.Y),
                        effect.Row,
                        effect.Lim,
                        effect.Scale), Capability);

                    return;
                }

                if (effect.Kind == EffectRules.CreatedParticle)
                {
                    ApiBase.Query("CreateParticle.SetParticle", () => CreateParticle.SetParticle(
                        effect.ParticleType,
                        new Vector3(effect.X, effect.Y, effect.Z),
                        effect.Row,
                        effect.SetLayer), Capability);

                    return;
                }

                SoundSync.Replay(effect.Sound, effect.Volume, effect.Pitch);
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// Whether this lawn may tell the room what its own game just made. An echo of what the room ordered
        /// is never news, and a seat that has no room to answer to keeps its effects to itself.
        /// </summary>
        private static bool MaySay()
        {
            return !_applying && !ZombieSync.ReplayingAMower && _room != null && _room.RoomDecides
                && !ApiBase.IsBroken(Capability);
        }

        [HarmonyPatch(typeof(ParticleManager), "SetParticle")]
        public static class ManagerParticleDoor
        {
            /// <summary>
            /// The overlay's own arguments, in the order the dump gives them: type, position, layer row,
            /// limit flag, scale. Reading them by position keeps the patch independent of parameter names.
            /// </summary>
            private static void Postfix(object[] __args)
            {
                try
                {
                    if (__args == null || __args.Length < 5)
                    {
                        return;
                    }

                    NoteManagerParticle(
                        (int)(ParticleType)__args[0],
                        (Vector2)__args[1],
                        (int)__args[2],
                        (bool)__args[3],
                        (float)__args[4]);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the particle relay threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(CreateParticle), "SetParticle")]
        public static class CreatedParticleDoor
        {
            private static void Postfix(object[] __args)
            {
                try
                {
                    if (__args == null || __args.Length < 4)
                    {
                        return;
                    }

                    NoteCreatedParticle(
                        (int)__args[0],
                        (Vector3)__args[1],
                        (int)__args[2],
                        (bool)__args[3]);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the created-particle relay threw: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        /// <summary>The mower's own start: it plays its sound and kills the row in one body.</summary>
        [HarmonyPatch(typeof(Mower), "StartMove")]
        public static class MowerStartDoor
        {
            private static void Prefix()
            {
                OpenEvent();
            }

            private static void Postfix()
            {
                CloseEvent();
            }
        }

        /// <summary>
        /// The mower's bite. Its body is a jump into the zombie's own table, so which death door it lands on
        /// is not measured here - what is measured is that a sound made under this call belongs to the mower.
        /// </summary>
        [HarmonyPatch(typeof(Mower), "AttackZombie")]
        public static class MowerAttackDoor
        {
            private static void Prefix()
            {
                OpenEvent();
            }

            private static void Postfix()
            {
                CloseEvent();
            }
        }

        /// <summary>
        /// The fusion's burst. The recipe body that leads here is not resolvable in the disassembly window -
        /// the method is 8192 bytes wide and its own table of callers is empty - so the burst is counted as
        /// the event in its own right, and what it plays is what the room repeats.
        /// </summary>
        [HarmonyPatch(typeof(CreatePlant), "MixEvent")]
        public static class FusionBurstDoor
        {
            private static void Prefix()
            {
                OpenEvent();
            }

            private static void Postfix()
            {
                CloseEvent();
            }
        }

        /// <summary>The fusion: the two plants become one, and the burst and its sound are the game's.</summary>
        [HarmonyPatch(typeof(CreatePlant), "CheckMix")]
        public static class FusionDoor
        {
            private static void Prefix()
            {
                OpenEvent();
            }

            private static void Postfix()
            {
                CloseEvent();
            }
        }
    }
}
