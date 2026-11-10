using System;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The sounds of a room. Nearly every presentation this game makes ends in `GameAPP.PlaySound`, and
    /// until now nothing carried it: a seat that saw a zombie fall, a mower run or two plants fuse heard its
    /// own lawn stay quiet, because the sound belonged to the lawn that acted. Proof: ref/EVIDENCE.md E50.
    ///
    /// The door is watched here, but the decision of what is the room's business is `EffectSync`'s: it keeps
    /// the count of the events being played - a zombie's death, a mower's start or attack, a fusion - and this
    /// class only says what the game just started to play. A sound outside those is left alone, because a
    /// room that relayed every idle rustle would drown the events worth hearing.
    ///
    /// A door of this shape costs the sounds and nothing else when it cannot be installed. It is installed by
    /// MelonLoader's own pass over the assembly's patch classes, not by the call below, so the line under it
    /// says what this class asks of the game and `EffectSync`'s count of the sounds it heard is the evidence
    /// that a door is really there. Proof: ref/EVIDENCE.md E51.
    /// </summary>
    public static class SoundSync
    {
        public const string Capability = "mp-sound";

        /// <summary>The room is asked nothing here: what travels and to whom is `EffectSync`'s answer.</summary>
        public static void Attach(MpSession room)
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.sound").PatchAll(typeof(SoundSync));
                Log.Info("hook", "the game's sounds are watched " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the sound gates could not be installed");
                Log.Error("hook", "gating the sounds failed: " + e.GetType().Name + " " + e.Message
                    + " - every seat keeps only the sounds its own lawn plays");
            }
        }

        /// <summary>
        /// The room playing a sound another seat told it about. The same native door the origin's lawn
        /// walked, with the same three numbers, so the seat hears what the others hear.
        /// </summary>
        public static void Replay(int sound, float volume, float pitch)
        {
            ApiBase.TryCall("GameAPP.PlaySound(" + sound + ")", () =>
            {
                GameAPP.PlaySound(sound, volume, pitch);
            }, Capability);
        }

        [HarmonyPatch(typeof(GameAPP), "PlaySound", new[] { typeof(int), typeof(float), typeof(float) })]
        public static class SoundDoor
        {
            /// <summary>
            /// The one door of the two overloads, named by its argument types rather than by its name:
            /// `PlaySound(SoundType, float, float)` is 104 bytes ending in a jump to this body, so every
            /// sound the game makes walks through here - and a door on each of the two would say every
            /// sound of the first kind twice. Proof: ref/EVIDENCE.md E51.
            /// </summary>
            private static bool Prefix(int theSoundID, float theVolume, float pitch)
            {
                try
                {
                    EffectSync.NoteSound(theSoundID, theVolume, pitch);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the sound relay threw: " + e.GetType().Name + " " + e.Message);
                }

                return true;
            }
        }
    }
}
