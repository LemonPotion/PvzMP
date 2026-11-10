using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The seed chooser as a room. The game lets every seat pick cards on its own screen, so two players
    /// could take the same plant and only one of them would ever get it on the host's lawn. The host
    /// therefore owns a small draft: a plant type belongs to one seat until that seat puts it back, and
    /// every other end darkens its copies of that card and writes the holder's name on them.
    ///
    /// The cards live in the selection UI, not in the lawn, so this class reads every `CardUI` in the
    /// scene, including the pages that are not on screen, and writes the draft onto each of them. Nothing
    /// is remembered between two reads: a card that was destroyed, rebuilt, or first seen late is met by
    /// the next sweep exactly like a card that has been there all along. A card added by the game's own
    /// pre-selection is not a player's pick and is excluded by a depth flag around `InitBoard`'s own
    /// preselection calls, a flag this end clears again every time the chooser opens or closes because a
    /// postfix that never ran would otherwise take the draft away from every later chooser as well.
    ///
    /// What the chooser shows of the room is the draft and nothing else: every card is asked who holds its
    /// plant, and the answer is the game's own disabled card with the holder's nickname written on it in
    /// that seat's colour. A draft order that arrives before this end has a chooser waits until one opens,
    /// and a seat that appears while the host is choosing is told the whole draft for that one seat.
    ///
    /// Nothing here decides whether a plant can be chosen: the player's click still runs the game's
    /// `AddCardToBank`, and the host only arbitrates who claimed what. A refusal is in the log and in the
    /// frame that carries it, not on the screen - this mod writes nothing on the game's notice line.
    /// </summary>
    public static class PlantSelect
    {
        public const string Capability = "mp-draft";

        /// <summary>
        /// How often the cards are swept again while a chooser is up, for the cards the first sweep could
        /// not see: `CardUI..ctor` writes a new card as available (E44) and the chooser's other pages are
        /// filled in as the player opens them. Nothing else can undo a mark - the game reads `disabled` in
        /// `CardUI.Update+0x7B` and `PutDown+0x9E` and never writes it (E44, E45) - so this is about late
        /// cards arriving, not about a fight with the chooser over a field.
        /// </summary>
        private const int RefreshMillis = 250;

        /// <summary>
        /// The chooser's own darkening, by child index: what `CardUI.Update` switches on while `disabled`
        /// is set and `CardUI.PutDown` computes from `disabled`, the cooldown and the price (E45).
        /// </summary>
        private const int ShadowChild = 3;

        private static MpSession _room;

        private static readonly PlantDraft Draft = new PlantDraft();

        /// <summary>
        /// The cards a local click added, by card instance. The game lets one bank hold two copies of a
        /// plant, so a type is not an identity here: a refusal has to be able to take both of them back.
        /// </summary>
        private static readonly Dictionary<int, CardUI> LocalTaken = new Dictionary<int, CardUI>();

        /// <summary>
        /// Depth of the game's own preselection; an add inside it is not a player's pick. It cannot be
        /// trusted to reach zero on its own: a postfix is skipped when the original throws or when
        /// another mod's prefix skips it, and a counter left raised would quietly stop the draft for
        /// every chooser after that one. `Tick` clears it on both edges of the chooser's life.
        /// </summary>
        private static int _preselecting;

        /// <summary>Whether the chooser was up on the previous tick, so a change of state can be seen.</summary>
        private static bool _wasOpen;

        /// <summary>Plant types whose card was already reported as not selectable, so the log is not spammed.</summary>
        private static readonly HashSet<int> BlockedLogged = new HashSet<int>();

        /// <summary>Plant types already reported as having both allowed slots in this bank.</summary>
        private static readonly HashSet<int> DuplicateLogged = new HashSet<int>();

        /// <summary>True only while this end runs a card change of its own, so its gates stay quiet.</summary>
        private static bool _applying;

        /// <summary>
        /// Picks and frees that were said while this end had no chooser up. A seat that appears while the
        /// host is already choosing is told the draft at a moment its own `InitBoard` is still loading, and
        /// a draft that arrives into a closed chooser is the one thing the sweep in `Tick` must throw away,
        /// so the orders wait here until there is a chooser to carry them out on.
        /// </summary>
        private static readonly DraftHold Hold = new DraftHold();

        private static int _nextRefresh;

        public static void Attach(MpSession room)
        {
            _room = room;
            room.PeerSeated += Resync;

            try
            {
                new HarmonyLib.Harmony("PvzMP.draft").PatchAll(typeof(PlantSelect));
                Log.Info("hook", "the seed chooser is gated on the room " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                ApiBase.Disable(Capability, "the seed chooser gates could not be installed");
                Log.Error("hook", "gating the seed chooser failed: " + e.GetType().Name + " " + e.Message
                    + " - two players can pick the same plant");
            }
        }

        /// <summary>
        /// Which plants the room says a seat holds, smallest type first. This is the draft and nothing
        /// else, so it answers for the whole level the chooser picked for and not only while that chooser
        /// is on the screen: a seat's plants are still its plants once the lawn is fighting.
        /// </summary>
        public static List<int> PlantsHeldBy(int peerId)
        {
            return Draft.PlantsHeldBy(peerId);
        }

        /// <summary>
        /// A seat that appeared while this chooser is up has heard no pick in its life, so every plant the
        /// room has given away is said to it again - to that seat alone, on the same lane the original
        /// order travelled. Only the host owns the draft: what a peer's own copy holds is a copy of what
        /// the host said, and telling a newcomer twice is the same as telling it once.
        /// </summary>
        private static void Resync(int peerId)
        {
            if (!_room.IsHost || !SelectionOpen())
            {
                return;
            }

            KeyValuePair<int, int>[] pairs = Draft.Snapshot();
            int told = 0;

            for (int i = 0; i < pairs.Length; i++)
            {
                if (_room.SendToPeer(peerId, MsgAction.PlantPick, new PlantPickPayload
                {
                    PlantType = pairs[i].Key,
                    OwnerPeer = pairs[i].Value
                }))
                {
                    told++;
                }
            }

            Log.Info("mp", "peer " + peerId + " is told the seed draft: " + told + " of " + pairs.Length
                + " plant type(s) are taken " + ApiBase.FrameTag());
        }

        /// <summary>Run once per frame from the game's main thread.</summary>
        public static void Tick()
        {
            if (_room == null)
            {
                return;
            }

            bool open = SelectionOpen();
            if (open != _wasOpen)
            {
                // The chooser opening or closing is the one moment this end can be sure no preselection
                // of the game is running: the call chain that raises the flag is synchronous and cannot
                // straddle a frame. Whatever a skipped postfix left behind is paid here.
                _preselecting = 0;
                _wasOpen = open;

                if (!open)
                {
                    // Orders kept for a chooser that closed without opening again are not for the next one.
                    Hold.GiveUp();
                }
            }

            if (!_room.RoomDecides)
            {
                if (Draft.Owners.Count > 0 || LocalTaken.Count > 0)
                {
                    ForgetTheLawn();
                }

                return;
            }

            if (!open)
            {
                // The chooser is shut and the level is running, but the draft is what a hover on a seat's
                // name answers with, and that question is asked all the way through the level. So closing
                // the chooser keeps the answers and only the chooser's own work is put away. A new level
                // clears the draft, and `LevelSync` does that before the new lawn is numbered.
                return;
            }

            if (Hold.Count > 0)
            {
                DrainHold();
            }

            if (_room.IsHost)
            {
                ReleaseGonePeers();
            }

            int now = Environment.TickCount;
            if (unchecked(now - _nextRefresh) < 0)
            {
                return;
            }

            _nextRefresh = now + RefreshMillis;
            SweepCards();
        }

        /// <summary>A new lawn: every claim and every mark belongs to the chooser that is gone.</summary>
        public static void ForgetTheLawn()
        {
            Draft.Clear();
            LocalTaken.Clear();
            BlockedLogged.Clear();
            DuplicateLogged.Clear();
            Hold.GiveUp();
            _preselecting = 0;
            _applying = false;
            _nextRefresh = 0;

            SweepCards();
        }

        /// <summary>Where the draft frames arrive; WorldSync forwards them here.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            switch (frame.Action)
            {
                case MsgAction.PlantPickRequest:
                    if (_room.IsHost)
                    {
                        ApplyRequest(peerId, Protocol.Payload<PlantPickRequestPayload>(frame));
                    }

                    break;

                case MsgAction.PlantPick:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        PlantPickPayload pick = Protocol.Payload<PlantPickPayload>(frame);
                        Say(pick.PlantType, pick.OwnerPeer, true);
                    }

                    break;

                case MsgAction.PlantFree:
                    if (_room.IsHost && !IsTheHost(peerId))
                    {
                        // A peer putting its own card back; the host accepts and repeats the order.
                        ReleaseFromPeer(peerId, Protocol.Payload<PlantFreePayload>(frame));
                    }
                    else if (TheHostSaidIt(peerId, frame))
                    {
                        PlantFreePayload free = Protocol.Payload<PlantFreePayload>(frame);
                        Say(free.PlantType, free.OwnerPeer, false);
                    }

                    break;

                case MsgAction.PlantDenied:
                    if (TheHostSaidIt(peerId, frame))
                    {
                        ApplyDenied(Protocol.Payload<PlantDeniedPayload>(frame));
                    }

                    break;

                default:
                    Log.Warn("mp", "no one handles " + frame.Name + " from peer " + peerId);
                    break;
            }
        }

        /// <summary>Asked by the card gate after a local click really put a card in the bank.</summary>
        public static void AfterAdded(CardUI card, bool added)
        {
            if (!added || _applying || _preselecting > 0 || !TheDraftDecides() || !SelectionOpen())
            {
                return;
            }

            if (!ApiBase.Present(card))
            {
                return;
            }

            int plantType = (int)card.thePlantType;
            if (plantType < 0)
            {
                return;
            }

            LocalTaken[card.GetInstanceID()] = card;
            PriceDuplicate(card);

            Log.Info("mp", "plant type " + plantType + " is added with cost " + card.theSeedCost
                + ", this bank holds " + BankCount(plantType) + " slot(s) " + ApiBase.FrameTag());

            if (_room.IsHost)
            {
                Take(plantType, _room.LocalPeerId);
                return;
            }

            _room.SendToHost(MsgAction.PlantPickRequest, new PlantPickRequestPayload { PlantType = plantType });

            Log.Info("mp", "plant type " + plantType + " is asked of the host " + ApiBase.FrameTag());
        }

        /// <summary>Asked by the card gate after a local click put a card back.</summary>
        public static void AfterRemoved(CardUI card, bool removed)
        {
            if (!removed || _applying || _preselecting > 0 || !TheDraftDecides() || !SelectionOpen())
            {
                return;
            }

            if (!ApiBase.Present(card))
            {
                return;
            }

            int plantType = (int)card.thePlantType;
            if (plantType < 0)
            {
                return;
            }

            LocalTaken.Remove(card.GetInstanceID());

            // A duplicate slot: the copy left in this bank still reserves the type for this seat, so the
            // room must not offer it to anybody else yet.
            if (BankCount(plantType) > 0)
            {
                Log.Info("mp", "plant type " + plantType + " still has a slot here, the room keeps it taken"
                    + " " + ApiBase.FrameTag());

                return;
            }

            if (_room.IsHost)
            {
                Release(plantType, _room.LocalPeerId);
                return;
            }

            _room.SendToHost(MsgAction.PlantFree, new PlantFreePayload
            {
                PlantType = plantType,
                OwnerPeer = _room.LocalPeerId
            });

            Log.Info("mp", "plant type " + plantType + " is returned to the room " + ApiBase.FrameTag());
        }

        /// <summary>The host taking a plant for itself, or arbitrating a peer's request.</summary>
        private static void Take(int plantType, int peerId)
        {
            if (!Draft.TryTake(plantType, peerId))
            {
                int owner = Draft.OwnerOf(plantType);

                if (peerId == _room.LocalPeerId)
                {
                    Log.Warn("mp", "plant type " + plantType + " was already taken by peer " + owner
                        + ", the local click is taken back");

                    RevertLocal(plantType);
                }
                else
                {
                    _room.SendToPeer(peerId, MsgAction.PlantDenied, new PlantDeniedPayload
                    {
                        PlantType = plantType,
                        Reason = TakenBy(plantType, owner)
                    });
                }

                return;
            }

            SweepCards();

            _room.BroadcastGameplay(MsgAction.PlantPick, new PlantPickPayload
            {
                PlantType = plantType,
                OwnerPeer = peerId
            });

            Log.Info("mp", "plant type " + plantType + " is taken by peer " + peerId
                + " for the room " + ApiBase.FrameTag());
        }

        private static void Release(int plantType, int peerId)
        {
            if (!Draft.Release(plantType, peerId))
            {
                return;
            }

            SweepCards();

            _room.BroadcastGameplay(MsgAction.PlantFree, new PlantFreePayload
            {
                PlantType = plantType,
                OwnerPeer = peerId
            });

            Log.Info("mp", "plant type " + plantType + " is free again, peer " + peerId
                + " returned it " + ApiBase.FrameTag());
        }

        private static void ApplyRequest(int peerId, PlantPickRequestPayload request)
        {
            if (!SelectionOpen())
            {
                _room.SendToPeer(peerId, MsgAction.PlantDenied, new PlantDeniedPayload
                {
                    PlantType = request.PlantType,
                    Reason = "the seed chooser is closed"
                });

                return;
            }

            Take(request.PlantType, peerId);
        }

        /// <summary>
        /// Carry a draft order out now, or keep it until this end has a chooser to carry it out on. The
        /// order of the frames is what makes the pair work: a plant given and put back leaves nobody
        /// holding it, and only the chooser that is up can say which cards that greys out.
        /// </summary>
        private static void Say(int plantType, int ownerPeer, bool taken)
        {
            if (SelectionOpen())
            {
                CarryOut(taken, plantType, ownerPeer);
                SweepCards();
                return;
            }

            Hold.Add(taken, plantType, ownerPeer);

            Log.Info("mp", "peer " + ownerPeer + (taken ? " took" : " put back") + " plant type "
                + plantType + " with no chooser up here, the order waits " + ApiBase.FrameTag());
        }

        /// <summary>Whose claim one order of the draft changes; the cards are swept by the caller.</summary>
        private static void CarryOut(bool taken, int plantType, int ownerPeer)
        {
            if (taken)
            {
                Draft.TryTake(plantType, ownerPeer);
                return;
            }

            Draft.Release(plantType, ownerPeer);
        }

        /// <summary>The orders kept for this chooser, carried out in the order they were said.</summary>
        private static void DrainHold()
        {
            List<DraftHold.Order> orders = Hold.Take();

            for (int i = 0; i < orders.Count; i++)
            {
                DraftHold.Order order = orders[i];
                CarryOut(order.Taken, order.PlantType, order.OwnerPeer);
            }

            SweepCards();

            Log.Info("mp", orders.Count + " draft order(s) waited for this chooser and are carried out "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// A peer putting its own card back. The owner is the peer that sent the frame, never the number it
        /// wrote about itself, so a seat cannot free another seat's plant.
        /// </summary>
        private static void ReleaseFromPeer(int peerId, PlantFreePayload free)
        {
            if (!Draft.Release(free.PlantType, peerId))
            {
                return;
            }

            SweepCards();

            _room.BroadcastGameplay(MsgAction.PlantFree, new PlantFreePayload
            {
                PlantType = free.PlantType,
                OwnerPeer = peerId
            });
        }

        /// <summary>The host said no: the local click is taken back so the card is not silently kept.</summary>
        private static void ApplyDenied(PlantDeniedPayload denial)
        {
            Draft.Release(denial.PlantType, _room.LocalPeerId);
            RevertLocal(denial.PlantType);
            SweepCards();

            // The card moved back out of the bank on its own, which the player cannot see: their click
            // looked accepted. The log says what the notice line used to.
            Log.Warn("mp", "the host refused plant type " + denial.PlantType + ": " + denial.Reason
                + ", the local click is taken back " + ApiBase.FrameTag());
        }

        /// <summary>What both ends say when a plant type already belongs to a seat.</summary>
        private static string TakenBy(int plantType, int owner)
        {
            return "peer " + owner + " already took plant type " + plantType;
        }

        /// <summary>
        /// Puts the cards this end added of a refused plant type back in the pool without telling the room
        /// about it. Every slot of that type goes: a copy left in this bank would keep showing a plant the
        /// room has just handed to another seat.
        /// </summary>
        private static void RevertLocal(int plantType)
        {
            var gone = new List<int>();

            foreach (KeyValuePair<int, CardUI> pair in LocalTaken)
            {
                CardUI card = pair.Value;
                if (ApiBase.Present(card) && (int)card.thePlantType == plantType)
                {
                    gone.Add(pair.Key);
                }
            }

            if (gone.Count == 0)
            {
                return;
            }

            _applying = true;

            try
            {
                InGameUI ui = InGameUI.Instance;

                for (int i = 0; i < gone.Count; i++)
                {
                    CardUI card = LocalTaken[gone[i]];
                    LocalTaken.Remove(gone[i]);

                    if (ApiBase.Present(ui) && ApiBase.Present(card))
                    {
                        ApiBase.TryCall("InGameUI.RemoveCardFromBank(revert)", () =>
                            ui.RemoveCardFromBank(card, true), Capability);
                    }
                }
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// Writes the draft onto every card the chooser has and onto nothing else. A card of a plant another
        /// seat holds takes the game's own dark picture and the holder's nickname; every other card is left
        /// the way the game made it. Nothing is remembered between two passes, so a card that came in on a
        /// page the player opened late, a card the game rebuilt and a card whose owner left are all answered
        /// by the next sweep over the whole chooser - which is also the only thing that can undo a mark,
        /// because nothing but this method writes `CardUI.disabled` (ref/EVIDENCE.md E44, E45).
        ///
        /// `isAvailable` is not written at all: `CardUI.Update` and `CardUI.CDUpdate` set it from the
        /// cooldown on their own (E44), and a card the chooser believes is paid for does not need the mod to
        /// say so again.
        /// </summary>
        private static void SweepCards()
        {
            if (_room == null)
            {
                return;
            }

            CardUI[] cards = AllCards();
            if (cards == null)
            {
                return;
            }

            ApiBase.Query("the draft is written on " + cards.Length + " card(s)", () =>
            {
                for (int i = 0; i < cards.Length; i++)
                {
                    MarkCard(cards[i]);
                }

                return cards.Length;
            }, Capability);
        }

        /// <summary>One card, told what the draft says about the plant it carries.</summary>
        private static void MarkCard(CardUI card)
        {
            int owner = Draft.OwnerOf((int)card.thePlantType);

            // This end never darkens its own card: the seat that holds a plant is shown it as pickable.
            bool held = owner >= 0 && owner != _room.LocalPeerId;

            if (card.disabled == held)
            {
                CardLabel.SetOwner(card, held ? NickOf(owner) : null, SeatTint.Label(owner));
                return;
            }

            card.disabled = held;

            if (!held)
            {
                PutShadowAway(card);
            }

            CardLabel.SetOwner(card, held ? NickOf(owner) : null, SeatTint.Label(owner));
        }

        /// <summary>
        /// The chooser's own darkening: the third child of a card, which the game switches on itself every
        /// frame its `disabled` byte is set and only ever switches off in `CardUI.PutDown` - the door a
        /// click on the card goes through (ref/EVIDENCE.md E45). So a byte this end cleared by itself leaves
        /// a dark card behind, which is exactly the plant that came back and still looks taken. The shadow
        /// is put away only when the mod's own mark goes away: a card the game darkened for a cooldown or a
        /// price is not ours to lighten, and `PutDown` answers those on the next click.
        /// </summary>
        private static void PutShadowAway(CardUI card)
        {
            bool put = ApiBase.Query("the shadow the draft lit is put away", () =>
            {
                Transform face = card.transform;

                if (face.childCount <= ShadowChild)
                {
                    return false;
                }

                Transform shadow = face.GetChild(ShadowChild);

                if (!ApiBase.Present(shadow))
                {
                    return false;
                }

                shadow.gameObject.SetActive(false);

                return true;
            }, Capability);

            if (!put)
            {
                return;
            }

            Log.Info("ui", "a card of plant type " + (int)card.thePlantType + " is free again and its own"
                + " shadow is taken off " + ApiBase.FrameTag());
        }

        /// <summary>What a seat is called on a card; a seat this end has never heard of is only a number.</summary>
        private static string NickOf(int peerId)
        {
            RosterEntry entry = _room.Roster == null ? null : _room.Roster.Find(peerId);
            string nick = entry == null ? null : entry.Nick;

            return string.IsNullOrEmpty(nick) ? "peer " + peerId : nick;
        }

        /// <summary>A seat that left takes its claims with it, so the plants become pickable again.</summary>
        private static void ReleaseGonePeers()
        {
            KeyValuePair<int, int>[] pairs = Draft.Snapshot();
            bool released = false;

            for (int i = 0; i < pairs.Length; i++)
            {
                int owner = pairs[i].Value;
                if (owner == _room.LocalPeerId || _room.Roster.Find(owner) != null)
                {
                    continue;
                }

                int plantType = pairs[i].Key;

                if (!Draft.Release(plantType, owner))
                {
                    continue;
                }

                released = true;

                _room.BroadcastGameplay(MsgAction.PlantFree, new PlantFreePayload
                {
                    PlantType = plantType,
                    OwnerPeer = owner
                });

                Log.Info("mp", "plant type " + plantType + " is free again: peer " + owner
                    + " left the room " + ApiBase.FrameTag());
            }

            if (released)
            {
                SweepCards();
            }
        }

        /// <summary>
        /// Every card the chooser has, including the pages that are not on screen: those are inactive
        /// objects and `FindObjectsOfType` would miss them, while the scene test drops the prefabs that
        /// `FindObjectsOfTypeAll` answers with. `PlantIcons` reads the same list for the game's own picture
        /// of a plant, which is on the card whether or not the page it sits on is open.
        /// </summary>
        internal static CardUI[] AllCards()
        {
            return ApiBase.Query("FindAllCardUI", () =>
            {
                var found = new List<CardUI>();
                CardUI[] all = Resources.FindObjectsOfTypeAll<CardUI>();

                for (int i = 0; i < all.Length; i++)
                {
                    CardUI card = all[i];
                    if (!ApiBase.Present(card))
                    {
                        continue;
                    }

                    GameObject go = card.gameObject;
                    if (!ApiBase.Present(go) || !go.scene.IsValid())
                    {
                        continue;
                    }

                    found.Add(card);
                }

                return found.ToArray();
            }, Capability);
        }

        /// <summary>How many copies of this plant this end's seed bank holds right now.</summary>
        private static int BankCount(int plantType)
        {
            return ApiBase.Query("CountBankCards(type=" + plantType + ")", () =>
            {
                Il2Cpp.InGameUI ui = Il2Cpp.InGameUI.Instance;
                if (!ApiBase.Present(ui) || !ApiBase.Present(ui.Cards))
                {
                    return 0;
                }

                Il2CppSystem.Collections.Generic.List<CardUI> cards = ui.Cards;
                int count = 0;

                for (int i = 0; i < cards.Count; i++)
                {
                    CardUI card = cards[i];
                    if (ApiBase.Present(card) && card.onCardBank
                        && (int)card.thePlantType == plantType)
                    {
                        count++;
                    }
                }

                return count;
            }, Capability);
        }

        /// <summary>
        /// The game's duplicate slot price: the second copy of a plant costs twice the first. The card the
        /// game just created is raised only when it still carries the first copy's price, so a build that
        /// already doubled it is left alone instead of being charged twice.
        /// </summary>
        private static void PriceDuplicate(CardUI card)
        {
            if (!TheDraftDecides() || !SelectionOpen() || !ApiBase.Present(card))
            {
                return;
            }

            int plantType = (int)card.thePlantType;
            if (plantType < 0)
            {
                return;
            }

            int cheapest = int.MaxValue;
            int slots = 0;

            ApiBase.Query("PriceDuplicate(type=" + plantType + ")", () =>
            {
                Il2Cpp.InGameUI ui = Il2Cpp.InGameUI.Instance;
                if (!ApiBase.Present(ui) || !ApiBase.Present(ui.Cards))
                {
                    return 0;
                }

                Il2CppSystem.Collections.Generic.List<CardUI> cards = ui.Cards;

                for (int i = 0; i < cards.Count; i++)
                {
                    CardUI other = cards[i];
                    if (!ApiBase.Present(other) || !other.onCardBank
                        || (int)other.thePlantType != plantType)
                    {
                        continue;
                    }

                    slots++;

                    int cost = other.theSeedCost;
                    if (cost > 0 && cost < cheapest)
                    {
                        cheapest = cost;
                    }
                }

                return slots;
            }, Capability);

            if (slots < 2 || cheapest <= 0 || cheapest == int.MaxValue)
            {
                return;
            }

            int own = card.theSeedCost;
            if (own <= 0 || own > cheapest)
            {
                // The game itself already priced this copy above the first; nothing to correct.
                return;
            }

            int doubled = cheapest * 2;

            ApiBase.TryCall("CardUI.theSeedCost = " + doubled, () => card.theSeedCost = doubled,
                Capability);

            Log.Info("mp", "plant type " + plantType + " second slot costs " + doubled
                + " (first " + cheapest + ") " + ApiBase.FrameTag());
        }

        /// <summary>True when this bank already holds both of the game's allowed copies of the plant.</summary>
        private static bool ExceedsDuplicateLimit(CardUI card)
        {
            if (!TheDraftDecides() || !SelectionOpen() || !ApiBase.Present(card))
            {
                return false;
            }

            int plantType = (int)card.thePlantType;
            if (plantType < 0 || BankCount(plantType) < 2)
            {
                return false;
            }

            if (DuplicateLogged.Add(plantType))
            {
                Log.Info("mp", "plant type " + plantType + " already has both slots here: the third card"
                    + " is not added " + ApiBase.FrameTag());
            }

            return true;
        }

        /// <summary>True when this card's plant type belongs to another seat and the add must not run.</summary>
        private static bool BlocksLocalClick(CardUI card)
        {
            if (_applying || _preselecting > 0 || !TheDraftDecides() || !SelectionOpen()
                || !ApiBase.Present(card))
            {
                return false;
            }

            int plantType = (int)card.thePlantType;
            if (plantType < 0)
            {
                return false;
            }

            int owner = Draft.OwnerOf(plantType);
            if (owner < 0 || owner == _room.LocalPeerId)
            {
                return false;
            }

            if (BlockedLogged.Add(plantType))
            {
                Log.Info("mp", "plant type " + plantType + " is held by peer " + owner
                    + ": its card is not selectable here " + ApiBase.FrameTag());
            }

            return true;
        }

        private static bool TheDraftDecides()
        {
            return _room != null && _room.RoomDecides && !ApiBase.IsBroken(Capability);
        }

        /// <summary>The chooser is up and has not started the level yet.</summary>
        private static bool SelectionOpen()
        {
            InitBoard board = InitBoard.Instance;
            return ApiBase.Present(board) && !board.ready;
        }

        private static bool IsTheHost(int peerId)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            return entry != null && entry.IsHost;
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action
                + ": only the host arbitrates the seed chooser");

            return false;
        }

        // -- Harmony doors -----------------------------------------------------

        [HarmonyPatch(typeof(InGameUI), "AddCardToBank")]
        public static class AddCardDoor
        {
            /// <summary>
            /// A card another seat already holds never reaches the bank at all. Greying it out is only how
            /// the chooser looks; the game's click still calls `AddCardToBank` on a dim card, which is why
            /// the refusal has to be here and not in the postfix.
            /// </summary>
            private static bool Prefix(object[] __args)
            {
                try
                {
                    CardUI card = __args != null && __args.Length > 0 ? __args[0] as CardUI : null;

                    return !BlocksLocalClick(card) && !ExceedsDuplicateLimit(card);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the seed pick gate threw before AddCardToBank: "
                        + e.GetType().Name + " " + e.Message + ", letting the game add it");
                    return true;
                }
            }

            private static void Postfix(CardUI card, bool __result)
            {
                try
                {
                    AfterAdded(card, __result);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the seed pick gate threw after AddCardToBank: "
                        + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(InGameUI), "RemoveCardFromBank")]
        public static class RemoveCardDoor
        {
            private static void Postfix(CardUI card, bool __result)
            {
                try
                {
                    AfterRemoved(card, __result);
                }
                catch (Exception e)
                {
                    Log.Error("hook", "the seed return gate threw after RemoveCardFromBank: "
                        + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(InitBoard), "PreSelectCard")]
        public static class PreSelectCardDoor
        {
            private static void Prefix()
            {
                _preselecting++;
            }

            private static void Postfix()
            {
                if (_preselecting > 0)
                {
                    _preselecting--;
                }
            }
        }

        [HarmonyPatch(typeof(InitBoard), "PreSelect")]
        public static class PreSelectDoor
        {
            private static void Prefix()
            {
                _preselecting++;
            }

            private static void Postfix()
            {
                if (_preselecting > 0)
                {
                    _preselecting--;
                }
            }
        }

        [HarmonyPatch(typeof(InitBoard), "AddCard")]
        public static class InitAddCardDoor
        {
            private static void Prefix()
            {
                _preselecting++;
            }

            private static void Postfix()
            {
                if (_preselecting > 0)
                {
                    _preselecting--;
                }
            }
        }
    }
}
