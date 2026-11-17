using Il2Cpp;
using Il2CppTMPro;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The ready button: whether this seat stands ready to start the room's lawn, and the press that says
    /// so. It hangs on the game's own upper canvas right under the lobby list (<see cref="LobbyHud"/>), so
    /// the list and the button move together and the button inherits the canvas scaling and the draw order
    /// instead of the mod drawing a window of its own.
    ///
    /// The word is the roster's, not a flag this end keeps: a peer's press is the same word the chooser's
    /// Let's Rock says, and a second press takes it back. The host's line answers the question its own
    /// press answers - it is green only when no seat is still waiting - and the host's press starts the
    /// lawn with the game's own Let's Rock button, so a press on this box only says where the room stands.
    ///
    /// The box is drawn, not a native widget: the game has no ready button to copy, and the press is read
    /// from the mouse the mod already reads. Neither the box nor its text takes clicks from the chooser
    /// underneath.
    /// </summary>
    public static class ReadyLabel
    {
        public const string Capability = "mp-ready";

        private const string HolderName = "PvzMP ready";

        private const string WaitingText = "not ready";
        private const string ReadyText = "ready";

        private const float FontSize = 22f;
        private const float BoxWidth = 320f;
        private const float BoxHeight = 34f;

        // The lobby list's own geometry: rows hang from the top-right corner on this margin and pitch,
        // and the button starts a gap below the last row. A change there has to be made here too.
        private const float RightMargin = 20f;
        private const float TopMargin = 8f;
        private const float RowHeight = 28f;
        private const float ListGap = 8f;

        private static readonly Color WaitingColor = new Color(0.52f, 0.12f, 0.12f, 0.92f);
        private static readonly Color WaitingHoverColor = new Color(0.72f, 0.18f, 0.18f, 0.96f);
        private static readonly Color ReadyColor = new Color(0.10f, 0.44f, 0.16f, 0.92f);
        private static readonly Color ReadyHoverColor = new Color(0.16f, 0.62f, 0.24f, 0.96f);
        private static readonly Color TextColor = new Color(1f, 1f, 1f, 1f);

        private static GameObject _holder;
        private static RectTransform _box;
        private static UnityEngine.UI.Image _image;
        private static TextMeshProUGUI _label;
        private static Camera _camera;

        /// <summary>The word standing on the button, so a sweep does not write the same text every frame.</summary>
        private static string _shown;

        /// <summary>The roster lines the button was placed under, so a seat coming or going moves it.</summary>
        private static int _lines;

        private static bool _hover;
        private static bool _warned;

        /// <summary>Run once per frame from the game's main thread.</summary>
        public static void Tick()
        {
            MpSession room = ConnectionEntry.Room;

            if (room == null || !room.IsActive || !room.RoomDecides || !ChooserIsOpen()
                || ApiBase.IsBroken(Capability))
            {
                Hide();
                return;
            }

            if ((!ApiBase.Present(_holder) || !ApiBase.Present(_box)) && !Build())
            {
                return;
            }

            Write(room);
            Pump(room);
        }

        /// <summary>
        /// The seed chooser is up and the lawn it asks about is still here: the board exists and has not
        /// been told to start. The board is the part that keeps the button from outliving the level - the
        /// game leaves the chooser object behind when it goes back to the menu, and a button that only
        /// asked about that object stayed painted over the menu (ref/EVIDENCE.md E75).
        /// </summary>
        private static bool ChooserIsOpen()
        {
            return ApiBase.Query("InitBoard is choosing", () =>
            {
                InitBoard board = InitBoard.Instance;

                return ApiBase.Present(board) && board.gameObject.activeInHierarchy && !board.ready
                    && Entities.Board() != null;
            }, Capability);
        }

        private static void Write(MpSession room)
        {
            bool ready = ThisSeatIsReady(room);
            int lines = Lines(room);

            if (lines != _lines)
            {
                Place(lines);
            }

            if (_shown == (ready ? ReadyText : WaitingText))
            {
                return;
            }

            _shown = ready ? ReadyText : WaitingText;

            ApiBase.TryCall("ready button text", () =>
            {
                _label.text = _shown;
            }, Capability);

            // The word and the colour change together; the hover is re-read on the next pump.
            _hover = false;
            ApiBase.TryCall("the ready button takes its colour", () =>
                _image.color = ready ? ReadyColor : WaitingColor, Capability);

            Log.Info("ui", "the ready button says '" + _shown + "' " + ApiBase.FrameTag());
        }

        /// <summary>How many lines the lobby list has: its header and one line per seat.</summary>
        private static int Lines(MpSession room)
        {
            return (room.Roster == null ? 0 : room.Roster.Entries.Count) + 1;
        }

        /// <summary>A peer's press toggles its word; the host's is the game's own Let's Rock.</summary>
        private static void Pump(MpSession room)
        {
            Vector2 point = ApiBase.Query("Input.mousePosition",
                () => (Vector2) Input.mousePosition, Capability);

            bool over = room.IsHost ? false : Over(point);
            Tint(ThisSeatIsReady(room), over);

            bool pressed = ApiBase.Query("Input.GetMouseButtonDown(0)",
                () => Input.GetMouseButtonDown(0), Capability);

            if (!pressed || !over)
            {
                return;
            }

            bool ready = !ThisSeatIsReady(room);
            room.TellHostIAmReady(ready);

            _hover = false;

            int waiting = room.Roster == null ? 0 : room.Roster.SeatsNotReady();

            Log.Info("mp", "this seat presses its ready button and is " + (ready ? "ready" : "not ready again")
                + "; " + waiting + " seat(s) still are not " + ApiBase.FrameTag());
        }

        private static bool Over(Vector2 point)
        {
            if (!ApiBase.Present(_box))
            {
                return false;
            }

            return ApiBase.Query("is the mouse on the ready button", () =>
                RectTransformUtility.RectangleContainsScreenPoint(_box, point, _camera),
                Capability);
        }

        private static void Tint(bool ready, bool hover)
        {
            if (!ApiBase.Present(_image) || _hover == hover)
            {
                return;
            }

            Color color = ready
                ? (hover ? ReadyHoverColor : ReadyColor)
                : (hover ? WaitingHoverColor : WaitingColor);

            bool tinted = ApiBase.TryCall("the ready button changes its colour",
                () => _image.color = color, Capability);

            if (tinted)
            {
                _hover = hover;
            }
        }

        /// <summary>
        /// A peer's word is its roster row. The host never marks itself ready - its press is the decision
        /// when nothing is waiting - so its line answers the same question the press does.
        /// </summary>
        private static bool ThisSeatIsReady(MpSession room)
        {
            if (room.Roster == null)
            {
                return false;
            }

            if (room.IsHost)
            {
                return room.Roster.SeatsNotReady() == 0;
            }

            RosterEntry entry = room.Roster.Find(room.LocalPeerId);

            return entry != null && entry.Ready;
        }

        private static bool Build()
        {
            Transform canvas = ApiBase.Query("GameAPP.canvasUp", () => GameAPP.canvasUp, Capability);
            if (!ApiBase.Present(canvas))
            {
                GiveUp("the game has no upper canvas to put the ready button on");
                return false;
            }

            // A canvas that draws through a camera needs that camera to turn a screen point into a point
            // inside a rectangle; an overlay canvas answers null here, which is the same answer.
            _camera = ApiBase.Query("the ready button's canvas camera", () =>
            {
                Canvas part = canvas.GetComponentInParent<Canvas>();
                return ApiBase.Present(part) ? part.worldCamera : null;
            }, Capability);

            RectTransform parent = ApiBase.Query("the upper canvas's own rectangle",
                () => canvas.GetComponent<RectTransform>(), Capability);
            if (!ApiBase.Present(parent))
            {
                GiveUp("the game's upper canvas has no rectangle to hang the button on");
                return false;
            }

            TMP_FontAsset font = PickFont();
            if (!ApiBase.Present(font))
            {
                GiveUp("the game has no default font");
                return false;
            }

            GameObject holder = ApiBase.Call("new GameObject(" + HolderName + ")",
                () => new GameObject(HolderName), Capability);
            if (!ApiBase.Present(holder))
            {
                GiveUp("the game refused a holder for the ready button");
                return false;
            }

            UnityEngine.UI.Image image = ApiBase.Call("ready AddComponent<Image>",
                () => holder.AddComponent<UnityEngine.UI.Image>(), Capability);
            if (!ApiBase.Present(image))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused a box for the ready button");
                return false;
            }

            GameObject textHolder = ApiBase.Call("new GameObject(" + HolderName + " text)",
                () => new GameObject(HolderName + " text"), Capability);
            if (!ApiBase.Present(textHolder))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused a text holder for the ready button");
                return false;
            }

            TextMeshProUGUI label = ApiBase.Call("ready text AddComponent<TextMeshProUGUI>",
                () => textHolder.AddComponent<TextMeshProUGUI>(), Capability);
            if (!ApiBase.Present(label))
            {
                ApiBase.TryCall("Destroy(" + HolderName + " text)", () => Object.Destroy(textHolder),
                    Capability);
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused a text label on the ready button");
                return false;
            }

            RectTransform box = ApiBase.Query("the ready box's rectangle",
                () => image.rectTransform, Capability);
            if (!ApiBase.Present(box))
            {
                ApiBase.TryCall("Destroy(" + HolderName + " text)", () => Object.Destroy(textHolder),
                    Capability);
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game gave the ready button no rectangle");
                return false;
            }

            int lines = Lines(ConnectionEntry.Room);

            bool placed = ApiBase.TryCall("the ready button takes its place under the list", () =>
            {
                image.transform.SetParent(parent, false);
                holder.layer = parent.gameObject.layer;

                box.anchorMin = new Vector2(1f, 1f);
                box.anchorMax = new Vector2(1f, 1f);
                box.pivot = new Vector2(0.5f, 0.5f);
                box.sizeDelta = new Vector2(BoxWidth, BoxHeight);
                box.anchoredPosition = Under(lines);

                image.color = WaitingColor;
                image.raycastTarget = false;

                label.transform.SetParent(box, false);
                textHolder.layer = parent.gameObject.layer;

                RectTransform textBox = label.rectTransform;
                textBox.anchorMin = Vector2.zero;
                textBox.anchorMax = Vector2.one;
                textBox.pivot = new Vector2(0.5f, 0.5f);
                textBox.sizeDelta = Vector2.zero;
                textBox.anchoredPosition = Vector2.zero;

                label.font = font;
                label.fontSize = FontSize;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = false;
                label.color = TextColor;

                // The button only says where the seat stands and takes its own press; a box that could be
                // hit would steal the click from the chooser underneath it.
                label.raycastTarget = false;

                holder.transform.SetAsLastSibling();
            }, Capability);

            if (!placed)
            {
                ApiBase.TryCall("Destroy(" + HolderName + " text)", () => Object.Destroy(textHolder),
                    Capability);
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused the button's place under the list");
                return false;
            }

            _holder = holder;
            _box = box;
            _image = image;
            _label = label;
            _shown = null;
            _lines = lines;
            _hover = false;

            Log.Info("ui", "the ready button stands under " + lines + " line(s) at "
                + Mathf.RoundToInt(box.anchoredPosition.x) + ","
                + Mathf.RoundToInt(box.anchoredPosition.y) + " in the font '" + font.name + "' "
                + Mathf.RoundToInt(BoxWidth) + "x" + Mathf.RoundToInt(BoxHeight) + " "
                + ApiBase.FrameTag());

            return true;
        }

        /// <summary>The button's place under a list of this many lines, from the upper right corner.</summary>
        private static void Place(int lines)
        {
            if (!ApiBase.Present(_box))
            {
                return;
            }

            bool placed = ApiBase.TryCall("the ready button moves under " + lines + " line(s)",
                () => _box.anchoredPosition = Under(lines), Capability);

            if (placed)
            {
                _lines = lines;
            }
        }

        private static Vector2 Under(int lines)
        {
            return new Vector2(
                -RightMargin - BoxWidth / 2f,
                -(TopMargin + lines * RowHeight + ListGap + BoxHeight / 2f));
        }

        /// <summary>The game's own default font; the box is plain Latin, so nothing else is asked for.</summary>
        private static TMP_FontAsset PickFont()
        {
            return ApiBase.Query("TMP_Settings.defaultFontAsset",
                () => TMP_Settings.defaultFontAsset, Capability);
        }

        /// <summary>
        /// Asked when the room or the chooser stops: the button belongs to the lawn the chooser is asking
        /// about, and the next level builds its own.
        /// </summary>
        public static void Hide()
        {
            if (_holder == null && _shown == null)
            {
                return;
            }

            GameObject holder = _holder;

            if (ApiBase.Present(holder))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
            }

            _holder = null;
            _box = null;
            _image = null;
            _label = null;
            _camera = null;
            _shown = null;
            _lines = 0;
            _hover = false;
        }

        private static void GiveUp(string why)
        {
            Hide();
            ApiBase.Disable(Capability, why);
            Warn(why);
        }

        private static void Warn(string why)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            Log.Warn("ui", "no ready button: " + why);
        }
    }
}
