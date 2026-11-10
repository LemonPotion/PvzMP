using System.Collections.Generic;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// What the other seats are about to do. Every end tells the room what its own pointer holds - the
    /// cell, the card on the mouse, or one of the four tools - and every other end shows that as a ghost
    /// made by the game's own preview, tinted per seat and named with the seat's nickname. The host passes
    /// a peer's word on to the other peers; nobody sees their own ghost.
    ///
    /// One method, `Draw`, answers both a peer's word and this end's own hand, so a seat's ghost is the
    /// same picture on every screen and the local preview is not a second implementation of it. What a
    /// hand draws:
    ///
    /// - a card over an empty cell draws that plant. The game previews a held card under its own cursor
    ///   already, so for the hand that holds it there is nothing to add and nothing is drawn;
    /// - a card over a cell that already holds a plant draws the type the game's own fusion table says the
    ///   two make, and draws nothing when that table has no recipe, because that is a cell the game would
    ///   refuse. This is the one picture the local player cannot get from the game;
    /// - the shovel, the hammer, the cart and a held fertilizer draw the game's own picture of that tool
    ///   over the pointed cell, and only for the seats that are not holding it: the holding end has the real
    ///   tool under its own cursor already, so a copy there is the second tool a player complains about.
    ///   The picture is taken from the tool of the lawn doing the drawing, because a seat that never picked
    ///   a shovel up still has the shovel of its own level;
    /// - a glove that carries a plant or a zombie draws what it carries, and a glove that carries nothing
    ///   draws itself;
    /// - a bare hand draws nothing at all: a plant that already stands needs no second picture of itself.
    ///
    /// Cursors travel in the cosmetic category: the transport keeps only the newest frame per peer, so a
    /// lagging cursor never backs up and never delays a plant.
    /// </summary>
    public static class PreviewSync
    {
        /// <summary>Nothing in the game's plant table goes this high; a bigger number is a broken peer.</summary>
        private const int MaxPlantType = 4096;

        private const int MaxCell = 31;

        private static MpSession _room;

        private static readonly Dictionary<int, Ghost> Ghosts = new Dictionary<int, Ghost>();

        /// <summary>This end's own hand, drawn by the same code that draws a peer's.</summary>
        private static readonly Ghost Local = new Ghost();

        /// <summary>The last word this end let go of, kept so a still pointer sends nothing.</summary>
        private static CursorIntentPayload _sent;

        /// <summary>True once the lawn is lost and until the next level: a lost lawn's pointers mean nothing.</summary>
        private static bool _ended;

        private static int _mixFirst = -1;
        private static int _mixSecond = -1;
        private static int _mixResult = Mixes.NoRecipe;

        /// <summary>One seat's ghost, its nickname and the word they were made from.</summary>
        private sealed class Ghost
        {
            public GameObject Object;
            public GameObject Nick;
            public string NickText;
            public int Column = Cursor.NoCell;
            public int Row = Cursor.NoCell;
            public int PlantType = Cursor.NoCard;
            public int ZombieType = Cursor.NoCard;
            public int Tool;
        }

        public static void Attach(MpSession room)
        {
            _room = room;
            room.CosmeticHandler = HandleFrame;

            Log.Info("mp", "the other seats' hands and pointers are shown from their own words "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Run once per frame: keeps this end's ghost current and sends its pointer when it changed. Outside
        /// the room's level nothing is drawn and nothing is asked, because a ghost is made by the game's own
        /// preview, and out of a level that body throws on every call - a remembered pointer of another seat
        /// would otherwise be paid for per frame. The room's own answer is asked rather than a board, which
        /// is still there when the level is not.
        /// </summary>
        public static void Tick()
        {
            if (_room == null)
            {
                return;
            }

            if (_ended)
            {
                if (Ghosts.Count > 0 || Local.Column != Cursor.NoCell)
                {
                    DropEverything();
                }

                return;
            }

            if (!_room.RoomDecides || !LevelSync.TheRoomIsInALevel)
            {
                if (Ghosts.Count > 0 || Local.Column != Cursor.NoCell)
                {
                    ForgetTheLawn();
                }

                return;
            }

            DropGhostsOfGonePeers();

            CursorIntentPayload own = ReadOwnIntent();

            Draw(Local, own, _room.LocalPeerId, true);

            SendIfChanged(own);
        }

        /// <summary>
        /// What this end's hand holds and where it points, in the same shape a peer's word arrives in.
        /// A glove with cargo takes the place of the card on the mouse, because it is what the hand carries;
        /// a tool in the hand puts the card back on the shelf.
        /// </summary>
        private static CursorIntentPayload ReadOwnIntent()
        {
            int column = Cursor.ReadColumn();
            int row = Cursor.ReadRow();
            bool shovel = Cursor.ReadShovelPickedUp();
            bool hammer = Cursor.ReadHammerPickedUp();
            bool wheel = Cursor.ReadWheelPickedUp();
            bool fertilizer = Cursor.ReadFertilizerPickedUp();

            int glovePlant = Cursor.ReadPlantOnGloveType();
            int gloveZombie = Cursor.ReadZombieOnGloveType();
            bool glove = glovePlant != Cursor.NoCard || gloveZombie != Cursor.NoCard
                || Cursor.ReadGlovePickedUp();

            int plantType = shovel || hammer || wheel || glove || fertilizer ? Cursor.NoCard
                : Cursor.ReadHeldCardPlantType();

            if (glovePlant != Cursor.NoCard)
            {
                plantType = glovePlant;
            }

            if (column < 0 || row < 0)
            {
                column = Cursor.NoCell;
                row = Cursor.NoCell;
                plantType = Cursor.NoCard;
                shovel = false;
                glove = false;
                hammer = false;
                wheel = false;
                fertilizer = false;
            }

            return new CursorIntentPayload
            {
                PeerId = _room.LocalPeerId,
                Column = column,
                Row = row,
                PlantType = plantType,
                Shovel = shovel,
                Glove = glove,
                GloveZombieType = gloveZombie,
                Hammer = hammer,
                Wheel = wheel,
                Fertilizer = fertilizer
            };
        }

        private static void SendIfChanged(CursorIntentPayload intent)
        {
            if (Same(_sent, intent))
            {
                return;
            }

            _sent = intent;

            if (_room.IsHost)
            {
                _room.BroadcastCosmetic(MsgAction.CursorIntent, intent);
            }
            else if (!_room.SendCosmeticToHost(MsgAction.CursorIntent, intent))
            {
                Log.Warn("mp", "the cursor intent could not leave this end");
            }
        }

        private static bool Same(CursorIntentPayload sent, CursorIntentPayload intent)
        {
            if (sent == null)
            {
                return false;
            }

            return sent.Column == intent.Column && sent.Row == intent.Row
                && sent.PlantType == intent.PlantType && sent.Shovel == intent.Shovel
                && sent.Glove == intent.Glove && sent.GloveZombieType == intent.GloveZombieType
                && sent.Hammer == intent.Hammer && sent.Wheel == intent.Wheel
                && sent.Fertilizer == intent.Fertilizer;
        }

        /// <summary>Where a seat's cursor arrives. The host relays it and shows it; a peer shows it.</summary>
        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Action != MsgAction.CursorIntent)
            {
                Log.Warn("mp", "no one handles " + frame.Name + " from peer " + peerId);
                return;
            }

            CursorIntentPayload intent = Protocol.Payload<CursorIntentPayload>(frame);

            if (!Acceptable(peerId, intent))
            {
                return;
            }

            if (_room.IsHost)
            {
                _room.BroadcastCosmetic(MsgAction.CursorIntent, intent, peerId);
            }

            if (_ended)
            {
                return;
            }

            Ghost ghost;
            if (!Ghosts.TryGetValue(intent.PeerId, out ghost))
            {
                ghost = new Ghost();
                Ghosts.Add(intent.PeerId, ghost);
            }

            Draw(ghost, intent, intent.PeerId, false);
        }

        /// <summary>A new level takes every ghost down with it, and the next look is sent again.</summary>
        public static void ForgetTheLawn()
        {
            DropEverything();
            _ended = false;
        }

        /// <summary>
        /// The lawn is lost. The room does not leave its level for a lost lawn - that is a level change -
        /// so the last cursor word each seat sent stays on every other screen and the lose menu shows a
        /// frozen glove or zombie in a corner. Every ghost goes down here, and an empty word goes out so
        /// the other screens take this seat's ghost down too; until the next level, no pointer is drawn
        /// or sent from this end. Proof: ref/EVIDENCE.md E64.
        /// </summary>
        public static void TheLevelWasLost(string reason)
        {
            if (_room == null)
            {
                return;
            }

            int ghosts = Ghosts.Count;

            DropEverything();
            _ended = true;

            SendIfChanged(new CursorIntentPayload
            {
                PeerId = _room.LocalPeerId,
                Column = Cursor.NoCell,
                Row = Cursor.NoCell,
                PlantType = Cursor.NoCard,
                GloveZombieType = Cursor.NoCard
            });

            Log.Info("mp", "the level was lost"
                + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")") + ": " + ghosts
                + " ghost(s) were taken down and this end's next word is an empty pointer "
                + ApiBase.FrameTag());
        }

        /// <summary>Every ghost down, this end's own included; the word that was sent is forgotten.</summary>
        private static void DropEverything()
        {
            foreach (Ghost ghost in Ghosts.Values)
            {
                Drop(ghost);
            }

            Ghosts.Clear();
            Hide(Local);
            _sent = null;
        }

        /// <summary>The game's table lookup, cached because this runs every frame over one cell.</summary>
        private static int MergeOf(int first, int second)
        {
            if (first == _mixFirst && second == _mixSecond)
            {
                return _mixResult;
            }

            _mixFirst = first;
            _mixSecond = second;
            _mixResult = Mixes.Result(first, second);
            return _mixResult;
        }

        /// <summary>
        /// Whose word this is. A peer's frame must name itself, the host's frame may name any of its seats,
        /// and this end's own word is never drawn from a frame - `Tick` draws it from the hand directly.
        /// </summary>
        private static bool Acceptable(int peerId, CursorIntentPayload intent)
        {
            if (_room.IsHost)
            {
                if (intent.PeerId != peerId)
                {
                    Log.Warn("mp", "peer " + peerId + " sent a cursor claiming to be peer " + intent.PeerId);
                    return false;
                }
            }
            else
            {
                RosterEntry sender = _room.Roster.Find(peerId);
                if (sender == null || !sender.IsHost)
                {
                    Log.Warn("mp", "peer " + peerId + " sent a cursor: only the host passes cursors on");
                    return false;
                }
            }

            return intent.PeerId != _room.LocalPeerId;
        }

        /// <summary>Keeps the dictionary to the seats that are still here.</summary>
        private static void DropGhostsOfGonePeers()
        {
            if (Ghosts.Count == 0)
            {
                return;
            }

            List<int> gone = null;

            foreach (int peerId in Ghosts.Keys)
            {
                RosterEntry entry = _room.Roster.Find(peerId);
                if (entry == null || !entry.Connected)
                {
                    if (gone == null)
                    {
                        gone = new List<int>();
                    }

                    gone.Add(peerId);
                }
            }

            if (gone == null)
            {
                return;
            }

            foreach (int peerId in gone)
            {
                Drop(Ghosts[peerId]);
                Ghosts.Remove(peerId);
            }
        }

        /// <summary>
        /// The picture a hand asks for, whether it is this end's or a peer's: the tool it holds, the plant
        /// it carries, or the mix its card and the standing plant make. Anything the game would refuse in
        /// that cell leaves the cell empty, and a ghost that already shows this hand stays where it stands.
        /// </summary>
        private static void Draw(Ghost ghost, CursorIntentPayload intent, int peerId, bool ownEnd)
        {
            if (intent.Column < 0 || intent.Row < 0 || intent.Column > MaxCell || intent.Row > MaxCell)
            {
                Hide(ghost);
                return;
            }

            int tool = ToolOf(intent);
            int plantType = Cursor.NoCard;
            int zombieType = Cursor.NoCard;

            if (tool != 0)
            {
                if (ownEnd)
                {
                    // The game carries the real tool under its own player's cursor, so a see-through copy of
                    // it at the pointed cell is a second tool on the screen and nothing more. A tool is a
                    // picture for the other seats, the way a held card already is.
                    Hide(ghost);
                    return;
                }

                // A tool is drawn as itself: what the hand points at is the cell it works on.
            }
            else if (intent.Glove)
            {
                if (intent.GloveZombieType != Cursor.NoCard)
                {
                    zombieType = intent.GloveZombieType;
                }
                else if (intent.PlantType != Cursor.NoCard)
                {
                    // The glove carries a plant, not a card, so the cell's own plant is not fused here.
                    plantType = intent.PlantType;
                }
                else
                {
                    Hide(ghost);
                    return;
                }
            }
            else if (intent.PlantType == Cursor.NoCard)
            {
                Hide(ghost);
                return;
            }
            else
            {
                NetEntity plant = _room.Entities.PlantAt(intent.Column, intent.Row);

                if (plant == null)
                {
                    if (ownEnd)
                    {
                        // The game draws this one itself, under the player's own cursor.
                        Hide(ghost);
                        return;
                    }

                    plantType = intent.PlantType;
                }
                else
                {
                    plantType = MergeOf(plant.Type, intent.PlantType);

                    if (plantType == Mixes.NoRecipe)
                    {
                        Hide(ghost);
                        return;
                    }
                }
            }

            if (plantType != Cursor.NoCard && (plantType < 0 || plantType > MaxPlantType))
            {
                Hide(ghost);
                return;
            }

            if (zombieType != Cursor.NoCard && (zombieType < 0 || zombieType > MaxPlantType))
            {
                Hide(ghost);
                return;
            }

            string nick = ownEnd ? null : NickOf(peerId);

            if (ApiBase.Present(ghost.Object) && ghost.Column == intent.Column && ghost.Row == intent.Row
                && ghost.PlantType == plantType && ghost.ZombieType == zombieType
                && ghost.Tool == tool && ghost.NickText == nick)
            {
                return;
            }

            Previews.Destroy(ghost.Object);

            GameObject made = tool != 0
                ? Previews.CreateTool(tool, intent.Column, intent.Row)
                : zombieType != Cursor.NoCard
                    ? Previews.CreateZombie(zombieType, intent.Column, intent.Row, SeatTint.Of(peerId))
                    : Previews.Create(plantType, intent.Column, intent.Row, SeatTint.Of(peerId));

            ghost.Object = made;
            ghost.Column = intent.Column;
            ghost.Row = intent.Row;
            ghost.PlantType = plantType;
            ghost.ZombieType = zombieType;
            ghost.Tool = tool;

            if (made == null)
            {
                Previews.Destroy(ghost.Nick);
                ghost.Nick = null;
                ghost.NickText = null;
                return;
            }

            int layer = Previews.SortingLayerOf(made);

            if (ApiBase.Present(ghost.Nick) && ghost.NickText == nick)
            {
                Previews.PlaceNick(ghost.Nick, intent.Column, intent.Row, layer);
                return;
            }

            Previews.Destroy(ghost.Nick);

            ghost.Nick = string.IsNullOrEmpty(nick) ? null
                : Previews.CreateNick(nick, intent.Column, intent.Row, SeatTint.Label(peerId), layer);
            ghost.NickText = nick;
        }

        /// <summary>
        /// Which of the game's tools this hand holds, or zero for a hand that holds none. The hammer, the
        /// cart and the shovel are drawn as themselves; an empty glove is drawn as the glove, while a
        /// loaded one is drawn as what it carries. The fertilizer is a bucket and not a tool of the strip,
        /// but a hand that holds one is pointed at a plant the same way.
        /// </summary>
        private static int ToolOf(CursorIntentPayload intent)
        {
            if (intent.Hammer)
            {
                return Previews.ToolHammer;
            }

            if (intent.Wheel)
            {
                return Previews.ToolWheel;
            }

            if (intent.Shovel)
            {
                return Previews.ToolShovel;
            }

            if (intent.Fertilizer)
            {
                return Previews.ToolFertilizer;
            }

            if (intent.Glove && intent.PlantType == Cursor.NoCard
                && intent.GloveZombieType == Cursor.NoCard)
            {
                return Previews.ToolGlove;
            }

            return 0;
        }

        private static string NickOf(int peerId)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            return entry == null || string.IsNullOrEmpty(entry.Nick) ? "peer " + peerId : entry.Nick;
        }

        private static void Hide(Ghost ghost)
        {
            Drop(ghost);
            ghost.Column = Cursor.NoCell;
            ghost.Row = Cursor.NoCell;
            ghost.PlantType = Cursor.NoCard;
            ghost.ZombieType = Cursor.NoCard;
            ghost.Tool = 0;
            ghost.NickText = null;
        }

        private static void Drop(Ghost ghost)
        {
            Previews.Destroy(ghost.Object);
            Previews.Destroy(ghost.Nick);
            ghost.Object = null;
            ghost.Nick = null;
        }
    }
}
