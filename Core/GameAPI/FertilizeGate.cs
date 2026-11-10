using System;
using HarmonyLib;
using Il2Cpp;
using PvzMP.Core.Session;
using UnityEngine;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Not a gate: every prefix returns true, nothing is stopped, and no capability is owned. A failed
    /// patch costs the measurement and nothing else, so the room keeps behaving exactly as it did before
    /// these lines were added. The two exceptions are the postfix on the drop, which is how the room learns
    /// which cell a fertilizer went onto, and the postfix on the body that changes the plant, which is how
    /// it learns what that cell's plant became (`FertilizeSync`).
    ///
    /// The fertilizer is the one lawn object whose doors the disassembly cannot put in order.
    /// `Fertilize.Use(int,int)` has three callers and one of them is `EndoFlame.OnFire`, which instantiates
    /// a prefab and calls it directly; the body that actually changes the plant - `Fertilize.Upgrade()`,
    /// with its sixteen `CreatePlant.SetPlant` calls - has no static caller in the image at all, like
    /// `Present.AnimEvent` before E40, so it is reached by an animation event and no name in a call graph
    /// will show it. Proof: ref/EVIDENCE.md E41.
    ///
    /// Four questions are only answerable in a live room, and one line per door answers them:
    ///
    /// - Which door does a player's click walk? The click path is `Use()` with no arguments, the drop on a
    ///   cell is `Use(col,row)`, and a third door - `Fertilize.Update` - calls the same drop with a plant
    ///   it rolled itself out of `ListExtensions.GetRandom`. Only the live run says which of them a hand
    ///   opening a fertilizer actually reaches, and whether the rolled one reaches it too.
    /// - Does every lawn hand out its own fertilizer? `GiveFertilize.AnimGive` loads the asset
    ///   "Items/Fertilize/Ferilize" from a scene object, and both ends load that scene, so each end may be
    ///   growing a supply of its own. The count of live fertilizer objects printed on both halves of one
    ///   line says whether the two lawns agree.
    /// - Does the change replace the plant or keep it? The line printed around `Upgrade` is the lawn
    ///   before and after, plant by plant, and a minus-and-plus pair on one object id is a change in place
    ///   while a minus on one id and a plus on another is a replacement. The live logs of 2026-11-13 answer
    ///   it: a replacement - a walnut at 2400/4000 leaves and a tallnut at 8000/8000 arrives on the same
    ///   cell (E53). So the kind that grew travels as its own frame (`FertilizeSync`), because a lawn that
    ///   was only told the cell keeps standing on the kind the fertilizer left behind.
    /// </summary>
    public static class FertilizeGate
    {
        /// <summary>The lawn as this end read it on the way into the body that changes a plant, or null when not watching.</summary>
        private static PlantState[] _plantsBefore;

        /// <summary>The fertilizer objects this end counted on the way into the door that hands one out.</summary>
        private static int _objectsBefore = -1;

        public static void Install()
        {
            try
            {
                new HarmonyLib.Harmony("PvzMP.fertilize").PatchAll(typeof(FertilizeGate));
                Log.Info("hook", "the fertilizer doors are watched, nothing is stopped " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "watching the fertilizer doors failed: " + e.GetType().Name + " " + e.Message
                    + " - the room behaves as before, only the answer about fertilizing is not measured");
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Fertilize), "Use", new[] { typeof(int), typeof(int) })]
        public static class DropOnCell
        {
            private static bool Prefix(int theColumn, int theRow)
            {
                if (Watching())
                {
                    Report("the drop of a fertilizer on cell " + theColumn + "," + theRow);
                }

                return true;
            }

            /// <summary>
            /// The one thing this watch does rather than print: every door that fertilizes a cell walks
            /// this body, so the cell the room needs is said here once the game has finished with it.
            /// </summary>
            private static void Postfix(int theColumn, int theRow)
            {
                FertilizeSync.AfterFertilize(theColumn, theRow);
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Fertilize), "Use", new Type[0])]
        public static class UseTheHeldFertilizer
        {
            private static bool Prefix()
            {
                if (Watching())
                {
                    Report("the no-argument Use of a fertilizer, the door a click on the held one reaches");
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Fertilize), "Pick")]
        public static class PickUp
        {
            private static bool Prefix()
            {
                if (Watching())
                {
                    Report("a fertilizer was picked up on this lawn");
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.Fertilize), "Upgrade")]
        public static class ChangeThePlant
        {
            private static bool Prefix()
            {
                _plantsBefore = Watching() ? Plants.ReadAll() : null;

                return true;
            }

            /// <summary>
            /// The second thing this watch does rather than print: the growth is a third of a second behind
            /// the drop that asked for it, so the kind it made cannot ride the cell's frame and says itself.
            /// </summary>
            private static void Postfix()
            {
                if (!Watching())
                {
                    return;
                }

                PlantState[] before = _plantsBefore;
                PlantState[] after = Plants.ReadAll();

                Report(before == null || after == null
                    ? "the body that changes the plant ran and this end could not read its lawn on one side"
                    : "the body that changes the plant left: " + FertilizeRules.Changed(Lines(before), Lines(after)));

                if (before != null && after != null)
                {
                    FertilizeSync.AfterGrow(FertilizeRules.Grown(WorldSync.Marks(before), WorldSync.Marks(after)));
                }

                _plantsBefore = null;
            }
        }

        [HarmonyPatch(typeof(Il2Cpp.GiveFertilize), "AnimGive")]
        public static class HandOneOut
        {
            private static bool Prefix()
            {
                _objectsBefore = Watching() ? ObjectsHere() : -1;

                return true;
            }

            private static void Postfix()
            {
                if (!Watching())
                {
                    return;
                }

                Report("the handing-out door ran: fertilizer objects here " + _objectsBefore + " -> "
                    + ObjectsHere());

                _objectsBefore = -1;
            }
        }

        /// <summary>Whether these lines are for a room: outside one the question is answered by the game working.</summary>
        private static bool Watching()
        {
            MpSession room = ConnectionEntry.Room;

            return room != null && room.RoomDecides;
        }

        private static void Report(string what)
        {
            try
            {
                Log.Info("fert", what + " | " + (ConnectionEntry.Room.IsHost ? "host" : "peer")
                    + ", fertilizer objects here: " + ObjectsHere() + " " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                Log.Warn("hook", "the fertilizer watch threw: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>Every plant this lawn read, as one line each, in the order the lawn was walked.</summary>
        private static string[] Lines(PlantState[] plants)
        {
            string[] lines = new string[plants.Length];

            for (int i = 0; i < plants.Length; i++)
            {
                PlantState one = plants[i];

                lines[i] = "obj #" + one.InstanceId + " type " + one.PlantType + " cell "
                    + one.Column + "," + one.Row + " hp " + one.Facts.Health + "/" + one.Facts.MaxHealth;
            }

            return lines;
        }

        /// <summary>How many fertilizers this lawn is showing, inactive pages and collected ones not counted.</summary>
        private static int ObjectsHere()
        {
            return ApiBase.Query("count the fertilizers on this lawn", () =>
            {
                Fertilize[] all = Resources.FindObjectsOfTypeAll<Fertilize>();
                int live = 0;

                for (int i = 0; all != null && i < all.Length; i++)
                {
                    Fertilize one = all[i];
                    if (!ApiBase.Present(one))
                    {
                        continue;
                    }

                    GameObject go = one.gameObject;
                    if (!ApiBase.Present(go) || !go.scene.IsValid() || !go.activeInHierarchy)
                    {
                        continue;
                    }

                    live++;
                }

                return live;
            });
        }
    }
}
