using System;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Zombies. Same rule as plants: creation goes through the game's creator so the entity gets its
    /// native Animator and per-frame update, which is why a replicated zombie walks by itself.
    /// Health is read and corrected from the host's periodic snapshot; see ZombieSync for why the
    /// fields are written directly instead of replaying the damage code.
    /// </summary>
    public static class Zombies
    {
        /// <summary>Objects already reported as unreadable, so the warning is not repeated every push.</summary>
        private static readonly System.Collections.Generic.HashSet<int> Unreadable =
            new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// Where the host has the zombie and how fast it was moving when it said so. A push only records
        /// the aim; Correct walks the local object onto it over the next frames, because moving the whole
        /// way in the frame the frame arrived is the teleport this exists to avoid. The host's own speed is
        /// carried forward between pushes, so the client follows at the same pace instead of stopping at a
        /// two-second-old spot.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, Aim> AimX =
            new System.Collections.Generic.Dictionary<int, Aim>();

        /// <summary>One zombie's aim: the host's last x, the speed it moved at and when it was read.</summary>
        private sealed class Aim
        {
            public float X;
            public float Velocity;
            public int At;
        }

        /// <summary>
        /// How fast a miss is paid back at most. The position push now arrives every second, so the
        /// residual is small; a lower cap keeps the correction from reading as a slide.
        /// </summary>
        private const float CorrectSpeed = 1.2f;

        /// <summary>
        /// How quickly the miss is closed within that cap. The step is exponential, so a correction fades
        /// in and out instead of starting and stopping at a constant rate.
        /// </summary>
        private const float CorrectResponse = 4f;

        /// <summary>A miss this large is a reset or a teleport, not drift, and is applied at once.</summary>
        private const float SnapX = 6f;

        /// <summary>No zombie walks faster than this, so a larger measured speed is a reset, not a walk.</summary>
        private const float MaxAimSpeed = 3f;

        /// <summary>Clearer than the square threshold already used for "close enough" below (0.02 units).</summary>
        private const float SettledX = 0.08f;

        public static int Spawn(int row, int zombieType, float spawnX)
        {
            return ApiBase.Call("SpawnZombie(row=" + row + ",type=" + zombieType + ",x=" + spawnX + ")", () =>
            {
                Il2Cpp.CreateZombie creator = Il2Cpp.CreateZombie.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "SpawnZombie -> no CreateZombie instance");
                    return 0;
                }

                Il2Cpp.Zombie zombie = creator.SetZombie(row, (Il2Cpp.ZombieType)zombieType, spawnX);
                if (!ApiBase.Present(zombie))
                {
                    Log.Warn("api", "SpawnZombie -> native creator returned no object");
                    return 0;
                }

                Log.Api("SpawnZombie -> " + Entities.Describe(zombie));
                return zombie.GetInstanceID();
            }, "core-zombie");
        }

        /// <summary>
        /// The glove's own move for a zombie: `Mouse.MoveZombie` is the method the game's glove and
        /// zombie card call, and it carries the object with its health and animation. The host uses it
        /// to repeat a client's move and a client uses it to repeat the host's.
        /// </summary>
        public static bool MoveGlove(int instanceId, float x, int row)
        {
            return ApiBase.TryCall("MoveGloveZombie(#" + instanceId + ",x=" + x + ",row=" + row + ")", () =>
            {
                Il2Cpp.Zombie zombie = Entities.FindZombie(instanceId);
                if (zombie == null)
                {
                    Log.Warn("api", "MoveGloveZombie -> no zombie #" + instanceId + " on the lawn");
                    return;
                }

                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse))
                {
                    Log.Warn("api", "MoveGloveZombie -> no Mouse instance");
                    return;
                }

                mouse.MoveZombie(zombie, x, row, true);
            }, "core-zombie");
        }

        public static bool Kill(int instanceId, int reason)
        {
            AimX.Remove(instanceId);

            return ApiBase.TryCall("KillZombie(#" + instanceId + ",reason=" + reason + ")", () =>
            {
                Il2Cpp.Zombie zombie = Entities.FindZombie(instanceId);
                if (zombie == null)
                {
                    Log.Warn("api", "KillZombie -> no zombie #" + instanceId + " on the lawn");
                    return;
                }

                zombie.Die(reason);
            }, "core-zombie");
        }

        /// <summary>
        /// The base walking speed the host's creator rolled, written onto this lawn's copy and followed by
        /// the game's own `Zombie.UpdateSpeed`, which turns the nine speed fields into `theSpeed`, the
        /// rigidbody's velocity and the animator's `Speed` parameter. Without that call the fields change
        /// but the body keeps the walk it was spawned with. Proof: ref/EVIDENCE.md E59.
        /// </summary>
        public static bool SetBaseSpeed(int instanceId, float originSpeed)
        {
            return ApiBase.TryCall("ZombieBaseSpeed(#" + instanceId + ",speed=" + originSpeed + ")", () =>
            {
                Il2Cpp.Zombie zombie = Entities.FindZombie(instanceId);
                if (zombie == null)
                {
                    Log.Warn("api", "ZombieBaseSpeed -> no zombie #" + instanceId + " on the lawn");
                    return;
                }

                zombie.theOriginSpeed = originSpeed;
                zombie.UpdateSpeed();
            }, "core-zombie");
        }

        public static bool TryRead(int instanceId, out ZombieState state)
        {
            Il2Cpp.Zombie zombie = ApiBase.Query("ReadZombie(#" + instanceId + ")",
                () => Entities.FindZombie(instanceId), "core-zombie");

            if (zombie == null)
            {
                state = default(ZombieState);
                return false;
            }

            Vector3 at = Anchor(zombie);

            state = new ZombieState
            {
                InstanceId = instanceId,
                ZombieType = (int)zombie.theZombieType,
                Row = zombie.theZombieRow,
                Health = zombie.theHealth,
                MaxHealth = zombie.theMaxHealth,
                FirstArmorHealth = zombie.theFirstArmorHealth,
                SecondArmorHealth = zombie.theSecondArmorHealth,
                Alive = zombie.Alive,
                MindControlled = zombie.isMindControlled,
                OriginSpeed = zombie.theOriginSpeed,
                FreezeSpeed = zombie.freezeSpeed,
                ColdSpeed = zombie.coldSpeed,
                ButterSpeed = zombie.butterSpeed,
                KelpSpeed = zombie.kelpSpeed,
                GarlicSpeed = zombie.garlicSpeed,
                UniqueSpeed = zombie.uniqueSpeed,
                HoleSpeed = zombie.holeSpeed,
                MaLiangSpeed = zombie.maLiangSpeed,
                X = at.x,
                Y = at.y
            };
            return true;
        }

        /// <summary>
        /// The point the game itself calls the zombie's position: `Entity.axis`. `Zombie.AdjustPosition`
        /// moves this transform onto the point it is given, and `Zombie.MoveYUpdate` eases its y onto the
        /// row's ground. The visual transform carries the walk animation's vertical offset, so it is not
        /// the same point and must not travel as one.
        /// </summary>
        private static Vector3 Anchor(Il2Cpp.Zombie zombie)
        {
            UnityEngine.Transform axis = zombie.axis;
            return ApiBase.Present(axis) ? axis.position : zombie.transform.position;
        }

        /// <summary>
        /// Makes this machine's zombie carry the host's current numbers. The health, the armor and the
        /// speed factors are plain fields the game's own code writes. There is no setter, and replaying
        /// TakeDamage would run the game's death and effect logic again.
        ///
        /// A position push also repeats the row and puts the walk's drift back on x with the game's own
        /// `Zombie.AdjustPosition`. `AdjustPosition` moves `Entity.axis` onto the point it is given, so
        /// the point must be an axis position; the host sends exactly that. The y stays local: the
        /// zombie's own `MoveYUpdate` eases it onto its row's ground, and feeding the host's visual y
        /// here makes it jump down and climb back every push. The session tick on the game's main
        /// thread owns this.
        /// </summary>
        public static bool ApplyState(int instanceId, ZombieState host, bool withPosition,
            out bool corrected, out bool moved)
        {
            corrected = false;
            moved = false;

            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "ApplyZombieState was called off the game's main thread");
                return false;
            }

            Il2Cpp.Zombie zombie = Entities.FindZombie(instanceId);
            if (zombie == null)
            {
                return false;
            }

            if (zombie.theHealth != host.Health || zombie.theFirstArmorHealth != host.FirstArmorHealth
                || zombie.theSecondArmorHealth != host.SecondArmorHealth
                || zombie.isMindControlled != host.MindControlled)
            {
                zombie.theHealth = host.Health;
                zombie.theFirstArmorHealth = host.FirstArmorHealth;
                zombie.theSecondArmorHealth = host.SecondArmorHealth;
                zombie.isMindControlled = host.MindControlled;
                corrected = true;
            }

            if (zombie.theOriginSpeed != host.OriginSpeed || zombie.freezeSpeed != host.FreezeSpeed
                || zombie.coldSpeed != host.ColdSpeed || zombie.butterSpeed != host.ButterSpeed
                || zombie.kelpSpeed != host.KelpSpeed || zombie.garlicSpeed != host.GarlicSpeed
                || zombie.uniqueSpeed != host.UniqueSpeed || zombie.holeSpeed != host.HoleSpeed
                || zombie.maLiangSpeed != host.MaLiangSpeed)
            {
                zombie.theOriginSpeed = host.OriginSpeed;
                zombie.freezeSpeed = host.FreezeSpeed;
                zombie.coldSpeed = host.ColdSpeed;
                zombie.butterSpeed = host.ButterSpeed;
                zombie.kelpSpeed = host.KelpSpeed;
                zombie.garlicSpeed = host.GarlicSpeed;
                zombie.uniqueSpeed = host.UniqueSpeed;
                zombie.holeSpeed = host.HoleSpeed;
                zombie.maLiangSpeed = host.MaLiangSpeed;

                // The fields are only the recipe: the game's own update is what turns them into the
                // rigidbody's velocity and the animator's speed (E59).
                zombie.UpdateSpeed();
                corrected = true;
            }

            if (!withPosition)
            {
                return true;
            }

            if (zombie.theZombieRow != host.Row)
            {
                zombie.theZombieRow = host.Row;
                moved = true;
            }

            UnityEngine.Transform axis = zombie.axis;
            if (!ApiBase.Present(axis))
            {
                // AdjustPosition itself moves nothing without an axis.
                return true;
            }

            Vector3 here = axis.position;
            float dx = host.X - here.x;
            int now = Environment.TickCount;

            if (dx * dx > 0.0004f)
            {
                if (dx > SnapX || dx < -SnapX)
                {
                    zombie.AdjustPosition(new Vector3(host.X, here.y, here.z));
                    AimX.Remove(instanceId);
                }
                else
                {
                    Aim aim;

                    if (!AimX.TryGetValue(instanceId, out aim))
                    {
                        aim = new Aim { X = host.X, At = now };
                        AimX.Add(instanceId, aim);
                    }
                    else
                    {
                        int elapsed = unchecked(now - aim.At);
                        if (elapsed > 40)
                        {
                            float velocity = (host.X - aim.X) * 1000f / elapsed;
                            if (velocity > MaxAimSpeed)
                            {
                                velocity = MaxAimSpeed;
                            }
                            else if (velocity < -MaxAimSpeed)
                            {
                                velocity = -MaxAimSpeed;
                            }
                            else if (velocity > -0.01f && velocity < 0.01f)
                            {
                                velocity = 0f;
                            }

                            // A slower low-pass than the old three-second pushes needed: the sample is
                            // now close enough that one bad frame should bend the walk only a little.
                            aim.Velocity += (velocity - aim.Velocity) * 0.25f;
                        }

                        aim.X = host.X;
                        aim.At = now;
                    }
                }

                moved = true;
            }
            else
            {
                Aim still;
                if (AimX.TryGetValue(instanceId, out still))
                {
                    // The host's sample is where this zombie already is: keep the aim and drop the
                    // carried speed instead of forgetting it and restarting the next prediction at zero.
                    still.X = host.X;
                    still.At = now;
                    still.Velocity = 0f;
                }
            }

            return true;
        }

        /// <summary>
        /// Walks each zombie onto its aim on this frame. The asymmetry is the point: a large miss is applied
        /// at once, because that is the game moving the zombie, while a push is followed in small steps
        /// through the game's own `AdjustPosition`. The distance is measured every frame against the aim,
        /// and the aim is carried forward at the host's own measured speed, so the local walk never has to
        /// stop and no correction is ever paid twice. `Time.deltaTime` is zero while paused, so the room
        /// stays still.
        /// </summary>
        public static void Correct()
        {
            if (AimX.Count == 0 || !ApiBase.OnMainThread)
            {
                return;
            }

            float step = CorrectSpeed * Time.deltaTime;
            if (step <= 0f)
            {
                return;
            }

            System.Collections.Generic.List<int> unreachable = null;

            foreach (System.Collections.Generic.KeyValuePair<int, Aim> pair in AimX)
            {
                Il2Cpp.Zombie zombie = Entities.FindZombie(pair.Key);
                UnityEngine.Transform axis = zombie == null ? null : zombie.axis;

                if (!ApiBase.Present(axis))
                {
                    if (unreachable == null)
                    {
                        unreachable = new System.Collections.Generic.List<int>();
                    }

                    unreachable.Add(pair.Key);
                    continue;
                }

                Aim aim = pair.Value;
                int elapsed = unchecked(Environment.TickCount - aim.At);
                if (elapsed < 0)
                {
                    elapsed = 0;
                }
                else if (elapsed > 1500)
                {
                    // A late push must not snap the aim back to its old sample. Hold the last
                    // predicted point until the next sample; the host sends positions every second.
                    elapsed = 1500;
                }

                float target = aim.X + aim.Velocity * (elapsed / 1000f);
                Vector3 here = axis.position;
                float dx = target - here.x;

                if (dx < SettledX && dx > -SettledX)
                {
                    // Keep the aim: it carries the velocity into the next sample. Dropping it here
                    // restarts the prediction from zero whenever the zombie briefly reaches its target.
                    continue;
                }

                // Exponential approach: a small miss is paid slowly and a large one quickly, always within
                // the per-frame cap. The correction therefore eases in and out instead of switching on at
                // a constant speed. `Time.deltaTime` is zero while paused, so the room stays still.
                float paid = dx * (1f - Mathf.Exp(-CorrectResponse * Time.deltaTime));

                if (paid > step)
                {
                    paid = step;
                }
                else if (paid < -step)
                {
                    paid = -step;
                }

                zombie.AdjustPosition(new Vector3(here.x + paid, here.y, here.z));
            }

            if (unreachable != null)
            {
                for (int i = 0; i < unreachable.Count; i++)
                {
                    AimX.Remove(unreachable[i]);
                }
            }
        }

        /// <summary>A new lawn owes nothing to the old one.</summary>
        public static void ForgetTheLawn()
        {
            AimX.Clear();
        }

        /// <summary>
        /// Every zombie the game's list holds. One unreadable object - a destroyed body the list has not
        /// let go of yet - must not cost the room the rest of the snapshot or trip the breaker, so each
        /// read is guarded alone. Such an object is left out; the host's existing "not in the list" path
        /// then treats it as gone.
        /// </summary>
        public static ZombieState[] ReadAll()
        {
            return ApiBase.Query("ReadAllZombies", () =>
            {
                var found = new System.Collections.Generic.List<ZombieState>();

                Entities.ForEachZombie(zombie =>
                {
                    try
                    {
                        found.Add(ReadOne(zombie));
                    }
                    catch (Exception e)
                    {
                        WarnUnreadable(zombie, e);
                    }
                });

                return found.ToArray();
            }, "core-zombie");
        }

        private static ZombieState ReadOne(Il2Cpp.Zombie zombie)
        {
            Vector3 at = Anchor(zombie);

            return new ZombieState
            {
                InstanceId = zombie.GetInstanceID(),
                ZombieType = (int)zombie.theZombieType,
                Row = zombie.theZombieRow,
                Health = zombie.theHealth,
                MaxHealth = zombie.theMaxHealth,
                FirstArmorHealth = zombie.theFirstArmorHealth,
                SecondArmorHealth = zombie.theSecondArmorHealth,
                Alive = zombie.Alive,
                MindControlled = zombie.isMindControlled,
                OriginSpeed = zombie.theOriginSpeed,
                FreezeSpeed = zombie.freezeSpeed,
                ColdSpeed = zombie.coldSpeed,
                ButterSpeed = zombie.butterSpeed,
                KelpSpeed = zombie.kelpSpeed,
                GarlicSpeed = zombie.garlicSpeed,
                UniqueSpeed = zombie.uniqueSpeed,
                HoleSpeed = zombie.holeSpeed,
                MaLiangSpeed = zombie.maLiangSpeed,
                X = at.x,
                Y = at.y
            };
        }

        /// <summary>One line per object, not per poll: a broken entry is a fact, not a repeating event.</summary>
        private static void WarnUnreadable(Il2Cpp.Zombie zombie, Exception e)
        {
            int instanceId = 0;

            try
            {
                instanceId = zombie.GetInstanceID();
            }
            catch
            {
                // The id is only for the log; the object is skipped either way.
            }

            if (!Unreadable.Add(instanceId))
            {
                return;
            }

            Log.Warn("api", "zombie obj " + instanceId + " is on the game's list but cannot be read ("
                + e.GetType().Name + "); the room treats it as gone " + ApiBase.FrameTag());
        }
    }
}
