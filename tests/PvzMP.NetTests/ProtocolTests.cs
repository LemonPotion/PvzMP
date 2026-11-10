using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace PvzMP.NetTests
{
    public class ProtocolTests
    {
        [Fact]
        public void FrameLayoutIsLengthCategoryActionJson()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                new CursorIntentPayload { PeerId = 3, Column = 5, Row = 2, PlantType = 8, Shovel = false });

            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            Assert.Equal(frame.Length - 4, bodyLength);
            Assert.Equal(Protocol.BodyOverhead + Encoding.UTF8.GetByteCount(frame.ToJsonFragment()), bodyLength);
            Assert.Equal((byte)MsgCategory.Cosmetic, frame[4]);
            Assert.Equal((byte)MsgAction.CursorIntent, frame[5]);
        }

        [Fact]
        public void EveryCategoryRoundTripsItsPayload()
        {
            var roster = new RosterPayload
            {
                Entries = new[]
                {
                    new RosterEntry { PeerId = 0, Nick = "host", IsHost = true, Connected = true },
                    new RosterEntry { PeerId = 1, Nick = "Lemon", IsHost = false, Connected = true }
                }
            };
            DecodedFrame control = RoundTrip(MsgCategory.Control, MsgAction.Roster, roster);
            Assert.Equal(2, Protocol.Payload<RosterPayload>(control).Entries.Length);
            Assert.Equal("host", Protocol.Payload<RosterPayload>(control).Entries[0].Nick);

            DecodedFrame gameplay = RoundTrip(MsgCategory.Gameplay, MsgAction.SpawnZombie,
                new SpawnZombiePayload { NetId = 11, ZombieType = 4, Row = 3, SpawnX = 9.9f, Wave = 2,
                    OriginSpeed = 4.7f });
            SpawnZombiePayload zombie = Protocol.Payload<SpawnZombiePayload>(gameplay);
            Assert.Equal(9.9f, zombie.SpawnX);
            Assert.Equal(2, zombie.Wave);
            Assert.Equal(4.7f, zombie.OriginSpeed);

            DecodedFrame cosmetic = RoundTrip(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                new CursorIntentPayload { PeerId = 2, Column = 1, Row = 4, PlantType = 6, Shovel = true });
            Assert.True(Protocol.Payload<CursorIntentPayload>(cosmetic).Shovel);
        }

        /// <summary>
        /// A fertilizing travels as the cell it happened on and the seat whose hand did it, because the
        /// result is each lawn's own numbers. All three are plain ints, so the frame needs no adapter.
        /// </summary>
        [Fact]
        public void TheFertilizedFrameCarriesTheCellAndTheSeatThatDroppedIt()
        {
            FertilizedPayload cell = Protocol.Payload<FertilizedPayload>(
                RoundTrip(MsgCategory.Gameplay, MsgAction.Fertilized,
                    new FertilizedPayload { OriginPeer = 3, Column = 4, Row = 2 }));

            Assert.Equal(3, cell.OriginPeer);
            Assert.Equal(4, cell.Column);
            Assert.Equal(2, cell.Row);
        }

        /// <summary>
        /// What a fertilizer grew travels as the cell and both kinds: the kind the lawn that held the
        /// fertilizer lost and the kind it gained. The objects are not said, because a lawn has its own.
        /// </summary>
        [Fact]
        public void TheGrewFrameCarriesTheCellAndTheTwoKindsItChangedBetween()
        {
            FertilizeGrewPayload grew = Protocol.Payload<FertilizeGrewPayload>(
                RoundTrip(MsgCategory.Gameplay, MsgAction.FertilizeGrew,
                    new FertilizeGrewPayload { OriginPeer = 3, Column = 1, Row = 0, OldType = 3, NewType = 1027 }));

            Assert.Equal(3, grew.OriginPeer);
            Assert.Equal(1, grew.Column);
            Assert.Equal(0, grew.Row);
            Assert.Equal(3, grew.OldType);
            Assert.Equal(1027, grew.NewType);
        }

        /// <summary>
        /// A cart's lift travels as the room's id of the plant and the cell it left. Every lawn keeps its
        /// own object and its own numbers; the frame only says which record is away and from where, and the
        /// drop that brings it back is the usual move. Proof: ref/EVIDENCE.md E55, E58.
        /// </summary>
        [Fact]
        public void TheLiftFrameCarriesThePlantIdAndTheCellItLeft()
        {
            PlantLiftPayload lift = Protocol.Payload<PlantLiftPayload>(
                RoundTrip(MsgCategory.Gameplay, MsgAction.PlantLift,
                    new PlantLiftPayload { NetId = 41, Column = 6, Row = 3 }));

            Assert.Equal(41, lift.NetId);
            Assert.Equal(6, lift.Column);
            Assert.Equal(3, lift.Row);
        }

        [Fact]
        public void TheLiftIsAGameplayActionAndNoOtherCategoryOwnsIt()
        {
            Protocol.Decode(Body(2, (byte)MsgAction.PlantLift, "{}"), 0, 2);

            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(1, (byte)MsgAction.PlantLift, "{}"), 0, 2));
        }

        /// <summary>
        /// A sound travels as the three numbers the game's own audio door was handed and the seat whose lawn
        /// made it, so every other seat calls the same door with the same numbers. Nothing is looked up on
        /// the way: an id is the same id on every lawn, and kind 2 is what says which of the three doors.
        /// </summary>
        [Fact]
        public void TheSoundFrameCarriesTheNumbersTheAudioDoorWasGivenAndTheSeatThatMadeIt()
        {
            EffectPayload sound = Protocol.Payload<EffectPayload>(
                RoundTrip(MsgCategory.Gameplay, MsgAction.Effect,
                    new EffectPayload
                    {
                        Kind = 2,
                        OriginPeer = 3,
                        Sound = 1047,
                        Volume = 0.5f,
                        Pitch = 1f
                    }));

            Assert.Equal(2, sound.Kind);
            Assert.Equal(3, sound.OriginPeer);
            Assert.Equal(1047, sound.Sound);
            Assert.Equal(0.5f, sound.Volume);
            Assert.Equal(1f, sound.Pitch);
        }

        /// <summary>
        /// The two particle kinds keep their own arguments through the same frame: kind 0 the Vector2 and
        /// the `lim` flag, kind 1 the Vector3 and the `setLayer` flag.
        /// </summary>
        [Fact]
        public void AParticleFrameKeepsTheArgumentsOfTheDoorThatMadeIt()
        {
            EffectPayload burst = Protocol.Payload<EffectPayload>(
                RoundTrip(MsgCategory.Gameplay, MsgAction.Effect,
                    new EffectPayload { Kind = 1, OriginPeer = 0, ParticleType = 44, X = 1.5f, Y = 2.5f,
                        Z = -1f, Row = 9, SetLayer = false }));

            Assert.Equal(44, burst.ParticleType);
            Assert.Equal(-1f, burst.Z);
            Assert.Equal(9, burst.Row);
            Assert.False(burst.SetLayer);
        }

        [Fact]
        public void HeartbeatCarriesNoPayloadAndStillDecodes()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Control, MsgAction.Heartbeat, null);
            DecodedType(frame, out DecodedFrame decoded);
            Assert.Equal("{}", decoded.Json);
            Assert.Equal("Control/Heartbeat", decoded.Name);
        }

        [Fact]
        public void UnknownCategoryIsRejected()
        {
            ProtocolException error = Assert.Throws<ProtocolException>(
                () => Protocol.Decode(Body(9, 1, "{}"), 0, 4));
            Assert.Contains("category", error.Message);
        }

        [Fact]
        public void ActionOutsideItsCategoryRangeIsRejected()
        {
            // 200 is not a gameplay action; a peer that invents one must not reach the session layer.
            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(2, 200, "{}"), 0, 4));
            // Cosmetic owns CursorIntent and the call frame, and nothing else.
            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(3, 2, "{}"), 0, 4));
            // Action 0 is not defined either.
            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(1, 0, "{}"), 0, 4));
        }

        [Fact]
        public void ActionBorrowedFromAnotherCategoryIsRejected()
        {
            // 12 is SpawnPlant: legal under Gameplay, a bug or an attack under Control.
            Protocol.Decode(Body(2, (byte)MsgAction.SpawnPlant, "{}"), 0, 2);
            ProtocolException error = Assert.Throws<ProtocolException>(
                () => Protocol.Decode(Body(1, (byte)MsgAction.SpawnPlant, "{}"), 0, 2));
            Assert.Contains("is not a Control action", error.Message);

            // 19 is SunState, and CursorIntent=31 means nothing to Control or Gameplay.
            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(3, (byte)MsgAction.SunState, "{}"), 0, 2));
            Assert.Throws<ProtocolException>(() => Protocol.Decode(Body(2, (byte)MsgAction.CursorIntent, "{}"), 0, 2));
        }

        [Fact]
        public void BodyWithoutRoomForCategoryAndActionIsRejected()
        {
            byte[] tooShort = { 1 };
            Assert.Throws<ProtocolException>(() => Protocol.Decode(tooShort, 0, tooShort.Length));
        }

        [Fact]
        public void MalformedJsonFailsWhenThePayloadIsRead()
        {
            DecodedFrame frame = Protocol.Decode(Body(2, (byte)MsgAction.SunState, "{ this is not json"), 0, 20);
            ProtocolException error = Assert.Throws<ProtocolException>(
                () => Protocol.Payload<SunStatePayload>(frame));
            Assert.Contains("SunStatePayload", error.Message);
        }

        [Fact]
        public void OversizedTextFromAPeerIsRejectedOnRead()
        {
            string huge = new string('x', Protocol.MaxTextChars + 1);

            // Top level: HelloPayload.Nick.
            string hello = "{\"Version\":\"0.1.0\",\"Nick\":\"" + huge + "\"}";
            byte[] helloBody = Body(1, (byte)MsgAction.Hello, hello);
            ProtocolException direct = Assert.Throws<ProtocolException>(() =>
                Protocol.Payload<HelloPayload>(Protocol.Decode(helloBody, 0, helloBody.Length)));
            Assert.Contains("Nick", direct.Message);

            // Nested: roster entries are the ones that end up painted on the lobby screen.
            string roster = "{\"Entries\":[{\"PeerId\":1,\"Nick\":\"" + huge + "\",\"IsHost\":false,"
                + "\"Connected\":true}]}";
            byte[] body = Body(1, (byte)MsgAction.Roster, roster);
            ProtocolException nested = Assert.Throws<ProtocolException>(() =>
                Protocol.Payload<RosterPayload>(Protocol.Decode(body, 0, body.Length)));
            Assert.Contains("Nick", nested.Message);
        }

        [Fact]
        public void EncodingRefusesAnythingLargerThanTheFrameCap()
        {
            ProtocolException error = Assert.Throws<ProtocolException>(() => Protocol.Encode(
                MsgCategory.Gameplay, MsgAction.GameOver,
                new GameOverPayload { Won = false, Reason = new string('y', Protocol.MaxFrameBytes) }));
            Assert.Contains("cap", error.Message);
        }

        [Fact]
        public void UnknownJsonPropertiesAreIgnoredSoAForwardCompatiblePeerStillWorks()
        {
            byte[] body = Body(2, (byte)MsgAction.SunState, "{\"Sun\":25,\"MaxSun\":5000,\"SomeNewerField\":[1,2,3]}");
            DecodedFrame frame = Protocol.Decode(body, 0, body.Length);
            SunStatePayload sun = Protocol.Payload<SunStatePayload>(frame);
            Assert.Equal(25, sun.Sun);
            Assert.Equal(5000, sun.MaxSun);
        }

        [Fact]
        public void NullPayloadObjectIsReportedInsteadOfReturningNullSilently()
        {
            DecodedFrame frame = Protocol.Decode(Body(1, 4, "null"), 0, 6);
            Assert.Throws<ProtocolException>(() => Protocol.Payload<RejectPayload>(frame));
        }

        [Fact]
        public void EveryActionHasItsOwnWireByteAndItsOwnCategory()
        {
            // The first draft numbered each category from 1, so Heartbeat and SpawnZombie were both 5 and
            // the name of a decoded frame depended on which enum member the runtime reported first.
            MsgAction[] actions = (MsgAction[])Enum.GetValues(typeof(MsgAction));
            var bytes = new HashSet<byte>();
            foreach (MsgAction action in actions)
            {
                Assert.True(bytes.Add((byte)action), "byte " + (byte)action + " is used twice, " + action);

                List<MsgCategory> owners = OwnersOf(action);
                foreach (MsgCategory owner in owners)
                {
                    byte[] body = Body((byte)owner, (byte)action, "{}");
                    DecodedFrame frame = Protocol.Decode(body, 0, body.Length);
                    Assert.Equal(action, frame.Action);
                    Assert.Equal(owner + "/" + action, frame.Name);
                }

                // Every category that does not own the byte must refuse it: a frame legal everywhere is a
                // frame whose lane is chosen by whoever wrote it instead of by the room.
                foreach (MsgCategory other in Enum.GetValues(typeof(MsgCategory)))
                {
                    if (!owners.Contains(other))
                    {
                        Assert.Throws<ProtocolException>(
                            () => Protocol.Decode(Body((byte)other, (byte)action, "{}"), 0, 2));
                    }
                }
            }
        }

        /// <summary>
        /// The lanes a byte may travel on. One action has two, and that is the whole point: a call frame
        /// is either the room's ordered history or a single latest value, and the coverage row - not the
        /// sender - decides which.
        /// </summary>
        private static List<MsgCategory> OwnersOf(MsgAction action)
        {
            var owners = new List<MsgCategory>();

            if ((action >= MsgAction.Hello && action <= MsgAction.SeatReady)
                || (action >= MsgAction.Ping && action <= MsgAction.Pong))
            {
                owners.Add(MsgCategory.Control);
            }
            else if (action == MsgAction.CursorIntent)
            {
                owners.Add(MsgCategory.Cosmetic);
            }
            else
            {
                owners.Add(MsgCategory.Gameplay);

                if (action == MsgAction.MethodCall)
                {
                    owners.Add(MsgCategory.Cosmetic);
                }
            }

            return owners;
        }

        private static DecodedFrame RoundTrip(MsgCategory category, MsgAction action, object payload)
        {
            byte[] frame = Protocol.Encode(category, action, payload);
            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            var body = new byte[bodyLength];
            Buffer.BlockCopy(frame, 4, body, 0, bodyLength);
            return Protocol.Decode(body, 0, body.Length);
        }

        private static byte[] Body(int category, int action, string json)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            var body = new byte[2 + jsonBytes.Length];
            body[0] = (byte)category;
            body[1] = (byte)action;
            Buffer.BlockCopy(jsonBytes, 0, body, 2, jsonBytes.Length);
            return body;
        }

        private static void DecodedType(byte[] frame, out DecodedFrame decoded)
        {
            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            var body = new byte[bodyLength];
            Buffer.BlockCopy(frame, 4, body, 0, bodyLength);
            decoded = Protocol.Decode(body, 0, body.Length);
        }
    }

    internal static class ProtocolTestExtensions
    {
        /// <summary>The JSON part of an encoded frame, for layout assertions only.</summary>
        internal static string ToJsonFragment(this byte[] frame)
        {
            return Encoding.UTF8.GetString(frame, 6, frame.Length - 6);
        }
    }
}
