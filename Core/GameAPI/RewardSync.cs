using System;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The treasure card the level leaves behind when it ends - the plant reward, not the sun and cards a
    /// zombie drops. In a room only the host picks it up: the host's click runs the game's own
    /// `DroppedTreasureCard.OnClick` and the room is told which plant was taken, so every other end runs the
    /// same click on its own copy of the card and the reward is collected once for everyone.
    ///
    /// The ordinary `DroppedCard` a zombie leaves is a different class and is deliberately not touched: those
    /// drops stay local, exactly as they are in single player.
    /// </summary>
    public static class RewardSync
    {
        public const string Capability = "mp-reward";

        private static MpSession _room;
        private static bool _applying;

        public static void Attach(MpSession room)
        {
            _room = room;

            try
            {
                new HarmonyLib.Harmony("PvzMP.reward").PatchAll(typeof(RewardSync));
                Log.Info("hook", "the level's treasure card is gated on the room " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the treasure-card gate could not be installed");
                Log.Error("hook", "gating the treasure card failed: " + e.GetType().Name + " " + e.Message
                    + " - every seat has to pick the reward up for itself");
            }
        }

        public static void ForgetTheLawn()
        {
            _applying = false;
        }

        /// <summary>Where the reward frame arrives; WorldSync forwards it here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Action != MsgAction.RewardClaim)
            {
                return;
            }

            if (!TheHostSaidIt(peerId, frame))
            {
                return;
            }

            ApplyClaim(Protocol.Payload<RewardClaimPayload>(frame));
        }

        /// <summary>Asked by the treasure card before the game's own click runs.</summary>
        private static bool TryCapture(DroppedTreasureCard card)
        {
            if (_applying || _room == null || !_room.RoomDecides || ApiBase.IsBroken(Capability)
                || Entities.Board() == null)
            {
                return true;
            }

            if (_room.IsHost)
            {
                if (ApiBase.Present(card))
                {
                    _room.BroadcastGameplay(MsgAction.RewardClaim, new RewardClaimPayload
                    {
                        PlantType = (int)card.thePlantType
                    });

                    Log.Info("mp", "the host takes the level's treasure card of type "
                        + (int)card.thePlantType + ", the room is told " + ApiBase.FrameTag());
                }

                return true;
            }

            Log.Info("mp", "the level's treasure card is not taken here: the host takes the reward for"
                + " the room " + ApiBase.FrameTag());

            return false;
        }

        /// <summary>The host's reward, clicked with this end's own mouse so the game does the rest.</summary>
        private static void ApplyClaim(RewardClaimPayload claim)
        {
            DroppedTreasureCard card = Find(claim.PlantType);

            if (!ApiBase.Present(card))
            {
                Log.Warn("mp", "the host took the treasure card of type " + claim.PlantType
                    + ", but this lawn has no such card");
                return;
            }

            _applying = true;

            try
            {
                Il2Cpp.Mouse mouse = ApiBase.Query("Mouse.Instance", () => Il2Cpp.Mouse.Instance, Capability);

                if (!ApiBase.Present(mouse))
                {
                    Log.Warn("mp", "the host's treasure card cannot be clicked here: no Mouse instance");
                    return;
                }

                bool taken = ApiBase.Query("DroppedTreasureCard.OnClick", () => card.OnClick(mouse), Capability);

                Log.Info("mp", "the host's treasure card of type " + claim.PlantType + " is clicked here: "
                    + (taken ? "the game's own click ran" : "the game did not take it")
                    + " " + ApiBase.FrameTag());
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// The scene's card of that plant type. `FindObjectsOfTypeAll` reaches inactive pages too, so the
        /// scene test is what keeps prefab assets out of the answer.
        /// </summary>
        private static DroppedTreasureCard Find(int plantType)
        {
            return ApiBase.Query("FindDroppedTreasureCard(type=" + plantType + ")", () =>
            {
                DroppedTreasureCard[] all = Resources.FindObjectsOfTypeAll<DroppedTreasureCard>();

                for (int i = 0; i < all.Length; i++)
                {
                    DroppedTreasureCard card = all[i];
                    if (!ApiBase.Present(card) || (int)card.thePlantType != plantType)
                    {
                        continue;
                    }

                    GameObject go = card.gameObject;
                    if (ApiBase.Present(go) && go.scene.IsValid())
                    {
                        return card;
                    }
                }

                return UnityEngine.Object.FindObjectOfType<DroppedTreasureCard>();
            }, Capability);
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action
                + ": only the host takes the level's reward");

            return false;
        }

        [HarmonyPatch(typeof(DroppedTreasureCard), "OnClick")]
        public static class ClickDoor
        {
            private static bool Prefix(DroppedTreasureCard __instance)
            {
                try
                {
                    return TryCapture(__instance);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the treasure-card gate threw: " + e.GetType().Name + " "
                        + e.Message + ", letting the game take it");
                    return true;
                }
            }
        }
    }
}
