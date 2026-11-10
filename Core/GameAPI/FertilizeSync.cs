using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The fertilizer of a room. A fertilizer does its work on the lawn that held it: the drop is the game's
    /// own body, and nothing in it is a number another lawn could reuse - it writes no health and calls no
    /// recover door, so what a seat ends up with is that seat's own plant, whole again after its own
    /// fertilizer. Proof: ref/EVIDENCE.md E48.
    ///
    /// So the cell travels and the result does not. The lawn that dropped the fertilizer says only where,
    /// the host repeats the ask as the room's order after putting its own plant of that cell back whole, and
    /// every other lawn heals what stands on that cell with its own numbers, through the game's own
    /// `Plant.Recover`. A plant that was already whole is not touched, so a seat that hears about a
    /// fertilizing it did itself - the host telling it back the cell it named - heals nothing twice.
    ///
    /// A type change is the other half of the ask, and it needs its own frame: the body that changes a
    /// plant leaves the old object and puts a new kind on the same cell (E53), and the layout only ever
    /// travels from the host, so a seat that was told the cell healed keeps standing on the kind its own
    /// fertilizer left behind. The growth therefore says itself, a third of a second after the drop that
    /// asked for it, from the lawn it happened on, and every other lawn replaces the plant of that cell
    /// that holds the kind which left.
    /// </summary>
    public static class FertilizeSync
    {
        private static MpSession _room;

        /// <summary>Told by the boot: which room a fertilized cell has to be said to.</summary>
        public static void Attach(MpSession room)
        {
            _room = room;

            Log.Info("mp", "a fertilizer is the lawn that held it: the cell travels and every lawn heals"
                + " its own plant there, and what that lawn grew travels with its two kinds "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the fertilizer watch after the game's own drop on a cell ran. This lawn is already
        /// changed by the body that just ran, so all that is left is to say which cell it was.
        /// </summary>
        public static void AfterFertilize(int column, int row)
        {
            if (!TheRoomDecides())
            {
                return;
            }

            FertilizedPayload cell = new FertilizedPayload
            {
                OriginPeer = _room.LocalPeerId,
                Column = column,
                Row = row
            };

            if (_room.IsHost)
            {
                _room.BroadcastGameplay(MsgAction.Fertilized, cell);
            }
            else
            {
                _room.SendToHost(MsgAction.Fertilized, cell);
            }

            Log.Info("mp", "a fertilizer went onto cell " + column + "," + row + " of this lawn, "
                + (_room.IsHost ? "every other seat is told " : "the host is asked ") + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the fertilizer watch after the game's own body that changes a plant ran. This lawn holds
        /// the kind that grew already, so all that is left is to say which cell and which two kinds.
        /// </summary>
        public static void AfterGrow(Growth[] grown)
        {
            if (!TheRoomDecides())
            {
                return;
            }

            for (int i = 0; i < grown.Length; i++)
            {
                Growth one = grown[i];

                FertilizeGrewPayload grew = new FertilizeGrewPayload
                {
                    OriginPeer = _room.LocalPeerId,
                    Column = one.Column,
                    Row = one.Row,
                    OldType = one.OldType,
                    NewType = one.NewType
                };

                if (_room.IsHost)
                {
                    _room.BroadcastGameplay(MsgAction.FertilizeGrew, grew);
                }
                else
                {
                    _room.SendToHost(MsgAction.FertilizeGrew, grew);
                }

                Log.Info("mp", "cell " + one.Column + "," + one.Row + " of this lawn grew from type "
                    + one.OldType + " to type " + one.NewType + ", "
                    + (_room.IsHost ? "every other seat is told " : "the host is asked ") + ApiBase.FrameTag());
            }
        }

        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (!TheRoomDecides())
            {
                return;
            }

            if (frame.Action == MsgAction.Fertilized)
            {
                AnswerTheCell(peerId, frame);
            }
            else if (frame.Action == MsgAction.FertilizeGrew)
            {
                AnswerTheGrowth(peerId, frame);
            }
        }

        private static void AnswerTheCell(int peerId, DecodedFrame frame)
        {
            FertilizedPayload cell = Protocol.Payload<FertilizedPayload>(frame);

            if (_room.IsHost)
            {
                // A seat asking the room. Whose hand it was is what the transport says and not what the
                // frame claims, and the host's own lawn never held that fertilizer, so the host heals too.
                cell.OriginPeer = peerId;

                HealTheCell(cell);
                _room.BroadcastGameplay(MsgAction.Fertilized, cell);

                return;
            }

            if (!TheHostSaidIt(peerId, frame))
            {
                return;
            }

            HealTheCell(cell);
        }

        private static void AnswerTheGrowth(int peerId, DecodedFrame frame)
        {
            FertilizeGrewPayload grew = Protocol.Payload<FertilizeGrewPayload>(frame);

            if (_room.IsHost)
            {
                grew.OriginPeer = peerId;

                GrowTheCell(grew);
                _room.BroadcastGameplay(MsgAction.FertilizeGrew, grew);

                return;
            }

            if (!TheHostSaidIt(peerId, frame))
            {
                return;
            }

            GrowTheCell(grew);
        }

        /// <summary>
        /// This lawn putting its own plants of that cell back whole. A cell can hold more than one plant, so
        /// every plant the lawn reads there is healed rather than one of them picked.
        /// </summary>
        private static void HealTheCell(FertilizedPayload cell)
        {
            if (!FertilizeRules.HealsHere(cell.OriginPeer, _room.LocalPeerId))
            {
                return;
            }

            PlantState[] plants = Plants.ReadAll();
            int here = 0;
            int healed = 0;

            for (int i = 0; plants != null && i < plants.Length; i++)
            {
                PlantState one = plants[i];
                if (one.Column != cell.Column || one.Row != cell.Row)
                {
                    continue;
                }

                here++;

                if (Plants.Heal(one.InstanceId) > 0)
                {
                    healed++;
                }
            }

            Log.Info("mp", "seat " + cell.OriginPeer + " fertilized cell " + cell.Column + "," + cell.Row
                + ": this lawn holds " + here + " plant(s) there and healed " + healed + " of them "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// This lawn putting the kind another lawn grew onto the cell it holds. The kind that left is asked
        /// for rather than "whatever stands there", because a cell can hold more than one plant and only the
        /// one the fertilizer found is the room's to move. The game's own creator makes the plant that
        /// arrives, so it comes with the game's own health and the game's own upgrade chain, and it keeps the
        /// room id of the plant it grew from: to the room this is the same plant, only of another kind.
        /// The old object is destroyed first and the creator is asked with the free flag, which is what
        /// `Fertilize.Upgrade` itself does - a plain spawn on an occupied cell returns nothing (E53, E56).
        /// </summary>
        private static void GrowTheCell(FertilizeGrewPayload grew)
        {
            if (!FertilizeRules.GrowsHere(grew.OriginPeer, _room.LocalPeerId))
            {
                return;
            }

            PlantState[] plants = Plants.ReadAll();
            int here = 0;
            int changed = 0;

            for (int i = 0; plants != null && i < plants.Length; i++)
            {
                PlantState one = plants[i];

                if (one.Column != grew.Column || one.Row != grew.Row)
                {
                    continue;
                }

                here++;

                if (one.PlantType != grew.OldType)
                {
                    continue;
                }

                NetEntity record = _room.Entities.ByInstanceId(one.InstanceId);

                int instanceId = Plants.Grow(one.InstanceId, grew.Column, grew.Row, grew.NewType);
                if (instanceId == 0)
                {
                    continue;
                }

                if (record != null && record.Kind == NetKind.Plant)
                {
                    _room.Entities.Bind(record.NetId, instanceId);
                    record.Type = grew.NewType;
                }

                changed++;
            }

            Log.Info("mp", "seat " + grew.OriginPeer + " grew cell " + grew.Column + "," + grew.Row
                + " from type " + grew.OldType + " to type " + grew.NewType + ": this lawn holds " + here
                + " plant(s) there and grew " + changed + " of them " + ApiBase.FrameTag());
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides;
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action + ": only the host says which cell"
                + " was fertilized " + ApiBase.FrameTag());

            return false;
        }
    }
}
