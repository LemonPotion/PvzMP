using System;
using System.Collections.Generic;
using System.Threading;
using PvzMP.Net;

namespace PvzMP.Core.Session
{
    public enum SessionPhase
    {
        /// <summary>No session: single player is untouched.</summary>
        Idle,

        /// <summary>Listening, waiting for a first hello.</summary>
        Hosting,

        /// <summary>A socket to the host is being opened on a worker thread.</summary>
        Connecting,

        /// <summary>Connected and waiting for the host to answer the hello.</summary>
        Handshake,

        /// <summary>The host assigned an OnlineId and sent a roster.</summary>
        Connected
    }

    /// <summary>
    /// The room: who is in it, who owns decisions, and what the two ends of the star do with the
    /// messages they get. Nothing in this class touches the game: a gameplay frame is forwarded to the
    /// handler the game side installs, so the caller pumps Tick() from the main thread and the session
    /// only ever moves sockets, queues and its own lists.
    ///
    /// Two rules shape the code. The host is the only one who may say who is in the room, so a client
    /// never edits the roster, it replaces its copy. And nothing is decided on a transport thread, so
    /// every fact the session needs is discovered in Tick() from the transport's own state instead of
    /// from events that would fire on a worker.
    /// </summary>
    public sealed class MpSession
    {
        /// <summary>Four seats was the product choice; the transport refuses the fifth hello.</summary>
        public const int MaxPlayers = 4;

        public const int ConnectTimeoutMillis = 5000;

        /// <summary>A dead link is noticed within this much of real time, and the poll costs nothing between checks.</summary>
        public const int LostPeerCheckMillis = 250;

        /// <summary>How often the host asks every peer for a round trip, for the lobby list.</summary>
        public const int PingIntervalMillis = 2000;

        private readonly SessionRoster _roster = new SessionRoster();
        private readonly EntityLedger _entities = new EntityLedger();
        private readonly CallInbox _calls = new CallInbox(Arbiter.Default);

        private int _nextLostCheck;
        private int _nextPing;

        /// <summary>The coverage audit is one room's fact, printed the first time the room decides.</summary>
        private bool _coverageAudited;

        /// <summary>When each ping left this host, so the pong can be turned into a round trip.</summary>
        private readonly Dictionary<int, int> _pingSentAt = new Dictionary<int, int>();

        private MpHost _host;
        private MpClient _client;
        private string _wantedNick = NickRules.Fallback;
        private int _connectMillis = ConnectTimeoutMillis;
        private string _joinAddress = string.Empty;
        private int _joinPort;

        // Written by the connect worker, read by Tick. Two fields, so the worker needs no lock.
        private volatile bool _connectDone;
        private string _connectError;

        // Set when this end's own room ends, taken once by the game side to say it out loud.
        private string _roomEndNotice;

        public SessionPhase Phase { get; private set; }

        public int LocalPeerId { get; private set; }

        /// <summary>Our nick as the host wrote it, which can carry a #2 the player did not type.</summary>
        public string LocalNick { get; private set; }

        public RejectCode LastReject { get; private set; }

        public string LastFailure { get; private set; }

        /// <summary>
        /// Why this end's room ended, or null when there is nothing owed to the player. One sentence each,
        /// and taken rather than read: Core/Session cannot draw, so the game side asks once a frame and the
        /// same loss never gets repeated for the sixty frames it stays on screen.
        /// </summary>
        public string TakeRoomEndNotice()
        {
            string notice = _roomEndNotice;
            _roomEndNotice = null;
            return notice;
        }

        public SessionRoster Roster
        {
            get { return _roster; }
        }

        /// <summary>What the room says is on the lawn, by net id. Only the host adds to it; a client repeats.</summary>
        public EntityLedger Entities
        {
            get { return _entities; }
        }

        /// <summary>
        /// Where a gameplay frame goes. The room itself decides nothing about plants or sun - the game
        /// side installs this, and Core/Session stays free of il2cpp types so its tests can run the
        /// source outside the game.
        /// </summary>
        public Action<int, DecodedFrame> GameplayHandler { get; set; }

        /// <summary>
        /// Where a cosmetic frame goes. Kept apart from the gameplay handler on purpose: a cursor is not
        /// a request about the lawn, and the world must not be able to mistake one for the other.
        /// </summary>
        public Action<int, DecodedFrame> CosmeticHandler { get; set; }

        /// <summary>
        /// Who is in the room now and heard nothing before this moment. A new seat is empty of history,
        /// and only this class knows when one appeared: the latest-wins values it never saw are handed
        /// over here, and what a feature keeps in its own frames, the level the host is standing in, is
        /// handed over here too.
        ///
        /// More than one feature has a state a late seat cannot know, so every feature adds itself here
        /// rather than replacing the one that subscribed first. A handler runs on the main thread, like
        /// the tick that seated the peer.
        /// </summary>
        public event Action<int> PeerSeated;

        /// <summary>
        /// The one inbox for call frames. It belongs to the room and not to a feature: the sequence
        /// numbers, the duplicates and the calls waiting for an object that has not arrived are the
        /// room's order, and two ends disagreeing about that order is the bug this class exists to stop.
        /// </summary>
        public CallInbox Calls
        {
            get { return _calls; }
        }

        public bool IsHost
        {
            get { return _host != null; }
        }

        /// <summary>The port the host is actually listening on, 0 for a client or before Host() binds.</summary>
        public int Port
        {
            get { return _host == null ? 0 : _host.Port; }
        }

        public bool IsActive
        {
            get { return Phase != SessionPhase.Idle; }
        }

        /// <summary>
        /// Whether the room - and not the game alone - decides what this end does right now. The host
        /// decides from the moment it is listening, so a level started before anybody joins is already the
        /// room's level. A peer only decides for itself until the host gives it a seat: a player still
        /// dialling keeps the clicks of a single player, because there is nobody on the other end who
        /// could be contradicted.
        /// </summary>
        public bool RoomDecides
        {
            get { return IsHost ? IsActive : Phase == SessionPhase.Connected; }
        }

        public void Host(string nick, int port)
        {
            if (Phase != SessionPhase.Idle)
            {
                Log.Warn("mp", "cannot host: the session is already " + Phase);
                return;
            }

            LastReject = RejectCode.None;
            LastFailure = null;
            _roomEndNotice = null;
            _wantedNick = NickRules.Resolve(nick, NickRules.Fallback);
            LocalNick = _wantedNick;
            LocalPeerId = MpHost.HostPeerId;
            _roster.Add(MpHost.HostPeerId, LocalNick, true);

            // The host takes one of the MaxPlayers seats, so the transport only ever accepts the rest.
            MpHost host = new MpHost(MaxPlayers - 1);

            try
            {
                host.Start(port);
            }
            catch (Exception e)
            {
                Log.Error("mp", "host could not listen on " + port + ": " + e.GetType().Name + " " + e.Message);
                host.Dispose();
                _roster.Clear();
                LocalNick = null;
                return;
            }

            _host = host;
            Phase = SessionPhase.Hosting;
            Log.Info("mp", "session hosting on port " + port + " as '" + LocalNick + "', capacity "
                + MaxPlayers + " - roster: " + _roster.Describe());
        }

        /// <summary>
        /// Starting a socket is a blocking call, and the game thread may not stop for it, so the worker
        /// owns only the connect. Everything after it - the hello, the welcome, the roster - happens in
        /// Tick on the main thread.
        /// </summary>
        public void Join(string address, int port, string nick, int timeoutMillis = ConnectTimeoutMillis)
        {
            if (Phase != SessionPhase.Idle)
            {
                Log.Warn("mp", "cannot join: the session is already " + Phase);
                return;
            }

            LastReject = RejectCode.None;
            LastFailure = null;
            _roomEndNotice = null;
            _wantedNick = NickRules.Resolve(nick, NickRules.Fallback);
            _joinAddress = address;
            _joinPort = port;
            _connectMillis = timeoutMillis;
            _connectDone = false;
            _connectError = null;

            // No seat until the host's Welcome says which: the default 0 is the host's own seat, and a
            // frame judged before the Welcome lands would be read as a call this end started.
            LocalPeerId = -1;
            Phase = SessionPhase.Connecting;

            Log.Info("mp", "joining " + address + ":" + port + " as '" + _wantedNick + "'");
            Thread worker = new Thread(ConnectToHost) { IsBackground = true, Name = "PvzMP-join" };
            worker.Start();
        }

        /// <summary>Run once per frame from the game's main thread; it is also how tests drive the room.</summary>
        public void Tick()
        {
            SyncInboxIdentity();

            // Before anything is drained: the coverage table is the answer to "may this be the room's
            // business", so it has to be known to agree before the first gameplay frame is applied.
            if (RoomDecides && !_coverageAudited)
            {
                _coverageAudited = true;
                Arbiter.Audit(Arbiter.Default);
            }

            if (_host != null)
            {
                TickHost();
            }

            if (_client != null || Phase == SessionPhase.Connecting)
            {
                TickClient();
            }

            // Last, because a frame that arrived above may be the object a call was waiting for: the
            // retry is what turns a call that came too early into one that was only early.
            _calls.Tick();
        }

        /// <summary>
        /// The inbox answers two questions that only the room can: which seat this end holds, and which
        /// seat owns the authority a HostOnly row claims. The seat changes while the room runs - the
        /// Welcome is what gives it - so the answer is refreshed where it changes and not only when the
        /// tick began: a resync value can arrive in the same tick as the Welcome that seated this end.
        /// </summary>
        private void SyncInboxIdentity()
        {
            _calls.LocalPeerId = LocalPeerId;
            _calls.HostPeerId = MpHost.HostPeerId;
        }

        public void Leave(string reason)
        {
            if (Phase == SessionPhase.Idle)
            {
                return;
            }

            Log.Info("mp", "session left: " + reason + " (was " + Phase + ", roster: " + _roster.Describe() + ")");

            if (_host != null)
            {
                _host.Broadcast(MsgCategory.Control, MsgAction.Bye, null);
                _host.Stop();
                _host = null;
            }

            if (_client != null)
            {
                _client.Send(MsgCategory.Control, MsgAction.Bye, null);
                _client.Disconnect("leaving: " + reason);
                _client = null;
            }

            _roster.Clear();
            _entities.Clear();

            // A new room counts its calls again from one: numbers carried over from the last session would
            // make the first call of the new one look like a duplicate of an old one.
            _calls.Reset();
            _pingSentAt.Clear();
            _nextPing = 0;
            _coverageAudited = false;
            _wantedNick = NickRules.Fallback;
            Phase = SessionPhase.Idle;
            LocalPeerId = -1;
            LocalNick = null;
        }

        /// <summary>A client asking the host to change the world. False when this end is not a client.</summary>
        public bool SendToHost(MsgAction action, object payload)
        {
            if (_client == null)
            {
                Log.Warn("mp", "cannot send " + action + " to the host: this end is not connected to one");
                return false;
            }

            return _client.Send(MsgCategory.Gameplay, action, payload);
        }

        /// <summary>The host's answer to one peer, usually a refusal.</summary>
        public bool SendToPeer(int peerId, MsgAction action, object payload)
        {
            if (_host == null)
            {
                Log.Warn("mp", "cannot send " + action + " to peer " + peerId + ": this end is not the host");
                return false;
            }

            return _host.SendTo(peerId, MsgCategory.Gameplay, action, payload);
        }

        /// <summary>The host telling every peer about something that now exists on its own lawn.</summary>
        public void BroadcastGameplay(MsgAction action, object payload)
        {
            if (_host == null)
            {
                Log.Warn("mp", "cannot broadcast " + action + ": this end is not the host");
                return;
            }

            _host.Broadcast(MsgCategory.Gameplay, action, payload);
        }

        /// <summary>
        /// A seat saying where it stands on the level to begin. Nothing is decided here: the host owns the
        /// roster, and the frame only asks it to remember this seat. The answer for every screen is the
        /// roster the host broadcasts back. The word is the state and not the press, so a seat that takes its
        /// press back can say so with the same frame.
        /// </summary>
        public bool TellHostIAmReady(bool ready)
        {
            if (_client == null)
            {
                Log.Warn("mp", "cannot say ready: this end is not connected to a host");
                return false;
            }

            return _client.Send(MsgCategory.Control, MsgAction.SeatReady,
                new SeatReadyPayload { Ready = ready });
        }

        /// <summary>
        /// The chooser is waiting for nobody again. Called when a level begins or is left: the seats that
        /// pressed ready have done it for a lawn that no longer exists, so the next chooser starts empty.
        /// </summary>
        public void ClearReadiness()
        {
            if (_host == null)
            {
                return;
            }

            if (_roster.ClearReadiness() == 0)
            {
                return;
            }

            Log.Info("mp", "the room asks every seat to press Let's Rock again");

            BroadcastRoster(false);
        }

        /// <summary>
        /// The host telling every seat - or every seat but one - to run one covered native method on its
        /// own lawn. This is the only frame a new synced feature needs, so a feature never asks for a send
        /// method of its own, and it never asks which lane either: the row decides whether the frame
        /// travels with the world or as a value that may be replaced.
        /// </summary>
        public bool BroadcastCall(MethodCall call, int exceptPeerId = -1)
        {
            if (_host == null)
            {
                Log.Warn("mp", "cannot broadcast " + call.Key + ": this end is not the host");
                return false;
            }

            MsgCategory lane = _calls.LaneOf(call.Key);

            if (lane == MsgCategory.Cosmetic)
            {
                // The door is the slot: two latest-wins doors that share one frame name must not replace
                // each other on the way out.
                _host.BroadcastCosmetic(lane, MsgAction.MethodCall, call.ToPayload(), exceptPeerId,
                                         call.Key);
                return true;
            }

            _host.Broadcast(lane, MsgAction.MethodCall, call.ToPayload(), exceptPeerId);
            return true;
        }

        /// <summary>A peer asking the room to run one covered native method; the host passes it on.</summary>
        public bool SendCallToHost(MethodCall call)
        {
            if (_client == null)
            {
                Log.Warn("mp", "cannot send " + call.Key + " to the host: this end is not connected to one");
                return false;
            }

            MsgCategory lane = _calls.LaneOf(call.Key);

            return lane == MsgCategory.Cosmetic
                ? _client.SendCosmetic(lane, MsgAction.MethodCall, call.ToPayload(), call.Key)
                : _client.Send(lane, MsgAction.MethodCall, call.ToPayload());
        }

        /// <summary>The host telling one seat about one door: what a resync is made of.</summary>
        public bool SendCallTo(int peerId, MethodCall call)
        {
            if (_host == null)
            {
                Log.Warn("mp", "cannot tell peer " + peerId + " about " + call.Key + ": this end is not the host");
                return false;
            }

            MsgCategory lane = _calls.LaneOf(call.Key);

            return lane == MsgCategory.Cosmetic
                ? _host.SendCosmeticTo(peerId, lane, MsgAction.MethodCall, call.ToPayload(), call.Key)
                : _host.SendTo(peerId, lane, MsgAction.MethodCall, call.ToPayload());
        }

        /// <summary>
        /// A cosmetic frame from this end. The transport keeps one slot per peer for those: a stale
        /// cursor has no value, so a newer one replaces it and nothing ever backs up.
        /// </summary>
        public bool SendCosmeticToHost(MsgAction action, object payload)
        {
            if (_client == null)
            {
                Log.Warn("mp", "cannot send cosmetic " + action + " to the host: this end is not connected");
                return false;
            }

            return _client.SendCosmetic(MsgCategory.Cosmetic, action, payload);
        }

        /// <summary>The host passing a peer's cosmetic frame on; the sender is skipped, it already shows it.</summary>
        public void BroadcastCosmetic(MsgAction action, object payload, int exceptPeerId = -1)
        {
            if (_host == null)
            {
                Log.Warn("mp", "cannot broadcast cosmetic " + action + ": this end is not the host");
                return;
            }

            _host.BroadcastCosmetic(MsgCategory.Cosmetic, action, payload, exceptPeerId);
        }

        private void TickHost()
        {
            int peerId;
            DecodedFrame frame;

            while (_host.TryReceive(out peerId, out frame))
            {
                HandleHostFrame(peerId, frame);
            }

            DetectLostPeers();
            SendPings();
        }

        private void HandleHostFrame(int peerId, DecodedFrame frame)
        {
            if (frame.Category == MsgCategory.Cosmetic)
            {
                DispatchCosmetic(peerId, frame);
                return;
            }

            if (frame.Category == MsgCategory.Gameplay)
            {
                Dispatch(peerId, frame);
                return;
            }

            if (frame.Category != MsgCategory.Control)
            {
                Log.Warn("mp", "dropping " + frame.Name + " from peer " + peerId
                    + ": the room only handles the handshake, gameplay and cosmetics");
                return;
            }

            if (frame.Action == MsgAction.Bye)
            {
                DropPeer(peerId, "the peer said goodbye");
                return;
            }

            if (frame.Action == MsgAction.Pong)
            {
                ApplyPong(peerId);
                return;
            }

            if (frame.Action == MsgAction.SeatReady)
            {
                SeatReadyPayload ready = Protocol.Payload<SeatReadyPayload>(frame);

                MarkSeatReady(peerId, ready == null || ready.Ready);
                return;
            }

            if (frame.Action != MsgAction.Hello)
            {
                Log.Warn("mp", "ignoring " + frame.Name + " from peer " + peerId);
                return;
            }

            HelloPayload hello;
            try
            {
                hello = Protocol.Payload<HelloPayload>(frame);
            }
            catch (ProtocolException e)
            {
                Reject(peerId, RejectCode.BadHandshake, e.Message);
                return;
            }

            Log.Info("mp", "peer " + peerId + " hello: version '" + hello.Version + "' nick '" + hello.Nick + "'");

            if (_roster.Find(peerId) != null)
            {
                Reject(peerId, RejectCode.BadHandshake, "peer " + peerId + " already has a seat");
                return;
            }

            if (hello.Version != Protocol.Version)
            {
                Reject(peerId, RejectCode.VersionMismatch, "the host speaks " + Protocol.Version
                    + ", the peer said " + hello.Version);
                return;
            }

            if (_roster.ConnectedCount >= MaxPlayers)
            {
                Reject(peerId, RejectCode.RoomFull, "the room holds " + MaxPlayers + " peers");
                return;
            }

            string wanted = NickRules.Resolve(hello.Nick, NickRules.Fallback);
            string nick = _roster.AllocateNick(wanted);

            if (nick == null)
            {
                Reject(peerId, RejectCode.TooManySuffixes, "every '" + wanted + "#2..' up to #"
                    + NickRules.MaxSuffixNumber + " is taken");
                return;
            }

            _roster.Add(peerId, nick, false);
            _host.SendTo(peerId, MsgCategory.Control, MsgAction.Welcome,
                new WelcomePayload { PeerId = peerId, Version = Protocol.Version });
            Log.Info("mp", "peer " + peerId + " accepted as OnlineId=" + peerId + " nick '" + nick + "'");

            BroadcastRoster();
            ResyncPeer(peerId);
        }

        /// <summary>
        /// What the room said before this seat arrived, said again to this seat alone. Ordered calls are
        /// not repeated: replaying a planting would plant twice, and a room cannot hand out a history it
        /// has no way to undo. A latest-wins door is different on purpose - its arguments are the state,
        /// so the last value is the one answer, and it is sent on the lane that answer belongs to. Then
        /// the feature modules are asked, because the level the host is standing in is not a native door.
        /// </summary>
        private void ResyncPeer(int peerId)
        {
            List<MethodCall> values = _calls.LastValues();

            foreach (MethodCall call in values)
            {
                SendCallTo(peerId, call);
            }

            Log.Info("mp", "peer " + peerId + " is told what the room said before it came: "
                + values.Count + " value(s)");

            Action<int> seated = PeerSeated;
            if (seated == null)
            {
                return;
            }

            try
            {
                seated(peerId);
            }
            catch (Exception e)
            {
                // A feature that cannot describe its own state must not stop the room from seating anyone.
                Log.Error("mp", "the room could not tell peer " + peerId + " what it missed: "
                    + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// A link disappears from the transport's list as soon as its reader thread notices the close,
        /// so polling that list is the whole disconnect detector - no event has to cross a thread.
        /// </summary>
        private void DetectLostPeers()
        {
            int now = Environment.TickCount;
            if (now - _nextLostCheck < 0)
            {
                return;
            }

            _nextLostCheck = now + LostPeerCheckMillis;

            var live = new HashSet<int>();
            foreach (PeerLink peer in _host.Peers())
            {
                live.Add(peer.PeerId);
            }

            // DropPeer removes the entry from the roster, so the walk reads a snapshot: enumerating the
            // live list would throw the moment a lost link is dropped.
            var entries = new List<RosterEntry>(_roster.Entries);

            foreach (RosterEntry entry in entries)
            {
                if (entry.PeerId == MpHost.HostPeerId)
                {
                    continue;
                }

                if (entry.Connected && !live.Contains(entry.PeerId))
                {
                    DropPeer(entry.PeerId, "the link died");
                }
            }
        }

        private void DropPeer(int peerId, string why)
        {
            if (!_roster.Remove(peerId))
            {
                return;
            }

            _pingSentAt.Remove(peerId);

            Log.Info("mp", "peer " + peerId + " gone: " + why);
            BroadcastRoster();
        }

        /// <summary>
        /// The host asking every seat for a round trip. The list shows a number next to each name, so a
        /// player can tell a slow link from a lost one; the cost is one empty control frame per seat every
        /// two seconds.
        /// </summary>
        private void SendPings()
        {
            int now = Environment.TickCount;
            if (now - _nextPing < 0)
            {
                return;
            }

            _nextPing = now + PingIntervalMillis;

            foreach (RosterEntry entry in _roster.Entries)
            {
                if (entry.PeerId != MpHost.HostPeerId
                    && _host.SendTo(entry.PeerId, MsgCategory.Control, MsgAction.Ping, null))
                {
                    _pingSentAt[entry.PeerId] = now;
                }
            }
        }

        private void ApplyPong(int peerId)
        {
            int sent;
            if (!_pingSentAt.TryGetValue(peerId, out sent))
            {
                return;
            }

            _pingSentAt.Remove(peerId);

            RosterEntry entry = _roster.Find(peerId);
            if (entry == null)
            {
                return;
            }

            int roundTrip = unchecked(Environment.TickCount - sent);
            if (roundTrip < 0 || roundTrip > 60000)
            {
                return;
            }

            bool first = entry.PingMillis < 0;
            entry.PingMillis = roundTrip;

            if (first)
            {
                Log.Info("mp", "peer " + peerId + " (" + entry.Nick + ") answers in " + roundTrip + "ms");
            }

            BroadcastRoster(false);
        }

        /// <summary>
        /// The seat pressed the chooser's button. Written here and broadcast, because a ready is one
        /// seat's word about itself and every screen has to show the same list. A seat that said it twice
        /// changes nothing, so the roster is not sent again for it.
        /// </summary>
        private void MarkSeatReady(int peerId, bool ready)
        {
            RosterEntry entry = _roster.Find(peerId);

            if (entry == null)
            {
                Log.Warn("mp", "peer " + peerId + " said ready without having a seat");
                return;
            }

            if (!_roster.SetReady(peerId, ready))
            {
                return;
            }

            Log.Info("mp", "peer " + peerId + " (" + entry.Nick + ") is "
                + (ready ? "ready" : "not ready again") + ", " + _roster.SeatsNotReady()
                + " seat(s) still are not");

            BroadcastRoster(false);
        }

        private void Reject(int peerId, RejectCode code, string reason)
        {
            LastReject = code;
            LastFailure = reason;
            Log.Warn("mp", "peer " + peerId + " rejected: " + code + " (" + reason + ")");
            _host.RejectAndClose(peerId, code, reason);
        }

        private void BroadcastRoster(bool announce = true)
        {
            _host.Broadcast(MsgCategory.Control, MsgAction.Roster, _roster.ToPayload());

            if (announce)
            {
                Log.Info("mp", "roster: " + _roster.Describe());
            }
        }

        /// <summary>
        /// A call frame is the room's own business from the moment it arrives: the inbox decides, applies
        /// and counts it, and no feature module is asked. A call that came from a seat other than the host
        /// is passed on after it is applied here, because the room is a star and the third end only hears
        /// about it through this end. The number and the origin travel unchanged, which is what lets every
        /// end recognise the same call instead of applying it twice, and the lane is the row's answer
        /// again, so a relay cannot quietly move a door to the other promise.
        /// </summary>
        private void ApplyCall(int peerId, DecodedFrame frame)
        {
            if (_calls.Accept(frame, peerId) != CallOutcome.Applied
                || _host == null
                || peerId == MpHost.HostPeerId)
            {
                return;
            }

            BroadcastCall(MethodCall.From(frame), peerId);
        }

        /// <summary>
        /// A gameplay frame leaves the room through the installed handler and is nobody's business
        /// here. A throw would otherwise stop the rest of Tick, including the disconnect detector, so
        /// it is caught: the frame is lost, the room survives.
        /// </summary>
        private void Dispatch(int peerId, DecodedFrame frame)
        {
            if (frame.Action == MsgAction.MethodCall)
            {
                ApplyCall(peerId, frame);
                return;
            }
            Action<int, DecodedFrame> handler = GameplayHandler;
            if (handler == null)
            {
                Log.Warn("mp", "no one listens for " + frame.Name + " from peer " + peerId);
                return;
            }

            try
            {
                handler(peerId, frame);
            }
            catch (Exception e)
            {
                Log.Error("mp", "the handler threw on " + frame.Name + " from peer " + peerId + ": "
                    + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// A cosmetic frame goes to its own listener and never to the gameplay one, except a call frame,
        /// which is the room's business and not a feature's. A throw is caught for the same reason as
        /// above: a cursor must not be able to stop the room.
        /// </summary>
        private void DispatchCosmetic(int peerId, DecodedFrame frame)
        {
            if (frame.Action == MsgAction.MethodCall)
            {
                ApplyCall(peerId, frame);
                return;
            }

            Action<int, DecodedFrame> handler = CosmeticHandler;
            if (handler == null)
            {
                Log.Warn("mp", "no one listens for " + frame.Name + " from peer " + peerId);
                return;
            }

            try
            {
                handler(peerId, frame);
            }
            catch (Exception e)
            {
                Log.Error("mp", "the cosmetic handler threw on " + frame.Name + " from peer " + peerId + ": "
                    + e.GetType().Name + " " + e.Message);
            }
        }

        private void ConnectToHost()
        {
            MpClient client = new MpClient();

            try
            {
                client.Connect(AddressRules.CleanHost(_joinAddress), _joinPort, _connectMillis);
            }
            catch (Exception e)
            {
                client.Dispose();
                _connectError = e.GetType().Name + ": " + e.Message;
                _connectDone = true;
                return;
            }

            if (Phase != SessionPhase.Connecting)
            {
                client.Dispose();
                return;
            }

            _client = client;
            _connectDone = true;
        }

        private void TickClient()
        {
            if (Phase == SessionPhase.Connecting)
            {
                if (!_connectDone)
                {
                    return;
                }

                _connectDone = false;

                if (_connectError != null)
                {
                    LastFailure = _connectError;
                    Log.Warn("mp", "join failed: " + _connectError + ", no session");
                    Phase = SessionPhase.Idle;
                    return;
                }

                _client.Send(MsgCategory.Control, MsgAction.Hello,
                    new HelloPayload { Version = Protocol.Version, Nick = _wantedNick });
                Phase = SessionPhase.Handshake;
                Log.Info("mp", "hello sent, waiting for the host");
                return;
            }

            DecodedFrame frame;
            while (_client != null && _client.TryReceive(out frame))
            {
                try
                {
                    HandleClientFrame(frame);
                }
                catch (ProtocolException e)
                {
                    Log.Warn("mp", "dropping " + frame.Name + ": " + e.Message);
                }
            }

            if (Phase != SessionPhase.Idle && Phase != SessionPhase.Connecting && !_client.IsConnected)
            {
                string reason = (_client.Link == null ? "no link" : _client.Link.CloseReason);

                Log.Warn("mp", "lost the host: " + reason + ", the session is over");

                // The player is standing on a lawn that stopped agreeing with anyone, and nothing about
                // that reaches the screen today: the log is the only witness. Reconnecting is a later
                // step; being honest is not something to postpone.
                LastFailure = "the host is gone (" + reason + "), the room is over";
                _roomEndNotice = LastFailure;

                _client = null;
                _roster.Clear();
                _entities.Clear();
                Phase = SessionPhase.Idle;
                LocalPeerId = -1;
                LocalNick = null;
            }
        }

        private void HandleClientFrame(DecodedFrame frame)
        {
            if (frame.Category == MsgCategory.Cosmetic)
            {
                DispatchCosmetic(MpHost.HostPeerId, frame);
                return;
            }

            if (frame.Category == MsgCategory.Gameplay)
            {
                Dispatch(MpHost.HostPeerId, frame);
                return;
            }

            if (frame.Category != MsgCategory.Control)
            {
                Log.Warn("mp", "dropping " + frame.Name
                    + ": the room only handles the handshake, gameplay and cosmetics");
                return;
            }

            if (frame.Action == MsgAction.Ping)
            {
                _client.Send(MsgCategory.Control, MsgAction.Pong, null);
                return;
            }

            if (frame.Action == MsgAction.Welcome)
            {
                WelcomePayload welcome = Protocol.Payload<WelcomePayload>(frame);

                if (welcome.Version != Protocol.Version)
                {
                    Log.Warn("mp", "the host answers version '" + welcome.Version + "', this mod speaks '"
                        + Protocol.Version + "' - continuing anyway, the wire format is what matters");
                }

                LocalPeerId = welcome.PeerId;
                Phase = SessionPhase.Connected;
                SyncInboxIdentity();
                Log.Info("mp", "welcome: OnlineId=" + welcome.PeerId);
                return;
            }

            if (frame.Action == MsgAction.Roster)
            {
                _roster.Apply(Protocol.Payload<RosterPayload>(frame));

                RosterEntry self = _roster.Find(LocalPeerId);
                LocalNick = self == null ? _wantedNick : self.Nick;

                Log.Info("mp", "roster: " + _roster.Describe() + " (me as '" + LocalNick + "')");
                return;
            }

            if (frame.Action == MsgAction.Reject)
            {
                RejectPayload reject = Protocol.Payload<RejectPayload>(frame);
                LastReject = reject.Code;
                LastFailure = reject.Reason;
                Log.Warn("mp", "the host refused the join: " + reject.Code + " (" + reject.Reason + ")");

                _client.Disconnect("rejected: " + reject.Code);
                _client = null;
                Phase = SessionPhase.Idle;
                return;
            }

            Log.Info("mp", "ignoring " + frame.Name);
        }
    }
}
