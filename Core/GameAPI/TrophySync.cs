using System;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The victory cup a level leaves at the end of a round. The game builds it with `Lawnf.SetAward`
    /// from seventeen native doors and collects it in `PrizeMgr.Clicked`, and most of those doors are
    /// this lawn's own per-frame win check: measured live, a peer was asked for a cup 21.6 seconds before
    /// the host's cup reached that end, and 45.8 seconds after the host's lift had been replayed there
    /// the same end was clicking a cup again (E39). So in a room the cup is the host's sentence and
    /// nothing else: its `SetAward` is followed by a `TrophySpawn` frame with the same position and flags,
    /// its click by a `TrophyCollect` frame, and a peer builds nothing of its own.
    ///
    /// The peer half does not depend on which board the game names. Comparing it with the board this end
    /// can name lets a cup through exactly when the two differ, which is the level change - and a
    /// cup that arrives that way is one no end announced, that this end cannot lift and the room cannot
    /// forget. Instead the room's cup is one object at a time: building the host's takes down whatever
    /// this lawn was showing, the host's click is followed by the same, and the level boundary does it
    /// whether or not a frame arrived.
    /// </summary>
    public static class TrophySync
    {
        public const string Capability = "mp-trophy";

        private static MpSession _room;
        private static bool _applying;

        /// <summary>Whether the room currently stands on one cup this end is told to hold.</summary>
        private static bool _roomHoldsCup;

        public static void Attach(MpSession room)
        {
            _room = room;

            try
            {
                new HarmonyLib.Harmony("PvzMP.trophy").PatchAll(typeof(TrophySync));
                Log.Info("hook", "the victory cup is the host's: only its spawn and click are replayed "
                    + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the victory-cup hooks could not be installed");
                Log.Error("hook", "gating the victory cup failed: " + e.GetType().Name + " " + e.Message
                    + " - every end builds its own cup");
            }
        }

        public static void ForgetTheLawn()
        {
            _applying = false;
            _roomHoldsCup = false;

            TakeDown("the room's level is over", 0);
        }

        /// <summary>
        /// Asked by the sweep key: take down the cups this lawn is showing when the room stands on none.
        /// The announced cup is not swept: it is the host's to lift, and a key that could hide it would
        /// hide the one reward the room agreed on.
        /// </summary>
        public static int SweepCups()
        {
            if (_roomHoldsCup)
            {
                return 0;
            }

            return TakeDown("the sweep key asked for what the room does not hold", 0);
        }

        /// <summary>Where the host's cup frames arrive; WorldSync forwards them here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (!TheHostSaidIt(peerId, frame))
            {
                return;
            }

            switch (frame.Action)
            {
                case MsgAction.TrophySpawn:
                    ApplySpawn(Protocol.Payload<TrophySpawnPayload>(frame));
                    break;

                case MsgAction.TrophyCollect:
                    ApplyCollect(Protocol.Payload<TrophyCollectPayload>(frame));
                    break;
            }
        }

        /// <summary>
        /// Asked before the game builds a cup. The host lets its own through and tells the room
        /// afterwards; a peer in a room refuses its own whatever the game names as the board, because the
        /// host's spawn is what times the room's cup and the peer reaches the door out of its own per-frame
        /// win check. Which board the game named is still a question, but it is the announcing half that
        /// asks it: a cup built on some minigame's own board is not the room's to describe.
        /// </summary>
        private static bool TryCaptureSpawn()
        {
            if (TrophyRules.RunsOwnCup(TheRoomDecides(), LevelSync.TheRoomIsInALevel, IsHostSeat(),
                                      _applying))
            {
                return true;
            }

            Log.Info("mp", "the victory cup is not built here: the host's cup is what the room sees "
                + ApiBase.FrameTag());

            return false;
        }

        /// <summary>Asked after the host's cup was built: send the arguments its own creator used.</summary>
        private static void NoteSpawn(PrizeMgr cup, object[] args)
        {
            Board lawn = Entities.Board();

            if (!TheRoomDecides() || _applying || !_room.IsHost || !ApiBase.Present(cup)
                || args == null || args.Length < 4 || lawn == null
                || !NamesThisLawn(args[0] as Board, lawn))
            {
                return;
            }

            Vector2 position = (Vector2)args[1];
            bool killZombie = (bool)args[2];
            bool fake = (bool)args[3];

            _roomHoldsCup = true;

            _room.BroadcastGameplay(MsgAction.TrophySpawn, new TrophySpawnPayload
            {
                X = position.x,
                Y = position.y,
                KillZombie = killZombie,
                Fake = fake
            });

            Log.Info("mp", "the host's victory cup appeared at " + position.x + "," + position.y
                + " (killZombie=" + killZombie + ", fake=" + fake + "), the room is told "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked before the game collects a cup. Only the host's click runs; a peer's click is refused
        /// while the room stands in a level, and the host's `TrophyCollect` frame plays the click on this
        /// end's own cup.
        /// </summary>
        private static bool TryCaptureCollect(PrizeMgr cup)
        {
            bool decides = TheRoomDecides();

            if (!TrophyRules.RunsOwnCup(decides, LevelSync.TheRoomIsInALevel, IsHostSeat(), _applying))
            {
                Log.Info("mp", "the victory cup is not lifted here: only the host lifts the room's cup "
                    + ApiBase.FrameTag());

                return false;
            }

            if (!decides || _applying || !IsHostSeat() || Entities.Board() == null)
            {
                return true;
            }

            Vector3 at = ApiBase.Present(cup) ? cup.transform.position : Vector3.zero;

            _room.BroadcastGameplay(MsgAction.TrophyCollect, new TrophyCollectPayload
            {
                X = at.x,
                Y = at.y
            });

            _roomHoldsCup = false;

            Log.Info("mp", "the host lifts the victory cup at " + at.x + "," + at.y
                + ", the room is told " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>The host's cup, built with the game's own creator and the host's arguments.</summary>
        private static void ApplySpawn(TrophySpawnPayload spawn)
        {
            if (spawn == null || !TheRoomDecides() || _applying || Entities.Board() == null)
            {
                return;
            }

            Board board = Entities.Board();

            // One cup at a time: a cup this end is already showing belongs to an older sentence, and left
            // beside the host's only one of the two can ever be lifted.
            TakeDown("the host announced the room's cup", 0);

            _roomHoldsCup = true;
            _applying = true;

            try
            {
                PrizeMgr cup = ApiBase.Query("Lawnf.SetAward", () => Lawnf.SetAward(board,
                    new Vector2(spawn.X, spawn.Y), spawn.KillZombie, spawn.Fake), Capability);

                Log.Info("mp", "the host's victory cup is built here: "
                    + (ApiBase.Present(cup) ? "the game's own SetAward ran" : "the game returned no cup")
                    + " " + ApiBase.FrameTag());
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// The host's click, played on this end's own cup. Which of this lawn's objects the host's world
        /// coordinates mean is not answerable from another machine, so the click runs on the cup this end
        /// finds nearest to those coordinates and every *other* cup still standing here is taken down.
        /// The cup that was clicked stays where it is and finishes the lift the game started: from this
        /// moment the room holds no cup, and a cup that outlives that is one nobody can lift.
        /// </summary>
        private static void ApplyCollect(TrophyCollectPayload collect)
        {
            if (collect == null || !TheRoomDecides() || _applying || Entities.Board() == null)
            {
                return;
            }

            _roomHoldsCup = false;

            PrizeMgr cup = Find(collect.X, collect.Y);

            // Read before the click: the lift is a coroutine on this object and the sweep below must not
            // name the cup that is rising.
            int clicked = ApiBase.Present(cup) ? cup.GetInstanceID() : 0;

            if (ApiBase.Present(cup))
            {
                _applying = true;

                try
                {
                    ApiBase.TryCall("PrizeMgr.Clicked", () => cup.Clicked(), Capability);

                    Log.Info("mp", "the host's victory-cup click ran here " + ApiBase.FrameTag());
                }
                finally
                {
                    _applying = false;
                }
            }
            else
            {
                Log.Warn("mp", "the host lifted the victory cup, but this lawn has no cup to click");
            }

            TakeDown("the host lifted the room's cup", clicked);
        }

        /// <summary>
        /// Takes down every cup this lawn is showing except the one named, and answers how many. The
        /// game's own click is the only door that ends a cup with its reward, and that door is the host's
        /// to open, so what is left for this end is the object: the click itself hides the cup's parts with
        /// `SetActive`, and a hidden cup is reached by no click and no animation. The cup that was just
        /// clicked is the one thing not to name here - its lift is a coroutine on that very object, and
        /// hiding it on the same frame is a cup that vanishes instead of rising.
        /// </summary>
        private static int TakeDown(string reason, int keepInstanceId)
        {
            int taken = 0;

            ApiBase.TryCall("hide the cups this lawn is showing", () =>
            {
                PrizeMgr[] all = Resources.FindObjectsOfTypeAll<PrizeMgr>();

                for (int i = 0; all != null && i < all.Length; i++)
                {
                    PrizeMgr cup = all[i];
                    if (!ApiBase.Present(cup) || !ApiBase.Present(cup.gameObject))
                    {
                        continue;
                    }

                    if (TrophyRules.IsLifting(keepInstanceId, cup.GetInstanceID()))
                    {
                        continue;
                    }

                    GameObject go = cup.gameObject;
                    if (!go.scene.IsValid() || !go.activeInHierarchy)
                    {
                        continue;
                    }

                    go.SetActive(false);
                    taken++;
                }
            }, Capability);

            if (taken > 0)
            {
                Log.Info("mp", taken + " victory cup(s) taken down here: " + reason + " "
                    + ApiBase.FrameTag());
            }

            return taken;
        }

        private static bool IsHostSeat()
        {
            return _room != null && _room.IsHost;
        }

        /// <summary>
        /// Whether the cup the game just built stands on the lawn this end is speaking about. Five of the
        /// seventeen doors are a minigame manager's own `Update` and hand `SetAward` a board of theirs
        /// (E39), and that cup is not the room's to describe.
        /// </summary>
        private static bool NamesThisLawn(Board named, Board lawn)
        {
            return !ApiBase.Present(named) || named.GetInstanceID() == lawn.GetInstanceID();
        }

        /// <summary>The scene's trophy, reaching inactive pages only through the scene test.</summary>
        private static PrizeMgr Find(float x, float y)
        {
            return ApiBase.Query("FindVictoryTrophy", () =>
            {
                PrizeMgr[] all = Resources.FindObjectsOfTypeAll<PrizeMgr>();
                PrizeMgr best = null;
                float bestDistance = float.MaxValue;

                for (int i = 0; i < all.Length; i++)
                {
                    PrizeMgr cup = all[i];
                    if (!ApiBase.Present(cup))
                    {
                        continue;
                    }

                    GameObject go = cup.gameObject;
                    if (!ApiBase.Present(go) || !go.scene.IsValid() || !go.activeInHierarchy)
                    {
                        continue;
                    }

                    Vector3 at = cup.transform.position;
                    float dx = at.x - x;
                    float dy = at.y - y;
                    float distance = dx * dx + dy * dy;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = cup;
                    }
                }

                return best != null ? best : UnityEngine.Object.FindObjectOfType<PrizeMgr>();
            }, Capability);
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides && !ApiBase.IsBroken(Capability);
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action
                + ": only the host builds and lifts the room's cup");

            return false;
        }

        [HarmonyPatch(typeof(Lawnf), "SetAward")]
        public static class SpawnDoor
        {
            private static bool Prefix()
            {
                try
                {
                    return TryCaptureSpawn();
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the victory-cup spawn gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game build its cup");
                    return true;
                }
            }

            private static void Postfix(PrizeMgr __result, object[] __args)
            {
                try
                {
                    NoteSpawn(__result, __args);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the victory-cup spawn report threw: " + e.GetType().Name + " "
                        + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(PrizeMgr), "Clicked")]
        public static class ClickDoor
        {
            private static bool Prefix(PrizeMgr __instance)
            {
                try
                {
                    return TryCaptureCollect(__instance);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the victory-cup click gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game collect it");
                    return true;
                }
            }
        }
    }
}
