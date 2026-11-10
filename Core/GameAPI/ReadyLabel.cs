using Il2Cpp;
using Il2CppTMPro;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The bottom line of the seed chooser: whether this seat stands ready to start the room's lawn.
    ///
    /// It is the game's own TextMeshProUGUI on the game's own upper canvas, like the lobby list, so it
    /// inherits the canvas scaling and the draw order instead of the mod drawing a window of its own. The
    /// word is the roster's, not a flag this end keeps: a peer's Let's Rock marks the seat and can be
    /// pressed again to take the word back, and the host's line is green only when no seat is still
    /// waiting - which is also the only moment its press starts the lawn.
    /// </summary>
    public static class ReadyLabel
    {
        public const string Capability = "mp-ready";

        private const string HolderName = "PvzMP ready";

        private const string WaitingText = "not ready";
        private const string ReadyText = "ready";

        private const float FontSize = 34f;
        private const float BoxWidth = 520f;
        private const float BoxHeight = 54f;

        /// <summary>Clear of the chooser's own bottom edge, so the line sits above the seed bank.</summary>
        private const float BottomMargin = 120f;

        private static readonly Color WaitingColor = new Color(0.95f, 0.2f, 0.2f, 1f);
        private static readonly Color ReadyColor = new Color(0.2f, 0.9f, 0.35f, 1f);

        private static GameObject _holder;
        private static TextMeshProUGUI _label;

        /// <summary>The word standing on the line, so a sweep does not write the same text every frame.</summary>
        private static string _shown;

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

            if (!ApiBase.Present(_label) && !Build())
            {
                return;
            }

            Write(room);
        }

        /// <summary>The seed chooser is up: the room's init board exists and has not been told to start.</summary>
        private static bool ChooserIsOpen()
        {
            return ApiBase.Query("InitBoard is choosing", () =>
            {
                InitBoard board = InitBoard.Instance;
                return ApiBase.Present(board) && !board.ready;
            }, Capability);
        }

        private static void Write(MpSession room)
        {
            bool ready = ThisSeatIsReady(room);
            string text = ready ? ReadyText : WaitingText;

            if (_shown == text)
            {
                return;
            }

            _shown = text;
            Color color = ready ? ReadyColor : WaitingColor;

            ApiBase.TryCall("ready label text", () =>
            {
                _label.text = text;
                _label.color = color;
            }, Capability);

            Log.Info("ui", "the ready label says '" + text + "' " + ApiBase.FrameTag());
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
                GiveUp("the game has no upper canvas to put the line on");
                return false;
            }

            RectTransform parent = ApiBase.Query("the upper canvas's own rectangle",
                () => canvas.GetComponent<RectTransform>(), Capability);
            if (!ApiBase.Present(parent))
            {
                GiveUp("the game's upper canvas has no rectangle to hang the line on");
                return false;
            }

            GameObject holder = ApiBase.Call("new GameObject(" + HolderName + ")",
                () => new GameObject(HolderName), Capability);
            if (!ApiBase.Present(holder))
            {
                GiveUp("the game refused a holder for the line");
                return false;
            }

            TextMeshProUGUI label = ApiBase.Call("holder.AddComponent<TextMeshProUGUI>",
                () => holder.AddComponent<TextMeshProUGUI>(), Capability);
            if (!ApiBase.Present(label))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused a text label on the holder");
                return false;
            }

            TMP_FontAsset font = PickFont();
            if (!ApiBase.Present(font))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game has no default font");
                return false;
            }

            bool placed = ApiBase.TryCall("the ready line takes its place over the chooser's bottom", () =>
            {
                holder.transform.SetParent(parent, false);
                holder.layer = parent.gameObject.layer;

                RectTransform box = label.rectTransform;
                box.anchorMin = new Vector2(0.5f, 0f);
                box.anchorMax = new Vector2(0.5f, 0f);
                box.pivot = new Vector2(0.5f, 0f);
                box.sizeDelta = new Vector2(BoxWidth, BoxHeight);
                box.anchoredPosition = new Vector2(0f, BottomMargin);

                label.font = font;
                label.fontSize = FontSize;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = false;

                // The line only tells the player where the seat stands; a line that could be hit would
                // steal the click from the start button above it.
                label.raycastTarget = false;
            }, Capability);

            if (!placed)
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
                GiveUp("the game refused the line's place in the chooser");
                return false;
            }

            _holder = holder;
            _label = label;
            _shown = null;

            Log.Info("ui", "the ready line stands over the chooser's bottom in the font '"
                + font.name + "' " + ApiBase.FrameTag());

            return true;
        }

        /// <summary>The game's own default font; the line is plain Latin, so nothing else is asked for.</summary>
        private static TMP_FontAsset PickFont()
        {
            return ApiBase.Query("TMP_Settings.defaultFontAsset",
                () => TMP_Settings.defaultFontAsset, Capability);
        }

        private static void Hide()
        {
            if (_shown == null && !ApiBase.Present(_holder))
            {
                return;
            }

            GameObject holder = _holder;

            if (ApiBase.Present(holder))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => Object.Destroy(holder), Capability);
            }

            _holder = null;
            _label = null;
            _shown = null;
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
            Log.Warn("ui", "no ready line in the chooser: " + why);
        }
    }
}
