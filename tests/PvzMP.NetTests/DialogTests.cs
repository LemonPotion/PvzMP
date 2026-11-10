using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// Drives PromptDialog over a fake screen: what matters is the order of the questions, what gets
    /// re-asked after a bad answer, and that the settings handed to the session are the cleaned ones.
    /// The native screen itself is verified by a live run, not here.
    /// </summary>
    public class DialogTests
    {
        [Fact]
        public void JoiningAsksAddressThenPortThenNick()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            Assert.Equal(PromptDialog.FieldAddress, dialog.CurrentField);

            screen.Answer("192.168.0.7");
            dialog.Tick();
            screen.Answer("7000");
            dialog.Tick();
            Assert.Equal(PromptDialog.FieldNick, dialog.CurrentField);
            screen.Answer("Lemon");
            dialog.Tick();

            Assert.NotNull(ready);
            Assert.False(ready.AsHost);
            Assert.Equal("192.168.0.7", ready.Host);
            Assert.Equal(7000, ready.Port);
            Assert.Equal("Lemon", ready.Nick);
            Assert.False(dialog.IsActive);
            Assert.Equal(1, screen.CloseCount);
        }

        [Fact]
        public void HostingAsksOnlyTheNickAndPublishesTheDefaultPort()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, true, settings => ready = settings);

            Assert.Equal(PromptDialog.FieldNick, dialog.CurrentField);
            screen.Answer("Lemon");
            dialog.Tick();

            Assert.True(ready.AsHost);
            Assert.Equal(string.Empty, ready.Host);
            Assert.Equal(AddressRules.DefaultPort, ready.Port);
        }

        [Fact]
        public void NickIsCleanedBeforeItReachesTheSession()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, true, settings => ready = settings);

            screen.Answer("Big Bob!");
            dialog.Tick();

            Assert.Equal("BigBob", ready.Nick);
        }

        [Fact]
        public void BadPortIsReAskedWithWhatThePlayerTyped()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            screen.Answer("host");
            dialog.Tick();
            screen.Answer("0");
            dialog.Tick();

            Assert.Equal("Port must be 1-65535", screen.Title);
            Assert.Equal("0", screen.Prefill);
            Assert.Equal(PromptDialog.FieldPort, dialog.CurrentField);
            Assert.Null(ready);

            screen.Answer("7777");
            dialog.Tick();
            screen.Answer("Player");
            dialog.Tick();

            Assert.Equal(7777, ready.Port);
            Assert.Equal("host", ready.Host);
        }

        [Fact]
        public void AddressThatCleansToNothingIsReAsked()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            screen.Answer("!!!");
            dialog.Tick();

            Assert.Equal("Address is empty", screen.Title);
            Assert.Equal(PromptDialog.FieldAddress, dialog.CurrentField);
            Assert.Null(ready);

            screen.Answer("my .host-name");
            dialog.Tick();
            Assert.Equal(PromptDialog.FieldPort, dialog.CurrentField);

            screen.Answer("7777");
            dialog.Tick();
            screen.Answer("Player");
            dialog.Tick();
            Assert.Equal("my.host-name", ready.Host);
        }

        [Fact]
        public void EmptyNickFallsBackToTheProfileName()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, true, settings => ready = settings, profileNick: "Sunny");

            screen.Answer(string.Empty);
            dialog.Tick();

            Assert.Equal("Sunny", ready.Nick);
        }

        [Fact]
        public void EachQuestionIsPrefilledWithWhatThePlayerCanJustAccept()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            Assert.Equal(PromptDialog.AddressTitle, screen.Title);
            Assert.Equal(string.Empty, screen.Prefill);

            screen.Answer("host");
            dialog.Tick();
            Assert.Equal(PromptDialog.PortTitle, screen.Title);
            Assert.Equal("7777", screen.Prefill);

            screen.Answer("7777");
            dialog.Tick();
            Assert.Equal(PromptDialog.NickTitle, screen.Title);
            Assert.Equal(NickRules.Fallback, screen.Prefill);
        }

        [Fact]
        public void CancellingStopsTheMachineWithoutStartingASession()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            dialog.Cancel();

            Assert.False(dialog.IsActive);
            Assert.Null(ready);
            Assert.Equal(1, screen.CloseCount);

            dialog.Tick();
            Assert.Null(ready);
        }

        [Fact]
        public void AScreenClosedByTheGameCountsAsACancel()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            // The player pressed the game's own back button: the menu is gone and nothing was answered.
            screen.SimulateGameClosedTheMenu();
            dialog.Tick();

            Assert.False(dialog.IsActive);
            Assert.Null(ready);
            Assert.Equal(1, screen.ShowCount);
            Assert.False(screen.IsOpen);
        }

        [Fact]
        public void ASecondRequestWhileADialogIsOpenIsIgnored()
        {
            var screen = new FakePromptScreen();
            ConnectionSettings ready = null;
            var dialog = Start(screen, false, settings => ready = settings);

            dialog.StartHost();

            Assert.Equal(PromptDialog.FieldAddress, dialog.CurrentField);
            Assert.Equal(1, screen.ShowCount);
        }

        private static PromptDialog Start(FakePromptScreen screen, bool asHost,
                                         System.Action<ConnectionSettings> onReady,
                                         string profileNick = "Player")
        {
            var dialog = new PromptDialog(screen, () => profileNick, onReady);

            if (asHost)
            {
                dialog.StartHost();
            }
            else
            {
                dialog.StartJoin();
            }

            return dialog;
        }

        private sealed class FakePromptScreen : IPromptScreen
        {
            private readonly Queue<string> _answers = new Queue<string>();

            public string Title { get; private set; }
            public string Prefill { get; private set; }
            public int ShowCount { get; private set; }
            public int CloseCount { get; private set; }

            public bool IsOpen { get; private set; }

            public void Answer(string text)
            {
                _answers.Enqueue(text);
            }

            public void SimulateGameClosedTheMenu()
            {
                IsOpen = false;
            }

            public void Show(string title, string prefill)
            {
                Title = title;
                Prefill = prefill;
                ShowCount++;
                IsOpen = true;
            }

            public void Close()
            {
                CloseCount++;
                IsOpen = false;
            }

            public bool TryTakeAnswer(out string text)
            {
                if (_answers.Count == 0)
                {
                    text = null;
                    return false;
                }

                text = _answers.Dequeue();
                return true;
            }
        }
    }
}
