using System;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Plants. A plant only ever comes into existence through the game's own creator, because the
    /// creator is what attaches the native Animator and update chain; nothing here decides whether
    /// an action is legal, it only performs the native call and reports what came back.
    /// </summary>
    public static class Plants
    {
        public static bool CanPlace(int column, int row, int plantType)
        {
            return ApiBase.Query("CanPlace(" + column + "," + row + ",type=" + plantType + ")", () =>
            {
                Il2Cpp.CreatePlant creator = Il2Cpp.CreatePlant.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "CanPlace -> no CreatePlant instance");
                    return false;
                }

                return creator.CheckBox(column, row, (Il2Cpp.PlantType)plantType);
            }, "core-plant");
        }

        /// <summary>Unity instance id of the new plant, or Entities-level 0 when the native call failed.</summary>
        public static int Spawn(int column, int row, int plantType)
        {
            return ApiBase.Call("SpawnPlant(col=" + column + ",row=" + row + ",type=" + plantType + ")", () =>
            {
                Il2Cpp.CreatePlant creator = Il2Cpp.CreatePlant.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "SpawnPlant -> no CreatePlant instance");
                    return 0;
                }

                Il2Cpp.Plant plant = creator.SetPlant(column, row, (Il2Cpp.PlantType)plantType);
                if (!ApiBase.Present(plant))
                {
                    Log.Warn("api", "SpawnPlant -> native creator returned no object");
                    return 0;
                }

                Log.Api("SpawnPlant -> " + Entities.Describe(plant));
                return plant.GetInstanceID();
            }, "core-plant");
        }

        /// <summary>
        /// The game's own growth of one plant into another kind on the same cell. `Fertilize.Upgrade`
        /// destroys the old object and asks the creator for the new kind with `isFreeSet`, the flag that
        /// places a plant on a cell the game still counts as occupied; this is that body without its
        /// animation - the destroy at +0x172, then `SetPlant` with the free flag. Returns the instance
        /// id the creator named, 0 when nothing arrived.
        /// Proof: ref/EVIDENCE.md E56.
        /// </summary>
        public static int Grow(int instanceId, int column, int row, int plantType)
        {
            return ApiBase.Call("GrowPlant(#" + instanceId + ",col=" + column + ",row=" + row
                + ",type=" + plantType + ")", () =>
            {
                Il2Cpp.Plant old = Entities.FindPlant(instanceId);
                if (old == null)
                {
                    Log.Warn("api", "GrowPlant -> no plant #" + instanceId + " on the lawn");
                    return 0;
                }

                Il2Cpp.CreatePlant creator = Il2Cpp.CreatePlant.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "GrowPlant -> no CreatePlant instance");
                    return 0;
                }

                if (ApiBase.Present(old.gameObject))
                {
                    UnityEngine.Object.Destroy(old.gameObject);
                }

                Il2Cpp.Plant plant = creator.SetPlant(column, row, (Il2Cpp.PlantType)plantType,
                    null, default(UnityEngine.Vector2), true, true, null, null);
                if (!ApiBase.Present(plant))
                {
                    Log.Warn("api", "GrowPlant -> native creator returned no object");
                    return 0;
                }

                Log.Api("GrowPlant -> " + Entities.Describe(plant));
                return plant.GetInstanceID();
            }, "core-plant");
        }

        /// <summary>
        /// The game's own creator on a cell that holds nothing, with the free flag the growth body uses
        /// (E56). The cart's drop builds from the preview it carries, and a fused kind has no such preview:
        /// the measured type 1027 dropped from a cart has left every cell empty while the lift had already
        /// destroyed this lawn's object (E63). This is the creator call that does make the fused kind, and
        /// it destroys nothing, so a cell that already holds a plant is not this door's to ask about.
        /// </summary>
        public static int SpawnFree(int column, int row, int plantType)
        {
            return ApiBase.Call("SpawnFreePlant(col=" + column + ",row=" + row + ",type=" + plantType + ")",
                () =>
            {
                Il2Cpp.CreatePlant creator = Il2Cpp.CreatePlant.Instance;
                if (!ApiBase.Present(creator))
                {
                    Log.Warn("api", "SpawnFreePlant -> no CreatePlant instance");
                    return 0;
                }

                Il2Cpp.Plant plant = creator.SetPlant(column, row, (Il2Cpp.PlantType)plantType,
                    null, default(UnityEngine.Vector2), true, true, null, null);
                if (!ApiBase.Present(plant))
                {
                    Log.Warn("api", "SpawnFreePlant -> native creator returned no object");
                    return 0;
                }

                Log.Api("SpawnFreePlant -> " + Entities.Describe(plant));
                return plant.GetInstanceID();
            }, "core-plant");
        }

        /// <summary>
        /// Whether this plant's object is shown. A plant the room says is in a cart is hidden and not
        /// destroyed, so the record, its health and its level stay exactly as this lawn left them; the
        /// drop shows the same object again on its new cell.
        /// </summary>
        public static bool SetShown(int instanceId, bool shown)
        {
            return ApiBase.TryCall("ShowPlant(#" + instanceId + ",shown=" + shown + ")", () =>
            {
                Il2Cpp.Plant plant = Entities.FindPlant(instanceId);
                if (plant == null || !ApiBase.Present(plant.gameObject))
                {
                    Log.Warn("api", "ShowPlant -> no plant #" + instanceId + " on the lawn");
                    return;
                }

                plant.gameObject.SetActive(shown);
            }, "core-plant");
        }

        /// <summary>
        /// True only while this end runs the game's own click for the room, which is the one moment the
        /// placement gate has to stand down: the gate blocks this very method, and without the flag the mod
        /// would ask the room about a click it is already carrying out.
        /// </summary>
        public static bool IsClickingForTheRoom { get; private set; }

        /// <summary>
        /// True only while this end runs the game's own death for the room - the layout's removal of a
        /// plant the host stopped naming, or the host's shovel and mower arriving as a despawn. It is the
        /// one thing that lets `Plant.Die` run on a peer for a room plant, because a room plant dies on
        /// the host's word and the mod's own call is that word arriving.
        /// Proof: ref/EVIDENCE.md E73.
        /// </summary>
        public static bool ForTheRoom { get; private set; }

        /// <summary>
        /// The clicking end planting the way its own mouse would: the cell and the card are put on the
        /// game's input object first, then the game's own click runs. What a click hides stays with the
        /// click - the price read off the card, the cooldown, the use limit, the fusion of the cell.
        ///
        /// The cooldown is the reason this exists and <see cref="PlantFromCard">does not: the game writes
        /// `CardUI.CD` inside `Mouse.TryToSetPlantByCard` itself (GameAssembly rva 0x763B80, the stores at
        /// +0xF41 and +0xF55), together with `CardUI.UseOnce` and `CardUI.PutDown`. Calling the inner
        /// planting method instead left every card ready to fire again, which is a player planting as fast
        /// as the pool allows. Proof: ref/EVIDENCE.md E4, E5, E6.
        ///
        /// What came of the click is measured from the lawn and not trusted: the click can refuse for a
        /// reason the mod cannot read (the game's own affordability test reads `Board.theSun`), and a
        /// refusal has to look like one here rather than like a plant.
        /// </summary>
        public static int ClickPlant(int column, int row, int plantType)
        {
            return ApiBase.Call("ClickPlant(col=" + column + ",row=" + row + ",type=" + plantType + ")", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse))
                {
                    Log.Warn("api", "ClickPlant -> no Mouse instance");
                    return 0;
                }

                string noCard = HoldTheCard(mouse, plantType);
                if (noCard != null)
                {
                    Log.Warn("api", "ClickPlant -> " + noCard);
                    return 0;
                }

                mouse.theMouseColumn = column;
                mouse.theMouseRow = row;

                PlantState[] before = ReadAll();

                IsClickingForTheRoom = true;

                try
                {
                    mouse.TryToSetPlantByCard();
                }
                finally
                {
                    IsClickingForTheRoom = false;
                }

                int planted = MeasureClick(before, ReadAll(), column, row);

                return planted;
            }, "input");
        }

        /// <summary>
        /// The card the click spends. A player holding this type has it on the mouse already and the click
        /// spends that one; a confirmation for a card the player no longer holds - or holds on another type
        /// - is put on the mouse from this end's own bank, which is the game's object with the game's price
        /// and the game's cooldown on it.
        /// </summary>
        private static string HoldTheCard(Il2Cpp.Mouse mouse, int plantType)
        {
            Il2Cpp.CardUI held = mouse.theCardOnMouse;

            if (ApiBase.Present(held) && (int)held.thePlantType == plantType)
            {
                return null;
            }

            Il2Cpp.CardUI bank = CardBank.Find(plantType);
            if (!ApiBase.Present(bank))
            {
                return "no card of type " + plantType + " in this bank, " + CardBank.Describe(plantType);
            }

            mouse.theCardOnMouse = bank;

            return null;
        }

        /// <summary>
        /// The plant the click put on the cell: an object that was not on the lawn a moment ago, or the
        /// object standing there with another type on it, which is what a fusion in place looks like.
        /// Anything else is a click that refused, and 0 says so.
        /// </summary>
        private static int MeasureClick(PlantState[] before, PlantState[] after, int column, int row)
        {
            for (int i = 0; i < after.Length; i++)
            {
                PlantState now = after[i];

                if (now.InstanceId == 0 || now.Column != column || now.Row != row)
                {
                    continue;
                }

                int was = Find(before, now.InstanceId);
                if (was < 0)
                {
                    return now.InstanceId;
                }

                if (before[was].PlantType != now.PlantType)
                {
                    Log.Warn("api", "ClickPlant -> the game changed plant #" + now.InstanceId + " in place"
                        + " from type " + before[was].PlantType + " to type " + now.PlantType);

                    return now.InstanceId;
                }
            }

            return 0;
        }

        private static int Find(PlantState[] states, int instanceId)
        {
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].InstanceId == instanceId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The game's own planting from a card, with the cell and the type the room agreed on instead of
        /// the ones under the local pointer. This is the inner half of the click: the plant appears, the
        /// card does not know it was spent, so nothing here is asked to pay a price or start a cooldown.
        /// It stays because it is the door the game's own click walks after its checks, and because a lawn
        /// that cannot hold a card has no other way to say what the room agreed to.
        /// </summary>
        public static int PlantFromCard(int column, int row, int plantType)
        {
            return ApiBase.Call("PlantFromCard(" + column + "," + row + ",type=" + plantType + ")", () =>
            {
                Il2Cpp.Mouse mouse = Il2Cpp.Mouse.Instance;
                if (!ApiBase.Present(mouse))
                {
                    Log.Warn("api", "PlantFromCard -> no Mouse instance");
                    return 0;
                }

                Il2Cpp.Plant plant = mouse.SetPlantFromCard(column, row, (Il2Cpp.PlantType)plantType);
                if (!ApiBase.Present(plant))
                {
                    Log.Warn("api", "PlantFromCard -> the game's card path returned no object");
                    return 0;
                }

                Log.Api("PlantFromCard -> " + Entities.Describe(plant));
                return plant.GetInstanceID();
            }, "input");
        }

        /// <summary>
        /// The game's own `Plant.MoveBox`: the glove's data change for a plant that kept its object.
        /// The method moves the plant's box and its column/row, so the next read of the lawn sees the
        /// same cell the host sent.
        /// </summary>
        public static bool MoveBox(int instanceId, int column, int row)
        {
            return ApiBase.TryCall("MovePlant(#" + instanceId + ",cell=" + column + "," + row + ")", () =>
            {
                Il2Cpp.Plant plant = Entities.FindPlant(instanceId);
                if (plant == null)
                {
                    Log.Warn("api", "MovePlant -> no plant #" + instanceId + " on the lawn");
                    return;
                }

                plant.MoveBox(column, row);

                // `Plant.MoveBox` updates the grid cell and the plant's box, but on a peer it can
                // leave the sprite at the old cell until the plant's own position update runs.
                // Put the visual object on the target cell immediately as well.
                Il2Cpp.GridManager grid = Il2Cpp.GridManager.Instance;
                if (ApiBase.Present(grid))
                {
                    plant.AdjustPosition(grid.GetGridCenter(column, row));
                }
            }, "core-plant");
        }

        /// <summary>reason is the int value of Plant.DieReason; 0 is Default in this build.</summary>
        public static bool Kill(int instanceId, int reason)
        {
            return ApiBase.TryCall("KillPlant(#" + instanceId + ",reason=" + reason + ")", () =>
            {
                Il2Cpp.Plant plant = Entities.FindPlant(instanceId);
                if (plant == null)
                {
                    Log.Warn("api", "KillPlant -> no plant #" + instanceId + " on the lawn");
                    return;
                }

                ForTheRoom = true;

                try
                {
                    plant.Die((Il2Cpp.Plant.DieReason)reason);
                }
                finally
                {
                    ForTheRoom = false;
                }
            }, "core-plant");
        }

        /// <summary>
        /// The game's own heal of one plant on this lawn. `Plant.Recover` takes an amount and not a target,
        /// so the amount asked for is what this end's own numbers say the plant is short of (E48); a plant
        /// that is already whole is left exactly as it stands. Returns the health put back, 0 when nothing
        /// was missing or this lawn has no such plant.
        /// </summary>
        public static int Heal(int instanceId)
        {
            return ApiBase.Call("HealPlant(#" + instanceId + ")", () =>
            {
                Il2Cpp.Plant plant = Entities.FindPlant(instanceId);
                if (plant == null)
                {
                    Log.Warn("api", "HealPlant -> no plant #" + instanceId + " on the lawn");
                    return 0;
                }

                int missing = plant.thePlantMaxHealth - plant.thePlantHealth;
                if (missing <= 0)
                {
                    return 0;
                }

                plant.Recover((float)missing, (Il2Cpp.DamageType)0, true, false);

                Log.Api("HealPlant -> " + Entities.Describe(plant) + " asked for " + missing
                    + " health of " + plant.thePlantMaxHealth);

                return missing;
            }, "core-plant");
        }

        public static bool TryRead(int instanceId, out PlantState state)
        {
            Il2Cpp.Plant plant = ApiBase.Query("ReadPlant(#" + instanceId + ")",
                () => Entities.FindPlant(instanceId), "core-plant");

            if (plant == null)
            {
                state = default(PlantState);
                return false;
            }

            state = new PlantState
            {
                InstanceId = instanceId,
                PlantType = (int)plant.thePlantType,
                Row = plant.thePlantRow,
                Column = plant.thePlantColumn,
                Facts = FactsOf(plant)
            };
            return true;
        }

        public static PlantState[] ReadAll()
        {
            return ApiBase.Query("ReadAllPlants", () =>
            {
                var found = new System.Collections.Generic.List<PlantState>();

                Entities.ForEachPlant(plant =>
                {
                    try
                    {
                        found.Add(new PlantState
                        {
                            InstanceId = plant.GetInstanceID(),
                            PlantType = (int)plant.thePlantType,
                            Row = plant.thePlantRow,
                            Column = plant.thePlantColumn,
                            Facts = FactsOf(plant)
                        });
                    }
                    catch (Exception e)
                    {
                        // One object mid-destruction must not cost the census the rest of the lawn.
                        Log.Warn("api", "ReadAllPlants skipped a plant: " + e.GetType().Name + " "
                            + e.Message);
                    }
                });

                return found.ToArray();
            }, "core-plant");
        }

        /// <summary>
        /// Everything the room keeps about a plant besides its kind and cell. Every field is the game's
        /// own, read in one place so the layout, the tests' rules and this read cannot disagree.
        /// Proof: ref/EVIDENCE.md E72.
        /// </summary>
        private static PlantFacts FactsOf(Il2Cpp.Plant plant)
        {
            PlantFacts facts = default(PlantFacts);

            facts.Health = plant.thePlantHealth;
            facts.MaxHealth = plant.thePlantMaxHealth;
            facts.Shield = plant.theShieldHealth;
            facts.Level = plant.theLevel;
            facts.Stage = plant.thePlantStage;
            facts.Skin = plant.skinType;
            facts.Magnet = plant.magnetCount;
            facts.Wheat = plant.wheatType;
            facts.ShootLevel = plant.shootingLevel;
            facts.ShootCurse = plant.shootingCurse;
            facts.Kills = plant.killCount;
            facts.Puff = plant.puffPlace;
            facts.Butter = plant.butterP;
            facts.Attribute = plant.attributeCount;
            facts.Disable = plant.disableCount;
            facts.Freeze = plant.freezeLevel;
            facts.FirstParent = (int) plant.firstParent;
            facts.SecondParent = (int) plant.secondParent;
            facts.LilyType = (int) plant.theLilyType;
            facts.Flags = PlantFactsRules.Pack(plant.invincible, plant.undead, plant.isLily,
                plant.imitatless, plant.keepShooting, plant.uncrashable, plant.melonSputter);

            return facts;
        }

        public const string FactsCapability = "mp-plant-facts";

        /// <summary>
        /// Make this lawn's plant show the host's numbers. The health difference goes through the game's
        /// own heal and damage doors, a higher level through `Plant.Upgrade`, and the rest are the game's
        /// own fields - the same ones its behaviour and its bar read. Nothing is done when the plant
        /// already matches, so a lawn that agrees costs no native call. Returns true when anything moved.
        /// Proof: ref/EVIDENCE.md E72.
        /// </summary>
        public static bool AlignFacts(int instanceId, PlantFacts wanted)
        {
            return ApiBase.Call("AlignPlantFacts(#" + instanceId + ")", () =>
            {
                Il2Cpp.Plant plant = Entities.FindPlant(instanceId);
                if (plant == null)
                {
                    return false;
                }

                // A host number the game could not read arrives as zero, and zero is not a fact.
                if (wanted.MaxHealth <= 0)
                {
                    return false;
                }

                bool changed = false;

                if (wanted.Level > plant.theLevel)
                {
                    changed |= plant.Upgrade(wanted.Level, true, false);
                }

                if (plant.theLevel != wanted.Level)
                {
                    plant.theLevel = wanted.Level;
                    changed = true;
                }

                if (plant.thePlantStage != wanted.Stage)
                {
                    plant.thePlantStage = wanted.Stage;
                    changed = true;
                }

                if (plant.skinType != wanted.Skin)
                {
                    plant.skinType = wanted.Skin;
                    changed = true;
                }

                if (plant.thePlantMaxHealth != wanted.MaxHealth)
                {
                    plant.thePlantMaxHealth = wanted.MaxHealth;
                    changed = true;
                }

                int amount;
                switch (PlantFactsRules.Between(plant.thePlantHealth, wanted.Health, out amount))
                {
                    case HealthStep.Heal:
                        plant.Recover(amount, (Il2Cpp.DamageType) 0, false, false);
                        changed = true;
                        break;

                    case HealthStep.Damage:
                        plant.RealTakeDamage(amount);
                        changed = true;
                        break;
                }

                // The doors above may apply modifiers of their own; the host's number is the answer.
                if (plant.thePlantHealth != wanted.Health)
                {
                    plant.thePlantHealth = wanted.Health;
                    changed = true;
                }

                if (plant.theShieldHealth != wanted.Shield)
                {
                    plant.theShieldHealth = wanted.Shield;
                    changed = true;
                }

                TakeNumber(ref changed, plant.magnetCount, wanted.Magnet, v => plant.magnetCount = v);
                TakeNumber(ref changed, plant.wheatType, wanted.Wheat, v => plant.wheatType = v);
                TakeNumber(ref changed, plant.shootingLevel, wanted.ShootLevel,
                    v => plant.shootingLevel = v);
                TakeNumber(ref changed, plant.shootingCurse, wanted.ShootCurse,
                    v => plant.shootingCurse = v);
                TakeNumber(ref changed, plant.killCount, wanted.Kills, v => plant.killCount = v);
                TakeNumber(ref changed, plant.puffPlace, wanted.Puff, v => plant.puffPlace = v);
                TakeNumber(ref changed, plant.butterP, wanted.Butter, v => plant.butterP = v);
                TakeNumber(ref changed, plant.attributeCount, wanted.Attribute,
                    v => plant.attributeCount = v);
                TakeNumber(ref changed, plant.disableCount, wanted.Disable,
                    v => plant.disableCount = v);
                TakeNumber(ref changed, plant.freezeLevel, wanted.Freeze,
                    v => plant.freezeLevel = v);

                int flags = wanted.Flags;
                TakeFlag(ref changed, plant.invincible,
                    PlantFactsRules.Has(flags, PlantFactsRules.Invincible), v => plant.invincible = v);
                TakeFlag(ref changed, plant.undead,
                    PlantFactsRules.Has(flags, PlantFactsRules.Undead), v => plant.undead = v);
                TakeFlag(ref changed, plant.isLily,
                    PlantFactsRules.Has(flags, PlantFactsRules.IsLily), v => plant.isLily = v);
                TakeFlag(ref changed, plant.imitatless,
                    PlantFactsRules.Has(flags, PlantFactsRules.Imitatless), v => plant.imitatless = v);
                TakeFlag(ref changed, plant.keepShooting,
                    PlantFactsRules.Has(flags, PlantFactsRules.KeepShooting), v => plant.keepShooting = v);
                TakeFlag(ref changed, plant.uncrashable,
                    PlantFactsRules.Has(flags, PlantFactsRules.Uncrashable), v => plant.uncrashable = v);
                TakeFlag(ref changed, plant.melonSputter,
                    PlantFactsRules.Has(flags, PlantFactsRules.MelonSputter), v => plant.melonSputter = v);

                if ((int) plant.firstParent != wanted.FirstParent)
                {
                    plant.firstParent = (Il2Cpp.PlantType) wanted.FirstParent;
                    changed = true;
                }

                if ((int) plant.secondParent != wanted.SecondParent)
                {
                    plant.secondParent = (Il2Cpp.PlantType) wanted.SecondParent;
                    changed = true;
                }

                if ((int) plant.theLilyType != wanted.LilyType)
                {
                    plant.theLilyType = (Il2Cpp.PlantType) wanted.LilyType;
                    changed = true;
                }

                // The bar is the game's own view of these two numbers; a plant that already matched
                // must cost nothing at all, so the bar is touched only when something moved.
                Il2Cpp.HealthSlider bar = plant.healthSlider;
                if (changed && ApiBase.Present(bar))
                {
                    bar.HealthValue = plant.thePlantHealth;
                    bar.ShieldValue = plant.theShieldHealth;
                }

                return changed;
            }, FactsCapability);
        }

        private static void TakeNumber(ref bool changed, int here, int wanted, Action<int> write)
        {
            if (here == wanted)
            {
                return;
            }

            write(wanted);
            changed = true;
        }

        private static void TakeFlag(ref bool changed, bool here, bool wanted, Action<bool> write)
        {
            if (here == wanted)
            {
                return;
            }

            write(wanted);
            changed = true;
        }
    }
}
