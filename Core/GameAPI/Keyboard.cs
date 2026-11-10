using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The legacy Input manager, which is what this build actually reads: the measured live run
    /// delivered F1/F2 through it, and the mobile-derived port reports no touch at all.
    ///
    /// Pure reads, so nothing here can change single player; the edge tracking is per key and only
    /// costs a bool, because a hotkey has to fire once per press rather than once per frame.
    /// </summary>
    public static class Keyboard
    {
        public const string Capability = "keyboard";

        public static bool WasPressed(KeyCode key)
        {
            return ApiBase.Query("Input.GetKeyDown(" + key + ")",
                () => Input.GetKeyDown(key), Capability);
        }
    }
}
