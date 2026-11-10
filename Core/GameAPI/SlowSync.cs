using System;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The slow-mode button in the bottom right corner. The game's `SlowTrigger` is the only thing that
    /// changes the lawn's clock, and it toggles `Time.timeScale` between normal and slow. In a room the
    /// toggle belongs to everybody: a press on any seat is heard by the host, the host runs the same native
    /// toggle on its own lawn and tells the room, and every other end replays it.
    ///
    /// The press itself is never blocked, which is the same rule pause follows: the game's own animation and
    /// cool timer run first, so the button never feels dead, and the state that follows is broadcast after
    /// the fact. A client's press is therefore applied locally at once and then agreed with the host; if the
    /// host was already in another state, the host's answer corrects it.
    ///
    /// While the room is paused the toggle is only stored, because the game's pause owns `timeScale = 0`;
    /// `Tick` puts the slow state back as soon as the room moves again.
    /// </summary>
    public static class SlowSync
    {
        public const string Capability = "mp-slow";

        /// <summary>How long a failed replay waits before it is tried again, so a broken trigger cannot spam.</summary>
        private const int RetryMillis = 1000;

        private static MpSession _room;

        /// <summary>True while this end runs the native toggle for an order, so its own door stays quiet.</summary>
        private static bool _applying;

        private static bool _slow;
        private static int _nextRetry;

        public static void Attach(MpSession room)
        {
            _room = room;

            try
            {
                new HarmonyLib.Harmony("PvzMP.slow").PatchAll(typeof(SlowSync));
                Log.Info("hook", "slow mode is gated on the room " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the slow-mode hooks could not be installed");
                Log.Error("hook", "gating slow mode failed: " + e.GetType().Name + " " + e.Message
                    + " - only the seat that presses it will slow down");
            }
        }

        /// <summary>Run once per frame: puts the room's slow state back after a pause reset the clock.</summary>
        public static void Tick()
        {
            if (!TheRoomDecides() || _applying || Entities.Board() == null)
            {
                return;
            }

            if (!_slow || IsSlow())
            {
                return;
            }

            // A paused lawn owns timeScale = 0 and a menu owns 1; only the running lawn can be slowed.
            if (Time.timeScale != 1f)
            {
                return;
            }

            int now = Environment.TickCount;
            if (unchecked(now - _nextRetry) < 0)
            {
                return;
            }

            _nextRetry = now + RetryMillis;

            if (ToggleNative())
            {
                Log.Info("mp", "the room's slow mode is put back after the lawn moved again "
                    + ApiBase.FrameTag());
            }
            else
            {
                Log.Warn("mp", "the room's slow mode could not be put back: the game's trigger failed");
            }
        }

        /// <summary>A new lawn starts at the game's own speed.</summary>
        public static void ForgetTheLawn()
        {
            _applying = false;
            _slow = false;
            _nextRetry = 0;
        }

        /// <summary>Where the slow frames arrive; WorldSync forwards them here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.SlowRequest:
                    ApplyRequest(peerId);
                    break;

                case MsgAction.SlowState:
                    ApplyState(peerId, Protocol.Payload<SlowStatePayload>(frame));
                    break;
            }
        }

        /// <summary>A seat pressed the button: the host runs the room's toggle and says what came of it.</summary>
        private static void ApplyRequest(int peerId)
        {
            if (_room == null || !_room.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " asked for slow mode, but this end is not the host");
                return;
            }

            if (Entities.Board() == null || Time.timeScale == 0f
                || App.ReadGameStatus() == (int)Il2Cpp.GameStatus.Pause)
            {
                Log.Info("mp", "peer " + peerId + " asked for slow mode while the room is paused:"
                    + " it is ignored until the lawn moves");
                return;
            }

            if (!ToggleNative())
            {
                Log.Warn("mp", "peer " + peerId + " asked for slow mode, but this end could not toggle it");
                return;
            }

            _slow = IsSlow();

            _room.BroadcastGameplay(MsgAction.SlowState, new SlowStatePayload { Slow = _slow });

            Log.Info("mp", "peer " + peerId + " toggled slow mode, the room is now "
                + (_slow ? "slow" : "normal") + " " + ApiBase.FrameTag());
        }

        /// <summary>The host's word on the room's clock, run natively unless this end is already there.</summary>
        private static void ApplyState(int peerId, SlowStatePayload state)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry == null || !entry.IsHost)
            {
                Log.Warn("mp", "peer " + peerId + " tried to set the room's slow mode: only the host says so");
                return;
            }

            _slow = state.Slow;

            if (IsSlow() == state.Slow)
            {
                return;
            }

            if (Time.timeScale == 0f || App.ReadGameStatus() == (int)Il2Cpp.GameStatus.Pause)
            {
                Log.Info("mp", "the host says the room is " + (state.Slow ? "slow" : "normal")
                    + " while paused: it is stored and applied when the lawn moves");
                return;
            }

            if (!ToggleNative())
            {
                Log.Warn("mp", "the host's slow state could not be applied: the game's trigger failed");
                return;
            }

            _slow = state.Slow;

            Log.Info("mp", "the host's slow mode is played here: the room is now "
                + (IsSlow() ? "slow" : "normal") + " " + ApiBase.FrameTag());
        }

        /// <summary>Asked by `SlowTrigger.TriggerSlow` after the game's own toggle really ran.</summary>
        private static void AfterNativeToggle()
        {
            Observe();
        }

        /// <summary>
        /// Asked by `SlowTrigger.Update` after every frame. The hotkey path may change the clock without
        /// going through `TriggerSlow`, so the state is also observed here; whichever door moved it, the
        /// change is reported once and the second door sees the same value.
        /// </summary>
        private static void AfterNativeToggleFrame()
        {
            Observe();
        }

        private static void Observe()
        {
            if (_applying || !TheRoomDecides() || Entities.Board() == null)
            {
                return;
            }

            bool slow = IsSlow();
            if (slow == _slow)
            {
                return;
            }

            _slow = slow;

            if (_room.IsHost)
            {
                _room.BroadcastGameplay(MsgAction.SlowState, new SlowStatePayload { Slow = slow });

                Log.Info("mp", "the host toggled slow mode, the room is now "
                    + (slow ? "slow" : "normal") + " " + ApiBase.FrameTag());
            }
            else
            {
                _room.SendToHost(MsgAction.SlowRequest, null);

                Log.Info("mp", "slow mode was toggled here, the host is asked to match "
                    + ApiBase.FrameTag());
            }
        }

        /// <summary>The game's own toggle, run on this end's own button, with the door told to stay quiet.</summary>
        private static bool ToggleNative()
        {
            _applying = true;

            try
            {
                Il2Cpp.SlowTrigger trigger = Trigger();
                if (!ApiBase.Present(trigger))
                {
                    Log.Warn("mp", "this lawn has no SlowTrigger, the room's slow state cannot be applied");
                    return false;
                }

                return ApiBase.TryCall("SlowTrigger.TriggerSlow", () => trigger.TriggerSlow(), Capability);
            }
            finally
            {
                _applying = false;
            }
        }

        private static Il2Cpp.SlowTrigger Trigger()
        {
            return ApiBase.Query("FindSlowTrigger", () =>
            {
                InGameUI ui = InGameUI.Instance;
                if (ApiBase.Present(ui) && ApiBase.Present(ui.SlowTrigger))
                {
                    Il2Cpp.SlowTrigger button = ui.SlowTrigger.GetComponent<Il2Cpp.SlowTrigger>();
                    if (ApiBase.Present(button))
                    {
                        return button;
                    }
                }

                return UnityEngine.Object.FindObjectOfType<Il2Cpp.SlowTrigger>();
            }, Capability);
        }

        /// <summary>Slow is the game's own clock between the pause and the normal speed.</summary>
        private static bool IsSlow()
        {
            float scale = Time.timeScale;
            return scale > 0f && scale < 0.999f;
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides && !ApiBase.IsBroken(Capability);
        }

        [HarmonyPatch(typeof(Il2Cpp.SlowTrigger), "TriggerSlow")]
        public static class TriggerDoor
        {
            private static void Postfix()
            {
                try
                {
                    AfterNativeToggle();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the slow-mode gate threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.SlowTrigger), "Update")]
        public static class UpdateDoor
        {
            private static void Postfix()
            {
                try
                {
                    AfterNativeToggleFrame();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the slow-mode watch threw: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
