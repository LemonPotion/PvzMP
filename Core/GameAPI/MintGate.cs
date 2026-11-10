using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Not a gate: nothing here stops the game, and at this door nothing could. `CreatePlant.SetPlant`
    /// hands its caller the plant it made, and 181 of the 399 static call sites the build has read through
    /// that pointer or pass it straight on before ever looking at it, so a body skipped there is a null
    /// dereference inside the game's own code rather than a refusal. What a room may refuse is a door that
    /// gives nothing back, and that is where the gift rule sits now.
    /// Proof: ref/EVIDENCE.md E43.
    ///
    /// So this only listens. It names every plant this end's own dice put on a lawn in the middle of a
    /// level with no frame behind it - the difference the census has to take down afterwards - and those
    /// lines are how the next door gets chosen from what a live room did instead of from what a dump
    /// suggests. Like the merge and the fertilizer watches, a failed patch costs the diagnosis and nothing
    /// else, so no capability is owned here.
    /// </summary>
    public static class MintGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.mint").PatchAll(typeof(MintGate));
                Log.Info("hook", "every mint this end makes of its own accord is named in the log "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "watching the mint door failed: " + e.GetType().Name + " " + e.Message
                    + " - the room behaves as before, only the doors that roll on a peer are not named");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.CreatePlant), "SetPlant")]
        public static class Mint
        {
            private static void Postfix(object[] __args)
            {
                try
                {
                    if (__args == null || __args.Length < 3)
                    {
                        return;
                    }

                    PlantSync.NoteMint(Convert.ToInt32(__args[2]), Convert.ToInt32(__args[0]),
                        Convert.ToInt32(__args[1]));
                }
                catch (Exception e)
                {
                    Log.Warn("hook", "the mint watch threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
