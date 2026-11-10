using System;
using System.IO;
using System.Text;
using PvzMP.Net;
using Xunit;

namespace PvzMP.NetTests
{
    public class FrameIoTests
    {
        [Fact]
        public void RoundTripSurvivesWriteThenRead()
        {
            var buffer = new MemoryStream();
            FrameIo.Write(buffer, Protocol.Encode(MsgCategory.Gameplay, MsgAction.SpawnPlant,
                new SpawnPlantPayload { NetId = 7, PlantType = 12, Column = 3, Row = 1, OwnerPeer = 2 }));

            buffer.Position = 0;
            DecodedFrame frame = FrameIo.Read(buffer);

            Assert.NotNull(frame);
            Assert.Equal(MsgCategory.Gameplay, frame.Category);
            Assert.Equal(MsgAction.SpawnPlant, frame.Action);
            SpawnPlantPayload payload = Protocol.Payload<SpawnPlantPayload>(frame);
            Assert.Equal(7, payload.NetId);
            Assert.Equal(2, payload.OwnerPeer);
        }

        [Fact]
        public void ReadHandlesStreamsThatGiveOneByteAtATime()
        {
            var source = new MemoryStream();
            for (int i = 1; i <= 3; i++)
            {
                FrameIo.Write(source, Protocol.Encode(MsgCategory.Gameplay, MsgAction.SunState,
                    new SunStatePayload { Sun = i, MaxSun = 5000 }));
            }

            var dribble = new OneByteAtATimeStream(source.ToArray());
            for (int i = 1; i <= 3; i++)
            {
                DecodedFrame frame = FrameIo.Read(dribble);
                Assert.Equal(i, Protocol.Payload<SunStatePayload>(frame).Sun);
            }

            Assert.Null(FrameIo.Read(dribble));
        }

        [Fact]
        public void EmptyStreamReadsAsCleanEndOfConnection()
        {
            Assert.Null(FrameIo.Read(new MemoryStream()));
        }

        [Fact]
        public void LengthOverTheCapIsRejectedBeforeAllocation()
        {
            FrameException error = Assert.Throws<FrameException>(
                () => FrameIo.Read(new MemoryStream(Header(Protocol.MaxFrameBytes + 1))));
            Assert.Contains("cap", error.Message);
        }

        [Fact]
        public void NegativeLengthIsRejected()
        {
            byte[] header = Header(unchecked((int)0xFFFFFFFF));
            Assert.Throws<FrameException>(() => FrameIo.Read(new MemoryStream(header)));
        }

        [Fact]
        public void LengthTooSmallForAHeaderIsRejected()
        {
            byte[] header = Header(1);
            FrameException error = Assert.Throws<FrameException>(() => FrameIo.Read(new MemoryStream(header)));
            Assert.Contains("too small", error.Message);
        }

        [Fact]
        public void BodyPromisedButNotDeliveredIsAnError()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Control, MsgAction.Roster,
                new RosterPayload { Entries = new[] { new RosterEntry { PeerId = 1, Nick = "host" } } });
            var truncated = new byte[FrameIo.HeaderBytes + 4];
            Buffer.BlockCopy(frame, 0, truncated, 0, truncated.Length);

            FrameException error = Assert.Throws<FrameException>(() => FrameIo.Read(new MemoryStream(truncated)));
            Assert.Contains("stream ended", error.Message);
        }

        [Fact]
        public void GarbageWhereAFrameShouldStartIsReportedNotGuessed()
        {
            // A stream that has lost its place must fail loudly: resynchronising by guessing turns a
            // desynchronised connection into silently wrong gameplay.
            byte[] garbage = Encoding.UTF8.GetBytes("{\"NetId\":1}");
            var withGarbageHeader = new byte[FrameIo.HeaderBytes + garbage.Length];
            Buffer.BlockCopy(garbage, 0, withGarbageHeader, 0, garbage.Length);

            Assert.Throws<FrameException>(() => FrameIo.Read(new MemoryStream(withGarbageHeader)));
        }

        private static byte[] Header(int bodyLength)
        {
            return new[]
            {
                (byte)(bodyLength >> 24),
                (byte)(bodyLength >> 16),
                (byte)(bodyLength >> 8),
                (byte)bodyLength
            };
        }

        private sealed class OneByteAtATimeStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            internal OneByteAtATimeStream(byte[] data)
            {
                _data = data;
            }

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override void Flush()
            {
            }

            public override long Length
            {
                get { return _data.Length; }
            }

            public override long Position
            {
                get { return _position; }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _data.Length)
                {
                    return 0;
                }

                buffer[offset] = _data[_position++];
                return 1;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
