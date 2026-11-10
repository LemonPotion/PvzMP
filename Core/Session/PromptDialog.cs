using System;
using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// Asks for everything a session needs through the game's own dialog: an address and a port to
    /// join, or only a nickname to host. The rules live here rather than in the screen, so they are
    /// testable without the game and the same answers reach the session unchanged.
    ///
    /// A host is never asked for a port: it publishes AddressRules.DefaultPort, and the number other
    /// players have to be told is decided once for the mod rather than per room.
    /// </summary>
    public sealed class PromptDialog
    {
        public const string FieldAddress = "address";
        public const string FieldPort = "port";
        public const string FieldNick = "nick";

        public const string AddressTitle = "Multiplayer address";
        public const string PortTitle = "Multiplayer port";
        public const string NickTitle = "Your nickname";

        private const string EmptyAddressProblem = "Address is empty";
        private const string BadPortProblem = "Port must be 1-65535";

        private readonly IPromptScreen _screen;
        private readonly Func<string> _profileNick;
        private readonly Action<ConnectionSettings> _onReady;
        private readonly List<string> _fields = new List<string>();

        private ConnectionSettings _settings;
        private int _index;

        public PromptDialog(IPromptScreen screen, Func<string> profileNick, Action<ConnectionSettings> onReady)
        {
            _screen = screen;
            _profileNick = profileNick;
            _onReady = onReady;
        }

        public bool IsActive { get; private set; }

        /// <summary>What the player is being asked for right now, or null while the dialog is closed.</summary>
        public string CurrentField
        {
            get { return IsActive && _index < _fields.Count ? _fields[_index] : null; }
        }

        public void StartHost()
        {
            Start(true);
        }

        public void StartJoin()
        {
            Start(false);
        }

        /// <summary>Run once per frame from the game's main thread.</summary>
        public void Tick()
        {
            if (!IsActive)
            {
                return;
            }

            string text;
            if (!_screen.TryTakeAnswer(out text))
            {
                if (!_screen.IsOpen)
                {
                    Cancel("the dialog was closed without an answer");
                }

                return;
            }

            string field = _fields[_index];
            string accepted;
            string problem;

            if (!TryApply(field, text, out accepted, out problem))
            {
                Log.Warn("mp", "dialog rejected " + field + " '" + text + "': " + problem);
                Ask(field, text, problem);
                return;
            }

            Log.Info("mp", "dialog " + field + " = '" + accepted + "'");
            _index++;

            if (_index >= _fields.Count)
            {
                Finish();
                return;
            }

            Ask(_fields[_index], PrefillFor(_fields[_index]), null);
        }

        public void Cancel()
        {
            Cancel("cancelled by the player");
        }

        private void Start(bool asHost)
        {
            if (IsActive)
            {
                Log.Warn("mp", "a dialog is already open, ignoring the request to open another");
                return;
            }

            _fields.Clear();

            if (!asHost)
            {
                _fields.Add(FieldAddress);
                _fields.Add(FieldPort);
            }

            _fields.Add(FieldNick);

            _settings = new ConnectionSettings
            {
                AsHost = asHost,
                Host = string.Empty,
                Port = AddressRules.DefaultPort,
                Nick = string.Empty
            };

            _index = 0;
            IsActive = true;

            Log.Info("mp", asHost ? "dialog started for a host" : "dialog started for a join");
            Ask(_fields[0], PrefillFor(_fields[0]), null);
        }

        private void Finish()
        {
            IsActive = false;
            _screen.Close();

            ConnectionSettings settings = _settings;
            _settings = null;
            _index = 0;

            Log.Info("mp", "dialog complete: " + settings);
            _onReady(settings);
        }

        private void Cancel(string reason)
        {
            IsActive = false;
            _settings = null;
            _index = 0;
            _screen.Close();
            Log.Info("mp", "dialog stopped: " + reason + ", no session");
        }

        private void Ask(string field, string prefill, string problem)
        {
            string title = problem ?? TitleFor(field);
            _screen.Show(title, prefill);
            Log.Info("mp", "dialog asks " + field + ": " + title);
        }

        private static string TitleFor(string field)
        {
            if (field == FieldAddress)
            {
                return AddressTitle;
            }

            return field == FieldPort ? PortTitle : NickTitle;
        }

        private string PrefillFor(string field)
        {
            if (field == FieldAddress)
            {
                return string.Empty;
            }

            if (field == FieldPort)
            {
                return AddressRules.DefaultPort.ToString();
            }

            return NickRules.Resolve(string.Empty, _profileNick == null ? null : _profileNick());
        }

        private bool TryApply(string field, string text, out string accepted, out string problem)
        {
            accepted = null;
            problem = null;

            if (field == FieldAddress)
            {
                accepted = AddressRules.CleanHost(text);

                if (!AddressRules.IsUsableHost(accepted))
                {
                    problem = EmptyAddressProblem;
                    return false;
                }

                _settings.Host = accepted;
                return true;
            }

            if (field == FieldPort)
            {
                int port;

                if (!AddressRules.TryPort(text, out port))
                {
                    problem = BadPortProblem;
                    return false;
                }

                accepted = port.ToString();
                _settings.Port = port;
                return true;
            }

            accepted = NickRules.Resolve(text, _profileNick == null ? null : _profileNick());
            _settings.Nick = accepted;
            return true;
        }
    }
}
