using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The name of the seat that holds a card, written on the card itself.
    ///
    /// The chooser has one text slot per card and the game owns it: `CardUI.Update` writes the price into
    /// `CardUI.text` every frame, and the child it dims a card with is the same shadow the game switches on
    /// when this lawn cannot pay for the plant (ref/EVIDENCE.md E45). Neither can carry a name, so the name
    /// is a child the mod adds to the card's own `RectTransform` and takes away again - the game's text
    /// component, the game's default font, inside the card so it is sized, moved and destroyed with it.
    ///
    /// Nothing is remembered between two sweeps: the label is found under the card by name, so a card that
    /// was rebuilt, came in on a page the player opened later, or lost its mod cannot keep a stale name or
    /// a second copy of it. It is added last among the card's children, which is what puts it above the
    /// shadow the game switches on - a name hidden by the darkening would be no name at all.
    /// </summary>
    public static class CardLabel
    {
        /// <summary>The child's name, which is also how it is found again.</summary>
        private const string HolderName = "PvzMP card owner";

        /// <summary>
        /// How much of the card's own height the name is allowed to eat. A card is a picture first, so the
        /// name is written over it rather than under it, and the chooser's cards are not all the same size:
        /// the size has to come from the card the name is written on, not from a number chosen here.
        /// </summary>
        private const float HeightShare = 0.34f;

        private const float SmallestFont = 16f;
        private const float BiggestFont = 28f;

        private static bool _warned;

        /// <summary>
        /// Put the holder's name on a card, or take the name off when nobody holds it. A card the game
        /// will not hand over a component to simply ends up without a name: the room's decision and the
        /// greyed-out card do not depend on this.
        /// </summary>
        public static void SetOwner(CardUI card, string nick, Color tint)
        {
            if (!ApiBase.Present(card))
            {
                return;
            }

            if (string.IsNullOrEmpty(nick))
            {
                Remove(card);
                return;
            }

            TextMeshProUGUI label = Find(card);

            if (!ApiBase.Present(label) && !TryBuild(card, out label))
            {
                return;
            }

            // The chooser is swept four times a second, so a name that already stands on the card is not
            // written again and not logged again: only a card whose holder changed is touched.
            bool written = ApiBase.Query("the holder's name on a card", () =>
            {
                if (label.text == nick && label.color == tint)
                {
                    return false;
                }

                label.text = nick;
                label.color = tint;
                return true;
            }, PlantSelect.Capability);

            if (written)
            {
                Log.Info("ui", "a card now carries the name '" + nick + "'");
            }
        }

        private static TextMeshProUGUI Find(CardUI card)
        {
            return ApiBase.Query("find the holder's name", () =>
            {
                Transform child = card.transform.Find(HolderName);
                return ApiBase.Present(child) ? child.GetComponent<TextMeshProUGUI>() : null;
            }, PlantSelect.Capability);
        }

        private static bool TryBuild(CardUI card, out TextMeshProUGUI label)
        {
            label = null;

            TMP_FontAsset font = ApiBase.Query("TMP_Settings.defaultFontAsset",
                () => TMP_Settings.defaultFontAsset, PlantSelect.Capability);
            if (!ApiBase.Present(font))
            {
                Warn("the game has no default font");
                return false;
            }

            GameObject holder = ApiBase.Call("new GameObject(" + HolderName + ")",
                () => new GameObject(HolderName), PlantSelect.Capability);
            if (!ApiBase.Present(holder))
            {
                Warn("the game refused a holder for the name");
                return false;
            }

            TextMeshProUGUI made = ApiBase.Call("holder.AddComponent<TextMeshProUGUI>",
                () => holder.AddComponent<TextMeshProUGUI>(), PlantSelect.Capability);

            if (!ApiBase.Present(made))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => UnityEngine.Object.Destroy(holder),
                    PlantSelect.Capability);
                Warn("the game refused a text label on the holder");
                return false;
            }

            bool placed = ApiBase.TryCall("the name goes inside the card", () =>
            {
                made.transform.SetParent(card.transform, false);

                // A UI object is drawn by the camera of the canvas it ends up on, which is decided by its
                // layer, so the card's own layer is the only right answer here.
                holder.layer = card.gameObject.layer;

                // The whole face of the card, so the name is centred on the plant the player is looking at
                // instead of on a band the mod chose under it.
                RectTransform box = made.rectTransform;
                box.anchorMin = new Vector2(0f, 0f);
                box.anchorMax = new Vector2(1f, 1f);
                box.offsetMin = new Vector2(2f, 2f);
                box.offsetMax = new Vector2(-2f, -2f);

                made.font = font;
                made.fontSize = FontOf(box.rect.height);
                made.alignment = TextAlignmentOptions.Midline;

                // One line, and a long nickname is allowed to reach past the card rather than be cut: a
                // name the player cannot finish reading is worse than a name that overlaps its edge.
                made.enableWordWrapping = false;
                made.overflowMode = TextOverflowModes.Overflow;

                // The card is clicked through the game's own mouse code; a label that can be hit would
                // steal the click from the plant it only describes.
                made.raycastTarget = false;
            }, PlantSelect.Capability);

            if (!placed)
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => UnityEngine.Object.Destroy(holder),
                    PlantSelect.Capability);
                Warn("the game refused the name's place on the card");
                return false;
            }

            Log.Info("ui", "a card's face is " + CardFace(card) + ", its holder's name is written on it at "
                + made.fontSize + " points in the font '" + font.name + "'");

            label = made;
            return true;
        }

        /// <summary>How big a name fits a card of this height.</summary>
        private static float FontOf(float cardHeight)
        {
            if (cardHeight <= 0f)
            {
                return SmallestFont;
            }

            return Mathf.Clamp(cardHeight * HeightShare, SmallestFont, BiggestFont);
        }

        private static string CardFace(CardUI card)
        {
            return ApiBase.Query("the size of the card the name is written on", () =>
            {
                RectTransform face = card.GetComponent<RectTransform>();

                return ApiBase.Present(face)
                    ? Mathf.RoundToInt(face.rect.width) + "x" + Mathf.RoundToInt(face.rect.height) + " px"
                    : "not a rectangle this end can read";
            }, PlantSelect.Capability);
        }

        private static void Remove(CardUI card)
        {
            GameObject gone = ApiBase.Query("take the holder's name off a card", () =>
            {
                Transform child = card.transform.Find(HolderName);
                return ApiBase.Present(child) ? child.gameObject : null;
            }, PlantSelect.Capability);

            if (ApiBase.Present(gone))
            {
                ApiBase.TryCall("Destroy(" + HolderName + ")", () => UnityEngine.Object.Destroy(gone),
                    PlantSelect.Capability);
            }
        }

        private static void Warn(string why)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            Log.Warn("ui", "no card will show a holder's name: " + why);
        }
    }
}
