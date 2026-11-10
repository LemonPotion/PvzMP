using System;
using System.Net.Sockets;

namespace PvzMP.Net
{
    /// <summary>
    /// The client end of the star. It owns exactly one link, to the host, and its own local peer id
    /// is unknown until the host answers the handshake - the transport does not invent one.
    /// </summary>
    public sealed class MpClient : IDisposable
    {
        private PeerLink _link;

        public bool IsConnected
        {
            get
            {
                PeerLink link = _link;
                return link != null && link.IsOpen;
            }
        }

        public PeerLink Link
        {
            get { return _link; }
        }

        /// <summary>Fires on a worker thread with the reason the link died.</summary>
        public event Action<string> Lost;

        /// <summary>
        /// Blocking connect on the calling thread. Callers from the game must run this through the
        /// main thread pump or a worker, never from Update.
        /// </summary>
        public void Connect(string address, int port, int timeoutMillis)
        {
            TcpClient client = new TcpClient();
            IAsyncResult pending = client.BeginConnect(address, port, null, null);
            bool reached = pending.AsyncWaitHandle.WaitOne(timeoutMillis, true);
            if (!reached)
            {
                client.Close();
                throw new TimeoutException("no answer from " + address + ":" + port + " within "
                    + timeoutMillis + "ms");
            }

            try
            {
                client.EndConnect(pending);
            }
            catch
            {
                client.Close();
                throw;
            }

            _link = PeerLink.Wrap(client, MpHost.HostPeerId);
            _link.Closed += (peer, reason) =>
            {
                Log.Net("client link to host closed: " + reason);
                Action<string> lost = Lost;
                if (lost != null)
                {
                    lost(reason);
                }
            };
            _link.Start();
            Log.Net("client connected to " + address + ":" + port);
        }

        public void Disconnect(string reason)
        {
            PeerLink link = _link;
            _link = null;
            if (link != null)
            {
                link.Close(reason);
            }
        }

        public bool Send(MsgCategory category, MsgAction action, object payload)
        {
            PeerLink link = _link;
            if (link == null)
            {
                Log.Warn("net", "client is not connected, dropping " + category + "/" + action);
                return false;
            }

            return link.Send(category, action, payload);
        }

        public bool SendCosmetic(MsgCategory category, MsgAction action, object payload, string slot = null)
        {
            PeerLink link = _link;
            return link != null && link.SendCosmetic(category, action, payload, slot);
        }

        public bool TryReceive(out DecodedFrame frame)
        {
            PeerLink link = _link;
            frame = null;
            return link != null && link.TryReceive(out frame);
        }

        public void Dispose()
        {
            Disconnect("disposed");
        }
    }
}
