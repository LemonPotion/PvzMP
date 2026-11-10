using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PvzMP.Net
{
    /// <summary>
    /// The star in peer-simulates + discrete events: every client talks only to the host, and the
    /// host is also peer 0. Threads here only accept, connect and queue; deciding anything about a
    /// message belongs to the session layer that drains these queues on the game's main thread.
    /// </summary>
    public sealed class MpHost : IDisposable
    {
        public const int HostPeerId = 0;

        /// <summary>How long the accept thread waits before trying again after a failed accept.</summary>
        public const int AcceptRetryMillis = 20;

        private readonly object _sync = new object();
        private readonly List<PeerLink> _peers = new List<PeerLink>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private int _nextPeerId = 1;
        private int _running;

        public MpHost(int maxPlayers)
        {
            MaxPlayers = maxPlayers;
        }

        public int MaxPlayers { get; private set; }

        public int Port
        {
            get
            {
                TcpListener listener = _listener;
                return listener == null
                    ? 0
                    : ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }

        public bool IsRunning
        {
            get { return Volatile.Read(ref _running) == 1; }
        }

        /// <summary>Fires on the accept thread; the session must marshal it to the main thread.</summary>
        public event Action<PeerLink> PeerConnected;

        public event Action<PeerLink, string> PeerLost;

        public void Start(int port)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                throw new InvalidOperationException("host already started");
            }

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PvzMP-accept" };
            _acceptThread.Start();
            Log.Net("host listening on port " + Port + ", capacity " + MaxPlayers + " peers");
        }

        public PeerLink[] Peers()
        {
            lock (_sync)
            {
                return _peers.ToArray();
            }
        }

        public bool SendTo(int peerId, MsgCategory category, MsgAction action, object payload)
        {
            PeerLink peer = Find(peerId);
            if (peer == null)
            {
                Log.Warn("net", "SendTo(" + peerId + ") -> no such peer, dropping " + category + "/" + action);
                return false;
            }

            return peer.Send(category, action, payload);
        }

        /// <summary>
        /// The refuse-and-hang-up step in one call: closing the link throws away a queued reject, and the
        /// reject is the only thing the refused player gets to read.
        /// </summary>
        public bool RejectAndClose(int peerId, RejectCode code, string reason)
        {
            PeerLink peer = Find(peerId);
            if (peer == null)
            {
                Log.Warn("net", "RejectAndClose(" + peerId + ") -> no such peer, dropping " + code);
                return false;
            }

            bool sent = peer.Send(MsgCategory.Control, MsgAction.Reject,
                new RejectPayload { Code = code, Reason = reason });
            peer.CloseAfterSend("rejected: " + code);
            return sent;
        }

        public bool Broadcast(MsgCategory category, MsgAction action, object payload, int exceptPeerId = -1)
        {
            int sent = 0;
            foreach (PeerLink peer in Peers())
            {
                if (peer.PeerId == exceptPeerId)
                {
                    continue;
                }

                if (peer.Send(category, action, payload))
                {
                    sent++;
                }
            }

            Log.Net("broadcast " + category + "/" + action + " to " + sent + " of " + Peers().Length + " peers");
            return sent > 0;
        }

        public bool BroadcastCosmetic(MsgCategory category, MsgAction action, object payload,
                                      int exceptPeerId = -1, string slot = null)
        {
            foreach (PeerLink peer in Peers())
            {
                if (peer.PeerId != exceptPeerId)
                {
                    peer.SendCosmetic(category, action, payload, slot);
                }
            }

            return true;
        }

        /// <summary>
        /// One seat's newest-wins traffic: what a peer that arrived in the middle of a level is told,
        /// without repeating to the seats that already heard it.
        /// </summary>
        public bool SendCosmeticTo(int peerId, MsgCategory category, MsgAction action, object payload,
                                   string slot)
        {
            PeerLink peer = Find(peerId);
            if (peer == null)
            {
                Log.Warn("net", "SendCosmeticTo(" + peerId + ") -> no such peer, dropping "
                    + category + "/" + action);
                return false;
            }

            return peer.SendCosmetic(category, action, payload, slot);
        }

        /// <summary>Drains one message from any peer. Call it in a loop from the main thread pump.</summary>
        public bool TryReceive(out int peerId, out DecodedFrame frame)
        {
            foreach (PeerLink peer in Peers())
            {
                if (peer.TryReceive(out frame))
                {
                    peerId = peer.PeerId;
                    return true;
                }
            }

            peerId = -1;
            frame = null;
            return false;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _running, 0) == 0)
            {
                return;
            }

            TcpListener listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception e)
                {
                    Log.Warn("net", "listener stop threw " + e.GetType().Name + ": " + e.Message);
                }
            }

            foreach (PeerLink peer in Peers())
            {
                peer.Close("host stopped");
            }

            lock (_sync)
            {
                _peers.Clear();
            }

            Log.Net("host stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private PeerLink Find(int peerId)
        {
            lock (_sync)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (_peers[i].PeerId == peerId)
                    {
                        return _peers[i];
                    }
                }
            }

            return null;
        }

        private void AcceptLoop()
        {
            while (IsRunning)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception e)
                {
                    if (!IsRunning)
                    {
                        return;
                    }

                    // Nothing in a failed accept says the listener is broken: a guest can vanish on the
                    // way in, and Windows has more than one answer for that, none of them fatal. Leaving
                    // the loop was how one transient failure would end every later join while the room
                    // went on reading as hosting, so the loop stops only with the room itself.
                    Log.Warn("net", "accept failed: " + e.GetType().Name + " " + e.Message
                        + ", still listening on port " + Port);
                    Thread.Sleep(AcceptRetryMillis);
                    continue;
                }

                int peerId;
                lock (_sync)
                {
                    if (_peers.Count >= MaxPlayers)
                    {
                        peerId = -1;
                    }
                    else
                    {
                        peerId = _nextPeerId++;
                    }
                }

                if (peerId < 0)
                {
                    Log.Warn("net", "rejected a connection from " + SafeRemote(client)
                        + ": room full at " + MaxPlayers + " peers");
                    RejectFullRoom(client);
                    continue;
                }

                PeerLink link = PeerLink.Wrap(client, peerId);
                link.Closed += (peer, reason) =>
                {
                    lock (_sync)
                    {
                        _peers.Remove(peer);
                    }

                    Action<PeerLink, string> lost = PeerLost;
                    if (lost != null)
                    {
                        lost(peer, reason);
                    }
                };

                lock (_sync)
                {
                    _peers.Add(link);
                }

                link.Start();
                Action<PeerLink> connected = PeerConnected;
                if (connected != null)
                {
                    connected(link);
                }
            }
        }

        private static void RejectFullRoom(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                FrameIo.Write(stream, Protocol.Encode(MsgCategory.Control, MsgAction.Reject,
                    new RejectPayload { Code = RejectCode.RoomFull, Reason = "room is full" }));
            }
            catch (Exception e)
            {
                Log.Warn("net", "could not report a full room: " + e.GetType().Name + " " + e.Message);
            }

            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        private static string SafeRemote(TcpClient client)
        {
            try
            {
                return client.Client.RemoteEndPoint == null ? "unknown" : client.Client.RemoteEndPoint.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
