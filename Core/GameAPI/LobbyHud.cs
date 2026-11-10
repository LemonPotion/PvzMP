using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The roster as the player sees it: a header that counts the seats and one line per seat, put up as
    /// soon as a room exists and taken down when it stops. Every line is the game's own TextMeshProUGUI
    /// with the game's own default font on the game's own upper canvas, so it inherits the font, the canvas
    /// scaling and the draw order instead of the mod drawing a window of its own - and it sits above the
    /// menus, which is why it is readable on every screen the room is alive through.
    ///
    /// One text object per line rather than one paragraph, for two reasons. A seat's colour is then its own
    /// label's colour, and <see cref="SeatTint"/> is the single answer to what a seat looks like - the same
    /// colour as its ghosts on the lawn and the names written on its cards. And a line the mouse can be
    /// asked about is a rectangle, which is what brings that seat's chosen plants out under its name: no
    /// native widget measures a paragraph by lines, so a single label would have no hover to speak of.
    ///
    /// The pictures are the game's own seed cards drawn small (<see cref="PlantIcons"/>), five to a band and
    /// a band more for every five past that, because a seat's whole hand has to be readable without any of
    /// it leaving the screen. What a seat holds is the room's draft (<see cref="PlantSelect.PlantsHeldBy"/>)
    /// - the list is never sent anywhere, because every end already carries the same draft.
    ///
    /// No Unity type here can be put in NativeProbe's spec table, which only resolves Assembly-CSharp, so
    /// every step is guarded per call and the first failure switches the whole capability off. A missing
    /// list is a lost feature; an exception thrown from OnUpdate is a broken game.
    /// </summary>
    public static class LobbyHud
    {
        public const string Capability = "mp-hud";

        // The seed bank owns the top of the screen and the lawn the middle, so the free band is the right
        // edge. Every line hangs from the top-right corner and is measured from it.
        private const float RowWidth = 320f;
        private const float RowHeight = 28f;
        private const float RightMargin = 20f;
        private const float TopMargin = 8f;
        private const float FontSize = 20f;

        // A held plant is shown as the game's own seed card, drawn small under the name of the seat that
        // chose it. The height is what the list asks for; the width follows the shape of the card the game
        // itself drew, so a preview is recognisable as a card instead of a square of art.
        private const float CardHeight = 56f;

        // Five cards to a band, because five is what the player asked to see; <see cref="PreviewBands"/>
        // then caps a card's width at the share of a line a band of them takes, so none of them leaves the
        // screen.
        private const int IconsPerBand = 5;
        private const float IconGap = 6f;

        /// <summary>The empty strip between the name and the first band, and between two bands.</summary>
        private const float BandGap = 6f;

        /// <summary>How many pictures one seat gets; past four bands the list is bigger than the screen.</summary>
        private const int MaxIcons = IconsPerBand * 4;

        private static readonly Color HeaderColor = new Color(1f, 0.9f, 0.35f, 1f);

        /// <summary>
        /// The canvas the list hangs on, kept as its rect. Every line is a child of this rect directly:
        /// an anchor is measured against the parent's rectangle, and a plain transform has none, so a list
        /// put under a holder of its own collapsed into the middle of the screen instead of the right edge.
        /// </summary>
        private static RectTransform _parent;

        private static Camera _camera;

        private static readonly List<Row> Rows = new List<Row>();
        private static readonly List<GameObject> Icons = new List<GameObject>();
        private static readonly List<int> Shown = new List<int>();

        /// <summary>The seat whose pictures stand, or -1 while none do.</summary>
        private static int _shownPeer = -1;

        /// <summary>The seat whose line the mouse was last on, so a landing is logged once and not per frame.</summary>
        private static int _loggedPeer = -1;

        /// <summary>One line of the list: its text, the seat it speaks of, or -1 for the header.</summary>
        private sealed class Row
        {
            public GameObject Holder;
            public RectTransform Box;
            public TextMeshProUGUI Label;
            public int PeerId = -1;
            public string Text;
        }

        /// <summary>Run once per frame from the game's main thread.</summary>
        public static void Tick()
        {
            MpSession room = ConnectionEntry.Room;

            if (room == null || !room.IsActive || ApiBase.IsBroken(Capability))
            {
                Hide();
                return;
            }

            if (!ApiBase.Present(_parent) && !Build())
            {
                return;
            }

            Show(room);
            ShowHeldPlants();
        }

        private static void Show(MpSession room)
        {
            IReadOnlyList<RosterEntry> seats = room.Roster == null
                ? new RosterEntry[0]
                : room.Roster.Entries;

            if (Rows.Count != seats.Count + 1 && !Rebuild(seats.Count + 1))
            {
                return;
            }

            int taken = room.Roster == null ? 0 : room.Roster.ConnectedCount;

            Write(Rows[0], -1, LobbyBoard.Header(taken, MpSession.MaxPlayers), HeaderColor);

            for (int i = 0; i < seats.Count; i++)
            {
                Write(Rows[i + 1], seats[i].PeerId, LobbyBoard.Line(seats[i]),
                    SeatTint.Label(seats[i].PeerId));
            }
        }

        /// <summary>
        /// A seat that came or went: the lines are made again from the top. The room's list is short and
        /// this happens once per join, so keeping the old lines and shifting them about would cost more
        /// than it saves and could leave a name above the wrong colour.
        /// </summary>
        private static bool Rebuild(int lines)
        {
            ClearIcons();
            DropRows();

            for (int i = 0; i < lines; i++)
            {
                if (!AddRow(i))
                {
                    GiveUp("the game refused a line of the list");
                    return false;
                }
            }

            Log.Info("mp", "the lobby list is redrawn with " + lines + " line(s) " + ApiBase.FrameTag());
            return true;
        }

        private static void Write(Row row, int peerId, string text, Color color)
        {
            if (row.PeerId == peerId && row.Text == text)
            {
                return;
            }

            TextMeshProUGUI label = row.Label;

            ApiBase.Query("lobby line text", () =>
            {
                label.text = text;
                label.color = color;
                return true;
            }, Capability);

            row.PeerId = peerId;
            row.Text = text;
        }

        /// <summary>The pictures of the plants the seat under the mouse chose.</summary>
        private static void ShowHeldPlants()
        {
            Row over = RowUnderMouse();

            if (over == null)
            {
                _loggedPeer = -1;
                ClearIcons();
                return;
            }

            List<int> held = PlantSelect.PlantsHeldBy(over.PeerId);

            // A landing is worth one line: it is the only answer to whether the mouse is being read on the
            // right band, and whether the seat under it has anything in the draft at all.
            if (over.PeerId != _loggedPeer)
            {
                _loggedPeer = over.PeerId;

                Log.Info("ui", "the mouse is on peer " + over.PeerId + "'s line, and the draft gives that"
                    + " seat " + held.Count + " plant(s) " + ApiBase.FrameTag());
            }

            if (held.Count == 0)
            {
                // Nothing to say about this seat, and saying so every frame would be noise: the pictures
                // that were standing are taken down and the next look starts from nothing again.
                ClearIcons();
                return;
            }

            if (over.PeerId == _shownPeer && SameList(held))
            {
                return;
            }

            ClearIcons();

            // One read of the game's card shape decides the whole band: the width is what the height asks
            // for at that shape, capped by the band rule, and the height is whatever the same shape leaves.
            float aspect = PlantIcons.CardAspect();
            float width = PreviewBands.CardWidth(RowWidth, IconsPerBand, IconGap, CardHeight, aspect);
            float height = width / aspect;
            int drawn = 0;

            // The cards are children of the line they speak of, so the line and its cards go to the end of
            // the canvas's children and draw over the lines below: positions come from each line's own
            // index, so nothing on the list moves when one of them is lifted.
            over.Holder.transform.SetAsLastSibling();

            for (int i = 0; i < held.Count && i < MaxIcons; i++)
            {
                int band = i / IconsPerBand;
                int slot = i % IconsPerBand;

                // The band hangs under the line, so the first one starts a gap below its bottom edge and
                // every band after it a gap below the last. A seat with more plants than one band holds
                // gets bands, because the reason the list is drawn is to show all of them.
                GameObject icon = PlantIcons.Create(held[i], over.Box,
                    PreviewBands.CenterX(slot, width, IconGap),
                    -(RowHeight + PreviewBands.CenterY(band, height, BandGap)),
                    width, height);

                if (ApiBase.Present(icon))
                {
                    Icons.Add(icon);
                    Shown.Add(held[i]);
                    drawn++;
                }
            }

            _shownPeer = over.PeerId;

            Log.Info("ui", "peer " + over.PeerId + " holds " + held.Count + " plant(s); " + drawn
                + " card(s) of " + Mathf.RoundToInt(width) + "x" + Mathf.RoundToInt(height)
                + " px are drawn under its line in " + PreviewBands.BandCount(drawn, IconsPerBand)
                + " band(s) " + ApiBase.FrameTag());
        }

        private static bool SameList(List<int> held)
        {
            if (Shown.Count != held.Count)
            {
                return false;
            }

            for (int i = 0; i < Shown.Count; i++)
            {
                if (Shown[i] != held[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ClearIcons()
        {
            foreach (GameObject icon in Icons)
            {
                if (ApiBase.Present(icon))
                {
                    ApiBase.TryCall("Destroy(plant icon)", () => Object.Destroy(icon), Capability);
                }
            }

            Icons.Clear();
            Shown.Clear();
            _shownPeer = -1;
        }

        /// <summary>Which line the mouse sits on, or null when it sits on none of them.</summary>
        private static Row RowUnderMouse()
        {
            Vector2 point = ApiBase.Query("the mouse's place on the screen", () =>
            {
                Vector3 mouse = Input.mousePosition;
                return new Vector2(mouse.x, mouse.y);
            }, Capability);

            for (int i = 1; i < Rows.Count; i++)
            {
                Row row = Rows[i];

                if (row.PeerId < 0 || !ApiBase.Present(row.Box))
                {
                    continue;
                }

                bool over = ApiBase.Query("is the mouse over a line of the list",
                    () => RectTransformUtility.RectangleContainsScreenPoint(row.Box, point, _camera),
                    Capability);

                if (over)
                {
                    return row;
                }
            }

            return null;
        }

        private static bool Build()
        {
            Transform canvas = ApiBase.Query("GameAPP.canvasUp", () => GameAPP.canvasUp, Capability);
            if (!ApiBase.Present(canvas))
            {
                GiveUp("the game has no upper canvas to put the list on");
                return false;
            }

            // A canvas that draws through a camera needs that camera to turn a screen point into a point
            // inside a rectangle; an overlay canvas answers null here, which is the same answer.
            _camera = ApiBase.Query("the list's canvas camera", () =>
            {
                Canvas part = canvas.GetComponentInParent<Canvas>();
                return ApiBase.Present(part) ? part.worldCamera : null;
            }, Capability);

            _parent = ApiBase.Query("the upper canvas's own rectangle", () =>
                canvas.GetComponent<RectTransform>(), Capability);

            if (!ApiBase.Present(_parent))
            {
                GiveUp("the game's upper canvas has no rectangle to hang the list on");
                return false;
            }

            // Where the list really is, said once per room: the anchor arithmetic is the part a player
            // cannot check from a screenshot, and this is the line that tells a wrong edge from a wrong
            // hover.
            Log.Info("mp", "the lobby list hangs on '" + _parent.name + "' of " + Describe(_parent.rect)
                + " " + (_camera == null ? "with no camera" : "on camera '" + _camera.name + "'") + " "
                + ApiBase.FrameTag());

            return true;
        }

        private static string Describe(Rect rect)
        {
            return Mathf.RoundToInt(rect.width) + "x" + Mathf.RoundToInt(rect.height) + " px";
        }

        private static bool AddRow(int index)
        {
            TMP_FontAsset font = ApiBase.Query("TMP_Settings.defaultFontAsset",
                () => TMP_Settings.defaultFontAsset, Capability);
            if (!ApiBase.Present(font))
            {
                return false;
            }

            GameObject holder = ApiBase.Call("new GameObject(PvzMP lobby line)",
                () => new GameObject("PvzMP lobby line"), Capability);
            if (!ApiBase.Present(holder))
            {
                return false;
            }

            TextMeshProUGUI label = ApiBase.Call("line.AddComponent<TextMeshProUGUI>",
                () => holder.AddComponent<TextMeshProUGUI>(), Capability);
            if (!ApiBase.Present(label))
            {
                ApiBase.TryCall("Destroy(PvzMP lobby line)", () => Object.Destroy(holder), Capability);
                return false;
            }

            bool placed = ApiBase.TryCall("the line takes its place in the list", () =>
            {
                label.transform.SetParent(_parent, false);

                // UI is drawn by the camera of the canvas an object ends up on, which is decided by its
                // layer, so the canvas's own layer is the only right answer here.
                holder.layer = _parent.gameObject.layer;

                RectTransform box = label.rectTransform;
                box.anchorMin = new Vector2(1f, 1f);
                box.anchorMax = new Vector2(1f, 1f);
                box.pivot = new Vector2(1f, 0.5f);
                box.sizeDelta = new Vector2(RowWidth, RowHeight);
                box.anchoredPosition = new Vector2(-RightMargin,
                    -(TopMargin + RowHeight / 2f + index * RowHeight));

                label.font = font;
                label.fontSize = FontSize;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.enableWordWrapping = false;

                // The list only tells the player what the room looks like; a line that could be hit would
                // steal the click from whatever the game has under it.
                label.raycastTarget = false;
            }, Capability);

            if (!placed)
            {
                ApiBase.TryCall("Destroy(PvzMP lobby line)", () => Object.Destroy(holder), Capability);
                return false;
            }

            Row row = new Row();
            row.Holder = holder;
            row.Box = label.rectTransform;
            row.Label = label;

            Rows.Add(row);
            return true;
        }

        private static void DropRows()
        {
            foreach (Row row in Rows)
            {
                if (ApiBase.Present(row.Holder))
                {
                    ApiBase.TryCall("Destroy(PvzMP lobby line)", () => Object.Destroy(row.Holder),
                        Capability);
                }
            }

            Rows.Clear();
        }

        private static void Hide()
        {
            if (_parent == null && Rows.Count == 0)
            {
                return;
            }

            Log.Info("mp", "the lobby list is removed: no room is running");
            TakeDown();
        }

        private static void TakeDown()
        {
            ClearIcons();
            DropRows();
            _parent = null;
            _camera = null;
            _loggedPeer = -1;
        }

        private static void GiveUp(string why)
        {
            TakeDown();
            ApiBase.Disable(Capability, why);
            Log.Warn("mp", "no lobby list: " + why);
        }
    }
}
