using System;
using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The one place a received frame becomes a native call. Every rule the old ten gates each had to
    /// remember is checked here from both directions: what a call is allowed to do, and what a room that
    /// is not being told anything must never do.
    ///
    /// Shared collection with the coverage tests: both read the log.
    /// </summary>
    [Collection("coverage")]
    public class CallInboxTests
    {
        private const string SunKey = "Board.SetSun/1";

        private static CoverageRule Rule(string cls, string method, int arity, Verdict verdict)
        {
            return new CoverageRule(cls, method, arity, Authority.AnyPeer, verdict);
        }

        private static CoverageFact Fact(string cls, string method, int arity, string cats,
                                        string effect)
        {
            return new CoverageFact(Arbiter.Key(cls, method, arity), 0x481250, 7, 0, false,
                                    "void T()", cats, effect, false);
        }

        /// <summary>A table with one covered door, so the applier can be asked anything of any shape.</summary>
        private static CoverageTable OneDoor(string cls, string method, int arity, Verdict verdict,
                                             string cats)
        {
            return new CoverageTable(new[] { Rule(cls, method, arity, verdict) },
                                     new[] { Fact(cls, method, arity, cats, "world") });
        }

        private static CallInbox Door(Verdict verdict, string cats, out List<string> ran)
        {
            List<string> calls = new List<string>();
            ran = calls;

            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, verdict, cats));
            inbox.Applier = delegate (MethodCall call)
            {
                calls.Add(call.Args);
                return CallOutcome.Applied;
            };

            return inbox;
        }

        private static MethodCall Call(string key, string args, long seq, int origin)
        {
            return new MethodCall(key, CallArgs.None, args, seq, origin);
        }

        // ---- what an accepted call does -------------------------------------

        [Fact]
        public void ACoveredCallRunsTheApplierExactlyOnce()
        {
            List<string> ran;
            CallInbox box = Door(Verdict.Replicate, "i", out ran);

            TestLog.Clear();
            CallOutcome outcome = box.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay);

            Assert.Equal(CallOutcome.Applied, outcome);
            Assert.Equal("[50]", Assert.Single(ran));
            Assert.Equal(1, box.AppliedCount);
            Assert.True(TestLog.Has("applied " + SunKey));
        }

        [Fact]
        public void WhileACallIsBeingAppliedTheRoomIsReplayingItself()
        {
            // The guard every gate is allowed to ask about, in the one place it can be set: nested native
            // calls inside a replay must not be announced again, and nothing outside the replay may see
            // the flag set.
            bool setInside = false;
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                setInside = CallInbox.IsApplying;
                Assert.Equal(1, CallInbox.ApplyingDepth);
                return CallOutcome.Applied;
            };

            inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);

            Assert.True(setInside, "the applier could not see that the room was replaying");
            Assert.False(CallInbox.IsApplying);
            Assert.Equal(0, CallInbox.ApplyingDepth);
        }

        [Fact]
        public void AThrowLeavesTheReplayGuardSetAndForgetsNothing()
        {
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                throw new InvalidOperationException("the lawn was gone under the call");
            };

            TestLog.Clear();
            CallOutcome outcome = inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);

            Assert.Equal(CallOutcome.Failed, outcome);
            Assert.Equal(0, CallInbox.ApplyingDepth);
            Assert.False(CallInbox.IsApplying);
            Assert.Equal(1, inbox.FailedCount);
            Assert.True(TestLog.Has("threw InvalidOperationException"));
        }

        [Fact]
        public void ShouldAnnounceIsTheOnlyQuestionAGateNeeds()
        {
            Assert.False(CallInbox.ShouldAnnounce(false), "a call outside a room is not the room's");
            Assert.True(CallInbox.ShouldAnnounce(true));

            CallInbox.EnterApplying();
            Assert.False(CallInbox.ShouldAnnounce(true), "a nested call inside a replay is not new");
            CallInbox.ExitApplying();
            Assert.True(CallInbox.ShouldAnnounce(true));
            Assert.Equal(0, CallInbox.ApplyingDepth);
        }

        [Fact]
        public void ANestedReplayStillUnwindsToQuiet()
        {
            CallInbox.EnterApplying();
            CallInbox.EnterApplying();
            CallInbox.ExitApplying();
            Assert.True(CallInbox.IsApplying);
            CallInbox.ExitApplying();
            Assert.False(CallInbox.IsApplying);

            // A gate that exits without entering cannot push the depth below zero and unlock the room.
            CallInbox.ExitApplying();
            Assert.Equal(0, CallInbox.ApplyingDepth);
        }

        [Fact]
        public void WithNoOneInstalledToRunCallsNothingIsPretendedApplied()
        {
            // Board.SetSun is the shipped latest-wins door, so its frame comes in on its own lane.
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.HostPeerId = 2;

            TestLog.Clear();
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal(0, inbox.AppliedCount);
            Assert.Contains("no one is installed", inbox.LastRefusal);
        }

        // ---- who a frame has to come from -----------------------------------

        [Fact]
        public void AHostOnlyDoorFromTheHostSeatIsApplied()
        {
            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.HostPeerId = 2;
            inbox.LocalPeerId = 3;
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal("[50]", Assert.Single(ran));
        }

        [Fact]
        public void AHostOnlyDoorFromAnotherSeatIsRefusedWhateverItsPayloadClaims()
        {
            // The authority column is a claim about the sender, and only the seat is believed: writing
            // OriginPeer = 0 into a payload is not the same as being the host.
            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.HostPeerId = 0;
            inbox.LocalPeerId = 3;
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            TestLog.Clear();
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[50]", 0, 0), 2, MsgCategory.Cosmetic));

            Assert.Empty(ran);
            Assert.Contains("only the host", inbox.LastRefusal);
            Assert.True(TestLog.Has("refused " + SunKey));
        }

        [Fact]
        public void AnAnyPeerDoorIsAppliedFromWhicheverSeatSentIt()
        {
            // A click on any seat is the room's business, so the sender column cannot refuse it.
            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.HostPeerId = 0;
            inbox.LocalPeerId = 0;
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            const string PlantKey = "Mouse.SetPlantFromCard/3";

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(PlantKey, "[1,2,3]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(PlantKey, "[1,2,4]", 2, 3), 3, MsgCategory.Gameplay));
            Assert.Equal(2, ran.Count);
        }

        [Fact]
        public void APeersDamageStepTravelsWholeOnItsOwnLane()
        {
            // Zombie.TakeDamage/5 is the room's hammer door (E60): any seat may start it, and what travels
            // is every number the game computed plus the class that keeps the maker, never a pointer.
            string args = CallArgs.Encode("ioeib", 1800, CallArgs.Singleton("Hammer"), 2, 270, false);

            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.HostPeerId = 0;
            inbox.LocalPeerId = 0;
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            Assert.Equal(CallOutcome.Applied, inbox.Accept(
                Call("Zombie.TakeDamage/5", args, 9, 3), 3, MsgCategory.Gameplay));
            Assert.Equal(args, Assert.Single(ran));

            // A frame for the same door that does not fit its measured signature never reaches the applier.
            TestLog.Clear();
            Assert.Equal(CallOutcome.Refused, inbox.Accept(
                Call("Zombie.TakeDamage/5", "[1800]", 10, 3), 3, MsgCategory.Gameplay));

            Assert.Single(ran);
            Assert.Contains("do not fit", inbox.LastRefusal);
        }

        [Fact]
        public void TheHostSeatQuestionIsAskedOfTheRoomNotOfTheFrame()
        {
            // The same frame is applied while this end counts seat 0 as the host and refused the moment
            // it does not: what decides is this room's roster, not a byte the sender chose.
            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.LocalPeerId = 3;
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            inbox.HostPeerId = 2;
            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[1]", 0, 2), 2, MsgCategory.Cosmetic));

            inbox.HostPeerId = 0;
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[2]", 0, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal("[1]", Assert.Single(ran));
        }

        // ---- what a receiver refuses, and says why ---------------------------

        [Theory]
        [InlineData("Hammer.EffectZombie/1")]
        [InlineData("Board.Update/0")]
        [InlineData("Zombie.GetAxeKilled/0")]
        public void AMethodTheTableDoesNotNameIsRefusedOnReceipt(string key)
        {
            // The table is an allow-list: what it does not say is refused, whatever the sender claims.
            List<string> ran = new List<string>();
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.Applier = delegate (MethodCall call)
            {
                ran.Add(call.Args);
                return CallOutcome.Applied;
            };

            TestLog.Clear();
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(key, "[]", 1, 2), 2, MsgCategory.Gameplay));

            Assert.Empty(ran);
            Assert.Contains("not in the coverage table", inbox.LastRefusal);
            Assert.True(TestLog.Has("refused " + key));
        }

        [Theory]
        [InlineData("CoinSun.Update/0")]
        [InlineData("Board.SetEvePlants/3")]
        public void ABodyThatCannotBeReplayedIsRefusedEvenThoughARowNamesIt(string key)
        {
            // The two kinds of row the measurement says no to: a per-frame callback and a body that
            // rolls. Neither can be a wire call, and neither may be applied from one.
            CallInbox inbox = new CallInbox(Arbiter.Default);

            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(key, "[1,2,3]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(1, inbox.RefusedCount);
        }

        [Theory]
        [InlineData(Verdict.Local)]
        [InlineData(Verdict.Never)]
        public void ARowThatDoesNotTravelIsRefusedOnEitherLane(Verdict verdict)
        {
            List<string> ran;
            CallInbox inbox = Door(verdict, "i", out ran);

            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Cosmetic));
            Assert.Empty(ran);
        }

        [Fact]
        public void ARowIsAppliedOnlyFromTheLaneItsOwnRowNames()
        {
            // The lane is part of the promise a frame makes: the ordered lane promises a number, a retry
            // queue and a gap it can point at, the latest-wins lane promises none of that. A row read on
            // the wrong lane is a sender that does not have the row it claims, and running it anyway is
            // how one lawn ends up behind.
            List<string> ordered;
            CallInbox replicate = Door(Verdict.Replicate, "i", out ordered);

            Assert.Equal(CallOutcome.Refused,
                         replicate.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Cosmetic));
            Assert.Contains("Replicate door", replicate.LastRefusal);
            Assert.Equal(CallOutcome.Applied,
                         replicate.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal("[50]", Assert.Single(ordered));

            List<string> latest;
            CallInbox mirror = Door(Verdict.Mirror, "i", out latest);

            Assert.Equal(CallOutcome.Refused,
                         mirror.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Contains("Mirror door", mirror.LastRefusal);
            Assert.Equal(CallOutcome.Applied,
                         mirror.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal("[50]", Assert.Single(latest));
        }

        // ---- the promise each lane makes ------------------------------------

        [Fact]
        public void ALatestWinsDoorIsAppliedEveryTimeItArrives()
        {
            // A number is what recognises an ordered frame it has already run. A value is worth exactly
            // what it says, so the same value twice is two lawns told, not one call heard twice - and a
            // receiver that deduplicated it would keep the lawn that asked for it out of date.
            List<string> ran;
            CallInbox inbox = Door(Verdict.Mirror, "i", out ran);

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic));

            Assert.Equal(2, ran.Count);
            Assert.Equal(0, inbox.DuplicateCount);
            Assert.Equal(0, inbox.GapCount);
        }

        [Fact]
        public void ALatestWinsDoorHoldsNothingForALaterTick()
        {
            // An ordered call waits for the plant it names; an old bank number describes a lawn that has
            // already moved on, so waiting for it would put yesterday's sun on today's lawn.
            int asked = 0;
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Mirror, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                asked++;
                return CallOutcome.Deferred;
            };

            TestLog.Clear();
            Assert.Equal(CallOutcome.Deferred,
                         inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic));
            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(0, inbox.LastValueCount);

            inbox.Tick();
            Assert.Equal(1, asked);
            Assert.True(TestLog.Has("dropped the " + SunKey + " value"), "the drop was silent");
        }

        // ---- what a seat that arrives late is told --------------------------

        [Fact]
        public void TheLastValueOfALatestWinsDoorIsKept()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Mirror, "i", out ran);

            inbox.Accept(Call(SunKey, "[50]", 0, 2), 2, MsgCategory.Cosmetic);
            inbox.Accept(Call(SunKey, "[75]", 0, 2), 2, MsgCategory.Cosmetic);

            Assert.Equal(1, inbox.LastValueCount);
            Assert.Equal("[75]", Assert.Single(inbox.LastValues()).Args);
        }

        [Fact]
        public void AnOrderedDoorIsNeverOfferedAsALastValue()
        {
            // Replaying an ordered call would replay a change: a second plant, a second coin. Only a door
            // whose arguments are the state is kept, which is the whole difference between the rows.
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(0, inbox.LastValueCount);
            Assert.Empty(inbox.LastValues());
        }

        [Fact]
        public void AValueTheHostAnnouncedIsKeptEvenThoughTheHostNeverReceivesIt()
        {
            // The host's own lawn already has the number, so no frame comes back to it and nothing applies
            // it: the room says the value out loud instead, which is the only way a seat that joins in the
            // middle of a level learns what the bank holds.
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.Remember(new MethodCall(SunKey, CallArgs.None, "[300]", 0, 0));

            Assert.Equal(1, inbox.LastValueCount);
            Assert.Equal(MsgCategory.Cosmetic, inbox.LaneOf(SunKey));
            Assert.Equal("[300]", Assert.Single(inbox.LastValues()).Args);

            inbox.Reset();
            Assert.Equal(0, inbox.LastValueCount);
        }

        [Fact]
        public void TheLaneOfEveryShippedRowIsTheLaneItsOwnVerdictNames()
        {
            foreach (CoverageRule rule in Arbiter.Default.RuleList)
            {
                CallInbox inbox = new CallInbox(Arbiter.Default);

                // Asked from the host seat, the row has to survive the measurement checks: a shipped row
                // the arbiter quietly downgrades would be sent on a lane its own verdict does not name.
                Decision d = Arbiter.Decide(Arbiter.Default, rule.Key, true, true);
                Assert.Equal(rule.Verdict, d.Verdict);
                Assert.Equal(Arbiter.LaneFor(rule.Verdict), inbox.LaneOf(rule.Key));
            }
        }

        [Theory]
        [InlineData("SetSun")]
        [InlineData(".SetSun/1")]
        [InlineData("Board.SetSun")]
        [InlineData("Board/SetSun/1")]
        [InlineData("Board.SetSun/")]
        [InlineData("Board.SetSun/x")]
        [InlineData("Board.SetSun/1x")]
        [InlineData("")]
        [InlineData("Board.SetSun-1")]
        public void AKeyThatIsNotACoverageKeyIsRefused(string key)
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            TestLog.Clear();
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(key, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Empty(ran);
            Assert.Contains("class.method/arity", inbox.LastRefusal);
        }

        [Theory]
        [InlineData("[\"fifty\"]")]
        [InlineData("[1.5]")]
        [InlineData("[]")]
        [InlineData("[1,2]")]
        [InlineData("[null]")]
        [InlineData("{}")]
        [InlineData("garbage")]
        public void ArgumentsThatDoNotFitTheMeasuredSignatureAreRefused(string args)
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, args, 1, 2), 2, MsgCategory.Gameplay));
            Assert.Empty(ran);
            Assert.Contains("arguments do not fit", inbox.LastRefusal);
        }

        [Fact]
        public void TheArgumentsAreCheckedAgainstTheBuildNotAgainstTheSender()
        {
            // A peer that claims its own arity is a peer that gets refused: the row's measured cats are
            // the only shape accepted.
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[50]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Refused, inbox.Accept(Call(SunKey, "[50,1]", 2, 2), 2, MsgCategory.Gameplay));
            Assert.Single(ran);
        }

        [Fact]
        public void ACallThisEndStartedIsNotAppliedAgain()
        {
            // The seat question is asked before the authority question here, so an AnyPeer row keeps the
            // test about the one thing it checks: a frame this end wrote must not land back on it.
            CallInbox inbox = new CallInbox(Arbiter.Default);
            inbox.LocalPeerId = 3;

            Assert.Equal(CallOutcome.Refused,
                         inbox.Accept(Call("Mouse.SetPlantFromCard/3", "[1,2,3]", 1, 3), 3, MsgCategory.Gameplay));
            Assert.Contains("this end started it", inbox.LastRefusal);
        }

        [Fact]
        public void AnEmptyFrameIsRefusedInsteadOfCrashingTheRoom()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Refused, inbox.Accept((MethodCall)null, 2, MsgCategory.Gameplay));
            Assert.Empty(ran);
        }

        // ---- order, duplicates and the gap that says a frame is missing ------

        [Fact]
        public void AFrameThatWasAlreadyAppliedIsDropped()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[10]", 5, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Duplicate, inbox.Accept(Call(SunKey, "[10]", 5, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Duplicate, inbox.Accept(Call(SunKey, "[9]", 3, 2), 2, MsgCategory.Gameplay));

            Assert.Single(ran);
            Assert.Equal(2, inbox.DuplicateCount);
        }

        [Fact]
        public void TwoEndsNumberTheirCallsApart()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[2]", 1, 4), 4, MsgCategory.Gameplay));

            Assert.Equal(2, ran.Count);
            Assert.Equal(0, inbox.DuplicateCount);
        }

        [Fact]
        public void ASkippedNumberIsReportedAndTheCallIsStillApplied()
        {
            // The only evidence this end has that the link dropped a frame. Freezing the room on it is
            // what the old build did wrong; the lawn takes what arrived and the log says what is missing.
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            TestLog.Clear();
            inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);
            CallOutcome outcome = inbox.Accept(Call(SunKey, "[2]", 5, 2), 2, MsgCategory.Gameplay);

            Assert.Equal(CallOutcome.Applied, outcome);
            Assert.Equal(1, inbox.GapCount);
            Assert.True(TestLog.Has("skipped 3 call(s)"));
        }

        [Fact]
        public void TheFirstNumberAnOriginSendsHasNothingToBeComparedAgainst()
        {
            // Sequence numbers start at one, so a missing entry in the ledger is not a previous zero:
            // reading the default as a baseline made every origin's first frames look like a loss.
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            TestLog.Clear();
            inbox.Accept(Call(SunKey, "[7]", 7, 2), 2, MsgCategory.Gameplay);

            Assert.Equal(0, inbox.GapCount);
            Assert.False(TestLog.Has("this lawn is behind"));
        }

        [Fact]
        public void ANumberAWaitingCallHoldsIsNotALostFrame()
        {
            // A hammer kill sends several calls at once; one whose zombie has not been read yet waits,
            // and the one after it applies first. That jump is the queue working, not a dropped frame.
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                return call.Seq == 2 ? CallOutcome.Deferred : CallOutcome.Applied;
            };

            TestLog.Clear();
            inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);
            inbox.Accept(Call(SunKey, "[2]", 2, 2), 2, MsgCategory.Gameplay);
            inbox.Accept(Call(SunKey, "[3]", 3, 2), 2, MsgCategory.Gameplay);

            Assert.Equal(1, inbox.PendingCount);
            Assert.Equal(0, inbox.GapCount);
            Assert.False(TestLog.Has("this lawn is behind"));
        }

        [Fact]
        public void TheOutgoingCounterNeverRepeatsANumber()
        {
            CallInbox inbox = new CallInbox(Arbiter.Default);

            Assert.Equal(1, inbox.NextSeq());
            Assert.Equal(2, inbox.NextSeq());
            Assert.Equal(3, inbox.NextSeq());
        }

        // ---- a call whose object has not arrived yet ------------------------

        [Fact]
        public void ADeferredCallIsRetriedAndAppliedOnce()
        {
            int asked = 0;
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                asked++;
                return asked < 3 ? CallOutcome.Deferred : CallOutcome.Applied;
            };

            Assert.Equal(CallOutcome.Deferred, inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay));
            Assert.Equal(1, inbox.PendingCount);

            inbox.Tick();
            Assert.Equal(1, inbox.PendingCount);
            Assert.Equal(0, inbox.AppliedCount);

            inbox.Tick();
            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(1, inbox.AppliedCount);
            Assert.Equal(3, asked);
        }

        [Fact]
        public void ACallThatNeverFindsItsTargetIsGivenUpOnWithALine()
        {
            // The plant the call names has not arrived on this end and never does: the room says so out
            // loud instead of holding the call forever or pretending it applied.
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "o"));
            inbox.Applier = delegate (MethodCall call)
            {
                return CallOutcome.Deferred;
            };

            TestLog.Clear();
            Assert.Equal(CallOutcome.Deferred, inbox.Accept(Call(SunKey, "[\"#9\"]", 1, 2), 2, MsgCategory.Gameplay));

            for (int i = 0; i < CallInbox.MaxPendingTicks; i++)
            {
                inbox.Tick();
            }

            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(1, inbox.FailedCount);
            Assert.Contains("never arrived", inbox.LastRefusal);
            Assert.True(TestLog.Has("never arrived"));
        }

        [Fact]
        public void FloodingTheRoomWithUnresolvableCallsCannotGrowTheQueue()
        {
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                return CallOutcome.Deferred;
            };

            for (int i = 1; i <= CallInbox.MaxPendingCalls + 40; i++)
            {
                inbox.Accept(Call(SunKey, "[1]", i, 2), 2, MsgCategory.Gameplay);
            }

            Assert.Equal(CallInbox.MaxPendingCalls, inbox.PendingCount);
            Assert.True(inbox.FailedCount > 0, "the overflow was kept instead of refused");
        }

        [Fact]
        public void ARefusalFromTheGameTakesTheCallOutOfTheQueue()
        {
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            int asked = 0;
            inbox.Applier = delegate (MethodCall call)
            {
                asked++;
                return asked == 1 ? CallOutcome.Deferred : CallOutcome.Refused;
            };

            inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);
            inbox.Tick();

            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(1, inbox.FailedCount);
            Assert.Equal(2, asked);
        }

        [Fact]
        public void TickWithoutAnythingWaitingCostsNothingAndLogsNothing()
        {
            CallInbox inbox = new CallInbox(Arbiter.Default);

            TestLog.Clear();
            inbox.Tick();
            inbox.Tick();

            Assert.False(TestLog.Has("[call]"), "an empty queue logged something");
        }

        [Fact]
        public void ResetMakesTheRoomReadyForANewStream()
        {
            CallInbox inbox = new CallInbox(OneDoor("Board", "SetSun", 1, Verdict.Replicate, "i"));
            inbox.Applier = delegate (MethodCall call)
            {
                return CallOutcome.Deferred;
            };

            inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay);
            inbox.Accept(Call(SunKey, "[2]", 2, 2), 2, MsgCategory.Gameplay);
            inbox.Reset();

            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(0, inbox.AppliedCount);
            Assert.Equal(0, inbox.GapCount);
            Assert.Null(inbox.LastRefusal);

            // The same numbers are a new room's numbers, not a replay of the last one.
            inbox.Applier = delegate (MethodCall call)
            {
                return CallOutcome.Applied;
            };
            Assert.Equal(CallOutcome.Applied, inbox.Accept(Call(SunKey, "[1]", 1, 2), 2, MsgCategory.Gameplay));
        }

        [Fact]
        public void TheCountersAddUpToTheFramesThatArrived()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);
            inbox.LocalPeerId = 9;

            int arrived = 0;
            Action<string, long, int> ask = delegate (string key, long seq, int origin)
            {
                arrived++;
                inbox.Accept(Call(key, "[1]", seq, origin), origin, MsgCategory.Gameplay);
            };

            ask(SunKey, 1, 2);
            ask(SunKey, 1, 2);
            ask("Board.Update/0", 2, 2);
            ask(SunKey, 3, 9);
            ask("Board.SetEvePlants/3", 4, 2);

            Assert.Equal(arrived, inbox.AppliedCount + inbox.DuplicateCount + inbox.RefusedCount
                                    + inbox.FailedCount);
        }

        // ---- the frame itself -----------------------------------------------

        [Fact]
        public void ACallTravelsAsOneFrameAndComesBackAsTheSameCall()
        {
            MethodCall sent = new MethodCall(SunKey, CallArgs.None, "[50]", 7, 2);

            byte[] bytes = Protocol.Encode(MsgCategory.Gameplay, MsgAction.MethodCall,
                                           sent.ToPayload());
            DecodedFrame frame;
            using (var stream = new System.IO.MemoryStream(bytes))
            {
                byte[] body = new byte[bytes.Length - 4];
                stream.Position = 4;
                stream.Read(body, 0, body.Length);
                frame = Protocol.Decode(body, 0, body.Length);
            }

            MethodCall received = MethodCall.From(frame);

            Assert.Equal(SunKey, received.Key);
            Assert.Equal(CallArgs.None, received.Target);
            Assert.Equal("[50]", received.Args);
            Assert.Equal(7, received.Seq);
            Assert.Equal(2, received.OriginPeer);
            Assert.Equal(MsgCategory.Gameplay + "/" + MsgAction.MethodCall, frame.Name);
        }

        [Fact]
        public void AnEntityArgumentTravelsAsTheRoomsIdNotAsAnAddress()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "o", out ran);

            Assert.Equal(CallOutcome.Applied,
                         inbox.Accept(Call(SunKey, "[\"#12\"]", 1, 2), 2, MsgCategory.Gameplay));

            Assert.Equal("[\"#12\"]", Assert.Single(ran));
        }

        [Fact]
        public void ACoveredCallCarryingNothingElseIsStillChecked()
        {
            MethodCallPayload payload = new MethodCallPayload
            {
                Key = null,
                Target = null,
                Args = null,
                Seq = 0,
                OriginPeer = 0
            };

            MethodCall call = new MethodCall(payload.Key, payload.Target, payload.Args, payload.Seq,
                                             payload.OriginPeer);

            Assert.Equal(CallArgs.None, call.Target);
            Assert.Equal("[]", call.Args);
            Assert.Equal(string.Empty, call.Key);

            List<string> ran;
            Assert.Equal(CallOutcome.Refused, Door(Verdict.Replicate, "i", out ran)
                                             .Accept(call, 2, MsgCategory.Gameplay));
            Assert.Empty(ran);
        }

        [Fact]
        public void ACallFrameTravelsOnBothDataLanesAndNeverOnAControlByte()
        {
            // One action for every synced feature, two lanes for it and a third category that must never
            // read it: a control frame is not the room's word about the lawn, and a call smuggled into one
            // would arrive without the lane the inbox asks about.
            byte[] bytes = Protocol.Encode(MsgCategory.Gameplay, MsgAction.MethodCall,
                                           new MethodCallPayload { Key = SunKey, Args = "[1]" });
            byte[] body = new byte[bytes.Length - 4];
            Buffer.BlockCopy(bytes, 4, body, 0, body.Length);

            Assert.Equal(MsgAction.MethodCall, Protocol.Decode(body, 0, body.Length).Action);

            body[0] = (byte)MsgCategory.Cosmetic;
            DecodedFrame latest = Protocol.Decode(body, 0, body.Length);
            Assert.Equal(MsgAction.MethodCall, latest.Action);
            Assert.Equal(MsgCategory.Cosmetic, latest.Category);
            Assert.Equal("Cosmetic/MethodCall", latest.Name);

            body[0] = (byte)MsgCategory.Control;
            Assert.Throws<ProtocolException>(() => Protocol.Decode(body, 0, body.Length));
        }

        [Fact]
        public void AFrameTooLargeForTheRoomIsRefusedBeforeTheCallIsRead()
        {
            string monster = "[\"" + new string('x', Protocol.MaxJsonChars + 1) + "\"]";

            Assert.Throws<ProtocolException>(() => Protocol.Encode(MsgCategory.Gameplay,
                MsgAction.MethodCall, new MethodCallPayload { Key = SunKey, Args = monster }));
        }

        [Fact]
        public void TheInboxReadsARealFrameTheSameWayAsAHandBuiltCall()
        {
            List<string> ran;
            CallInbox inbox = Door(Verdict.Replicate, "i", out ran);

            byte[] bytes = Protocol.Encode(MsgCategory.Gameplay, MsgAction.MethodCall,
                new MethodCall(SunKey, CallArgs.None, "[30]", 4, 2).ToPayload());
            byte[] body = new byte[bytes.Length - 4];
            Buffer.BlockCopy(bytes, 4, body, 0, body.Length);

            Assert.Equal(CallOutcome.Applied,
                         inbox.Accept(Protocol.Decode(body, 0, body.Length), 2));
            Assert.Equal("[30]", Assert.Single(ran));
        }

        [Fact]
        public void EveryRowThatTravelsInTheShippedTableCanBeAppliedFromAFrame()
        {
            // The acceptance test for the whole design: every door the room covers needs nothing but the
            // row, the measured signature and this one frame - no payload class and no gate of its own.
            // The row also picks the lane, which is the one thing a new synced feature has to get right.
            foreach (CoverageRule rule in Arbiter.Default.RuleList)
            {
                if (!Arbiter.Travels(rule.Verdict))
                {
                    continue;
                }

                CoverageFact fact = Arbiter.Default.FindFact(rule.Key);
                CallInbox inbox = new CallInbox(Arbiter.Default);

                // The frame arrives from the seat that owns the room, which is the only sender a
                // HostOnly row is believed from and the only AnyPeer row needs no favour from.
                inbox.HostPeerId = 2;
                inbox.LocalPeerId = 3;

                string[] ran = new string[1];
                inbox.Applier = delegate (MethodCall call)
                {
                    ran[0] = call.Key;
                    return CallOutcome.Applied;
                };

                Assert.Equal(CallOutcome.Applied,
                             inbox.Accept(new MethodCall(rule.Key, CallArgs.None,
                                             Expected.Shape(fact.Cats), 1, 2), 2,
                                             Arbiter.LaneFor(rule.Verdict)));
                Assert.Equal(rule.Key, ran[0]);

                // The same frame on the other lane is the sender's mistake, and a mistake about which
                // promises were made is refused rather than run with the wrong ones.
                MsgCategory other = Arbiter.LaneFor(rule.Verdict) == MsgCategory.Gameplay
                    ? MsgCategory.Cosmetic : MsgCategory.Gameplay;
                Assert.Equal(CallOutcome.Refused,
                             inbox.Accept(new MethodCall(rule.Key, CallArgs.None,
                                             Expected.Shape(fact.Cats), 2, 2), 2, other));
                Assert.Equal(rule.Key, ran[0]);
            }
        }
    }

    /// <summary>An argument vector of the shape a measured row names, for tests that only need a call.</summary>
    internal static class Expected
    {
        internal static string Shape(string cats)
        {
            List<object> values = new List<object>();
            foreach (char cat in cats ?? string.Empty)
            {
                switch (cat)
                {
                    case 'i':
                    case 'e':
                        values.Add(1);
                        break;
                    case 'b':
                        values.Add(true);
                        break;
                    case 'f':
                        values.Add(1.5f);
                        break;
                    case 's':
                        values.Add("one");
                        break;
                    case 'o':
                        values.Add(CallArgs.Entity(1));
                        break;
                    case 't':
                        values.Add(new object[] { 0f, 1f, 0f });
                        break;
                    case 'a':
                        values.Add(new object[] { CallArgs.Entity(1) });
                        break;
                    default:
                        throw new FormatException("the test helper does not know " + cat);
                }
            }

            return CallArgs.Encode(cats, values.ToArray());
        }
    }
}
