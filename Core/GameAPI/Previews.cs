using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The game's own pictures, used as a remote player's ghost. `CreatePlant.CreatePlantPreview` is the
    /// method the garden and the mouse use for theirs: it builds the sprite set of a plant type, tints it
    /// with the color it is given and adds no collider or gameplay component of its own. The tools have no
    /// preview method, so a tool's ghost is a copy of the game's own picture of that tool, drawn on the
    /// layer the previews use and partly see-through. Proof: ref/EVIDENCE.md E19, E21, E45 and E47.
    ///
    /// The nickname over a ghost is the game's own world-space TextMeshPro with the game's default font.
    /// The game has no such label of its own; only the font, the text object and the placement are
    /// native. The ghost is parented to the board, so a level change takes it down together with the
    /// lawn it was pointing at.
    /// </summary>
    public static class Previews
    {
        /// <summary>Above every plant's own order, so a ghost over a plant is not hidden behind it.</summary>
        private const int GhostSortingOrder = 9000;

        private const int NickSortingOrder = 9001;

        /// <summary>
        /// How visible a tool's ghost is. The game's own hand shows the tool on the mouse, so a ghost that
        /// is fully opaque is a second tool on the screen; at 60% it stays readable as that tool and still
        /// lets the plant under it be seen.
        /// </summary>
        private const float GhostAlpha = 0.6f;

        /// <summary>World units between the cell centre and the nickname's baseline.</summary>
        private const float NickHeight = 1.1f;

        private const float NickFontSize = 36f;
        private const float NickScale = 0.1f;

        /// <summary>Which pictures this end has already said it uses, so the hunt is told once and not a frame.</summary>
        private static readonly List<string> PictureSaid = new List<string>();

        /// <summary>Which previews this end has already said it could not build, for the same reason.</summary>
        private static readonly List<string> FailureSaid = new List<string>();

        private static bool _labelWarned;

        /// <summary>
        /// The sorting layer the game's own preview pictures are drawn on, learned from the first ghost this
        /// end made and kept for the tools, which have no layer of their own worth borrowing. Zero until a
        /// ghost has been seen.
        /// </summary>
        private static int _ghostLayer;

        private static bool _ghostLayerKnown;

        public static GameObject Create(int plantType, int column, int row, Color color)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "CreatePlantPreview was called off the game's main thread");
                return null;
            }

            try
            {
                Il2Cpp.Board board = Il2Cpp.Board.Instance;
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;

                if (!ApiBase.Present(board) || !ApiBase.Present(grid))
                {
                    return null;
                }

                GameObject ghost = Il2Cpp.CreatePlant.CreatePlantPreview(
                    (Il2Cpp.PlantType)plantType, color, board.transform, Vector2.zero);

                if (!ApiBase.Present(ghost))
                {
                    Log.Warn("api", "CreatePlantPreview -> the game returned no object for type " + plantType);
                    return null;
                }

                ghost.transform.position = grid.GetGridCenter(column, row);

                SortingGroup group = ghost.GetComponent<SortingGroup>();
                if (ApiBase.Present(group))
                {
                    group.sortingOrder = GhostSortingOrder;
                    RememberGhostLayer(group.sortingLayerID);
                }

                return ghost;
            }
            catch (Exception e)
            {
                FailOnce("CreatePlantPreview(type=" + plantType + ")", e);
                return null;
            }
        }

        /// <summary>
        /// The game's own zombie preview at a cell, for the glove's ghost. `CreateZombie.CreateZombiePreview`
        /// is the same picture the mouse builds when a zombie card is held, so the ghost walks and animates
        /// like the real preview without carrying a collider or a zombie's update chain.
        /// </summary>
        public static GameObject CreateZombie(int zombieType, int column, int row, Color color)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "CreateZombiePreview was called off the game's main thread");
                return null;
            }

            try
            {
                Il2Cpp.Board board = Il2Cpp.Board.Instance;
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;

                if (!ApiBase.Present(board) || !ApiBase.Present(grid))
                {
                    return null;
                }

                GameObject ghost = Il2Cpp.CreateZombie.CreateZombiePreview(
                    (Il2Cpp.ZombieType)zombieType, color, board.transform, Vector2.zero);

                if (!ApiBase.Present(ghost))
                {
                    Log.Warn("api", "CreateZombiePreview -> the game returned no object for type "
                        + zombieType);
                    return null;
                }

                ghost.transform.position = grid.GetGridCenter(column, row);

                SortingGroup group = ghost.GetComponent<SortingGroup>();
                if (ApiBase.Present(group))
                {
                    group.sortingOrder = GhostSortingOrder;
                    RememberGhostLayer(group.sortingLayerID);
                }

                return ghost;
            }
            catch (Exception e)
            {
                FailOnce("CreateZombiePreview(type=" + zombieType + ")", e);
                return null;
            }
        }

        /// <summary>
        /// What the game moves to the cursor as a tool. A ghost of the one a seat holds is drawn on the cell
        /// that seat points at, and these numbers are how the room says which of them it is.
        /// </summary>
        public const int ToolShovel = 1;
        public const int ToolGlove = 2;
        public const int ToolHammer = 3;
        public const int ToolWheel = 4;

        /// <summary>
        /// The fertilizer, which is not one of the four tools of the strip: it is a bucket, and the game
        /// hands it out on the lawn. It is drawn the same way because while it is held it sits on the mouse
        /// like a tool does, and that is where its picture is. Proof: ref/EVIDENCE.md E47.
        /// </summary>
        public const int ToolFertilizer = 5;

        /// <summary>
        /// A copy of one of the game's four tool pictures at a cell, named after the tool. The object the
        /// game moves to the cursor is the source; the ghost takes only its sprite, never the behaviour or
        /// the collider, so it cannot be clicked or start moving.
        /// </summary>
        public static GameObject CreateTool(int tool, int column, int row)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "CreateToolPreview was called off the game's main thread");
                return null;
            }

            try
            {
                Il2Cpp.Board board = Il2Cpp.Board.Instance;
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;

                if (!ApiBase.Present(board) || !ApiBase.Present(grid))
                {
                    return null;
                }

                Sprite sprite;
                Material material;
                int layer;

                if (!ToolPicture(tool, out sprite, out material, out layer))
                {
                    return null;
                }

                GameObject ghost = new GameObject("PvzMP " + ToolName(tool) + " preview");
                SpriteRenderer view = ghost.AddComponent<SpriteRenderer>();

                view.sprite = sprite;
                view.sharedMaterial = material;

                // A sorting order is only compared inside its own layer, so the layer is the whole answer
                // to whether a ghost is above the lawn. The tool's own layer is its strip in the menu, and
                // a ghost drawn there sits under every plant it is supposed to point at, however high its
                // order is: the ghost goes on the layer the game's own previews use.
                view.sortingLayerID = GhostLayer(layer);
                view.sortingOrder = GhostSortingOrder;
                view.color = new Color(1f, 1f, 1f, GhostAlpha);

                ghost.transform.SetParent(board.transform, true);
                ghost.transform.position = grid.GetGridCenter(column, row);

                FitToCell(view, grid, column, row);

                return ghost;
            }
            catch (Exception e)
            {
                FailOnce("CreateToolPreview(" + ToolName(tool) + ")", e);
                return null;
            }
        }

        /// <summary>
        /// How wide a tool's ghost is drawn: the cell it points at. The game makes its tool art for a cursor
        /// and not for a lawn, so a hammer and a zombie glove come out of the sprite bigger than the plant
        /// they are aimed at, which is what the row of ghosts looks like today. The cell is measured from the
        /// game's own grid - the distance between two neighbouring centres, which is the grid's answer and
        /// not a guess about the units of an art file - and the picture is scaled by its own world bounds,
        /// which is Unity's arithmetic for a sprite at any distance. A plant's or a zombie's ghost is not
        /// fitted: `CreatePlantPreview` and `CreateZombiePreview` already draw the thing at its own size.
        /// </summary>
        private static void FitToCell(SpriteRenderer view, Il2Cpp.GridManager grid, int column, int row)
        {
            Bounds here = view.bounds;
            if (here.size.x <= 0f || here.size.y <= 0f)
            {
                return;
            }

            float width = Math.Abs(grid.GetGridCenter(1, row).x - grid.GetGridCenter(0, row).x);
            float height = Math.Abs(grid.GetGridCenter(column, 1).y - grid.GetGridCenter(column, 0).y);

            if (width <= 0f || height <= 0f)
            {
                return;
            }

            float fit = Math.Min(width / here.size.x, height / here.size.y);

            view.transform.localScale = view.transform.localScale * fit;
        }

        /// <summary>
        /// A tool's picture, taken from the tool of this lawn and not from this end's hand. A ghost of a
        /// peer's shovel is drawn on a screen where nobody is holding a shovel, so the search has to answer
        /// there too: every tool of the strip is an object of the level and writes its own static `Instance`
        /// in `Awake`, before any click of any kind, and a fertilizer is a bucket that stands on its own
        /// lawn (proof: ref/EVIDENCE.md E48). The sprite of a UI image - which is how a tool kept only as a
        /// button shows itself - is drawn over a plant's material, the one sprite material the lawn is known
        /// to use, and last comes the object this end has on its mouse.
        /// </summary>
        private static bool ToolPicture(int tool, out Sprite sprite, out Material material, out int layer)
        {
            sprite = null;
            material = null;
            layer = 0;

            GameObject source = ToolObject(tool);

            if (ApiBase.Present(source))
            {
                SpriteRenderer view = source.GetComponentInChildren<SpriteRenderer>(true);
                if (ApiBase.Present(view) && ApiBase.Present(view.sprite))
                {
                    sprite = view.sprite;
                    material = view.sharedMaterial;
                    layer = view.sortingLayerID;

                    SayPicture(tool, "the sprite '" + view.sprite.name + "' of this lawn's own "
                        + ToolName(tool));
                    return true;
                }

                UnityEngine.UI.Image image = source.GetComponentInChildren<UnityEngine.UI.Image>(true);
                SpriteRenderer plantPicture = FirstPlantRenderer();

                if (ApiBase.Present(image) && ApiBase.Present(image.sprite) && ApiBase.Present(plantPicture))
                {
                    sprite = image.sprite;
                    material = plantPicture.sharedMaterial;
                    layer = plantPicture.sortingLayerID;

                    SayPicture(tool, "the picture '" + image.sprite.name + "' of the " + ToolName(tool)
                        + " button, drawn with a plant's material");
                    return true;
                }
            }

            if (tool == ToolGlove)
            {
                Il2Cpp.Glove glove = Il2Cpp.Glove.Instance;

                Sprite carried = ApiBase.Present(glove)
                    ? (glove.isZombie ? glove.zombieGlove : glove.plantGlove)
                    : null;

                SpriteRenderer plantPicture = FirstPlantRenderer();

                if (ApiBase.Present(carried) && ApiBase.Present(plantPicture))
                {
                    sprite = carried;
                    material = plantPicture.sharedMaterial;
                    layer = plantPicture.sortingLayerID;

                    SayPicture(tool, "the sprite '" + carried.name + "' the glove itself carries");
                    return true;
                }
            }

            Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
            if (ApiBase.Present(mouse) && ApiBase.Present(mouse.theItemOnMouse))
            {
                SpriteRenderer view = mouse.theItemOnMouse.GetComponentInChildren<SpriteRenderer>(true);
                if (ApiBase.Present(view) && ApiBase.Present(view.sprite))
                {
                    sprite = view.sprite;
                    material = view.sharedMaterial;
                    layer = view.sortingLayerID;

                    SayPicture(tool, "the sprite '" + view.sprite.name + "' of the object on this mouse");
                    return true;
                }
            }

            WarnNoPicture(tool, source);
            return false;
        }

        /// <summary>
        /// Which object of this lawn carries the tool's picture. A seat that never picked a shovel up still
        /// has the shovel of its level, because `Shovel.Awake` writes the static `Instance` the game itself
        /// uses (E48), and the cart and the glove are looked for the same way. A fertilizer has no such
        /// field and its hand is the mouse, which is empty on a seat that is not holding one, so the lawn
        /// is asked for a fertilizer of its own after the mouse.
        /// </summary>
        private static GameObject ToolObject(int tool)
        {
            if (tool == ToolFertilizer)
            {
                // The mouse is asked only while it really holds a fertilizer: the same field carries the plant
                // a glove lifted and the zombie it dragged, so taking whatever is there could paint a
                // stranger's fertilizer with one of those. Proof: ref/EVIDENCE.md E47.
                GameObject held = Cursor.ReadFertilizerPickedUp() ? ItemOnMouse() : null;

                return ApiBase.Present(held) ? held : FertilizerOfTheLawn();
            }

            Component owned = null;

            switch (tool)
            {
                case ToolGlove:
                    owned = Il2Cpp.Glove.Instance;
                    break;
                case ToolHammer:
                    owned = Il2Cpp.Hammer.Instance;
                    break;
                case ToolWheel:
                    owned = Cursor.WheelObject();
                    break;
                default:
                    owned = Il2Cpp.Shovel.Instance;
                    break;
            }

            if (ApiBase.Present(owned))
            {
                return owned.gameObject;
            }

            // A level that has not run its tool's Awake yet, or a tool the game keeps off the scene: the
            // object is still somewhere in what this end loaded, and switched off is not the same as gone.
            return ToolObjectOfTheScene(tool);
        }

        /// <summary>
        /// Every tool of the strip, asked of the objects this end loaded rather than of a hand. The four are
        /// one family in the game - `InGameTool` - so one search of the loaded scene answers for all of them.
        /// </summary>
        private static GameObject ToolObjectOfTheScene(int tool)
        {
            Il2Cpp.InGameTool[] found;

            switch (tool)
            {
                case ToolGlove:
                    found = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.Glove>();
                    break;
                case ToolHammer:
                    found = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.Hammer>();
                    break;
                case ToolWheel:
                    found = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.Wheel>();
                    break;
                default:
                    found = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.Shovel>();
                    break;
            }

            return FirstPresent(found);
        }

        private static GameObject FirstPresent(Il2Cpp.InGameTool[] found)
        {
            for (int i = 0; found != null && i < found.Length; i++)
            {
                Il2Cpp.InGameTool one = found[i];

                if (ApiBase.Present(one) && ApiBase.Present(one.gameObject))
                {
                    return one.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// The fertilizer of this lawn, held or not. A bucket handed out on the lawn keeps no static field to
        /// ask, so a seat that is not holding one still has the object whose picture its ghost needs - and
        /// the game's own count of these objects is what the fertilizer watch prints.
        /// </summary>
        private static GameObject FertilizerOfTheLawn()
        {
            Il2Cpp.Fertilize[] found = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.Fertilize>();

            for (int i = 0; found != null && i < found.Length; i++)
            {
                Il2Cpp.Fertilize one = found[i];

                if (ApiBase.Present(one) && ApiBase.Present(one.gameObject))
                {
                    return one.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// The object the game has put on the mouse, which is where a held bucket - the fertilizer - is
        /// while the hand that picked it up still holds it: `Bucket.Pick` writes this very field (E47).
        /// </summary>
        private static GameObject ItemOnMouse()
        {
            Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;

            return ApiBase.Present(mouse) ? mouse.theItemOnMouse : null;
        }

        private static string ToolName(int tool)
        {
            switch (tool)
            {
                case ToolGlove:
                    return "glove";
                case ToolHammer:
                    return "hammer";
                case ToolWheel:
                    return "cart";
                case ToolFertilizer:
                    return "fertilizer";
                default:
                    return "shovel";
            }
        }

        /// <summary>
        /// One line per tool and per kind of source, for as long as this end runs: a ghost that is drawn is
        /// silent, and the question a live room asks is which of the lawn's objects answered, since the five
        /// tools are not kept the same way.
        /// </summary>
        private static void SayPicture(int tool, string from)
        {
            string said = ToolName(tool) + ": " + from;
            if (PictureSaid.Contains(said))
            {
                return;
            }

            PictureSaid.Add(said);
            Log.Info("api", "a ghost of the " + ToolName(tool) + " takes its picture from " + from
                + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// One line per tool that answered nothing, naming what this lawn did have: the ghost is then not
        /// drawn at all, which also takes the seat's nickname under it away, and this is the line that says
        /// whether the lawn has no such object or only an object with no picture on it.
        /// </summary>
        private static void WarnNoPicture(int tool, GameObject source)
        {
            string said = "nothing " + tool;
            if (PictureSaid.Contains(said))
            {
                return;
            }

            PictureSaid.Add(said);

            Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
            bool onMouse = ApiBase.Present(mouse) && ApiBase.Present(mouse.theItemOnMouse);

            Log.Warn("api", "this lawn gives no picture of a " + ToolName(tool) + ": its own object is "
                + (ApiBase.Present(source) ? "'" + source.name + "'" : "here not at all") + ", this end has "
                + (onMouse ? "an object on its mouse" : "nothing on its mouse")
                + ", so neither the ghost nor the name over it is drawn");
        }

        /// <summary>
        /// A preview the game refused to build, said once per call and per answer. Ghosts are made from the
        /// frame loop, so a lawn that cannot answer the question - no level, no picture for that type - would
        /// otherwise pay an error line and an exception through the interop for every frame it is asked, which
        /// is what a room felt as a pause of about a second after another seat left the level.
        /// </summary>
        private static void FailOnce(string call, Exception e)
        {
            string said = call + " " + e.GetType().Name + " " + e.Message;
            if (FailureSaid.Contains(said))
            {
                return;
            }

            FailureSaid.Add(said);

            Log.Error("api", call + " -> FAIL " + e.GetType().Name + ": " + e.Message + " "
                + ApiBase.FrameTag());
        }

        /// <summary>
        /// The layer a ghost belongs on: the one the game's own preview objects carry. Until this end has
        /// made a plant or a zombie ghost - which is what learns it - the lawn's own plants are asked,
        /// because `CreatePlant.SetPlant` gives every plant it makes a sorting group too. A lawn with
        /// neither is a lawn with nothing to draw over, so the borrowed layer answers that.
        /// </summary>
        private static int GhostLayer(int borrowed)
        {
            if (_ghostLayerKnown)
            {
                return _ghostLayer;
            }

            SortingGroup group = FirstPlantGroup();

            if (ApiBase.Present(group))
            {
                RememberGhostLayer(group.sortingLayerID);
            }

            return _ghostLayerKnown ? _ghostLayer : borrowed;
        }

        private static void RememberGhostLayer(int layer)
        {
            if (_ghostLayerKnown && _ghostLayer == layer)
            {
                return;
            }

            _ghostLayer = layer;
            _ghostLayerKnown = true;
        }

        /// <summary>Any standing plant's own sorting group, read once for its layer.</summary>
        private static SortingGroup FirstPlantGroup()
        {
            SortingGroup found = null;

            Entities.ForEachPlant(plant =>
            {
                if (found == null)
                {
                    found = plant.GetComponent<SortingGroup>();
                }
            });

            return ApiBase.Present(found) ? found : null;
        }

        /// <summary>Any standing plant's renderer, borrowed for its material and layer.</summary>
        private static SpriteRenderer FirstPlantRenderer()
        {
            SpriteRenderer found = null;

            Entities.ForEachPlant(plant =>
            {
                if (found == null)
                {
                    found = plant.GetComponentInChildren<SpriteRenderer>(true);
                }
            });

            return ApiBase.Present(found) ? found : null;
        }

        /// <summary>
        /// A seat's nickname over a ghost, in the game's own world-space text object and default font.
        /// Null when the game has no font, which leaves the ghost without a name rather than the game
        /// without a frame.
        /// </summary>
        public static GameObject CreateNick(string nick, int column, int row, Color color, int sortingLayer)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "CreateNick was called off the game's main thread");
                return null;
            }

            try
            {
                Il2Cpp.Board board = Il2Cpp.Board.Instance;
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;
                TMP_FontAsset font = TMP_Settings.defaultFontAsset;

                if (!ApiBase.Present(board) || !ApiBase.Present(grid))
                {
                    return null;
                }

                if (!ApiBase.Present(font))
                {
                    WarnNoLabel("the game has no default font");
                    return null;
                }

                GameObject holder = new GameObject("PvzMP preview nick");
                TextMeshPro label = holder.AddComponent<TextMeshPro>();

                label.font = font;
                label.fontSize = NickFontSize;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = false;
                label.text = nick;
                label.color = color;
                label.sortingLayerID = sortingLayer;
                label.sortingOrder = NickSortingOrder;

                holder.transform.SetParent(board.transform, true);
                holder.transform.position = grid.GetGridCenter(column, row) + new Vector3(0f, NickHeight, 0f);
                holder.transform.localScale = new Vector3(NickScale, NickScale, NickScale);

                return holder;
            }
            catch (Exception e)
            {
                WarnNoLabel("the text object threw " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Moves a nickname that is already up to another cell, without rebuilding its mesh.</summary>
        public static void PlaceNick(GameObject nick, int column, int row, int sortingLayer)
        {
            if (!ApiBase.Present(nick))
            {
                return;
            }

            try
            {
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;
                if (!ApiBase.Present(grid))
                {
                    return;
                }

                nick.transform.position = grid.GetGridCenter(column, row) + new Vector3(0f, NickHeight, 0f);

                TextMeshPro label = nick.GetComponent<TextMeshPro>();
                if (ApiBase.Present(label))
                {
                    label.sortingLayerID = sortingLayer;
                }
            }
            catch (Exception e)
            {
                WarnNoLabel("moving the text threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The layer a ghost's pictures live on, so the nickname sorts with them.</summary>
        public static int SortingLayerOf(GameObject ghost)
        {
            if (!ApiBase.Present(ghost))
            {
                return 0;
            }

            SortingGroup group = ghost.GetComponent<SortingGroup>();
            if (ApiBase.Present(group))
            {
                return group.sortingLayerID;
            }

            Renderer view = ghost.GetComponentInChildren<Renderer>(true);
            return ApiBase.Present(view) ? view.sortingLayerID : 0;
        }

        public static void Destroy(GameObject ghost)
        {
            if (!ApiBase.Present(ghost))
            {
                return;
            }

            try
            {
                UnityEngine.Object.Destroy(ghost);
            }
            catch (Exception e)
            {
                Log.Warn("api", "Destroy(preview) threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void WarnNoLabel(string why)
        {
            if (_labelWarned)
            {
                return;
            }

            _labelWarned = true;
            Log.Warn("api", "a preview has no nickname: " + why);
        }
    }
}
