using System;
using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the room did with one call frame, and the only answer the transport needs.
    ///
    /// Applied and Duplicate are the two ways a frame changes the lawn; Deferred is the honest third:
    /// the call is covered and correct but the thing it names has not arrived on this end yet. Refused
    /// means the table said no, Failed means the game itself threw.
    /// </summary>
    public enum CallOutcome
    {
        None,
        Applied,
        Deferred,
        Duplicate,
        Refused,
        Failed
    }

    /// <summary>
    /// One call of one covered native method, as it travels. The key is the coverage table's own, the
    /// target and the arguments are tokens and shapes the adapter registry writes, and the sequence is
    /// the host's: the end that owns the room numbers the frames it broadcasts, which is what lets a
    /// receiver recognise a frame it already applied and notice a frame that never came.
    /// </summary>
    public sealed class MethodCall
    {
        public MethodCall(string key, string target, string args, long seq, int originPeer)
        {
            Key = key ?? string.Empty;
            Target = string.IsNullOrEmpty(target) ? CallArgs.None : target;
            Args = string.IsNullOrEmpty(args) ? "[]" : args;
            Seq = seq;
            OriginPeer = originPeer;
        }

        public string Key { get; private set; }

        /// <summary>The instance the game is asked to run the method on, as a reference token.</summary>
        public string Target { get; private set; }

        /// <summary>The argument vector, written against the row's measured categories.</summary>
        public string Args { get; private set; }

        public long Seq { get; private set; }

        /// <summary>The end whose game asked for this, so a log can say who moved the lawn.</summary>
        public int OriginPeer { get; private set; }

        public MethodCallPayload ToPayload()
        {
            return new MethodCallPayload
            {
                Key = Key,
                Target = Target,
                Args = Args,
                Seq = Seq,
                OriginPeer = OriginPeer
            };
        }

        /// <summary>
        /// The frame as it arrived, with nothing checked yet. Protocol.Payload does the shape; whether
        /// the call may be the room's business at all is the inbox's question.
        /// </summary>
        public static MethodCall From(DecodedFrame frame)
        {
            MethodCallPayload payload = Protocol.Payload<MethodCallPayload>(frame);

            return new MethodCall(payload.Key, payload.Target, payload.Args, payload.Seq,
                                  payload.OriginPeer);
        }

        public override string ToString()
        {
            return Key + " on " + Target + " " + Args + " seq " + Seq + " from " + OriginPeer;
        }
    }

    /// <summary>
    /// The one place a call frame becomes a native call, which is the point of the whole redesign: the
    /// old mod asked ten different gates to remember the same four rules and every gate had its own way
    /// of forgetting. Here a frame is refused if the table does not cover it, counted as a duplicate if
    /// its sequence has already been applied, held if the object it names has not arrived, and only then
    /// handed to the applier, whose throw is caught and logged instead of being mistaken for success.
    ///
    /// The room's own rule, "the call travels and the same machine code runs on every end", needs one
    /// more guard the old gates never had: while this end is applying a call it is running the game's
    /// code, and that code calls other covered methods. Those nested calls are already implied by the
    /// frame being applied, so announcing them would send the room the same change again and again.
    /// IsApplying is that guard, in one place, for every gate.
    /// </summary>
    public sealed class CallInbox
    {
        /// <summary>How long a call may wait for the entity it names before the room says it lost it.</summary>
        public const int MaxPendingTicks = 120;

        /// <summary>What a peer flooding the room with unresolvable calls is allowed to hold.</summary>
        public const int MaxPendingCalls = 64;

        private readonly CoverageTable _table;
        private readonly List<PendingCall> _pending = new List<PendingCall>();
        private readonly Dictionary<int, long> _lastAppliedByOrigin = new Dictionary<int, long>();
        private readonly Dictionary<string, MethodCall> _lastByDoor
            = new Dictionary<string, MethodCall>(StringComparer.Ordinal);

        public CallInbox(CoverageTable table)
        {
            _table = table;
        }

        /// <summary>The engine's own answer to "run this call now": applied, deferred or thrown.</summary>
        public Func<MethodCall, CallOutcome> Applier { get; set; }

        /// <summary>Which peer this end is, so a call the end itself started is never applied twice.</summary>
        public int LocalPeerId { get; set; }

        /// <summary>
        /// Which seat owns the room. A HostOnly row announced by anybody else is refused on receipt: the
        /// authority column is a claim about the sender, and a sender is only believed when the frame
        /// actually came from that seat, not when its payload says so.
        /// </summary>
        public int HostPeerId { get; set; }

        public int AppliedCount { get; private set; }

        public int DuplicateCount { get; private set; }

        public int RefusedCount { get; private set; }

        public int FailedCount { get; private set; }

        /// <summary>Frames that arrived out of the host's order: the sign of a link that dropped data.</summary>
        public int GapCount { get; private set; }

        public int PendingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>The last reason this end said no, for the log and for the tests.</summary>
        public string LastRefusal { get; private set; }

        private static int _applyingDepth;

        /// <summary>True while a received call is being run, which is when a gate must stay quiet.</summary>
        public static bool IsApplying
        {
            get { return _applyingDepth > 0; }
        }

        public static int ApplyingDepth
        {
            get { return _applyingDepth; }
        }

        /// <summary>Every gate's question before it announces anything: is this the room replaying itself.</summary>
        public static bool ShouldAnnounce(bool inRoom)
        {
            return inRoom && !IsApplying;
        }

        public static void EnterApplying()
        {
            _applyingDepth++;
        }

        public static void ExitApplying()
        {
            if (_applyingDepth > 0)
            {
                _applyingDepth--;
            }
        }

        /// <summary>Forget every counter, every held call, every kept value and the outgoing number: a
        /// new room is a new stream.</summary>
        public void Reset()
        {
            _pending.Clear();
            _lastAppliedByOrigin.Clear();
            _lastByDoor.Clear();
            _outgoingSeq = 0;
            AppliedCount = 0;
            DuplicateCount = 0;
            RefusedCount = 0;
            FailedCount = 0;
            GapCount = 0;
            LastRefusal = null;
        }

        /// <summary>
        /// A call this end is about to announce, with the sequence the host owns. Kept here so the number
        /// comes from one counter and a gate cannot reuse one.
        /// </summary>
        public long NextSeq()
        {
            return ++_outgoingSeq;
        }

        private long _outgoingSeq;

        /// <summary>
        /// The one entry point for a frame that arrived. It answers with what happened and logs every
        /// answer that is not Applied, so a room that silently does nothing is impossible.
        /// </summary>
        public CallOutcome Accept(DecodedFrame frame, int senderPeer)
        {
            MethodCall call;
            MsgCategory lane;

            try
            {
                lane = frame.Category;
                call = MethodCall.From(frame);
            }
            catch (Exception e)
            {
                return Refuse(frame == null ? "?" : frame.Name, "the frame is not a readable call: "
                    + e.Message);
            }

            return Accept(call, senderPeer, lane);
        }

        /// <summary>
        /// Which lane the frame came in on is part of the question, not a detail of the transport: an
        /// ordered frame naming a latest-wins door and a latest-wins frame naming an ordered door are both
        /// a sender that does not have the row it thinks it has, and each would be run with promises the
        /// other lane never made.
        /// </summary>
        public CallOutcome Accept(MethodCall call, int senderPeer, MsgCategory lane)
        {
            if (call == null)
            {
                return Refuse("?", "an empty call frame");
            }

            // Authority is a claim about the sender, and a sender is only believed when the frame really
            // came from that seat - writing the host's id into a payload is not being the host.
            Decision decision = Arbiter.Decide(_table, call.Key, true, senderPeer == HostPeerId);
            if (!Arbiter.Travels(decision.Verdict))
            {
                return Refuse(call.Key, decision.Reason);
            }

            if (Arbiter.LaneFor(decision.Verdict) != lane)
            {
                return Refuse(call.Key, "is a " + decision.Verdict + " door: its frame belongs on the "
                    + Arbiter.LaneFor(decision.Verdict) + " lane and this one came in on " + lane);
            }

            if (call.OriginPeer == LocalPeerId)
            {
                return Refuse(call.Key, "this end started it, so applying it again would change the "
                    + "lawn twice");
            }

            if (decision.Fact == null)
            {
                return Refuse(call.Key, "the row carries no measured fact, so nothing can be decoded");
            }

            try
            {
                CallArgs.Decode(decision.Fact.Cats, call.Args);
            }
            catch (Exception e)
            {
                return Refuse(call.Key, "the arguments do not fit the measured signature ["
                    + decision.Fact.Cats + "]: " + e.Message);
            }

            if (decision.Verdict == Verdict.Mirror)
            {
                return ApplyLatest(call);
            }

            if (SeenBefore(call))
            {
                DuplicateCount++;
                Log.Info("call", "already applied " + call.Key + " seq " + call.Seq + ", dropped");
                return CallOutcome.Duplicate;
            }

            CallOutcome outcome = Apply(call);
            if (outcome == CallOutcome.Deferred)
            {
                Hold(call);
            }

            return outcome;
        }

        /// <summary>
        /// A latest-wins door: no sequence, no gap, no retry queue. The number it carries is the whole
        /// message, so a value that cannot be run right now describes a lawn that has moved on, and
        /// holding it would put an old bank on a lawn that already has a new one. What did run is what a
        /// seat that arrives in the middle of the level is told.
        /// </summary>
        private CallOutcome ApplyLatest(MethodCall call)
        {
            CallOutcome outcome = Apply(call);
            if (outcome != CallOutcome.Applied)
            {
                if (outcome == CallOutcome.Deferred)
                {
                    Log.Info("call", "dropped the " + call.Key + " value: this lawn cannot run it yet, "
                        + "and a newer one will come");
                }

                return outcome;
            }

            Remember(call);

            return outcome;
        }

        /// <summary>
        /// The last value this end ran for one latest-wins door. Only a door whose arguments are the state
        /// may be kept this way, which is what Mirror means, so an ordered door is never offered here and a
        /// new seat is not handed a second plant or a second coin.
        /// </summary>
        public void Remember(MethodCall call)
        {
            _lastByDoor[call.Key] = call;
        }

        public int LastValueCount
        {
            get { return _lastByDoor.Count; }
        }

        /// <summary>A snapshot: the host hands these out while the seat is still being seated.</summary>
        public List<MethodCall> LastValues()
        {
            return new List<MethodCall>(_lastByDoor.Values);
        }

        /// <summary>
        /// Which lane a stored value has to travel on. The row answers, because a replay that picked a
        /// lane from the door's shape would be a second copy of the rule the inbox already enforces.
        /// </summary>
        public MsgCategory LaneOf(string key)
        {
            return Arbiter.LaneFor(Arbiter.Decide(_table, key, true, true).Verdict);
        }

        /// <summary>
        /// Retry what is waiting for an object that had not arrived when the call did. A call leaves the
        /// queue only by being applied, refused or given up on, and giving up prints the reason.
        /// </summary>
        public void Tick()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            List<PendingCall> drain = new List<PendingCall>(_pending);
            foreach (PendingCall pending in drain)
            {
                CallOutcome outcome = Apply(pending.Call);
                if (outcome == CallOutcome.Deferred)
                {
                    pending.Ticks++;
                    if (pending.Ticks >= MaxPendingTicks)
                    {
                        _pending.Remove(pending);
                        GiveUp(pending);
                    }

                    continue;
                }

                _pending.Remove(pending);
            }
        }

        private CallOutcome Apply(MethodCall call)
        {
            Func<MethodCall, CallOutcome> applier = Applier;
            if (applier == null)
            {
                return Refuse(call.Key, "no one is installed to run calls, so the room cannot apply "
                    + "anything");
            }

            EnterApplying();
            try
            {
                CallOutcome outcome = applier(call);
                if (outcome == CallOutcome.Applied)
                {
                    AppliedCount++;
                    MarkApplied(call);
                    Log.Info("call", "applied " + call.Key + " " + call.Args + " seq " + call.Seq
                        + " from peer " + call.OriginPeer);
                }
                else if (outcome == CallOutcome.Refused || outcome == CallOutcome.Failed)
                {
                    FailedCount++;
                    Log.Error("call", "the game said no to " + call.Key + ": " + outcome);
                }

                return outcome;
            }
            catch (Exception e)
            {
                FailedCount++;
                Log.Error("call", "applying " + call.Key + " threw " + e.GetType().Name + ": "
                    + e.Message);
                return CallOutcome.Failed;
            }
            finally
            {
                ExitApplying();
            }
        }

        private bool SeenBefore(MethodCall call)
        {
            long last;
            return _lastAppliedByOrigin.TryGetValue(call.OriginPeer, out last) && call.Seq <= last;
        }

        private void MarkApplied(MethodCall call)
        {
            long previous;
            bool known = _lastAppliedByOrigin.TryGetValue(call.OriginPeer, out previous);
            _lastAppliedByOrigin[call.OriginPeer] = call.Seq;

            // A jump in one origin's numbers is the only sign this end has that a frame never arrived.
            // The lawn still takes the call it has: freezing the room on a suspicion is how the old
            // build lost a whole battle, while the missing frame is what the resync will ask for.
            // Numbers a held call is already waiting for are not lost frames, and the first number an
            // origin sends has nothing to be compared against.
            if (!known)
            {
                return;
            }

            long waiting = WaitingBetween(call.OriginPeer, previous, call.Seq);
            long missing = call.Seq - previous - 1 - waiting;

            if (missing > 0)
            {
                GapCount++;
                Log.Error("call", "peer " + call.OriginPeer + " skipped " + missing
                    + " call(s) after seq " + previous + ", this lawn is behind");
            }
        }

        /// <summary>How many of the numbers between two applied ones are already waiting in the queue.</summary>
        private long WaitingBetween(int origin, long after, long before)
        {
            long waiting = 0;

            foreach (PendingCall pending in _pending)
            {
                MethodCall held = pending.Call;

                if (held.OriginPeer == origin && held.Seq > after && held.Seq < before)
                {
                    waiting++;
                }
            }

            return waiting;
        }

        private void Hold(MethodCall call)
        {
            if (_pending.Count >= MaxPendingCalls)
            {
                GiveUp(new PendingCall { Call = call, Ticks = MaxPendingTicks });
                return;
            }

            _pending.Add(new PendingCall { Call = call, Ticks = 0 });
        }

        private void GiveUp(PendingCall pending)
        {
            FailedCount++;
            LastRefusal = pending.Call.Key + " was given up on: the object it names never arrived";
            Log.Error("call", LastRefusal + " (" + pending.Call.Args + " seq " + pending.Call.Seq
                + " from peer " + pending.Call.OriginPeer + ")");
        }

        private CallOutcome Refuse(string key, string reason)
        {
            RefusedCount++;
            LastRefusal = key + ": " + reason;
            Log.Warn("call", "refused " + LastRefusal);
            return CallOutcome.Refused;
        }

        private sealed class PendingCall
        {
            public MethodCall Call;
            public int Ticks;
        }
    }
}
