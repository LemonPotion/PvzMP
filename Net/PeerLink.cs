using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace PvzMP.Net
{
    /// <summary>
    /// One TCP peer. Two threads per direction plus a watchdog, all of them managed-only: nothing in
    /// this class may touch il2cpp, so the game's main thread only ever drains queues that these
    /// threads filled. That is what keeps a slow or stalled peer from freezing the lawn.
    ///
    /// Reliable traffic is a queue that is never silently dropped: when it overflows the link is
    /// closed instead, because a missing spawn event is worse than a visible disconnect. Cosmetic
    /// traffic is the opposite - one slot per identity holding only the newest frame, because a stale
    /// cursor has no value and a growing backlog of them is how a lag spike becomes permanent. The
    /// identity is named by the caller rather than read out of the payload: two latest-wins doors that
    /// share a frame name must not replace each other, and this class does not decode json.
    /// </summary>
    public sealed class PeerLink : IDisposable
    {
        public const int HeartbeatMillis = 500;
        public const int TimeoutMillis = 3000;
        public const int CosmeticIntervalMillis = 100;
        public const int PollMillis = 50;
        public const int MaxOutstandingReliable = 512;
        public const int MaxIncomingUnread = 4096;

        /// <summary>
        /// What one link is allowed to hold of the traffic that is worth replacing. It is a separate
        /// number from the reliable backlog because a frame the room will throw away cannot be the reason
        /// a link is cut: a cursor that stopped being read is not a lost spawn.
        /// </summary>
        public const int MaxIncomingCosmetic = 64;

        public const int DrainCloseMillis = 250;

        private readonly TcpClient _client;
        private readonly ConcurrentQueue<byte[]> _outgoing = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<DecodedFrame> _incoming = new ConcurrentQueue<DecodedFrame>();
        private readonly ConcurrentQueue<DecodedFrame> _incomingCosmetic
            = new ConcurrentQueue<DecodedFrame>();
        private readonly Dictionary<string, byte[]> _pendingCosmetic
            = new Dictionary<string, byte[]>();
        private readonly object _cosmeticLock = new object();
        private readonly int _heartbeatMillis;
        private readonly int _timeoutMillis;
        private readonly int _cosmeticIntervalMillis;
        private long _lastSeenMillis;
        private long _lastHeartbeatSentMillis;
        private long _lastCosmeticSentMillis;
        private int _outstanding;
        private int _closed;
        private string _closeReason;

        private PeerLink(TcpClient client, int peerId, int heartbeatMillis, int timeoutMillis,
                         int cosmeticIntervalMillis)
        {
            _client = client;
            PeerId = peerId;
            _heartbeatMillis = heartbeatMillis;
            _timeoutMillis = timeoutMillis;
            _cosmeticIntervalMillis = cosmeticIntervalMillis;
            Remote = client.Client.RemoteEndPoint == null
                ? "unknown"
                : client.Client.RemoteEndPoint.ToString();
            _lastSeenMillis = Now();
        }

        public int PeerId { get; private set; }

        public string Remote { get; private set; }

        public bool IsOpen
        {
            get { return Volatile.Read(ref _closed) == 0; }
        }

        public string CloseReason
        {
            get { return _closeReason; }
        }

        public int ReliableQueued
        {
            get { return Volatile.Read(ref _outstanding); }
        }

        public int IncomingQueued
        {
            get { return _incoming.Count; }
        }

        /// <summary>
        /// The unread latest-wins frames. Not counted with the reliable ones, because a peer that reads
        /// slowly is not a peer that lost a spawn: what is in here is by definition replaceable.
        /// </summary>
        public int CosmeticQueued
        {
            get { return _incomingCosmetic.Count; }
        }

        /// <summary>
        /// Latest-wins frames thrown away because nobody was reading them. A value that was replaced is
        /// not a loss, but a number is what tells a room why one lawn shows a different bank.
        /// </summary>
        public int DroppedCosmeticCount { get; private set; }

        /// <summary>Fires on a worker thread. The session layer has to move it onto the main thread.</summary>
        public event Action<PeerLink, string> Closed;

        /// <summary>
        /// The timing arguments exist for the tests, which cannot wait three seconds for a timeout to
        /// happen; production callers use the defaults and never pass anything.
        /// </summary>
        public static PeerLink Wrap(TcpClient client, int peerId,
                                    int heartbeatMillis = HeartbeatMillis,
                                    int timeoutMillis = TimeoutMillis,
                                    int cosmeticIntervalMillis = CosmeticIntervalMillis)
        {
            client.NoDelay = true;
            return new PeerLink(client, peerId, heartbeatMillis, timeoutMillis, cosmeticIntervalMillis);
        }

        public void Start()
        {
            Thread receive = new Thread(ReceiveLoop) { IsBackground = true, Name = "PvzMP-recv-" + PeerId };
            Thread send = new Thread(SendLoop) { IsBackground = true, Name = "PvzMP-send-" + PeerId };
            Thread watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "PvzMP-tick-" + PeerId };
            receive.Start();
            send.Start();
            watchdog.Start();
            Log.Net("peer " + PeerId + " (" + Remote + ") link started");
        }

        public bool Send(MsgCategory category, MsgAction action, object payload)
        {
            byte[] frame;
            try
            {
                frame = Protocol.Encode(category, action, payload);
            }
            catch (ProtocolException e)
            {
                Log.Error("net", "peer " + PeerId + " refused " + category + "/" + action + ": " + e.Message);
                return false;
            }

            return QueueReliable(frame, category + "/" + action);
        }

        /// <summary>
        /// Newest-wins: whatever is still unsent for this identity is replaced, and sends are spaced out.
        /// The identity is what may replace what - a second cursor hides the first one, a door's value
        /// never hides another door's.
        /// </summary>
        public bool SendCosmetic(MsgCategory category, MsgAction action, object payload, string slot = null)
        {
            byte[] frame;
            try
            {
                frame = Protocol.Encode(category, action, payload);
            }
            catch (ProtocolException e)
            {
                Log.Error("net", "peer " + PeerId + " refused cosmetic " + category + "/" + action + ": " + e.Message);
                return false;
            }

            if (!IsOpen)
            {
                return false;
            }

            lock (_cosmeticLock)
            {
                _pendingCosmetic[slot ?? (category + "/" + action)] = frame;
            }

            return true;
        }

        /// <summary>
        /// The ordered traffic first, every time: a latest-wins frame has no place in that order, so
        /// reading one early would only make a lawn show a value before the world frame that caused it.
        /// </summary>
        public bool TryReceive(out DecodedFrame frame)
        {
            return _incoming.TryDequeue(out frame) || _incomingCosmetic.TryDequeue(out frame);
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _closeReason = reason;
            try
            {
                _client.Close();
            }
            catch (Exception e)
            {
                Log.Warn("net", "peer " + PeerId + " socket close threw " + e.GetType().Name + ": " + e.Message);
            }

            Log.Net("peer " + PeerId + " (" + Remote + ") closed: " + reason);
            Action<PeerLink, string> handler = Closed;
            if (handler != null)
            {
                handler(this, reason);
            }
        }

        /// <summary>
        /// Closing throws away everything still queued, so the last message a peer must read - the reject
        /// that tells it why it was refused - has to reach the socket first. Bounded, because a peer that
        /// stopped reading cannot be drained and a handshake refusal must not hold the main thread.
        /// </summary>
        public void CloseAfterSend(string reason, int timeoutMillis = DrainCloseMillis)
        {
            long deadline = Now() + timeoutMillis;
            while (IsOpen && Volatile.Read(ref _outstanding) > 0 && Now() < deadline)
            {
                Thread.Sleep(1);
            }

            Close(reason);
        }

        public void Dispose()
        {
            Close("disposed");
        }

        private bool QueueReliable(byte[] frame, string name)
        {
            if (!IsOpen)
            {
                Log.Warn("net", "dropped " + name + " for peer " + PeerId + ": link already closed");
                return false;
            }

            if (Interlocked.Increment(ref _outstanding) > MaxOutstandingReliable)
            {
                Interlocked.Decrement(ref _outstanding);
                Close("outgoing backlog over " + MaxOutstandingReliable + " reliable frames");
                return false;
            }

            _outgoing.Enqueue(frame);
            Log.Net("send " + name + " -> peer " + PeerId);
            return true;
        }

        private void ReceiveLoop()
        {
            string reason = "peer stopped reading";
            try
            {
                NetworkStream stream = _client.GetStream();
                while (IsOpen)
                {
                    DecodedFrame frame = FrameIo.Read(stream);
                    if (frame == null)
                    {
                        reason = "connection ended by peer";
                        break;
                    }

                    Touch();
                    if (frame.Action == MsgAction.Heartbeat && frame.Category == MsgCategory.Control)
                    {
                        continue;
                    }

                    if (frame.Category == MsgCategory.Cosmetic)
                    {
                        QueueCosmetic(frame);
                        continue;
                    }

                    if (_incoming.Count >= MaxIncomingUnread)
                    {
                        reason = "incoming backlog over " + MaxIncomingUnread + " unread frames";
                        break;
                    }

                    _incoming.Enqueue(frame);
                    Log.Net("recv " + frame.Name + " from peer " + PeerId);
                }
            }
            catch (FrameException e)
            {
                reason = "framing error: " + e.Message;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + ": " + e.Message;
            }

            Close(reason);
        }

        /// <summary>
        /// Newest-wins on the way in as well as on the way out: the oldest unread frame is what makes
        /// room, because the one that just arrived describes the newer world. Bounded, and bounded by its
        /// own number - a peer that stops reading may pile replaceable frames against this link, but it
        /// cannot be disconnected for it, which is how one stalled cursor would end a room.
        /// </summary>
        private void QueueCosmetic(DecodedFrame frame)
        {
            DecodedFrame stale;

            if (_incomingCosmetic.Count >= MaxIncomingCosmetic
                && _incomingCosmetic.TryDequeue(out stale))
            {
                DroppedCosmeticCount++;
            }

            _incomingCosmetic.Enqueue(frame);
            Log.Net("recv " + frame.Name + " from peer " + PeerId);
        }

        private void SendLoop()
        {
            try
            {
                NetworkStream stream = _client.GetStream();
                while (IsOpen)
                {
                    byte[] frame;
                    if (_outgoing.TryDequeue(out frame))
                    {
                        // The count means "not yet handed to the operating system", so it drops only after
                        // the write returns - CloseAfterSend waits on it and would otherwise cut the socket
                        // out from under a frame still on its way out, which is how a reject reason vanishes.
                        try
                        {
                            FrameIo.Write(stream, frame);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _outstanding);
                        }

                        continue;
                    }

                    List<byte[]> cosmetics = TakeCosmetics();
                    if (cosmetics != null)
                    {
                        // Every identity goes out together: one frame per interval would make a door's
                        // value wait behind however many cursors the same peer moved.
                        foreach (byte[] cosmetic in cosmetics)
                        {
                            FrameIo.Write(stream, cosmetic);
                        }

                        continue;
                    }

                    Thread.Sleep(2);
                }
            }
            catch (Exception e)
            {
                Log.Warn("net", "peer " + PeerId + " send loop stopped: " + e.GetType().Name + " " + e.Message);
            }

            Close("send loop stopped");
        }

        private List<byte[]> TakeCosmetics()
        {
            if (Now() - Volatile.Read(ref _lastCosmeticSentMillis) < _cosmeticIntervalMillis)
            {
                return null;
            }

            lock (_cosmeticLock)
            {
                if (_pendingCosmetic.Count == 0)
                {
                    return null;
                }

                Volatile.Write(ref _lastCosmeticSentMillis, Now());

                List<byte[]> frames = new List<byte[]>(_pendingCosmetic.Values);
                _pendingCosmetic.Clear();

                return frames;
            }
        }

        private void WatchdogLoop()
        {
            try
            {
                while (IsOpen)
                {
                    Thread.Sleep(PollMillis);
                    if (!IsOpen)
                    {
                        return;
                    }

                    long now = Now();
                    long silent = now - Volatile.Read(ref _lastSeenMillis);
                    if (silent > _timeoutMillis)
                    {
                        Close("no traffic for " + silent + "ms, the limit is " + _timeoutMillis + "ms");
                        return;
                    }

                    if (_heartbeatMillis > 0
                        && now - Volatile.Read(ref _lastHeartbeatSentMillis) >= _heartbeatMillis)
                    {
                        Volatile.Write(ref _lastHeartbeatSentMillis, now);
                        QueueReliable(Protocol.Encode(MsgCategory.Control, MsgAction.Heartbeat, null),
                            "Control/Heartbeat");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("net", "peer " + PeerId + " watchdog stopped: " + e.GetType().Name + " " + e.Message);
            }
        }

        private void Touch()
        {
            Volatile.Write(ref _lastSeenMillis, Now());
        }

        private static long Now()
        {
            return Environment.TickCount64;
        }
    }
}
