using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// How a session starts in a port where mouse clicks are unreliable: F1 hosts, F2 joins, F3 leaves,
    /// F4 takes off this lawn the objects the room never announced, and Escape cancels the dialog; a room
    /// never starts while a lawn exists - joining in the middle of somebody's battle is refused by the
    /// product rules, not by a preference.
    ///
    /// The battle test is the board, not `theGameStatus`: the measured live run read 0/InGame in the
    /// main menu and 3/Selecting elsewhere, so that field does not say whether a game is running.
    ///
    /// The questions belong to PromptDialog and the native screen, the room to MpSession; this class is
    /// the gate, the hotkeys, and the once-a-frame pump that keeps the room moving on the main thread.
    /// </summary>
    public static class ConnectionEntry
    {
        public const KeyCode HostKey = KeyCode.F1;
        public const KeyCode JoinKey = KeyCode.F2;
        public const KeyCode CancelKey = KeyCode.Escape;
        public const KeyCode LeaveKey = KeyCode.F3;
        public const KeyCode SweepKey = KeyCode.F4;

        private static readonly PromptDialog Dialog = new PromptDialog(
            new NativePromptScreen(), App.ReadPlayerName, OnSettings);

        private static readonly MpSession Session = new MpSession();

        /// <summary>The room this install talks about: the gameplay side is told which frames are its business.</summary>
        public static MpSession Room
        {
            get { return Session; }
        }

        /// <summary>Run once per frame from the game's main thread.</summary>
        public static void Tick()
        {
            // The room is pumped whether or not a dialog is open: a peer can leave while the local
            // player is answering a question.
            Session.Tick();

            // The room ended: the sentence is the session's rule about why, and the log is where it is said.
            string ended = Session.TakeRoomEndNotice();
            if (!string.IsNullOrEmpty(ended))
            {
                Log.Info("mp", ended + " " + ApiBase.FrameTag());
            }

            if (Dialog.IsActive)
            {
                if (Keyboard.WasPressed(CancelKey))
                {
                    Dialog.Cancel();
                }
                else
                {
                    Dialog.Tick();
                }

                return;
            }

            if (Keyboard.WasPressed(HostKey))
            {
                Open("host", true);
            }
            else if (Keyboard.WasPressed(JoinKey))
            {
                Open("join", false);
            }
            else if (Keyboard.WasPressed(LeaveKey))
            {
                Leave();
            }
            else if (Keyboard.WasPressed(SweepKey))
            {
                // Unlike the keys above this one is for a running level: it is how a player takes an
                // object off a lawn the room will not say a word about.
                LawnSweep.Sweep(Session);
            }
        }

        /// <summary>
        /// The main menu's Coop button answers through this same gate, so the button and the hotkey
        /// cannot drift apart: both refuse a running battle and a live session the same way, and both
        /// open the one native dialog PromptDialog owns.
        /// </summary>
        public static void HostFromMenu()
        {
            Open("coop host", true);
        }

        public static void JoinFromMenu()
        {
            Open("coop client", false);
        }

        private static void Open(string kind, bool asHost)
        {
            // What the game says about itself right now, in every press, because the measured run
            // showed theGameStatus is not a battle test: the main menu reads 0/InGame.
            Il2Cpp.Board board = Entities.Board();
            bool inBattle = board != null;

            Log.Info("mp", kind + " pressed: theGameStatus=" + App.DescribeStatus()
                + " lawn=" + (inBattle ? "present" : "none"));

            if (inBattle)
            {
                Log.Info("mp", "ignored " + kind + ": a battle is already running on this lawn");
                return;
            }

            if (Session.IsActive)
            {
                Log.Info("mp", "ignored " + kind + ": the session is already " + Session.Phase
                    + ", " + LeaveKey + " ends it first");
                return;
            }

            if (ApiBase.IsBroken(Menu.Capability))
            {
                Log.Warn("mp", "ignored " + kind + ": the " + Menu.Capability
                    + " capability is broken, no native dialog is possible");
                return;
            }

            if (!Menu.IsPromptRegistered())
            {
                Log.Error("mp", "ignored " + kind + ": " + Menu.PromptMenu
                    + " is not among the game's registered prefabs");
                return;
            }

            if (asHost)
            {
                Dialog.StartHost();
            }
            else
            {
                Dialog.StartJoin();
            }
        }

        private static void Leave()
        {
            if (!Session.IsActive)
            {
                Log.Info("mp", LeaveKey + " pressed with no session running");
                return;
            }

            Session.Leave("the player pressed " + LeaveKey);
        }

        private static void OnSettings(ConnectionSettings settings)
        {
            Log.Info("mp", "entry ready: " + settings + " " + ApiBase.FrameTag());

            if (settings.AsHost)
            {
                Session.Host(settings.Nick, settings.Port);
            }
            else
            {
                Session.Join(settings.Host, settings.Port, settings.Nick);
            }
        }
    }
}
