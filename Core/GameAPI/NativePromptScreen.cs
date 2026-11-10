using Il2Cpp;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The connection dialog drawn by the game itself: one ReNameMenu is pushed once and then re-asked
    /// in place, so the menu stack never grows past the screen the player was already on.
    ///
    /// The click handler deliberately does nothing but read the box and set a flag. Reading native
    /// Unity state from inside the game's own event is safe; popping the menu or starting a socket
    /// from there is not, because the click is still in flight. PromptDialog.Tick polls the flag from
    /// the main-thread pump and owns every decision, which keeps this class free of rules and testable
    /// through the same fake the dialog tests use.
    /// </summary>
    public sealed class NativePromptScreen : IPromptScreen
    {
        private BaseMenu _menu;
        private bool _answered;
        private string _answer = string.Empty;
        private int _textLimit = -1;

        /// <summary>False as soon as anything but this menu is on top, including the game closing it.</summary>
        public bool IsOpen
        {
            get { return _menu != null && Menu.IsTop(_menu); }
        }

        public void Show(string title, string prefill)
        {
            if (_menu != null && !Menu.IsTop(_menu))
            {
                Log.Info("mp", "the dialog menu is no longer on top, pushing a fresh one");
                _menu = null;
            }

            if (_menu == null)
            {
                _menu = Menu.PushPrompt();
                if (_menu == null)
                {
                    Log.Error("mp", "Push(" + Menu.PromptMenu + ") returned null, no dialog");
                    return;
                }

                Menu.WatchConfirm(_menu, OnConfirm);

                // The prefab is a name box and the live run proved it: a pasted ipv4 address came back
                // from input.text as '26.225.12'. The mod's own cap is what the field may hold while
                // the dialog owns it, and Close puts the game's number back.
                _textLimit = Menu.ReadPromptTextLimit(_menu);
                Menu.SetPromptTextLimit(_menu, AddressRules.MaxHostLength);
                Log.Info("mp", "the dialog box holds " + AddressRules.MaxHostLength
                    + " characters, the menu came with " + _textLimit);
            }

            Menu.SetPromptTitle(_menu, title);
            Menu.SetPromptText(_menu, prefill);
            _answered = false;
        }

        public void Close()
        {
            if (_menu == null)
            {
                return;
            }

            if (Menu.IsTop(_menu))
            {
                // Restoring the box's own character limit is part of giving the screen back: whoever
                // opens this menu after the mod must get the prefab the game shipped, not the mod's.
                if (_textLimit >= 0)
                {
                    Menu.SetPromptTextLimit(_menu, _textLimit);
                    _textLimit = -1;
                }

                Menu.Pop();
            }
            else
            {
                Log.Info("mp", "the dialog is not the top menu any more, not popping what is above "
                    + "the stack instead");
                _textLimit = -1;
            }

            _menu = null;
            _answered = false;
            _answer = string.Empty;
        }

        public bool TryTakeAnswer(out string text)
        {
            if (!_answered)
            {
                text = null;
                return false;
            }

            _answered = false;
            text = _answer;
            return true;
        }

        private void OnConfirm()
        {
            if (_menu == null)
            {
                return;
            }

            _answer = Menu.ReadPromptText(_menu);
            _answered = true;
            Log.Info("mp", "dialog confirm clicked, box = '" + _answer + "'");
        }
    }
}
