using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The rules the redesigned screen stands on and that can be checked without a running game: which
    /// plants a seat is said to hold and in what order, what one line of the roster says about a seat, and
    /// how the preview cards of a hand are laid out under that line. The pictures themselves are the game's
    /// own and can only be seen in the game.
    /// </summary>
    public class UiRulesTests
    {
        [Fact]
        public void ASeatHoldsItsOwnPlantsSmallestFirst()
        {
            PlantDraft draft = new PlantDraft();

            draft.TryTake(9, 1);
            draft.TryTake(3, 1);
            draft.TryTake(6, 1);

            Assert.Equal(new List<int> { 3, 6, 9 }, draft.PlantsHeldBy(1));
        }

        [Fact]
        public void ASeatThatHoldsNothingIsListedAsHoldingNothing()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(3, 1);

            Assert.Empty(draft.PlantsHeldBy(2));
        }

        [Fact]
        public void ASeatThatLeavesIsListedAsHoldingNothing()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(3, 1);
            draft.TryTake(7, 1);

            Assert.Equal(2, draft.ReleasePeer(1).Count);
            Assert.Empty(draft.PlantsHeldBy(1));
        }

        [Fact]
        public void TwoSeatsDoNotShareOneList()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(3, 1);
            draft.TryTake(4, 2);

            Assert.Equal(new List<int> { 3 }, draft.PlantsHeldBy(1));
            Assert.Equal(new List<int> { 4 }, draft.PlantsHeldBy(2));
        }

        [Fact]
        public void OneLineTellsOneSeat()
        {
            RosterEntry seat = Seat(2, "Second");
            seat.PingMillis = 37;
            seat.Ready = true;

            Assert.Equal("#2 Second <color=" + LobbyBoard.ReadyColor + ">(ready)</color> 37ms",
                LobbyBoard.Line(seat));
        }

        [Fact]
        public void TheHostSaysSoInItsOwnLine()
        {
            RosterEntry seat = Seat(0, "Player");
            seat.IsHost = true;
            seat.PingMillis = -1;

            Assert.Equal("#0 Player (host)", LobbyBoard.Line(seat));
        }

        [Fact]
        public void TheHeaderCountsTheSeatsInUse()
        {
            Assert.Equal("Lobby 1/4", LobbyBoard.Header(1, 4));
        }

        [Fact]
        public void TheWholeListIsTheHeaderOverTheLinesOfTheSeats()
        {
            SessionRoster roster = new SessionRoster();
            roster.Add(0, "Player", true);
            roster.Add(1, "Second", false);

            string text = LobbyBoard.Text(roster, 4);

            Assert.Equal("Lobby 2/4\n" + LobbyBoard.Line(roster.Find(0)) + "\n"
                + LobbyBoard.Line(roster.Find(1)), text);
        }

        [Fact]
        public void TheCursorWordCarriesTheHammerAndTheCart()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                new CursorIntentPayload { PeerId = 1, Column = 3, Row = 4, Hammer = true, Wheel = true });

            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            var body = new byte[bodyLength];
            System.Buffer.BlockCopy(frame, 4, body, 0, bodyLength);

            CursorIntentPayload read = Protocol.Payload<CursorIntentPayload>(
                Protocol.Decode(body, 0, body.Length));

            Assert.True(read.Hammer);
            Assert.True(read.Wheel);
        }

        [Fact]
        public void ACursorWordThatNamesNoToolNamesNoTool()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                new CursorIntentPayload { PeerId = 1, Column = 3, Row = 4, PlantType = 8 });

            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            var body = new byte[bodyLength];
            System.Buffer.BlockCopy(frame, 4, body, 0, bodyLength);

            CursorIntentPayload read = Protocol.Payload<CursorIntentPayload>(
                Protocol.Decode(body, 0, body.Length));

            Assert.False(read.Hammer);
            Assert.False(read.Wheel);
            Assert.False(read.Glove);
            Assert.Equal(-1, read.GloveZombieType);
        }

        /// <summary>
        /// The fertilizer is the fifth tool a hand can hold, and it travels on the same word as the other
        /// four: a peer that is holding a bag of fertilizer has to be seen holding it.
        /// </summary>
        [Fact]
        public void TheCursorWordCarriesTheFertilizerToo()
        {
            byte[] frame = Protocol.Encode(MsgCategory.Cosmetic, MsgAction.CursorIntent,
                new CursorIntentPayload { PeerId = 1, Column = 3, Row = 4, Fertilizer = true });

            CursorIntentPayload read = Protocol.Payload<CursorIntentPayload>(Read(frame));

            Assert.True(read.Fertilizer);
            Assert.False(read.Hammer);
        }

        /// <summary>Readiness is a state a seat is in, so the word that carries it says which state.</summary>
        [Fact]
        public void TheReadyWordSaysWhichStateTheSeatIsIn()
        {
            SeatReadyPayload ready = Protocol.Payload<SeatReadyPayload>(
                Read(Protocol.Encode(MsgCategory.Control, MsgAction.SeatReady,
                    new SeatReadyPayload { Ready = true })));
            SeatReadyPayload notReady = Protocol.Payload<SeatReadyPayload>(
                Read(Protocol.Encode(MsgCategory.Control, MsgAction.SeatReady,
                    new SeatReadyPayload { Ready = false })));

            Assert.True(ready.Ready);
            Assert.False(notReady.Ready);
        }

        /// <summary>
        /// The bug the bands exist for: a seat holding more plants than one line could carry ran off the
        /// edge of the screen. Five cards of whatever shape the game's own card has, side by side in a line
        /// of the width the roster uses, have to end inside that line.
        /// </summary>
        [Theory]
        [InlineData(0.5f)]
        [InlineData(0.83f)]
        [InlineData(1f)]
        [InlineData(1.2f)]
        [InlineData(3f)]
        public void ABandOfFiveCardsEndsInsideTheLineWhateverShapeTheCardHas(float aspect)
        {
            float width = PreviewBands.CardWidth(LineWidth, Slots, Gap, CardHeight, aspect);
            float rightEdge = PreviewBands.CenterX(Slots - 1, width, Gap) + width / 2f;

            Assert.True(rightEdge <= LineWidth, "a band of " + width + " px cards reaches " + rightEdge
                + " px into a " + LineWidth + " px line");
        }

        /// <summary>
        /// A card wider than the share of the line a band takes is narrowed to that share - which narrows
        /// its height with it, because the shape stays the game's and only the size gives way.
        /// </summary>
        [Fact]
        public void ACardWiderThanAFifthOfTheLineIsNarrowedToThatShare()
        {
            float width = PreviewBands.CardWidth(LineWidth, Slots, Gap, CardHeight, 3f);

            Assert.Equal(59.2f, width, 2);
            Assert.True(width < CardHeight * 3f);
        }

        /// <summary>A card that fits keeps the shape the game drew it in; the cap is a ceiling, not a rule.</summary>
        [Fact]
        public void ACardNarrowerThanThatShareKeepsTheShapeTheGameDraws()
        {
            Assert.Equal(28f, PreviewBands.CardWidth(LineWidth, Slots, Gap, CardHeight, 0.5f), 2);
        }

        /// <summary>
        /// The pictures used to stand over the name and cover the seat above. The first band's top edge is
        /// now a gap below the line's bottom edge, which is the whole of what "under the name" means.
        /// </summary>
        [Fact]
        public void TheFirstBandHangsUnderTheLineAndNotAcrossIt()
        {
            float topEdge = PreviewBands.CenterY(0, CardHeight, Gap) - CardHeight / 2f;

            Assert.Equal(Gap, topEdge, 2);
        }

        [Fact]
        public void EveryBandStandsBelowTheOneBeforeIt()
        {
            float below = PreviewBands.CenterY(1, CardHeight, Gap) - PreviewBands.CenterY(0, CardHeight, Gap);

            Assert.Equal(CardHeight + Gap, below, 2);
        }

        [Fact]
        public void AHandOfElevenPlantsIsSaidInThreeBands()
        {
            Assert.Equal(0, PreviewBands.BandCount(0, Slots));
            Assert.Equal(1, PreviewBands.BandCount(Slots, Slots));
            Assert.Equal(3, PreviewBands.BandCount(11, Slots));
        }

        // The roster's own numbers, copied here because the screen itself is not compiled into these tests:
        // what is checked is the rule, and the rule is only the arithmetic these four numbers go through.
        private const float LineWidth = 320f;
        private const float CardHeight = 56f;
        private const int Slots = 5;
        private const float Gap = 6f;

        private static DecodedFrame Cursor(byte[] frame)
        {
            return Read(frame);
        }

        private static DecodedFrame Read(byte[] frame)
        {
            int bodyLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            var body = new byte[bodyLength];
            System.Buffer.BlockCopy(frame, 4, body, 0, bodyLength);

            return Protocol.Decode(body, 0, body.Length);
        }

        private static RosterEntry Seat(int peerId, string nick)
        {
            return new RosterEntry { PeerId = peerId, Nick = nick, Connected = true };
        }
    }
}
