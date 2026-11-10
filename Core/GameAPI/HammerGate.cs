using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The third gate on the local player's hands, and the only one that stops nothing: every prefix here
    /// lets the game run. A hammer that does not fall where the player clicked is a broken tool, and the
    /// damage this lawn took is the smaller problem - the host's zombie health is what every seat is told
    /// within a second, so a local blow that the room disagreed with corrects itself.
    ///
    /// What the gate takes from the game is the answer to a question the mod must not ask twice. The click
    /// body queries the engine for the zombies around this machine's pointer and damages each one it got
    /// back; asking the same question with the mod's own numbers would be a second hit test that can only
    /// disagree. So the gate opens a window over the bodies that do that work and listens for the game's
    /// own damage step, `Zombie.TakeDamage` - every step heard is one the room is told about, with the
    /// numbers the game computed. The freeze step that `Use` writes is logged as well, so a click that
    /// only froze is visible; it carries no damage to repeat.
    ///
    /// The window is the crush, not the click. A live click's `Use` reported "touched nothing" a third of a
    /// second before the animation event `AnimCrush` ran, and the zombies died anyway; `Use` asks the
    /// engine around `Mouse.MousePosition`, while the crush asks it around the tool's own position and
    /// applies the damage through an interface dispatch the static scan cannot name. The room's order is
    /// the same damage step the game itself ran - `Zombie.TakeDamage`, with the damage the crush computed,
    /// the tool as the maker, `DamageType.NormalAll` and the hammer's own `PlantType` - because the body
    /// the room used before, `Hammer.EffectZombie`, draws bounds, an explosion and a freeze and writes no
    /// damage at all (E60). Proof: E54, E57, E60.
    ///
    /// The three doors of the tool above the damage step are watched rather than trusted; which of them a
    /// live click walks is written to the log, because a Unity animation event is precisely the caller a
    /// static scan cannot see.
    ///
    /// A failed patch costs the diagnosis and the sync of one tool, so this gate disables its own
    /// capability and leaves the game exactly as it was.
    /// </summary>
    public static class HammerGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.hammer").PatchAll(typeof(HammerGate));
                Log.Info("hook", "the hammer's blow is witnessed and repeated by the room "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(HammerSync.Capability, "the hammer hooks could not be installed");
                Log.Error("hook", "gating the hammer failed: " + e.GetType().Name + " " + e.Message
                    + " - a blow lands on the lawn that clicked and nowhere else");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Hammer), "Use")]
        public static class Use
        {
            private static void Prefix()
            {
                try
                {
                    EffectSync.OpenEvent();
                    HammerSync.BeginBlow("Use");
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the hammer watch could not start: " + e.GetType().Name + " "
                        + e.Message + ", this blow stays on this lawn");
                }
            }

            private static void Postfix()
            {
                try
                {
                    HammerSync.EndBlow();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the hammer blow could not be said: " + e.GetType().Name + " "
                        + e.Message + ", the lawn that clicked is the only one hit");
                }
                finally
                {
                    EffectSync.CloseEvent();
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Zombie), "AddfreezeLevel")]
        public static class AddFreezeLevel
        {
            private static void Prefix(Il2Cpp.Zombie __instance)
            {
                try
                {
                    Report("the freeze step ran on "
                        + (ApiBase.Present(__instance) ? Entities.Describe(__instance) : "nothing"));
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the freeze step could not be witnessed: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        /// <summary>
        /// The crush's damage door. `AnimCrush` reaches it through an interface dispatch the static scan
        /// cannot name, and its arguments say it is the hammer's own blow: the damage it computed, the
        /// tool as the maker, `DamageType.NormalAll`, and the hammer's own `PlantType` (270). The step is
        /// kept whole, because the room repeats this door and not the tool. Proof: E57, E60.
        /// </summary>
        [HarmonyPatch(typeof(Il2Cpp.Zombie), "TakeDamage")]
        public static class TakeDamage
        {
            private static void Prefix(Il2Cpp.Zombie __instance, int theDamage,
                                       Il2Cpp.IDamageMaker damageFrom, Il2Cpp.DamageType theDamageType,
                                       Il2Cpp.PlantType reportType, bool fix)
            {
                try
                {
                    HammerSync.NoteBlow(__instance, theDamage, damageFrom, theDamageType, reportType, fix);
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the damage step could not be witnessed: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Hammer), "EffectZombie")]
        public static class EffectZombie
        {
            private static bool Prefix(Il2Cpp.Zombie zombie)
            {
                Report("the blow body of one named zombie ran"
                    + (ApiBase.Present(zombie) ? " on " + Entities.Describe(zombie) : " on nothing"));

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Hammer), "CrushZombie")]
        public static class CrushZombie
        {
            private static bool Prefix()
            {
                Report("the crush body of the tool itself ran");

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Hammer), "AnimCrush")]
        public static class AnimCrush
        {
            private static void Prefix()
            {
                Report("the animation event of a crush ran");

                try
                {
                    EffectSync.OpenEvent();
                    HammerSync.BeginBlow("the animation event AnimCrush");
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the crush watch could not start: " + e.GetType().Name + " "
                        + e.Message + ", this blow stays on this lawn");
                }
            }

            private static void Postfix()
            {
                try
                {
                    HammerSync.EndBlow();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the crush could not be said: " + e.GetType().Name + " "
                        + e.Message + ", the lawn that crushed is the only one hit");
                }
                finally
                {
                    EffectSync.CloseEvent();
                }
            }
        }

        /// <summary>
        /// One line per door, and only while a room decides: outside a room the question is already
        /// answered by the game working, and a click log nobody is reading is noise in a file that has to
        /// stay readable.
        /// </summary>
        private static void Report(string what)
        {
            try
            {
                HammerSync.DescribeDoor(what);
            }
            catch (Exception e)
            {
                Log.Warn("hook", "the hammer watch threw: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
