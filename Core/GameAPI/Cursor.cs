using System;
using System.Collections.Generic;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// What the local player is holding and which cell it points at, read straight from the game's
    /// own input object. This is the remote-cursor source: the mod never recomputes a cell from
    /// screen coordinates, because Mouse already did it.
    /// </summary>
    public static class Cursor
    {
        public const int NoCell = -1;
        public const int NoCard = -1;

        private static readonly int[] NothingUnderCursor = new int[0];

        /// <summary>How long a lawn with no cart is believed before the scene is searched again.</summary>
        private const int WheelSearchMillis = 1000;

        private static Il2Cpp.Wheel _wheel;
        private static int _nextWheelSearch;

        public static int ReadColumn()
        {
            return ApiBase.Query("ReadCursorColumn", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                return ApiBase.Present(mouse) ? mouse.theMouseColumn : NoCell;
            }, "input");
        }

        public static int ReadRow()
        {
            return ApiBase.Query("ReadCursorRow", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                return ApiBase.Present(mouse) ? mouse.theMouseRow : NoCell;
            }, "input");
        }

        /// <summary>Plant type of the card currently on the mouse, or NoCard when the hand is empty.</summary>
        public static int ReadHeldCardPlantType()
        {
            return ApiBase.Query("ReadHeldCard", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.theCardOnMouse))
                {
                    return NoCard;
                }

                return (int)mouse.theCardOnMouse.thePlantType;
            }, "input");
        }

        /// <summary>True while the game's glove tool is in this player's hand.</summary>
        public static bool ReadGlovePickedUp()
        {
            return ApiBase.Query("ReadGlovePickedUp", () =>
            {
                Il2Cpp.Glove glove = Il2Cpp.Glove.Instance;
                return ApiBase.Present(glove) && glove.isPickUp;
            }, "input");
        }

        /// <summary>The plant the glove carries, or NoCard when it carries a zombie or nothing.</summary>
        public static int ReadPlantOnGloveType()
        {
            return ApiBase.Query("ReadPlantOnGlove", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.thePlantOnGlove))
                {
                    return NoCard;
                }

                return (int)mouse.thePlantOnGlove.thePlantType;
            }, "input");
        }

        /// <summary>The zombie the glove carries, or NoCard when it carries a plant or nothing.</summary>
        public static int ReadZombieOnGloveType()
        {
            return ApiBase.Query("ReadZombieOnGlove", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.theZombieOnGlove))
                {
                    return NoCard;
                }

                return (int)mouse.theZombieOnGlove.theZombieType;
            }, "input");
        }

        /// <summary>
        /// True while the game's shovel is picked up: `InGameTool.isPickUp`, the field the game's
        /// `PickUp`/`PutDown` toggle. `Mouse.mouseItemType` is not read here - this build never sets it
        /// to `Shovel`, so a preview that waits for that value never draws a shovel at all.
        /// </summary>
        public static bool ReadShovelPickedUp()
        {
            return ApiBase.Query("ReadShovelPickedUp", () =>
            {
                Il2Cpp.Shovel shovel = Il2Cpp.Shovel.Instance;
                return ApiBase.Present(shovel) && shovel.isPickUp;
            }, "shovel");
        }

        /// <summary>True while the game's hammer is picked up, on the same field the shovel uses.</summary>
        public static bool ReadHammerPickedUp()
        {
            return ApiBase.Query("ReadHammerPickedUp", () =>
            {
                Il2Cpp.Hammer hammer = Il2Cpp.Hammer.Instance;
                return ApiBase.Present(hammer) && hammer.isPickUp;
            }, "shovel");
        }

        /// <summary>True while the game's cart is picked up.</summary>
        public static bool ReadWheelPickedUp()
        {
            Il2Cpp.Wheel wheel = WheelObject();

            return ApiBase.Present(wheel) && wheel.isPickUp;
        }

        /// <summary>
        /// True while this hand holds the fertilizer. It has no tool of the strip to ask: the fertilizer is
        /// a bucket, and `Bucket.Pick` writes the object it picked into `Mouse.theItemOnMouse` and clears
        /// the plant fields the mouse was carrying (ref/EVIDENCE.md E47). So the question is what the mouse
        /// is holding, and the answer is this lawn's fertilizer object while it is in a hand.
        /// </summary>
        public static bool ReadFertilizerPickedUp()
        {
            return ApiBase.Query("ReadFertilizerPickedUp", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;

                if (!ApiBase.Present(mouse) || !ApiBase.Present(mouse.theItemOnMouse))
                {
                    return false;
                }

                return ApiBase.Present(mouse.theItemOnMouse.GetComponent<Il2Cpp.Fertilize>());
            }, "input");
        }

        /// <summary>
        /// The cart this lawn is playing with, or null. `Shovel`, `Glove` and `Hammer` each keep a static
        /// `Instance`; `Wheel` does not, so the object has to be looked for. The answer is kept because a
        /// frame that looked for it every time would spend itself in the search, and it is looked for
        /// again a second after the lawn that has no cart, which is what a level change is.
        /// </summary>
        public static Il2Cpp.Wheel WheelObject()
        {
            if (ApiBase.Present(_wheel))
            {
                return _wheel;
            }

            int now = Environment.TickCount;
            if (unchecked(now - _nextWheelSearch) < 0)
            {
                return null;
            }

            _nextWheelSearch = now + WheelSearchMillis;

            // A cart this end already had and can no longer see is a lawn change or a cart torn down
            // under it: the case a cached handle used to hide (E61). Said once per replacement, because
            // a search on a lawn with no cart runs again every second and must stay quiet.
            Il2Cpp.Wheel gone = _wheel;

            _wheel = ApiBase.Query("FindWheel", () =>
                UnityEngine.Object.FindObjectOfType<Il2Cpp.Wheel>(), "shovel");

            if (gone != null)
            {
                Log.Info("mp", "the cart this lawn was playing with is gone; the lawn's cart was looked "
                    + "for again and " + (ApiBase.Present(_wheel) ? "found" : "not found") + " "
                    + ApiBase.FrameTag());
            }

            return _wheel;
        }

        /// <summary>
        /// What the game's own hit test says is under the local pointer, as Unity instance ids in the
        /// game's order. The shovel is aimed by this and not by the hovered cell: a cell can hold two
        /// plants and the game, not the mod, decides which one a tool takes. A blocked query answers with
        /// no plants rather than null, so a caller that walks the answer cannot throw.
        /// </summary>
        public static int[] ReadPlantInstanceIdsUnderCursor()
        {
            int[] found = ApiBase.Query("ReadPlantsUnderCursor", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse))
                {
                    return NothingUnderCursor;
                }

                Il2CppSystem.Collections.Generic.List<Il2Cpp.Plant> under = mouse.GetPlantsOnMouse();
                if (!ApiBase.Present(under))
                {
                    return NothingUnderCursor;
                }

                var plants = new List<int>();
                for (int i = 0; i < under.Count; i++)
                {
                    Il2Cpp.Plant plant = under[i];
                    if (ApiBase.Present(plant))
                    {
                        plants.Add(plant.GetInstanceID());
                    }
                }

                return plants.ToArray();
            }, "input");

            return found ?? NothingUnderCursor;
        }
    }
}
