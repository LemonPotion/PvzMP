using System;
using System.Collections.Generic;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests.Scenario
{
    /// <summary>
    /// A real room for the scenarios: one host on a loopback port, a seat per client, and one model
    /// lawn per seat. The frames are the same `PlantSnapshot` the game sends, over the same sockets,
    /// so a scenario tests the payload and the mapping as they ship; the lawns are models because the
    /// game's own plant list only exists inside the game.
    /// </summary>
    internal sealed class RoomHarness : IDisposable
    {
        private readonly List<MpClient> _clients = new List<MpClient>();

        private RoomHarness(int capacity)
        {
            Host = new MpHost(capacity);
            HostLawn = new LawnModel();
            ClientLawns = new List<LawnModel>();
        }

        public MpHost Host { get; private set; }

        public LawnModel HostLawn { get; private set; }

        public List<LawnModel> ClientLawns { get; private set; }

        public int Seats
        {
            get { return _clients.Count; }
        }

        public static RoomHarness Start(int capacity = 4)
        {
            var room = new RoomHarness(capacity);
            room.Host.Start(0);
            return room;
        }

        /// <summary>Adds one seat now, which is what a late join looks like to the host.</summary>
        public int Join()
        {
            var client = new MpClient();
            client.Connect("127.0.0.1", Host.Port, 3000);

            _clients.Add(client);
            ClientLawns.Add(new LawnModel());

            Assert.True(Wait.Until(() => Host.Peers().Length == _clients.Count),
                "the host never registered seat " + _clients.Count);

            return _clients.Count - 1;
        }

        /// <summary>The host's periodic walk, as one frame to every seat.</summary>
        public void SendLayout()
        {
            Assert.True(Host.Broadcast(MsgCategory.Gameplay, MsgAction.PlantSnapshot,
                new PlantSnapshotPayload { Plants = HostLawn.Layout() }),
                "the host could not broadcast the plant layout");
        }

        /// <summary>The next layout a seat reads, applied to its model lawn.</summary>
        public LayoutReport TakeLayout(int seat)
        {
            DecodedFrame frame = null;

            Assert.True(Wait.Until(() => _clients[seat].TryReceive(out frame)),
                "no plant layout arrived at seat " + seat);

            Assert.Equal(MsgAction.PlantSnapshot, frame.Action);
            Assert.Equal(MsgCategory.Gameplay, frame.Category);

            return ClientLawns[seat].ApplyLayout(
                Protocol.Payload<PlantSnapshotPayload>(frame).Plants);
        }

        public void Dispose()
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                _clients[i].Dispose();
            }

            _clients.Clear();
            Host.Dispose();
        }
    }

    /// <summary>
    /// A scenario as named steps, so a failure says which moment of the timeline broke rather than
    /// only which line: "step 3 (the host's glove moves the plant): expected 1 moved".
    /// </summary>
    internal sealed class Timeline
    {
        private readonly List<KeyValuePair<string, Action>> _steps =
            new List<KeyValuePair<string, Action>>();

        public Timeline Step(string what, Action action)
        {
            _steps.Add(new KeyValuePair<string, Action>(what, action));
            return this;
        }

        public void Run()
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                try
                {
                    _steps[i].Value();
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException("scenario step " + (i + 1) + " ("
                        + _steps[i].Key + ") failed: " + e.Message, e);
                }
            }
        }
    }
}
