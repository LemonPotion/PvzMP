using Il2Cpp;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Answers the one question the connection dialog cannot be written without: is the game's own
    /// menu stack reachable from the main-menu screen, and does it know the text-prompt prefab.
    /// NativeSmoke only sees frame 0, where UIManager legitimately does not exist yet, so it cannot
    /// tell a menu scene that has not loaded from a build that never builds one.
    ///
    /// Pure reads, a verdict in at most three log lines, then it stops for the rest of the session.
    /// </summary>
    public static class MenuWatch
    {
        public const int CheckEveryFrames = 30;
        public const int CheckLimit = 120;

        private static int _checksDone;
        private static int _framesSinceCheck;
        private static bool _reported;

        public static void Tick()
        {
            if (_reported || _checksDone >= CheckLimit)
            {
                return;
            }

            _framesSinceCheck++;
            if (_framesSinceCheck < CheckEveryFrames)
            {
                return;
            }

            _framesSinceCheck = 0;
            _checksDone++;

            bool live = ApiBase.Present(Menu.Loader());

            if (_checksDone == 1)
            {
                Log.Info("menu", "watch check 1 " + ApiBase.FrameTag()
                    + " UIManager=" + (live ? "present" : "null"));
            }

            if (live)
            {
                _reported = true;
                Log.Info("menu", "menu stack is live " + ApiBase.FrameTag()
                    + " after " + _checksDone + " checks: prefabs=" + Menu.RegisteredPrefabs()
                    + " promptRegistered=" + Menu.IsPromptRegistered());
                return;
            }

            if (_checksDone == CheckLimit)
            {
                _reported = true;
                Log.Warn("menu", "UIManager stayed null for " + _checksDone + " checks ("
                    + CheckLimit * CheckEveryFrames + " frames, last " + ApiBase.FrameTag()
                    + ") - the native menu stack is not usable from this screen");
            }
        }
    }
}
