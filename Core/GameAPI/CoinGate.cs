using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The shared sun pool, kept host-owned without taking the coins off a peer's screen. A peer still runs
    /// the game's own sun drops and sees them, but a coin it collects pays this lawn nothing: the credit
    /// point (`Board.GetSun`, ref/EVIDENCE.md E10) is blocked on a peer, so a peer's counter can move only
    /// when the host's absolute value arrives. The host's own collection still pays and is then pushed.
    ///
    /// Killing the peer's coins would also stop the drift, but it leaves the client with no sun to look at,
    /// so the coins stay and only their payment is refused. A peer's coins are its own rolls and do not have
    /// to match the host's positions - the number they drain into is the host's, and that is the one truth.
    ///
    /// Outside a room nothing here is done, so a single player game is byte-for-byte as before.
    /// </summary>
    public static class CoinGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.coin").PatchAll(typeof(CoinGate));
                Log.Info("hook", "a coin collected on a peer pays nothing, a host collection is pushed "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                // Without this gate every end pays its own sun into its own bank, so the shared pool is
                // a fiction and each lawn starts buying plants with money the room never agreed to. A
                // room that cannot agree about money must stop spending, not pretend: the same rule the
                // other gates already follow, which is why this one used to be the odd way round.
                ApiBase.Disable(WorldSync.Capability, "the sun gate could not be installed");
                Log.Error("hook", "gating the sun pool failed: " + e.GetType().Name + " " + e.Message
                    + " - placement inside a room stops rather than let every bank drift apart");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Board), "GetSun")]
        public static class GetSun
        {
            // The credit point of a collection (EVIDENCE E10). A peer may collect a coin for the picture,
            // but only the host's pool is the room's pool, so a peer credit is refused.
            private static bool Prefix()
            {
                return WorldSync.MayCreditSun();
            }

            // Runs only for the host (MayCreditSun let it through): the collection just moved the pool, so
            // the absolute value travels, because a grant is not the only event that changes the number.
            private static void Postfix()
            {
                try
                {
                    WorldSync.NoteSunCredited();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "pushing sun after a credit threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
