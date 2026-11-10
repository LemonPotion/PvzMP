using System;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The graves of a room. Where they stand, which kind of grave each cell holds and which zombie comes
    /// out of one are all rolls, and a roll belongs to one lawn: the host's. This class sends the host's
    /// grave layout as a whole - the same shape the plant layout already uses - and a peer makes its own
    /// graves match it, cell by cell, with the game's own creator and the game's own `Die`.
    ///
    /// The whole layout goes rather than only the changes because the doors that roll graves are not
    /// reachable by a frame: `GridItem.SetGridItem` rolls inside its own body even though it takes the kind
    /// as a parameter, so a replay of that call is two graves, and `Board.SetGrave` and `Board.SupplyGrave`
    /// roll the cells they pass to it. A seat that joined late, or a level script that put a grave down
    /// while no frame was flowing, is corrected by the next layout instead of staying different (E42).
    ///
    /// The zombie half needs no new machinery. A grave does not carry a zombie in a field: it is the
    /// delivery, and the game calls `Grave.SetZombie` when the wave that owns that grave is due. A peer
    /// refuses that call, so its own roll of the type never happens, and the host's zombie arrives as the
    /// zombie it already is - minted and sent by `ZombieSync` the moment the host's creator returns it.
    /// </summary>
    public static class GraveSync
    {
        public const string Capability = "mp-graves";

        private const int LayoutMillis = 1000;

        private static MpSession _room;

        /// <summary>True only while this end is repeating the host's layout, so one layout cannot start another.</summary>
        private static bool _applying;

        private static int _layoutAt;

        public static void Attach(MpSession room)
        {
            _room = room;

            GraveGate.Install();

            Log.Info("mp", "the graves are the host's: its layout is sent and its rolls are not repeated "
                + ApiBase.FrameTag());
        }

        /// <summary>Run once per frame: only the host describes its graves.</summary>
        public static void Tick()
        {
            if (!TheRoomDecides() || !_room.IsHost || ApiBase.IsBroken(Capability))
            {
                return;
            }

            int now = Environment.TickCount;
            if (!Due(now, _layoutAt, LayoutMillis))
            {
                return;
            }

            _layoutAt = now;
            SendLayout();
        }

        /// <summary>
        /// Asked by the game's own creator on the host. A grave that was just rolled has to be on every
        /// screen before the zombie it delivers is due, so the layout is not left to the next second. It
        /// goes on the next frame rather than inside this call, because every lawn object - the crater a
        /// plant leaves, a frozen square - walks the same door, and a burst of them must not be a burst of
        /// frames.
        /// </summary>
        public static void NoteGraveRolled()
        {
            if (!TheRoomDecides() || !_room.IsHost || ApiBase.IsBroken(Capability))
            {
                return;
            }

            _layoutAt = 0;
        }

        /// <summary>
        /// Asked by the two doors that roll grave cells. True means this end may not roll them, which the
        /// gate turns into a skipped call: the cells arrive with the layout instead. The peer's own replay
        /// of the host's grave goes through `GridItem.SetGridItem`, which no prefix stops, so a replay does
        /// not need to be let through here.
        /// </summary>
        public static bool MayRollGraves(string what)
        {
            bool rolls = GraveRules.RollsOwnGraves(TheRoomDecides(), LevelSync.TheRoomIsInALevel,
                _room != null && _room.IsHost);

            if (!rolls)
            {
                Log.Info("mp", "no " + what + " is rolled here: the host's graves travel as its layout "
                    + ApiBase.FrameTag());
            }

            return rolls;
        }

        /// <summary>
        /// Asked by the door that opens a grave. A peer that let its own open would roll its own zombie
        /// type from the same body, so it keeps its grave closed and waits for the host's zombie and the
        /// host's layout; the opening animation stays on the lawn that decided it.
        /// </summary>
        public static bool MayOpenGrave()
        {
            bool opens = GraveRules.RollsOwnGraves(TheRoomDecides(), LevelSync.TheRoomIsInALevel,
                _room != null && _room.IsHost);

            if (!opens)
            {
                Log.Info("mp", "a grave is not opened here: the zombie that comes out of one is the host's "
                    + "roll " + ApiBase.FrameTag());
            }

            return opens;
        }

        public static void ForgetTheLawn()
        {
            _applying = false;
            _layoutAt = 0;
        }

        /// <summary>The graves this lawn holds, or null when there is no lawn to read.</summary>
        private static LawnGrave[] Read()
        {
            return ApiBase.Present(Entities.Board()) ? Graves.ReadAll() : null;
        }

        public static void HandleFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Action != MsgAction.GraveLayout)
            {
                return;
            }

            if (!TheHostSaidIt(peerId, frame))
            {
                return;
            }

            GraveLayoutPayload layout = Protocol.Payload<GraveLayoutPayload>(frame);

            if (_room.IsHost || ApiBase.IsBroken(Capability))
            {
                return;
            }

            ApplyLayout(layout);
        }

        /// <summary>The host's graves, as one frame.</summary>
        private static void SendLayout()
        {
            LawnGrave[] graves = Graves.ReadAll();
            if (graves == null)
            {
                return;
            }

            var snaps = new GraveSnap[graves.Length];

            for (int i = 0; i < graves.Length; i++)
            {
                snaps[i] = new GraveSnap
                {
                    Column = graves[i].Column,
                    Row = graves[i].Row,
                    GraveType = graves[i].GraveType
                };
            }

            _room.BroadcastGameplay(MsgAction.GraveLayout, new GraveLayoutPayload { Graves = snaps });

            Log.Info("mp", "grave layout: " + snaps.Length + " graves from the host " + ApiBase.FrameTag());
        }

        /// <summary>
        /// A peer making its graves look like the host's. Everything that has to leave goes first, because a
        /// cell the host keeps with a different kind is taken down and rebuilt, and the game's creator will
        /// not put a second grave on a cell that still holds one.
        /// </summary>
        private static void ApplyLayout(GraveLayoutPayload layout)
        {
            if (layout == null || layout.Graves == null || !TheRoomDecides() || _applying)
            {
                return;
            }

            LawnGrave[] local = Read();
            if (local == null)
            {
                Log.Warn("mp", "the host named " + layout.Graves.Length + " graves and this lawn cannot read"
                    + " its own " + ApiBase.FrameTag());

                return;
            }

            var host = new LawnGrave[layout.Graves.Length];

            for (int i = 0; i < layout.Graves.Length; i++)
            {
                GraveSnap snap = layout.Graves[i];
                if (snap == null)
                {
                    continue;
                }

                host[i] = new LawnGrave
                {
                    Column = snap.Column,
                    Row = snap.Row,
                    GraveType = snap.GraveType
                };
            }

            GraveChange change = GraveRules.Reconcile(host, local);
            if (change.IsEmpty)
            {
                return;
            }

            _applying = true;

            try
            {
                int built = 0;
                int removed = 0;

                for (int i = 0; i < change.ToRemove.Length; i++)
                {
                    LawnGrave gone = change.ToRemove[i];

                    if (Graves.TakeDown(gone.InstanceId))
                    {
                        removed++;

                        Log.Info("mp", "grave obj " + gone.InstanceId + " leaves cell " + gone.Column + ","
                            + gone.Row + ": the host's layout does not name it " + ApiBase.FrameTag());
                    }
                }

                for (int i = 0; i < change.ToBuild.Length; i++)
                {
                    LawnGrave wanted = change.ToBuild[i];

                    if (Graves.Build(wanted.Column, wanted.Row, wanted.GraveType) != 0)
                    {
                        built++;

                        Log.Info("mp", "grave kind " + wanted.GraveType + " grows on cell "
                            + wanted.Column + "," + wanted.Row + " as the host has it "
                            + ApiBase.FrameTag());
                    }
                }

                Log.Info("mp", "the graves of this lawn match the host's: " + built + " grown, "
                    + removed + " taken down, " + host.Length + " named " + ApiBase.FrameTag());
            }
            finally
            {
                _applying = false;
            }
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides;
        }

        private static bool TheHostSaidIt(int peerId, DecodedFrame frame)
        {
            RosterEntry entry = _room == null ? null : _room.Roster.Find(peerId);
            if (entry != null && entry.IsHost)
            {
                return true;
            }

            Log.Warn("mp", "peer " + peerId + " sent a " + frame.Action + ": only the host says where the"
                + " graves stand " + ApiBase.FrameTag());

            return false;
        }

        private static bool Due(int now, int since, int millis)
        {
            return unchecked((uint)(now - since)) >= (uint)millis;
        }
    }
}
