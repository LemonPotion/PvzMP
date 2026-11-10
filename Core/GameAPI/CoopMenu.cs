using Il2Cpp;
using Il2CppTMPro;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The Coop entry on the game's main menu: one button that offers HOST or CLIENT, and those two
    /// answers start exactly what F1 and F2 start. The game has no co-op screen to push, so the drawing
    /// is the mod's own: the game's text renderer on the game's own upper canvas, with the press read
    /// from the mouse the mod already reads - the native host/join dialog that follows is untouched and
    /// is the same one the hotkeys open.
    ///
    /// It stands only while the main menu itself is the top of the game's stack. A session, a battle,
    /// the native prompt the button opens and every submenu take it off the screen - which is also how
    /// it comes back when the player cancels the dialog.
    /// </summary>
    public static class CoopMenu
    {
        public const string Capability = "mp-coop";

        private const string CoopText = "COOP";
        private const string HostText = "HOST";
        private const string ClientText = "CLIENT";

        private const float FontSize = 32f;

        private const float CoopWidth = 240f;
        private const float CoopHeight = 64f;
        private const float ChoiceWidth = 150f;

        /// <summary>The free strip between the survival entry and the button under it, and between two answers.</summary>
        private const float Gap = 18f;
        private const float ChoiceGap = 10f;

        /// <summary>Where the button goes when the main menu's own survival entry cannot be read.</summary>
        private const float FallbackMargin = 60f;
        private const float FallbackBottomMargin = 90f;

        private static readonly Color BoxColor = new Color(0.10f, 0.13f, 0.18f, 0.92f);
        private static readonly Color BoxHoverColor = new Color(0.20f, 0.30f, 0.45f, 0.96f);
        private static readonly Color TextColor = new Color(1f, 1f, 1f, 1f);

        private enum Step
        {
            Collapsed,
            Choosing,
        }

        /// <summary>One drawn button: the image that is its box and the game's own text on top of it.</summary>
        private sealed class Box
        {
            public GameObject Holder;
            public RectTransform Rect;
            public UnityEngine.UI.Image Image;
            public TextMeshProUGUI Label;
            public bool Hover;
        }

        private static Box _coop;
        private static Box _host;
        private static Box _client;

        private static Step _step;

        /// <summary>The main-menu instance the boxes were placed for; a new one is placed from the top.</summary>
        private static int _menuId;

        private static RectTransform _parent;
        private static Camera _camera;
        private static TMP_FontAsset _font;

        /// <summary>Where the button stands on the upper canvas, in that canvas's local coordinates.</summary>
        private static Vector2 _place;

        /// <summary>Run once per frame from the game's main thread.</summary>
        public static void Tick()
        {
            if (ApiBase.IsBroken(Capability))
            {
                Hide();
                return;
            }

            MpSession room = ConnectionEntry.Room;
            BaseMenu top = Menu.Top();

            if (room == null || room.IsActive || !MainMenuOnTop(top))
            {
                Hide();
                return;
            }

            bool haveBoxes = _step == Step.Collapsed
                ? Live(_coop)
                : Live(_host) && Live(_client);

            if (!ApiBase.Present(_parent) || _menuId != top.GetInstanceID() || !haveBoxes)
            {
                if (!Build(top))
                {
                    return;
                }
            }

            Pump();
        }

        /// <summary>
        /// The main menu is a pushed screen of the game's own loader (UIType.MainMenu and its travel
        /// twin), so the stack says whether it is the screen the player is looking at right now.
        /// </summary>
        private static bool MainMenuOnTop(BaseMenu top)
        {
            if (!ApiBase.Present(top))
            {
                return false;
            }

            return ApiBase.Query("the top of the menu stack is the main menu", () =>
                top.uIType == UIType.MainMenu || top.uIType == UIType.MainMenu_travel, Capability);
        }

        private static void Pump()
        {
            Vector2 point = ApiBase.Query("Input.mousePosition",
                () => (Vector2) Input.mousePosition, Capability);
            bool pressed = ApiBase.Query("Input.GetMouseButtonDown(0)",
                () => Input.GetMouseButtonDown(0), Capability);

            if (_step == Step.Collapsed)
            {
                bool over = Over(_coop, point);
                Tint(_coop, over);

                if (pressed && over)
                {
                    ShowChoice();
                }

                return;
            }

            bool overHost = Over(_host, point);
            bool overClient = Over(_client, point);
            Tint(_host, overHost);
            Tint(_client, overClient);

            if (!pressed)
            {
                return;
            }

            if (overHost)
            {
                Log.Info("mp", "the coop button asks for a host room, the same as F1 "
                    + ApiBase.FrameTag());
                BackToCollapsed();
                ConnectionEntry.HostFromMenu();
                return;
            }

            if (overClient)
            {
                Log.Info("mp", "the coop button asks to join a room, the same as F2 "
                    + ApiBase.FrameTag());
                BackToCollapsed();
                ConnectionEntry.JoinFromMenu();
                return;
            }

            // A press anywhere else is the player changing their mind: the one button comes back.
            BackToCollapsed();
        }

        private static bool Build(BaseMenu top)
        {
            DropAll();

            Transform canvas = ApiBase.Query("GameAPP.canvasUp", () => GameAPP.canvasUp, Capability);
            if (!ApiBase.Present(canvas))
            {
                GiveUp("the game has no upper canvas to put the coop button on");
                return false;
            }

            // A canvas that draws through a camera needs that camera to turn a screen point into a
            // point inside a rectangle; an overlay canvas answers null here, which is the same answer.
            _camera = ApiBase.Query("the coop button's canvas camera", () =>
            {
                Canvas part = canvas.GetComponentInParent<Canvas>();
                return ApiBase.Present(part) ? part.worldCamera : null;
            }, Capability);

            _parent = ApiBase.Query("the upper canvas's own rectangle",
                () => canvas.GetComponent<RectTransform>(), Capability);
            if (!ApiBase.Present(_parent))
            {
                GiveUp("the game's upper canvas has no rectangle to hang the button on");
                return false;
            }

            _font = ApiBase.Query("TMP_Settings.defaultFontAsset",
                () => TMP_Settings.defaultFontAsset, Capability);
            if (!ApiBase.Present(_font))
            {
                GiveUp("the game has no default font");
                return false;
            }

            bool anchored = FindPlace(top);

            _menuId = top.GetInstanceID();
            _coop = AddBox(CoopText, _place, CoopWidth, CoopHeight);
            if (_coop == null)
            {
                GiveUp("the game refused the coop button");
                return false;
            }

            _step = Step.Collapsed;

            Log.Info("ui", "the coop button stands " + (anchored
                    ? "under the main menu's survival entry"
                    : "at the lower left of the screen")
                + " at " + Mathf.RoundToInt(_place.x) + "," + Mathf.RoundToInt(_place.y)
                + " in the font '" + _font.name + "' " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>
        /// The survival entry is the last of the menu's mode buttons, so the free strip under it is the
        /// one place the mod knows is clear. The entry's place is turned into the upper canvas's own
        /// coordinates, so a camera canvas and an overlay canvas both land the same way.
        /// </summary>
        private static bool FindPlace(BaseMenu top)
        {
            _place = FallbackPlace();

            MainMenu menu = ApiBase.Query("the main menu's own screen",
                () => top.TryCast<MainMenu>(), Capability);
            if (!ApiBase.Present(menu))
            {
                return false;
            }

            UIButton_mainMenu survival = ApiBase.Query("MainMenu.survivalLevel",
                () => menu.survivalLevel, Capability);
            if (!ApiBase.Present(survival))
            {
                return false;
            }

            RectTransform source = ApiBase.Query("the survival entry's rectangle", () =>
            {
                RectTransform box = survival.rectTransform;
                return ApiBase.Present(box) ? box : survival.GetComponent<RectTransform>();
            }, Capability);
            if (!ApiBase.Present(source))
            {
                return false;
            }

            Vector2 screen = ApiBase.Query("the survival entry's place on the screen", () =>
            {
                Canvas part = source.GetComponentInParent<Canvas>();
                Camera cam = ApiBase.Present(part) ? part.worldCamera : null;
                Vector3 world = source.position;
                return (Vector2) RectTransformUtility.WorldToScreenPoint(cam, world);
            }, Capability);

            Vector2 local = Vector2.zero;
            bool mapped = ApiBase.Query("the survival entry's place on the upper canvas", () =>
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_parent, screen, _camera,
                    out local), Capability);
            if (!mapped)
            {
                return false;
            }

            Rect rect = ApiBase.Query("the survival entry's size", () => source.rect, Capability);
            Vector2 pivot = ApiBase.Query("the survival entry's pivot", () => source.pivot, Capability);

            // The mapped point is the entry's pivot; the strip below is measured from its bottom edge.
            Vector2 center = local + new Vector2((0.5f - pivot.x) * rect.width,
                (0.5f - pivot.y) * rect.height);
            _place = new Vector2(center.x, center.y - rect.height / 2f - Gap - CoopHeight / 2f);

            return true;
        }

        private static Vector2 FallbackPlace()
        {
            Rect rect = ApiBase.Query("the upper canvas's rectangle", () => _parent.rect, Capability);

            return new Vector2(rect.xMin + FallbackMargin + CoopWidth / 2f,
                rect.yMin + FallbackBottomMargin + CoopHeight / 2f);
        }

        private static void ShowChoice()
        {
            DropBox(ref _coop);

            float offset = (ChoiceWidth + ChoiceGap) / 2f;
            _host = AddBox(HostText, _place + new Vector2(-offset, 0f), ChoiceWidth, CoopHeight);
            _client = AddBox(ClientText, _place + new Vector2(offset, 0f), ChoiceWidth, CoopHeight);

            if (_host == null || _client == null)
            {
                GiveUp("the game refused the host/client choice");
                return;
            }

            _step = Step.Choosing;

            Log.Info("ui", "the coop button offers " + HostText + " or " + ClientText + " "
                + ApiBase.FrameTag());
        }

        private static void BackToCollapsed()
        {
            DropBox(ref _host);
            DropBox(ref _client);

            _step = Step.Collapsed;
            _coop = AddBox(CoopText, _place, CoopWidth, CoopHeight);

            if (_coop == null)
            {
                GiveUp("the game refused the coop button after a choice");
            }
        }

        private static Box AddBox(string text, Vector2 center, float width, float height)
        {
            GameObject holder = ApiBase.Call("new GameObject(PvzMP " + text + ")",
                () => new GameObject("PvzMP " + text), Capability);
            if (!ApiBase.Present(holder))
            {
                return null;
            }

            UnityEngine.UI.Image image = ApiBase.Call(text + " box AddComponent<Image>",
                () => holder.AddComponent<UnityEngine.UI.Image>(), Capability);
            if (!ApiBase.Present(image))
            {
                DropObject(holder);
                return null;
            }

            GameObject textHolder = ApiBase.Call("new GameObject(PvzMP " + text + " text)",
                () => new GameObject("PvzMP " + text + " text"), Capability);
            if (!ApiBase.Present(textHolder))
            {
                DropObject(holder);
                return null;
            }

            TextMeshProUGUI label = ApiBase.Call(text + " text AddComponent<TextMeshProUGUI>",
                () => textHolder.AddComponent<TextMeshProUGUI>(), Capability);
            if (!ApiBase.Present(label))
            {
                DropObject(textHolder);
                DropObject(holder);
                return null;
            }

            Rect canvasRect = ApiBase.Query("the upper canvas's rectangle", () => _parent.rect,
                Capability);
            RectTransform boxRect = ApiBase.Query(text + " box's rectangle",
                () => image.rectTransform, Capability);

            bool placed = ApiBase.Present(boxRect) && ApiBase.TryCall(
                "the " + text + " button takes its place on the upper canvas", () =>
                {
                    image.transform.SetParent(_parent, false);

                    // UI is drawn by the camera of the canvas an object ends up on, which is decided by
                    // its layer, so the canvas's own layer is the only right answer here.
                    holder.layer = _parent.gameObject.layer;

                    boxRect.anchorMin = Vector2.zero;
                    boxRect.anchorMax = Vector2.zero;
                    boxRect.pivot = new Vector2(0.5f, 0.5f);
                    boxRect.sizeDelta = new Vector2(width, height);
                    boxRect.anchoredPosition = new Vector2(center.x - canvasRect.xMin,
                        center.y - canvasRect.yMin);

                    image.color = BoxColor;

                    // The press is read by this class, not by the game's event system: a box that could
                    // be hit would steal the click from whatever the menu has under it.
                    image.raycastTarget = false;

                    label.transform.SetParent(boxRect, false);
                    textHolder.layer = _parent.gameObject.layer;

                    RectTransform textBox = label.rectTransform;
                    textBox.anchorMin = Vector2.zero;
                    textBox.anchorMax = Vector2.one;
                    textBox.pivot = new Vector2(0.5f, 0.5f);
                    textBox.sizeDelta = Vector2.zero;
                    textBox.anchoredPosition = Vector2.zero;

                    label.font = _font;
                    label.fontSize = FontSize;
                    label.alignment = TextAlignmentOptions.Center;
                    label.enableWordWrapping = false;
                    label.color = TextColor;
                    label.text = text;
                    label.raycastTarget = false;
                }, Capability);

            if (!placed)
            {
                DropObject(textHolder);
                DropObject(holder);
                return null;
            }

            Box made = new Box();
            made.Holder = holder;
            made.Rect = boxRect;
            made.Image = image;
            made.Label = label;

            return made;
        }

        private static bool Over(Box box, Vector2 point)
        {
            if (!Live(box) || !ApiBase.Present(box.Rect))
            {
                return false;
            }

            return ApiBase.Query("is the mouse on a coop button", () =>
                RectTransformUtility.RectangleContainsScreenPoint(box.Rect, point, _camera),
                Capability);
        }

        private static void Tint(Box box, bool hover)
        {
            if (!Live(box) || box.Hover == hover)
            {
                return;
            }

            bool tinted = ApiBase.TryCall("a coop button changes its colour",
                () => box.Image.color = hover ? BoxHoverColor : BoxColor, Capability);

            if (tinted)
            {
                box.Hover = hover;
            }
        }

        private static bool Live(Box box)
        {
            return box != null && ApiBase.Present(box.Holder);
        }

        private static void Hide()
        {
            if (_coop == null && _host == null && _client == null)
            {
                return;
            }

            Log.Info("ui", "the coop button is taken down: the main menu is not the screen on top");

            DropAll();
            _step = Step.Collapsed;
            _menuId = 0;
        }

        private static void DropAll()
        {
            DropBox(ref _coop);
            DropBox(ref _host);
            DropBox(ref _client);

            _parent = null;
            _camera = null;
            _font = null;
        }

        private static void DropBox(ref Box box)
        {
            if (box == null)
            {
                return;
            }

            if (ApiBase.Present(box.Label))
            {
                DropObject(box.Label.gameObject);
            }

            DropObject(box.Holder);
            box = null;
        }

        /// <summary>
        /// No capability on this call: the box must come down even when the reads that placed it were
        /// the ones that broke, or a dead feature would stay painted over the menu.
        /// </summary>
        private static void DropObject(GameObject holder)
        {
            if (ApiBase.Present(holder))
            {
                ApiBase.TryCall("Destroy(PvzMP coop button)", () => Object.Destroy(holder));
            }
        }

        private static void GiveUp(string why)
        {
            DropAll();
            ApiBase.Disable(Capability, why);
            Log.Warn("ui", "no coop button: " + why);
        }
    }
}
