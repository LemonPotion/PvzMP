using System.Net;
using System.Net.Sockets;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    public class EntityLedgerTests
    {
        [Fact]
        public void IdsOnlyGrowSoADeadEntityCannotBeMistakenForANewOne()
        {
            var ledger = new EntityLedger();

            Assert.Equal(1, ledger.Add(NetKind.Plant, 0, 0, 1, 1).NetId);
            Assert.Equal(2, ledger.Add(NetKind.Plant, 1, 3, 2, 2).NetId);

            ledger.Remove(2);

            // Reusing id 2 would let a despawn that was already in flight name a different plant.
            Assert.Equal(3, ledger.Add(NetKind.Sun, 1, 42, 2, 2).NetId);
            Assert.Null(ledger.Find(2));
            Assert.Equal(42, ledger.Find(3).Type);
        }

        [Fact]
        public void TheRecordAnswersBothDirectionsItIsAskedAbout()
        {
            var ledger = new EntityLedger();
            NetEntity plant = ledger.Add(NetKind.Plant, 2, 7, 4, 3);

            Assert.True(ledger.Bind(plant.NetId, 512));

            NetEntity byId = ledger.Find(plant.NetId);
            Assert.Same(plant, byId);
            Assert.Equal(2, byId.OwnerPeer);
            Assert.Equal(7, byId.Type);
            Assert.Equal(4, byId.Column);
            Assert.Equal(3, byId.Row);
            Assert.Equal(512, byId.InstanceId);

            Assert.Same(plant, ledger.ByInstanceId(512));
            Assert.Null(ledger.ByInstanceId(999));
        }

        [Fact]
        public void EachMachineBindsTheSameNetIdToItsOwnObject()
        {
            var hostSide = new EntityLedger();
            var peerSide = new EntityLedger();

            NetEntity hostRecord = hostSide.Add(NetKind.Plant, 1, 0, 2, 5);
            NetEntity peerRecord = peerSide.Add(NetKind.Plant, 1, 0, 2, 5);

            // The spawn is the game's own creator on each machine, so the Unity ids differ by definition.
            hostSide.Bind(hostRecord.NetId, 401);
            peerSide.Bind(peerRecord.NetId, 900713);

            Assert.Equal(401, hostSide.Find(1).InstanceId);
            Assert.Equal(900713, peerSide.Find(1).InstanceId);
            Assert.Same(hostRecord, hostSide.ByInstanceId(401));
            Assert.Null(hostSide.ByInstanceId(900713));
        }

        [Fact]
        public void OnlyAPlantTakesUpACell()
        {
            var ledger = new EntityLedger();

            NetEntity sun = ledger.Add(NetKind.Sun, 0, 42, 3, 1);
            Assert.Null(ledger.PlantAt(3, 1));

            NetEntity pea = ledger.Add(NetKind.Plant, 0, 0, 3, 1);
            Assert.Same(pea, ledger.PlantAt(3, 1));
            Assert.Null(ledger.PlantAt(3, 2));
            Assert.Null(ledger.PlantAt(4, 1));

            Assert.Same(pea, ledger.Remove(pea.NetId));
            Assert.Null(ledger.PlantAt(3, 1));
            Assert.Same(sun, ledger.Find(sun.NetId));
        }

        [Fact]
        public void ABindingIsRefusedForAnUnknownIdOrNoRealObject()
        {
            var ledger = new EntityLedger();
            NetEntity plant = ledger.Add(NetKind.Plant, 0, 0, 0, 0);

            Assert.False(ledger.Bind(plant.NetId + 50, 12));
            Assert.False(ledger.Bind(plant.NetId, 0));
            Assert.Equal(0, ledger.Find(plant.NetId).InstanceId);
            Assert.Null(ledger.ByInstanceId(0));

            Assert.True(ledger.Bind(plant.NetId, 12));
            Assert.Equal(12, ledger.Find(plant.NetId).InstanceId);
        }

        [Fact]
        public void RemovingAnUnknownIdSaysSoInsteadOfPretending()
        {
            var ledger = new EntityLedger();
            Assert.Null(ledger.Remove(1));

            ledger.Add(NetKind.Zombie, 0, 1, 8, 2);
            Assert.NotNull(ledger.Remove(1));
            Assert.Null(ledger.Remove(1));
            Assert.Equal(string.Empty, ledger.Describe());
        }

        [Fact]
        public void DescribePrintsOneEntryPerEntityWithTheLocalObject()
        {
            var ledger = new EntityLedger();
            NetEntity pea = ledger.Add(NetKind.Plant, 1, 0, 2, 4);
            ledger.Bind(pea.NetId, 77);
            ledger.Add(NetKind.Sun, 0, 42, 5, 0);

            Assert.Equal("#1:Plant(owner 1, type 0, cell 2,4, obj 77) "
                + "#2:Sun(owner 0, type 42, cell 5,0, obj 0)", ledger.Describe());
        }

        [Fact]
        public void TheRoomOwnsOneLedgerPerEndAndWipesItWhenTheSessionEnds()
        {
            var session = new MpSession();
            int port = ClosedPort();

            session.Host("Player", port);
            Assert.True(session.IsActive, "the host did not start listening on " + port);

            session.Entities.Add(NetKind.Plant, 0, 0, 1, 1);
            Assert.Single(session.Entities.Entries);

            session.Leave("test over");
            Assert.False(session.IsActive);
            Assert.Empty(session.Entities.Entries);

            // A second room in the same process must not inherit the ids of the first.
            session.Host("Player", port);
            Assert.Equal(1, session.Entities.Add(NetKind.Plant, 0, 0, 1, 1).NetId);
            session.Leave("test over");
        }

        [Fact]
        public void AClientRepeatsTheIdTheHostGaveAndRefusesATwin()
        {
            var ledger = new EntityLedger();

            NetEntity record = ledger.Adopt(7, NetKind.Plant, 1, 0, 2, 4);
            Assert.NotNull(record);
            Assert.Equal(7, record.NetId);
            Assert.Equal(0, record.InstanceId);

            Assert.Null(ledger.Adopt(7, NetKind.Plant, 1, 0, 2, 4));
            Assert.Null(ledger.Adopt(0, NetKind.Plant, 1, 0, 2, 4));
            Assert.Single(ledger.Entries);

            Assert.True(ledger.Bind(7, 33));
            Assert.Same(record, ledger.PlantAt(2, 4));
        }

        /// <summary>A port the operating system just handed out and gave back: free long enough to bind.</summary>
        private static int ClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
