using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// A plant's preview as the game's own seed card drawn small.
    ///
    /// Two images make the card the chooser shows and both are the game's art. `CardUI.SetImage` asks the
    /// card's own GameObject for its `Image` and writes the background sprite into it, which is the frame,
    /// and `CardUI.ChangeCardSprite` takes that card's first child, asks it for its `Image` and fills it
    /// with `UIExtend.SetIcon`, which is the plant (ref/EVIDENCE.md E46, E49). Copying both sprites and
    /// both materials is therefore the whole of what this class has to know about the game's art.
    ///
    /// The size is the mod's, not the game's: `SetIcon` works its aspect arithmetic out of two float
    /// arguments whose literals are named nowhere the tools resolve (E46), and a band of cards under a
    /// seat's name has to fit the line the mod laid out. The caller hands the box in: the frame fills it,
    /// because a frame is the box, and the plant is letterboxed inside it by Unity's own
    /// `Image.preserveAspect`.
    ///
    /// A plant no card carries has no picture here and is left out of the list: a missing icon says less
    /// than a drawn-on picture the game never made would.
    /// </summary>
    public static class PlantIcons
    {
        private const string HolderName = "PvzMP plant card";
        private const string PictureName = "PvzMP plant picture";

        /// <summary>
        /// Where the plant sits inside its frame, as shares of the card's own box. The game's card leaves
        /// the middle of the frame for the picture and its bottom band for the price, so the picture is
        /// inset instead of stretched across the border.
        /// </summary>
        private const float SideInset = 0.1f;
        private const float TopInset = 0.1f;
        private const float BottomInset = 0.25f;

        private static bool _warned;

        /// <summary>
        /// The width of the game's own card over its height, so a band of previews keeps the shape of the
        /// real card instead of squaring it up. A square when no card can be read.
        /// </summary>
        public static float CardAspect()
        {
            CardUI[] cards = Cards();

            if (cards == null)
            {
                return 1f;
            }

            return ApiBase.Query("the shape of the game's own card", () =>
            {
                for (int i = 0; i < cards.Length; i++)
                {
                    RectTransform face = cards[i].GetComponent<RectTransform>();

                    if (ApiBase.Present(face) && face.rect.width > 0f && face.rect.height > 0f)
                    {
                        return face.rect.width / face.rect.height;
                    }
                }

                return 1f;
            }, LobbyHud.Capability);
        }

        /// <summary>
        /// The preview card of one plant under a line of the roster, at an offset from that line's top-left
        /// corner and in a box of the width and height the list uses. Null when no card of the game's
        /// carries the plant or the game refused the objects.
        /// </summary>
        public static GameObject Create(int plantType, Transform parent, float x, float y,
            float width, float height)
        {
            if (!ApiBase.OnMainThread || !ApiBase.Present(parent))
            {
                return null;
            }

            Art art = Find(plantType);

            if (art == null)
            {
                Warn(plantType);
                return null;
            }

            GameObject holder = Part(HolderName, parent, x, y, width, height, art.Frame, false);

            if (!ApiBase.Present(holder))
            {
                return null;
            }

            float inner = width * (1f - 2f * SideInset);
            float innerHeight = height * (1f - TopInset - BottomInset);

            GameObject picture = Part(PictureName, holder.transform, width / 2f,
                -(height * TopInset + innerHeight / 2f), inner, innerHeight, art.Picture, true);

            if (!ApiBase.Present(picture))
            {
                Destroy(holder, HolderName);
                return null;
            }

            return holder;
        }

        /// <summary>
        /// The cards that could carry a plant's art, in the order the mod wants them: first the deck this
        /// lawn brought, then every card the chooser holds. A plant outside the deck is still drawn with
        /// its own picture, because the chooser's pages carry every plant the game loaded.
        /// </summary>
        private static CardUI[] Cards()
        {
            CardUI[] all = PlantSelect.AllCards();

            if (all == null)
            {
                return null;
            }

            return ApiBase.Query("the deck before the menu", () =>
            {
                var ordered = new List<CardUI>(all.Length);

                foreach (CardUI card in all)
                {
                    if (ApiBase.Present(card) && card.onCardBank)
                    {
                        ordered.Add(card);
                    }
                }

                foreach (CardUI card in all)
                {
                    if (ApiBase.Present(card) && !card.onCardBank)
                    {
                        ordered.Add(card);
                    }
                }

                return ordered.ToArray();
            }, LobbyHud.Capability);
        }

        /// <summary>
        /// The two images of the card that carries this plant, or null when no card paints it. A card whose
        /// first child has no sprite cannot paint, so the search walks on to one that can.
        /// </summary>
        private static Art Find(int plantType)
        {
            CardUI[] cards = Cards();

            if (cards == null)
            {
                return null;
            }

            return ApiBase.Query("the game's card of plant " + plantType, () =>
            {
                for (int i = 0; i < cards.Length; i++)
                {
                    CardUI card = cards[i];

                    if (!ApiBase.Present(card) || (int) card.thePlantType != plantType)
                    {
                        continue;
                    }

                    UnityEngine.UI.Image picture = PictureOf(card);

                    if (picture == null)
                    {
                        continue;
                    }

                    Art art = new Art();
                    art.Picture = picture;
                    art.Frame = FrameOf(card);

                    return art;
                }

                return null;
            }, LobbyHud.Capability);
        }

        /// <summary>
        /// One image of the preview card, as a child of `parent` at an offset from that parent's top-left
        /// corner. A source the game did not hand over leaves an empty box: a card that never got its frame
        /// still shows the plant standing in its place.
        /// </summary>
        private static GameObject Part(string name, Transform parent, float x, float y, float width,
            float height, UnityEngine.UI.Image source, bool letterbox)
        {
            GameObject holder = ApiBase.Call("new GameObject(" + name + ")",
                () => new GameObject(name), LobbyHud.Capability);

            if (!ApiBase.Present(holder))
            {
                return null;
            }

            UnityEngine.UI.Image image = ApiBase.Call("holder.AddComponent<Image>",
                () => holder.AddComponent<UnityEngine.UI.Image>(), LobbyHud.Capability);

            if (!ApiBase.Present(image))
            {
                Destroy(holder, name);
                return null;
            }

            bool placed = ApiBase.TryCall(name + " takes its place under its line", () =>
            {
                image.transform.SetParent(parent, false);
                holder.layer = parent.gameObject.layer;

                RectTransform box = image.rectTransform;
                box.anchorMin = new Vector2(0f, 1f);
                box.anchorMax = new Vector2(0f, 1f);
                box.pivot = new Vector2(0.5f, 0.5f);
                box.anchoredPosition = new Vector2(x, y);
                box.sizeDelta = new Vector2(width, height);

                if (ApiBase.Present(source))
                {
                    image.sprite = source.sprite;
                    image.material = source.material;
                    image.color = letterbox ? Color.white : source.color;

                    // A sliced frame keeps its corners at their own size, which is what lets a card look
                    // like a card at a size the game never drew it at.
                    image.type = source.type;
                }

                image.preserveAspect = letterbox;

                // The preview only tells the player what a seat holds; it must not stand between the mouse
                // and whatever the game has under it.
                image.raycastTarget = false;
            }, LobbyHud.Capability);

            if (!placed)
            {
                Destroy(holder, name);
                return null;
            }

            return holder;
        }

        /// <summary>
        /// The image of the card's first child, which is where the game paints the plant (E46). Read inside
        /// a guard, so nothing here can reach the game's main loop.
        /// </summary>
        private static UnityEngine.UI.Image PictureOf(CardUI card)
        {
            if (card.transform.childCount == 0)
            {
                return null;
            }

            Transform icon = card.transform.GetChild(0);

            if (!ApiBase.Present(icon))
            {
                return null;
            }

            UnityEngine.UI.Image image = icon.GetComponent<UnityEngine.UI.Image>();

            return ApiBase.Present(image) && ApiBase.Present(image.sprite) ? image : null;
        }

        /// <summary>The image on the card's own GameObject, which carries its frame (E49).</summary>
        private static UnityEngine.UI.Image FrameOf(CardUI card)
        {
            UnityEngine.UI.Image image = card.GetComponent<UnityEngine.UI.Image>();

            return ApiBase.Present(image) && ApiBase.Present(image.sprite) ? image : null;
        }

        private static void Destroy(GameObject holder, string name)
        {
            ApiBase.TryCall("Destroy(" + name + ")", () => Object.Destroy(holder), LobbyHud.Capability);
        }

        private static void Warn(int plantType)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            Log.Warn("ui", "no card carries the picture of plant type " + plantType
                + ", so a held plant is left out of the list");
        }

        /// <summary>The two images the game paints one card with: the frame it carries and the plant in it.</summary>
        private sealed class Art
        {
            public UnityEngine.UI.Image Frame;
            public UnityEngine.UI.Image Picture;
        }
    }
}
