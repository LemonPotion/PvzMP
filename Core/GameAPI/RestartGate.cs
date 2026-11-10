using System;
using HarmonyLib;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The pause menu's restart button. The game's restart takes the lawn down first - it clears the level
    /// save, pops the menus and destroys the board - and only then calls UIMgr.EnterGame, so the level gate
    /// cannot save a peer that got this far: by the time EnterGame is refused, the room's lawn is already
    /// gone. This detour is what keeps a peer from pressing it; the host's press runs in full, and its own
    /// EnterGame call is the one the room hears.
    ///
    /// The body that really runs is inlined in `PauseMenu_Btn.OnMouseUp`, not in `Restart()`, and the lose
    /// screen has the same destructive body in `LoseMenuBtn.OnMouseUp`. Both are gated by the button's own
    /// identity, so quit, options and the other lose buttons still do what the game made them do.
    ///
    /// Installed once at boot and left in, like every other gate: there is no window where a restart could
    /// slip through half-installed while a session starts up.
    /// </summary>
    public static class RestartGate
    {
        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.restart").PatchAll(typeof(RestartGate));
                Log.Info("hook", "the pause-menu restart buttons are gated " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Error("hook", "gating the pause-menu restart buttons failed: " + e.GetType().Name + " "
                    + e.Message + " - a peer that presses restart takes its own lawn down and loads nothing");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.PauseMenu_Btn), "Restart")]
        public static class Restart
        {
            private static bool Prefix()
            {
                try
                {
                    return !LevelSync.TryCaptureRestart();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the restart gate threw: " + e.GetType().Name + " " + e.Message
                        + ", letting the game restart the lawn itself");
                    return true;
                }
            }
        }

        /// <summary>The button that really carries the restart body in this build.</summary>
        [HarmonyPatch(typeof(Il2Cpp.PauseMenu_Btn), "OnMouseUp")]
        public static class PauseButton
        {
            private static bool Prefix(Il2Cpp.PauseMenu_Btn __instance)
            {
                try
                {
                    Il2Cpp.PauseMenu menu = Il2Cpp.PauseMenu.Instance;
                    if (!ApiBase.Present(menu) || !ApiBase.Present(__instance))
                    {
                        return true;
                    }

                    UnityEngine.GameObject restart = menu.btnRestart;
                    UnityEngine.GameObject own = __instance.gameObject;
                    if (!ApiBase.Present(restart) || !ApiBase.Present(own)
                        || own.GetInstanceID() != restart.GetInstanceID())
                    {
                        return true;
                    }

                    return !LevelSync.TryCaptureRestart();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the pause-menu button gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game run the button itself");
                    return true;
                }
            }
        }

        /// <summary>The lose screen's restart, which carries the same inlined destructiveness.</summary>
        [HarmonyPatch(typeof(Il2Cpp.LoseMenuBtn), "OnMouseUp")]
        public static class LoseButton
        {
            private static bool Prefix(Il2Cpp.LoseMenuBtn __instance)
            {
                try
                {
                    if (!ApiBase.Present(__instance)
                        || __instance.type != Il2Cpp.LoseMenuBtn.LoseBtnType.Restart)
                    {
                        return true;
                    }

                    return !LevelSync.TryCaptureRestart();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the lose-menu restart gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game run the button itself");
                    return true;
                }
            }
        }
    }
}
