# EVIDENCE - native mechanics, with proof and how to reproduce

Every row: `symbol -> RVA -> size -> what it proves`. Tools are `tools/re/`
(run with `py -3`, see `tools/re/README.md`). RVAs/sizes come from
`ref/dump-en/script.json` (`ScriptMethod[].Address`, decimal) and field offsets
from `ref/dump-en/dump.cs`.

Build: en-4.1, `GameAssembly.dll`, `Assembly-CSharp`. Addresses are stable for
the identical `D:\PvzFusion2` install.

Status tags (never present unverified as verified):
- **[P]** re-confirmed by `tools/re/` in this session (Nov 2026).
- **[C]** carried from prior disassembly (spec section 8): RVA/size and field
  offsets re-checked here, but the byte-level body semantics were NOT
  independently re-derived this session. Reproduce the exact bytes with the
  listed command before building new logic on the claim.

---

## 1. Battle start

**E1. `InGameUI.OnStartBattleButtonClick`** - rva `0x7D67C0`, size 192 **[P]**
- Has ZERO static callers in the image.
- Proves: gating only this method never fires; the real "Let's Rock" click is
  `InGameBtn.OnMouseUpAsButton`.
- Repro: `py -3 tools/re/pvz-xref.py "InGameUI$$OnStartBattleButtonClick"` ->
  "no static caller found".

**E2. `InGameBtn.OnMouseUpAsButton`** - rva `0x7D2FE0`, size 1936 **[P/C]**
- Reads `Mouse.buttonNumber` ([rbx+0x28]), compares `cmp eax,0x13` (19) at
  body+`0x13C`, then a 20-entry jump table; case 4 (+`0x474`) runs the four
  battle steps; writes `timeUntilNextWave` = 3.0f at body+`0x502`
  (`mov dword [rax+0x18C],0x40400000`).
- Proves: `BattleAnswer.StartBattleButton = 4` (button index that starts a
  wave-based level).
- Repro (byte claims, [P] this session):
  `py -3 tools/re/pvz-pat.py "83F813"` -> `...OnMouseUpAsButton+0x13C`
  `py -3 tools/re/pvz-pat.py "C7808C01000000004040"` -> `+0x502`
  (case-4 body layout is [C]; read `pvz-hex.py "InGameBtn$$OnMouseUpAsButton"`).

**E3. `PauseMenu_Btn.Restart`** - rva `0x7E3380`, size 416 **[P]**
- Calls `Board.ClearTheBoard` (+`0xD1`), `UIResourcesLoader.PopAll` (+`0x10E`),
  `Board.Die` (+`0x12B`), then `UIMgr.EnterGame` (+`0x18B`, id = -1). Before
  that, `HoverGardenManager.get_IsLevel` (+`0x4B`) branches the garden case to
  `HoverGardenManager.RestartRun` (tail jump at +`0xB0`, a coroutine start).
- Proves: the game's restart destroys the board itself before it enters the
  level; the earlier reading "a restart must go through `UIMgr.EnterMainMenu`"
  was wrong - E16 shows the menu destroys nothing. A peer that was allowed to
  run this would lose the room's lawn without loading anything, which is why
  `RestartGate` refuses it there.
- Repro: `py -3 tools/re/pvz-disasm.py "PauseMenu_Btn$$Restart"`.

**E16. What actually takes a lawn down, and what does not** **[P]**
- `Board.ClearTheBoard` rva `0x475C60`, size 400: reads level type
  `[board+0x9C]`; only 3 and 10 reach `SaveMgr.ClearBoard` (+`0x17F`); every
  other type, including 0, returns at +`0x18E` having cleared nothing. It never
  touches a plant. So the user-facing name "ClearTheBoard" clears the level SAVE,
  not the lawn.
- `BoardGrid.ClearPlants` rva `0x45F870`, size 400: `Array.Clear` x2 and a
  dictionary clear. Arrays only - no `Object.Destroy`.
- `Board.Die` rva `0x476FA0`, size 944: `Object.Destroy(this.gameObject)` at
  +`0x11E`, `EventManager.ClearAllEvents` +`0x139`, `Array.Clear` x3,
  `BoardGrid.ClearPlants` +`0x2F2`, `TravelMgr.Clear` +`0x37C`. Destroying the
  board root is what removes the standing plants.
- `UIMgr.EnterGame` rva `0x8E11D0`, size 3344: `Push` + `Instantiate` + `Push`;
  it never kills the board that is already up.
- `UIMgr.EnterMainMenu` rva `0x8E2B90`, size 688: `Pop`, camera reset,
  `timeScale`, music, `InGameText`, `Push` x2 - no board destroy.
- Live proof of the last two: peer log `26-11-11_6-31-47.log` 06:33:06
  `still standing in level type -1 level -1 with 10 plant(s)` after
  `UIMgr.EnterMainMenu`, and 06:33:11 `the menu did not take the old lawn down
  within 5000ms` - the 10 plants stood through the whole wait. Loading
  `EnterGame` over them is the reported defect.
- Proves: a peer told to play the host's level must replay the restart tail on
  the lawn it is standing in - `ClearTheBoard` -> `PopAll` -> `Die` -> `EnterGame`
  (`LevelSync.ClearThisLawn`) - and a peer must not be allowed to run
  `PauseMenu_Btn.Restart` itself, because that tail would fire before its
  `EnterGame` reaches `LevelGate` (`RestartGate`).
- Repro: `py -3 tools/re/pvz-disasm.py 'Board$$ClearTheBoard' 'Board$$Die'
  'BoardGrid$$ClearPlants' 'UIMgr$$EnterGame' 'UIMgr$$EnterMainMenu'`.

## 2. Placement / cooldown

**E4. `Mouse.TryToSetPlantByCard`** - rva `0x763B80`, size 4400 **[C]**
- Writes `CardUI.CD` directly in its tail: `CD=0` at +`0xF14`, `CD=max(0,
  fullCD-7.5f)` at +`0xF1A..+0xF48` when the planted type is `0x3ED`, and
  `CD=fullCD` at +`0xF5C` on the travel path. Exact order and guards: E15.
- Proves: the ONLY placement path that yields cooldown, price, use-limit and
  merge is the whole `TryToSetPlantByCard`. Reproduce bytes with
  `pvz-hex.py "Mouse$$TryToSetPlantByCard"` (disp32 store search does not match
  here; read the hex around +0xF14).
- Only caller: `Mouse.LeftClickWithSomeThing+0xE10`; the caller jumps straight to
  its epilogue after the call, so this method owns the whole card tail.

**E5. `Mouse.SetPlantFromCard`** - rva `0x75E110`, size 240 **[P]**
- Calls ONLY `TravelRogueCardData.Snapshot` and `CreatePlant.SetPlant` (the call
  site is body+`0x0CA`). No CheckBox, no price check, no CD write.
- Proves: `SetPlantFromCard` bypasses cooldown/price/use-limit (E4/E6 own those)
  but NOT the fusion: the one method it calls is `CreatePlant.SetPlant`, and E12
  proves SetPlant reaches `CheckMix`. It is a usable creator for the room, but it
  can neither pay for a plant nor start its cooldown.
- Repro: `py -3 tools/re/pvz-disasm.py "Mouse$$SetPlantFromCard"` ->
  `+0x0CA -> CreatePlant$$SetPlant`.

**E6. `CardUI.UseOnce`** - rva `0x43F850`, size 224 **[C]** and
  `CardUI.PutDown` - rva `0x43E9A0`, size 256 **[C]**
- `UseOnce` only `inc [rbx+0x4C]` (usedTimes) and writes Durability (0xD4).
  `PutDown` compares theSeedCost (0x24) to `Board.theSun` (0x110) and sets
  isPickUp=false (0x57). Neither touches `CD`.
- Proves: cooldown is not produced by UseOnce/PutDown; only E4 writes CD.
- RVAs/sizes/offsets [P]; body semantics [C] (`pvz-disasm.py "CardUI$$UseOnce"`).

**E15. The card's recharge, and what a room placement must repeat** - `0x763B80` size 4400 **[C]**
- Readiness: +`0x3EA` loads `Mouse.theCardOnMouse` (`[mouse+0x58]`); +`0x3F7`
  `movss xmm0,[card+0x48]` (fullCD), +`0x3FA` `comiss xmm0,[card+0x44]` (CD),
  +`0x400` `ja` -> epilogue. +`0x419` `cmp [board+0x110]` (theSun) with
  `card.theSeedCost` (0x24), `jl` -> the same epilogue. Proves: the game refuses a
  recharging or unaffordable card before it creates anything and changes nothing
  when it does.
- Direction: `CardUI.CDUpdate` (rva `0x43D210`, size 944) adds `deltaTime` to `CD`
  until `fullCD`; the card is ready when `CD >= fullCD`. Starting a recharge means
  setting `CD` LOW, not high.
- Tail after the plant exists (normal branch, `board+0x286 == 0`): sun payment
  `Board.UseSun(card.theSeedCost)` at +`0xE23`; recharge at +`0xEFA..+0xF48`: the
  planted type `[plant+0x1AC] == 0x3ED` (`PlantType.SuperCherryShooter` 1005) ->
  `CD = max(0, fullCD - 7.5f)` (float 7.5 at RVA `0x2A9CC70`), otherwise `CD = 0`
  (+`0xF14`); travel flag `[rsp+0x108] != 0` -> `CD = fullCD` (+`0xF5C`). Then
  +`0xFC0` `CardUI.UseOnce`, +`0xFD4` `CardUI.PutDown`, +`0x10B0`
  `Object.Destroy(mouse[0x50])`, +`0x10BD` `Mouse.ClearItemOnMouse(false)`; the
  travel path clears with `true` at +`0x10DE`.
- `Mouse.ClearItemOnMouse(bool)` rva `0x756880`, size 304: `true` destroys
  `[mouse+0x50]` first; both values clear the card reference (`[mouse+0x58]`) and
  the cell fields. So `ClearItemOnMouse(true)` equals Destroy + clear(false).
- Proves: `CardUse.Spend` repeats exactly those steps for a room placement, on the
  card the click carried, and `CardUse.Recharging` uses the same comparison.
- Not repeated, measured as separate branches: travel (`board+0x286 != 0`) does
  `CardUI.Die` + `ConsumeCardEquipmentUse` (+`0xD14..+0xDE7`); a card with
  `thePlantLevel` (0x30) != -1 calls `Plant.Upgrade` (+`0xF73..+0xF93`); and
  `board+0x282 != 0` removes the card instead of UseOnce/PutDown (+`0xFF8..+0x1048`).
  The room is exercised on ordinary lawns; if one of these ever fires in a room its
  effect is missing, and the `SpendCard` log line is the anchor to reopen this entry.
- Repro: disassemble from the method start (an interior offset desyncs); read the
  float at RVA `0x2A9CC70`.

## 3. Coins (single mint door)

**E7. `CreateItem.SetCoin(int col,int row,int itemType,int moveType,Vector3 pos,bool freeSet=false)`**
- rva `0x44D130`, size 1184 **[P]**
- Has 204 static call-sites (the largest concentration: `Board.SunUpdate` x8,
  `CreatePlant.CheckMix` x12+, bullets, graves, dropped cards, ...).
- Proves: this is the ONE practical "door of the mint". Every coin in the game
  comes from here, so a single prefix gate here can reject a peer's mint.
- Repro: `py -3 tools/re/pvz-xref.py "CreateItem$$SetCoin"` -> "204".

**E8. `Board.SunUpdate`** - rva `0x484DC0`, size 1248 **[P]**
- Calls `CreateItem.SetCoin` exactly 8 times (sky-sun rolls) plus only
  `Time.get_deltaTime` x2, `Object.op_Inequality`, `Random.Range`.
- Proves: nothing else meaningful happens in SunUpdate, so its timer may run on
  EVERY peer safely; for sky sun `theRow=-1`, type 0, move 1, coordinate is in
  `Vector3 pos` (column must be recomputed via `Lawnf.GetColumnFromX`).
- Repro: `py -3 tools/re/pvz-disasm.py "Board$$SunUpdate"` (8 SetCoin lines).

**E9. `CoinSun.Die`** - rva `0x44B860`, size 96 **[P]**
- Does ONLY `Component.get_gameObject` + `Object.Destroy`; credits NOTHING.
- Proves: "coin died" != "coin collected"; removing a coin without paying is
  exactly this `Die`.
- Repro: `py -3 tools/re/pvz-disasm.py "CoinSun$$Die"`.

**E10. `CoinSun.MoveToPosition`** - rva `0x44B8C0`, size 1104 **[P]**
- At body+`0x3BE` calls `Board.GetSun(float sunPrice, bool save)`
  (xmm = sunPrice from [rbx+0x28], r8 = save from [rbx+0x30]), then `Destroy`.
- Proves: the credit point is `MoveToPosition` at +0x3BE, not `Die`.
- Repro: `py -3 tools/re/pvz-disasm.py "CoinSun$$MoveToPosition"` ->
  `...+0x3BE -> Board$$GetSun` then `+0x406 -> Object$$Destroy`.

**E11. `CoinSun.PositionUpdate`** - rva `0x44BD10`, size 496 **[P]**
- Dispatcher: on `target` (0x40) equality it branches to `Die` (body+`0x1D0`)
  else to `MoveToPosition` (body+`0x1E4`).
- Proves: the choice between paid pickup and expiry is made here.
- Repro: `py -3 tools/re/pvz-disasm.py "CoinSun$$PositionUpdate"`.

**E13. `Board.SunUpdate` ignores the `SetCoin` return** **[P]**
- At each of the 8 `CreateItem.SetCoin` call sites the instruction right after
  the `E8` call is `48 8B 05 <disp32>` (`mov rax,[rip+...]`): RAX (the returned
  GameObject) is overwritten before any use, so the return is never dereferenced.
- Proves: returning `null` from a blocked `SetCoin` is safe for this one caller
  but the other 203 are unaudited, so a coin gate must not rely on a null return.
  The design keeps a peer's coins for the picture (they are its own rolls) and
  refuses their payment instead: the credit point `Board.GetSun` (E10) is blocked
  on a peer, so a peer's counter moves only when the host's absolute value lands.
- Repro: `py -3 tools/re/pvz-hex.py "Board$$SunUpdate"` (read +0x23F,+0x28D,...).

## 4. Merge (fusion)

**E12. `CreatePlant.SetPlant`** - rva `0x455A00`, size 5712 **[P]**;
  `CreatePlant.CheckMix` - rva `0x44F2C0`, size 8688 **[P]**
- `CheckMix` (reached from `SetPlant`) calls `CreatePlant.SetPlant` 4x (the new
  plants on the cells), `CreateItem.SetCoin` 12+ times (sun flash),
  `Plant.set_AttributeCountdown`, and `CreatePlant.MixFail`.
- Proves: the mod must NOT implement merge and must NOT refuse a merge; it must
  stop refusing on a busy cell and rebroadcast the measured result. The eaten
  plant leaves via its own death with `Plant.DieReason.ByLevelUp = 4`.
- `CreatePlant.CheckBox(col,row,type)` - rva `0x44EFB0`, size 784 - returns true
  on a plain same-type overlap too, so CheckBox is NOT a merge signal.
- Repro: `py -3 tools/re/pvz-disasm.py "CreatePlant$$CheckMix"` -> SetPlant x4
  (+0xDD6,+0xE9E,+0xECC,+0xFC9), SetCoin x15+ visible (body capped at 8192 of
  8688 bytes).

## 5. Shovel (two steps: select, then dig)

**E14. `Shovel.OnClick(Mouse)`** - rva `0x7DD0D0`, size 256 **[P]**
- Calls only `Object.op_Equality`, `Component.get_gameObject`, `GameAPP.PlaySound`,
  `InGameTool.PickUp` (`0x7D4470`, size 96). No seam, no plant read, no hole.
- Proves: key "1" only picks the shovel up. A detour on `OnClick` turns the
  selection into whatever the prefix decides, which is why pressing "1" dug.

**E14b. `Shovel.Use(Mouse)`** - rva `0x7DD710`, size 1744 **[P]**
- Body order: `InGameTool.PutDown` (+0x10F), `Mouse.ClearItemOnMouse` (+0x125,
  arg 0 = keep the item flags), `Mouse.GetPlantsOnMouse` (+0x12F),
  `Physics2D.RaycastAll`, `ZenGarden.GardenPlant.Die`, `GameAPP.PlaySound`,
  `Shovel.ReturnPlantToGarden`, `AdvantureData.CheckTalent`,
  `CreateItem.SetCoin` x2 (refund), `Lawnf.GetUpgradedPlantCost`.
- Proves: the left click is the dig, and the same body first puts the tool back
  in place. A blocked `Use` never reaches those two calls, so a room that stops
  the dig must call `Shovel.PutDown()` and `Mouse.ClearItemOnMouse(false)` itself
  or the peer keeps holding the shovel.
- `InGameTool.PutDown` - rva `0x7D44D0`, size 160 - `Behaviour.set_enabled`,
  `get_transform`, `get defaultParent`'s transform, `Transform.set_parentInternal`,
  `set_localPosition` - the literal "shovel returns to its slot".
- `InGameTool.PickUp` / `PutDown` write `isPickUp` (0x2C) = 1 / 0. `Mouse.mouseItemType`
  (0x60) has no `Shovel` write in this build: the only constant store is
  `Mouse.ClickOnCard+0x2B0` (`mov dword [mouse+0x60], 0`). Proves "shovel is in hand" is
  `Shovel.Instance.isPickUp`, and a preview waiting for `mouseItemType == Shovel` never draws.
- Dump: `public class Shovel : InGameTool` (`Instance` static), `InGameTool :
  MonoBehaviour, IClickable` with public `OnClick(Mouse)`, `Use(Mouse)`,
  `PickUp()`, `PutDown()`; `Mouse.ClearItemOnMouse(bool clearItem = False)`.
- Repro: `py -3 tools/re/pvz-disasm.py 'Shovel$$OnClick' 'Shovel$$Use' 'InGameTool$$PutDown'`
  (single quotes: bash expands `$$` inside double quotes).

## 6. Pause and resume

**E17. The two doors of a room's pause** **[P]**
- `UIMgr.EnterPauseMenu` rva `0x8E2F90`, size 192: `GameAPP.PlaySound` +`0x4D`,
  `GameAPP.theGameStatus` (`[GameAPP+0xB8]` -> `[+0xA8]`) = 1 at +`0x65`,
  `Time.set_timeScale(0)` +`0x6F`, `UIResourcesLoader.Push(UIType 16 = PauseMenu)`
  at +`0xA3` (only when the loader is present; otherwise it returns at +`0x74`).
- `UIMgr.BackToGame` rva `0x8E02A0`, size 720: `UIResourcesLoader.Pop` +`0x81`,
  `HoverGardenManager.get_IsLevel` +`0x88` branches the garden case, `timeScale`
  restore +`0x17F`; then `cmp [theGameStatus], -2` (OpenOptions) at +`0x192`
  returns early at +`0x138`, otherwise status = 0 at +`0x1B7`, clock restored
  +`0x1E6`, `AudioSource.UnPause` x3. The garden branch ends in a tail jump to
  `HoverGardenManager.KeepPanelPaused` (+`0x133`).
- Static callers: `EnterPauseMenu` <- `Mouse.Update` +`0x689`,
  `IZBottomMenu.PauseGame` +`0x22`, `IZBottomMenu.Update` +`0x2A3`,
  `InGameUI.LookTalent` +`0x22`, `InGameUI.PauseGame` +`0x72`. `BackToGame` <-
  `PauseMenu_Btn.OnMouseUp` +`0x643`, `PauseMenu.Update` +`0x4A`. Two methods
  therefore cover every pause and resume entry the game has, which is why
  `PauseGate` patches exactly these two.
- Proves: a room-wide pause is `EnterPauseMenu` (status 1, clock 0, menu pushed)
  and lifting it is `BackToGame` (pop, clock, status 0). Neither takes a board,
  so an end without a lawn must ignore the order - and pause state is readable
  as `GameAPP.theGameStatus == 1` (`GameStatus.Pause`), which is what makes the
  room idempotent.
- Repro: `py -3 tools/re/pvz-disasm.py 'UIMgr$$EnterPauseMenu' 'UIMgr$$BackToGame'`
  and `py -3 tools/re/pvz-xref.py 'UIMgr$$EnterPauseMenu' 'UIMgr$$BackToGame'`.

**E17b. What "Let's Rock" actually does, at every door** **[P]**
- The chooser's LET'S ROCK is `StartGameBtn.OnMouseUp` rva `0x81D350`, size 1008
  (this is the button drawn on the "Choose Your Plants" screen):
  `StartGameBtn.clicked` guard (`[this+0x50]`) at +`0xE5`; `StartGameBtn.SaveCards`
  +`0x0F0`; buff cost `TreasureData.GetBuffCost` +`0x287` and the static money
  paid at +`0x2AF..+0x2CA`; `SaveInfo.SavePlayerData` +`0x2E9`; the button put
  back at its origin +`0x31D`; **`InitBoard.Instance.ready = 1`** at +`0x33C`
  (class -> `static_fields` +`0xB8` -> `Instance` +`0` -> byte +`0x20`, and
  `InitBoard.ready` is the bool at +`0x20` that `InitBoard.WaitPlayerSelect`
  polls at state-machine +`0x7A`); `GameAPP.PlaySound` +`0x382`; the refusal
  text through `InGameText.ShowText` +`0x3BF`.
- Live proof that the two doors below are not this one: in the pair run
  `26-11-11_12-32-22.log` the client loaded the host's level and played through
  LET'S ROCK and beyond, and the whole log has not one `[mp]` battle line - no
  gate spoke, so the press never reached `InGameBtn.OnMouseUpAsButton` or
  `InGameUI.OnStartBattleButtonClick`.
- `InGameBtn.OnMouseUpAsButton` case `buttonNumber` 4 (body +`0x474`): refuses
  unless `theGameStatus == 0` (+`0x49A`), `GameObject.SetActive(false)` +`0x4C2`,
  `Board[+0x29C] = 0` +`0x4E1`, `Board.timeUntilNextWave = 3.0f` +`0x502`.
- `InGameUI.OnStartBattleButtonClick` rva `0x7D67C0`, size 192: the same three
  steps - status check +`0x57`, `SetActive(false)` +`0x71`, `[Board+0x29C] = 0`
  +`0x8C`, `[Board+0x18C] = 3.0f` +`0xA9` - and still zero static callers (E1).
- Proves: the room must gate the chooser's press where the click lands, and
  when it replays the host's press on a peer it must call that same
  `StartGameBtn.OnMouseUp` on the peer's button (the body is what saves the
  cards, pays the buff and ends the chooser). The other two doors stay gated as
  the in-level way to the same order; `BattleGate` still takes button 4 only and
  leaves the other 19 buttons alone.
- Repro: `py -3 tools/re/pvz-disasm.py 'StartGameBtn$$OnMouseUp'
  'InitBoard.<WaitPlayerSelect>d__19$$MoveNext'`.
  The `ready` write is also the only `c6402001` site in the image:
  `py -3 tools/re/pvz-pat.py "C6402001"`.

## 7. Zombies, waves and mowers

**E18. Where a wave is rolled, and why two ends never roll the same one** **[P]**
- `Board.NewZombieUpdate` rva `0x47A4A0`, size 3904: reads `Time.get_deltaTime`
  at +`0x1E6`/`+0x201`/`+0x6FF`, `Board.get_CanStartNextWave` at +`0x34D`,
  calls `CreateZombie.SetZombie` at +`0x52C` (the grave path) and
  `BoardSpawner.SummonZombies` at +`0xDB0` (the wave path).
- `BoardSpawner.SummonZombies(int wave)` rva `0x48DE00`, size 2304: calls
  `GetRandomRow` +`0x4E4`, `AdjustZombieForTerrain` +`0x521`,
  `AdjustRowForSpecialZombies` +`0x543`, the one creator `CreateZombie.SetZombie`
  +`0x5A1`, and `UnityEngine.Random.Range` +`0x7CB`. xref: the only game caller is
  `Board.NewZombieUpdate+0xDB0` plus one rogue-mode coroutine.
- `CreateZombie.SetZombie(int theRow, ZombieType, float theX = 9.9,
  bool isMindControlled = false, bool ignoreTerrain = false)` rva `0x45A5C0`,
  size 2896: `Instantiate` +`0x4B1`, `CreateZombie.AddToList` +`0x749`,
  `CreateZombie.GetZombieData` +`0x8B0`.
- Proves: the row, the type and the timing are rolled on each end's own update,
  so two ends that both run it legitimately differ. A peer blocks the summon and
  repeats `SetZombie` with the host's four numbers (`ZombieSync.ApplySpawn`).
- The wave clock is the three plain fields `Board.theWave` (0x138),
  `Board.theMaxWave` (0x13C) and `timeUntilNextWave` (0x18C); E2 already shows
  the game writing `timeUntilNextWave = 3.0f` from the battle-start button. No
  native setter exists for the three, so the peer stores the host's absolute
  numbers (`ZombieSync.ApplyWave`).
- Zombies born outside a wave stay local: NewZombieUpdate's own `SetZombie`
  (+`0x52C`, graves) and abilities do not pass through the flagged summon.
- Repro: `py -3 tools/re/pvz-disasm.py 'Board$$NewZombieUpdate'
  'BoardSpawner$$SummonZombies' 'CreateZombie$$SetZombie'` and
  `py -3 tools/re/pvz-xref.py 'BoardSpawner$$SummonZombies'`.

**E18b. Zombie health has no usable death hook** **[P]**
- `Zombie.theHealth` is `long` at 0xB0, `theMaxHealth` 0xB8,
  `theFirstArmorHealth` `int` 0xC0, `theSecondArmorHealth` 0xC8,
  `theZombieRow` 0xDC, `theZombieType` 0x68 (same dump block).
- `Zombie.Die(int reason = 0)` rva `0x705D90`, `Slot: 34`,
  `public virtual void Die`. The dump has 42 `override void Die(` bodies; a
  postfix on the base would not see an override that does not call base.
- Proves: deaths are inferred, not hooked - the host compares its live
  `Board.zombieArray` (0x70) with the ledger and sends a despawn for a bound
  zombie that is missing (`ZombieSync.PushZombieState`). Health and armor are
  copied as absolute values on a 500 ms snapshot; both writes are plain fields.
- Repro: `py -3 tools/re/pvz-disasm.py 'Zombie$$Die'`;
  read the Zombie block in `ref/dump-en/dump.cs`.

**E18c. The two mower doors, and the one point they share** **[P]**
- `Mower.OnTriggerStay2D(Collider2D)` rva `0x4A6E40`, size 624:
  `BoardSpawner.MowerStart` +`0x1DB`, `Animator.SetTrigger` +`0x1F7`,
  `Rigidbody2D.set_velocity` +`0x218`.
- `Mower.StartMove()` rva `0x4A70B0`, size 256 (public, not virtual):
  `BoardSpawner.MowerStart` +`0xAB`, `SetTrigger` +`0xC7`, `set_velocity` +`0xED`.
  xref: `StartMove` is called from `AdvBuffData.Random_...OnSelect+0x12E`;
  `BoardSpawner.MowerStart` only from these two mower bodies.
- `BoardSpawner.MowerStart(int row, int wave)` rva `0x48DD40`, size 112: its
  only call is `Dictionary<int,int>.set_Item` +`0x57` (the `mowerSpawns`
  bookkeeping). It starts nothing by itself.
- `Mower.theMowerRow` 0x20, `started` 0x30 (dump).
- Proves: a mower moves from two private/public doors that both first record
  the start in the same `BoardSpawner.MowerStart`, so a peer blocks all three
  and a host's start is broadcast once from the bookkeeping method's postfix.
  A replay calls the game's own `Mower.StartMove` on the row's object, with the
  gate opened by `_mowerApplying` (`ZombieSync.ApplyMower`, `Mowers.StartMoving`).
- Repro: `py -3 tools/re/pvz-disasm.py 'Mower$$OnTriggerStay2D'
  'Mower$$StartMove' 'BoardSpawner$$MowerStart'` and
  `py -3 tools/re/pvz-xref.py 'Mower$$StartMove' 'BoardSpawner$$MowerStart'`.

## 8. Remote intent preview (cosmetic ghosts) **[P]**

**E19. The game's own plant preview is the ghost**
- `CreatePlant.CreatePlantPreview(PlantType theSeedType, Color color,
  Transform parent, Vector2 position)` rva `0x4516B0`, size 2416,
  `public static GameObject`: `Instantiate` +`0x291`, `Animator.Play` +`0x2FF`,
  `Lawnf.GetSprites` +`0x335`, then per sprite `SpriteRenderer.set_color`
  +`0x3CD` and `Renderer.set_sortingOrder` +`0x3E5`, and
  `Transform.set_parentInternal` +`0x6F0`, `GameObject.set_tag` +`0x702`.
- Its body adds no `Collider2D` and no gameplay component (the one
  `AddComponent` is a `SpriteMask` at +`0x59C`), so the copy it returns is a
  tinted picture of the type, not a plant.
- `GridManager.GetGridCenter(int col, int row)` rva `0x742110`,
  `public Vector3`; the same class carries the grid geometry read by the game.
- `Mouse.mouseItemType` 0x60; `MouseItemType.Shovel = 3`, `Glove = 4`
  (`ref/dump-en/dump.cs`), which is how an empty hand is told from a tool.
- Proves: `Previews.Create` may build a seat's ghost from the game's own method,
  parent it to the board (so a level change takes it down), place it at
  `GetGridCenter` and tint it per seat; nothing is drawn by the mod itself.
- Repro: `py -3 tools/re/pvz-disasm.py 'CreatePlant$$CreatePlantPreview'`.

## 9. Fusion lookup, level settings and zombie movement

**E20. The fusion table, the difficulty and the speed slider** **[P]**
- `MixData.TryGetMix(PlantType a, PlantType b, out PlantType c, bool order = true)` rva
  `0x4ECDD0`, size 352: builds the pair, one `Dictionary<ValueTuple,PlantType>.TryGetValue`
  and no other work. When `order` is **true** only the pair `(a,b)` is asked; when it is
  **false** the swapped pair `(b,a)` is asked as well (+`0xC2` tests `order`, the second
  lookup at +`0xF6`). Pure lookup: safe inside a silent Query.
- `CreatePlant.CheckMix` first `TryGetMix` call is at +`0x6D3`: `r9b = 1` (ordered),
  `edx = [rsp+0x368]` (the used type), `ecx = [plant+0x1AC]` (the standing plant's type,
  set at +`0x698` from `r12`). So the game asks `(standing, used)` with `order = true`, and
  the mod asks the same pair the same way (`Mixes.Result`).
- `GameAPP.config` is a static `GameConfig` at static-field offset 0x128 (`dump.cs`).
  `GameConfig.difficulty` is `int` at 0x10, `GameConfig.gameSpeed` is `float` at 0x14.
- `DifficultyMgr.Update` rva `0x718D80`, size 992: loads `GameAPP`'s class static fields
  (`[class+0xB8]`), reads `config` (`+0x128`), converts the slider to int (`cvttss2si`)
  and stores it at `config.difficulty` (`89 47 10` at +`0x1A2`).
- `GameSpeedMgr.Update` rva `0x7395E0`, size 480: the same `config` load and a
  `movss [config+0x14], xmm0` at +`0x100`, store `config.gameSpeed`.
- `GameSpeedMgr.Start` rva `0x739320`, size 704: sets the slider to `min = 0`,
  `max = Gears.Count - 1`, `wholeNumbers = true` (+`0x9B`, +`0xF1`, +`0x108`), so the speed
  slider's own value is an **index into `GameSpeedMgr.Gears`**, and `Update` stores
  `Gears[(int)slider.value]` into `config.gameSpeed` (`List<T>.get_Item` at +`0xF7`).
  Moving the client's slider therefore needs the gear index, not the speed (`LevelSettings.GearIndex`).
- Proves: the two pause-menu sliders are plain writes into `GameAPP.config`, there is no
  setter to call, and the mod copies exactly those two fields; while the menu is open its
  own `Update` rewrites them from the sliders, which is why the client's sliders are moved too.
- Repro: `py -3 tools/re/pvz-hex.py 'MixData$$TryGetMix' 'DifficultyMgr$$Update'
  'GameSpeedMgr$$Update'` and `py -3 tools/re/pvz-disasm.py 'CreatePlant$$CheckMix'`.

**E21. What moves a zombie, and what the speed fields mean** **[P]**
- `Zombie.UpdateSpeed` rva `0x713E50`, size 336: unconditionally multiplies
  `theOriginSpeed` (0x110) by `freezeSpeed` (0x114), `coldSpeed` (0x118), `butterSpeed`
  (0x11C), `kelpSpeed` (0x120), `garlicSpeed` (0x124), `uniqueSpeed` (0x128),
  `holeSpeed` (0x12C) and `maLiangSpeed` (0x130), stores the product in `theSpeed`
  (0x10C), then writes `Rigidbody2D.velocity` and `Animator.SetFloat`. With buff 5 present
  (`Entity.HasBuff`) it stores 0 instead.
- Proves: writing the eight factors and the origin speed makes the game itself recompute
  the same walking speed; syncing only `theSpeed` would be overwritten on the next update.
- `Zombie.AdjustPosition(Vector3 position)` rva `0x702AE0`, size 352: null-checks `Entity.axis`
  (0x38), reads `axis.position`, and writes `transform.position + (position - axis.position)`.
  So the argument is a point for the **axis**, not for the visual transform.
  `Zombie.MoveYUpdate` rva `0x70DAC0`, size 272, compares `axis.position.y` with
  `Mouse.GetLandY(axis.position.x, theZombieRow)` and feeds the difference into the animator's
  accumulated root motion, easing a wrong y back over about a second. The game's own
  `MoneyZombie.PositionUpdate` rva `0x6A9CC0`, size 176, calls
  `AdjustPosition(new Vector3(edge, axis.position.y, 0))` - it moves x and deliberately keeps
  the current y.
- Proves: the room must send the host's **axis** point, and a peer must pass its own local y to
  `AdjustPosition`; sending the host's `transform.position.y` (which carries the walk-animation
  offset) puts the axis one animation-offset off on every push, and `MoveYUpdate` visibly
  climbs it back - the 2 s downward twitch.
- `Shovel` has no preview creator; `InGameTool.PickUp` (rva `0x7D4470`, size 96) only
  enables the tool and reparents its own transform to the cursor. The shovel ghost therefore
  copies the tool's own sprite at runtime (`Previews.CreateShovel`); that the tool really
  carries a `SpriteRenderer` (with an `Image` fallback) is measured at the first live run,
  not claimed here.
- Repro: `py -3 tools/re/pvz-hex.py 'Zombie$$UpdateSpeed' 'Zombie$$AdjustPosition'
  'Zombie$$MoveYUpdate' 'MoneyZombie$$PositionUpdate'` and
  `py -3 tools/re/pvz-disasm.py 'InGameTool$$PickUp'`.

## 10. Field offsets (from dump.cs) **[P]**

| Class | Field | Offset |
|-------|-------|--------|
| Board | maxSun | 0x100 |
| Board | theSun | 0x110 |
| Board | timeUntilNextWave | 0x18C |
| Mouse | Instance (static) | 0x0 |
| Mouse | theMouseRow | 0x28 |
| Mouse | theMouseColumn | 0x2C |
| Mouse | thePlantTypeOnMouse | 0x38 |
| Mouse | theItemOnMouse | 0x50 |
| Mouse | theCardOnMouse | 0x58 |
| Mouse | board | 0xA0 |
| CardUI | theSeedType | 0x20 |
| CardUI | theSeedCost | 0x24 |
| CardUI | isAvailable | 0x40 |
| CardUI | CD | 0x44 |
| CardUI | fullCD | 0x48 |
| CardUI | usedTimes | 0x4C |
| CardUI | isPickUp | 0x57 |
| CardUI | onCardBank | 0x5A |
| CardUI | thePlantType | 0x88 |
| CardUI | Durability | 0xD4 |
| CoinSun | theMoveType | 0x20 |
| CoinSun | theCoinType | 0x24 |
| CoinSun | sunPrice | 0x28 |
| CoinSun | realCost | 0x2C |
| CoinSun | save | 0x30 |
| CoinSun | action | 0x38 |
| CoinSun | target | 0x40 |
| Board | boardSpawner | 0x30 |
| Board | zombieArray | 0x70 |
| Board | mowerArray | 0x98 |
| Board | theWave | 0x138 |
| Board | theMaxWave | 0x13C |
| BoardSpawner | theWave | 0x18 |
| BoardSpawner | rowsSpawns | 0x20 |
| BoardSpawner | mowerSpawns | 0x28 |
| Zombie | theZombieType | 0x68 |
| Zombie | theHealth (long) | 0xB0 |
| Zombie | theMaxHealth (long) | 0xB8 |
| Zombie | theFirstArmorHealth | 0xC0 |
| Zombie | theSecondArmorHealth | 0xC8 |
| Zombie | theZombieRow | 0xDC |
| Zombie | theSpeed | 0x10C |
| Zombie | theOriginSpeed | 0x110 |
| Zombie | freezeSpeed / coldSpeed / butterSpeed | 0x114 / 0x118 / 0x11C |
| Zombie | kelpSpeed / garlicSpeed / uniqueSpeed | 0x120 / 0x124 / 0x128 |
| Zombie | holeSpeed / maLiangSpeed | 0x12C / 0x130 |
| GameConfig | difficulty | 0x10 |
| GameConfig | gameSpeed | 0x14 |
| GameAPP | config (static) | 0x128 |
| Mower | theMowerRow | 0x20 |
| Mower | started | 0x30 |
| Mouse | mouseItemType | 0x60 |

Repro: `grep -nE 'theSun; //|timeUntilNextWave; //|... ref/dump-en/dump.cs`.

Health of a plant is `int`; health of a zombie is `long`.

## 11. Carried-only claims (verify before use)

- Sky-sun `SetCoin` argument roles (`theRow=-1`, type 0, move 1, pos vector) -
  read `pvz-hex.py "Board$$SunUpdate"` around the 8 call sites; column from
  `Lawnf.GetColumnFromX`.
- `Board[+0x29C] = 0` in battle case 4 - bytes re-confirmed [P] (E17b, both
  doors); the FIELD is still unnamed in the Board block scan, so read the write,
  not a name.
- Mode check `[r14+0x1AC],0x3ED` (r14 is NOT Board here) - [C].

## 12. The method atlas (`tools/re/pvz-index.py`) **[P]**

Reads first, code later: `ref/NATIVE-INDEX.md` is the human summary of
`ref/native-index.json` (generated, not in git). Every number below is reprinted
by `py -3 tools/re/pvz-index.py --stats`.

**E22. The whole image, and what "game code" means** - 204,331 methods have a
body; 25,863 of them, over 4,366 class tokens, are `Assembly-CSharp.dll`. dump.cs
declares 17,843 game bodies and the atlas attributes all 17,843, 0 missing.
- Proves: the coverage table can start from a complete list, not a search.
- Repro: `--stats` -> "is the game image fully attributed?".

**E23. `script.json` cannot tell you which assembly a method belongs to**
- Its objects have exactly four keys (`Address`, `Name`, `Signature`,
  `TypeSignature`); `Name` is `Class$$Method` with no image or type index.
- So the image comes from joining to dump.cs by name, and the join is settled by
  dump.cs's own `// RVA:`. 97,236 method pairs agree on the address, 11 more are
  shared-generic instantiations (script.json names one instantiation, dump.cs
  declares the open generic once) and **0 disagree**.
- Proves: an address is the only identity a method has across the two dumps; the
  atlas never claims an image it cannot pay for with one.
- Repro: `--stats` -> "dump.cs RVA agrees with script.json Address:".

**E24. The game is two assemblies, and only one of them simulates**
- `Core.dll` (image 34) is the other first-party assembly: 20 types, 138 methods,
  0 verbs - `Lawnf` (61 static helpers), `InGameText`, `GameMaterial`,
  `Singleton<T>`, `DictionaryExtensions`, `ListExtensions`, `Vector3Extensions`,
  `ResetOnBoardDieAttribute`.
- Proves: nothing sendable is lost by calling `Assembly-CSharp.dll` the game, but
  a helper called `Lawnf` exists twice (`Lawnf` in the game image, `Core.Lawnf`
  in `Core.dll`), so a name-only lookup will pick the wrong one.
- Repro: `--class Core.Lawnf`, `--stats` -> "the game is not one assembly".

**E25. dump.cs spellings that break a naive name join** (five, all measured)
- Nested types appear as `A+B`, as `Outer.Inner` with the namespace line set, and
  as `Arm.Neon` where the namespace line is only the last fragment.
- Generics: dump.cs writes `List<T>`, script.json writes ``List`1<Vector3>``;
  async state machines keep their name (`InitBoard.<Awake>d__12`), anonymous types
  keep their angle brackets (`<>f__AnonymousType0<<IsZombie>j__TPar,`).
- 428 type names collide, so 15,326 distinct names cover 15,754 TypeDefIndexes.
- Proves: `open_name` (arity stripped, `+` folded, `<` cut only when it is not a
  member suffix) plus the address tie-break is what makes the join 100% on game
  bodies; a substring or last-dot match is not.
- Repro: `--stats` -> "class tokens with no dump.cs type" (2,723 tokens, all
  async/Burst/shared-generic library machinery, none of it game code).

**E26. Method bodies are int3-padded, so the padding is the true end**
- `0x3ED8E0` is `c2 00 00 cc cc ...`: a bare `ret`, and 508 game methods live at
  that one address, 138 of them verbs (`InGameTool.Use`,
  `TravelRogueTooltip.Hide`, `Bullet_cabbageMelon.SetPenetrationTime`).
- `0x3ED870` is `33 d2 e9 ...` (1,423 empty `.ctor`s), `0x49E6A0` tail-jumps into
  `Plant..ctor` `0x4D6150` (138 derived constructors), `0x3F1980` is
  `b8 02 00 00 00 c3` (124 getters returning the constant 2).
- Proves: a method on the `ret` stub is provably a no-op, so a Harmony gate on it
  is free; and body size must be measured to the padding, not to the next method
  address (older [P] sizes in this file are up to 15 bytes too big).
- Repro: `py -3 tools/re/pvz-index.py --method InGameTool@Use` (sz 3, ncall 0).

**E27. `Board.SetSun` is one store** - rva `0x481250`, size 7, 0 resolved calls,
`wr_own`
- Proves: the sun bank is a plain `Board` field write, so a peer can set it
  without running any of the `GetSun` hooks (talent checks, `Lawnf.TravelCurse`,
  `Board.OnGetSun` - see E28).
- Repro: `--method Board@SetSun`.

**E28. `Board.GetSun` is not `SetSun`** - rva `0x478BF0`, size 899, 10 calls:
`Lawnf.TravelCurse`, `AdvantureData.CheckTalent`, `Lawnf.TravelAdvanced`,
enumerator moves, `WaitForUpdate.MainThreadAwaiter.GetResult` x3,
`Object.op_Inequality`, `Board.OnGetSun`
- Proves: adding sun and setting sun are different operations; the room must
  repeat `GetSun(float,bool)` for a pickup and reserve `SetSun(int)` for a
  snapshot, or talent hooks fire twice on the client that receives a store.

**E29. Removal goes through Unity, not through the game** - 495 game methods call
`UnityEngine.Object.Destroy`/`DestroyImmediate`; `CoinSun.Die` (`0x44B860`, 86
bytes) is only `Component.get_gameObject` + `Object.Destroy`.
- Proves: an entity's disappearance is not a field write anywhere, so the atlas
  had to grow a `destroy` feature or `CoinSun.Die` reads as a pure accessor.
- Repro: `--stats` -> body feature `destroy`; `--calibrate` row `CoinSun$$Die`.

**E30. `Board.SunUpdate` rolls eight suns per call** - rva `0x484DC0`, size
1,244, 12 calls: `Time.get_deltaTime` x2, `Random.Range`, `CreateItem.SetCoin`
**exactly 8 times**
- Proves (again, and this time automatically): one call can mint eight coins, so
  `SetCoin` is the only mint door and `SunUpdate` must never be re-sent.
- Repro: `--calibrate` row `Board$$SunUpdate`.

**E31. What the classifier is measured against**
- Noise floor, the 1,653 game getters whose body is 32 bytes or less and must not
  store: `wr_board` 0, `wr_plant` 0, `wr_zombie` 0, `wr_own` 0, `wr_config` 0.
  Of 3,017 small constructors: 2, 0, 29, 0, 46.
- `wr_board`/`wr_plant`/`wr_zombie` use only the offsets that belong to one of
  `Board`, `Plant`, `Zombie`, `GameConfig` and no other, because `Plant` and
  `Zombie` both extend `Entity` and share 85-89 offsets with `Board` (section 10).
- 112,505 of 297,950 `E8`/`E9` positions resolve to a known method (38%); `FF 15`
  thunks do not resolve at all, so "calls nothing" never means "does nothing".
- Proves: `world` (3,904 methods) and `config` (1,851) rest on a field-offset
  signal that is right to about 1%, not on a name guess.
- Repro: `--calibrate` (noise floor), `--stats` (call yield, effects).

**E32. The planting intent is not a parameter list** - `Mouse.TryToSetPlantByCard`
(rva `0x763B80`, 4,385 bytes, 84 calls) and `Mouse.TryToSetPlantByGlove` (`0x764CB0`,
1,680 bytes, 31 calls) both have arity 0: `void Mouse__TryToSetPlantByCard (Mouse_o*,
MethodInfo*)`. `Mouse.SetPlantFromCard(int,int,int)` (`0x75E110`, 233 bytes) resolves
to exactly two calls, `TravelRogueCardData.Snapshot` and `CreatePlant.SetPlant`.
- Proves: the by-card and by-glove doors can never travel, because there is nothing to
  send - the card, the cell and the type live in `Mouse` fields a peer does not have.
  And `SetPlantFromCard` is only the spawn half of planting: it spends no sun and touches
  no cooldown, so the sun spend has to stay its own `HostOnly` row or it double-pays.
- Repro: `--sig 'Mouse$$TryToSetPlantByCard'`, `--method 'Mouse$$SetPlantFromCard'`.

**E33. `Shovel.Use` pays for the dig itself** - rva `0x7DD710`, 1,732 bytes, 21 calls:
`InGameTool.PutDown`, `Mouse.ClearItemOnMouse`, `Mouse.GetPlantsOnMouse`, two
`List.RemoveAll`, `Physics2D.RaycastAll`, `GardenPlant.Die`, `Shovel.ReturnPlantToGarden`
(`0x7DD360`), `AdvantureData.CheckTalent`, `PlantDataManager.GetPlantData`,
`Lawnf.GetUpgradedPlantCost` and **two `CreateItem.SetCoin`** at `+0x56B` and `+0x649`.
`Shovel.PayBackSun` (`0x7DD250`, 272 bytes) is `Lawnf.GetUpgradedPlantCost` + `SetCoin`.
- Proves: one dig mints its own refund coins, so the refund doors are `Local`. If a peer
  replayed `PayBackSun` after the replicated `Use` had already run it, the bank would pay
  twice; instead every end spawns the coins it can see, and only the credit is refused.
- Repro: `--method 'Shovel$$Use'`, `--method 'Shovel$$PayBackSun'`.

**E34. `Board.OnGetSun` is a hook, not the bank** - rva `0x47D480`, 470 bytes, 6 calls,
measured effect `read`: `Dictionary<Int32Enum, object>.TryGetValue`, a `KeyValuePair`
enumerator, `UltimateSunNut.GetEnergy`. It stores no sun (E27 is the only store).
- Proves: what a pickup triggers besides the number is per-plant bonus bookkeeping, so
  running `GetSun` at both ends runs that bookkeeping twice while a `SetSun` snapshot
  runs it zero times - the room repeats `GetSun` only when a peer's own click did the
  crediting, which is exactly the `HostOnly` rule on the row.
- Repro: `--method 'Board$$OnGetSun'`.

**E35. The merge door rolls** - `Board.SetEvePlants`
(rva `0x47C6F0`, 1,291 bytes, 22 calls) reaches `Lawnf.GetAllPlantTypes`,
`UnityEngine.Random.Range` (`+0x141`) and `EveManager.GetPlantType` before its four
`CreatePlant.SetPlant` calls (`+0x1C4`, `+0x3EF`, `+0x43B`, `+0x4AC`).
`CreatePlant.SetPlant` (`0x455A00`, 5,712 bytes) reaches `UnityEngine.Random.Range` six
times (`+0x646`, `+0x689`, `+0xD5E`, `+0xD95`, `+0xDC9`, `+0x1401`).
- Proves: replaying `SetEvePlants` runs the dice again on every end, so "the same machine
  code on every machine" cannot be the merge protocol - the plant the host rolled would
  differ from the plant the peer rolls. What has to travel is the decided value, and
  until that channel exists the merge stays on the lawn diff measured in stage 1. The
  row is `Verdict.Never`, and `--arbiter` now refuses any `Replicate` row whose body
  resolves a call into `UnityEngine.Random`.
- Corrects itself (2026-11-12, E40): the six `Random.Range` hits inside
  `CreatePlant.SetPlant` are *not* a second roll of the plant. Its signature is
  `Plant SetPlant(int theBoxColumn, int theBoxRow, PlantType theSeedType, ...)` - cats
  `iieotbboo`, the type is argument three - and that same door is what every placement
  the room replicates already runs on every end, lawns agreeing. Only the caller rolls.
  `--arbiter`'s "rolls" column reads the body's own call list, so a row whose inner door
  rolls and a row whose inner door jitters a position look the same there. The gift
  box's own dice is a different door, measured in E40; `Board.SetEvePlants` is reached
  from `Board.InitIZSettings`, `Plant.WheatUpdate`, `EveManager.SetPlant` and
  `EveManager.StartNewEveMode`.
- Repro: `--method 'Board$$SetEvePlants'`, `--method 'CreatePlant$$SetPlant'`,
  `py -3 tools/re/pvz-xref.py 'Board\$\$SetEvePlants'`, `--arbiter` (the "rolls" column).

**E36. The game's own in-level notice line is `Core.InGameText.ShowText`** - rva
`0xB14770`, 403 bytes, image `Core.dll` (its proxy assembly is `Il2CppCore.dll`,
which the mod did not reference until now), instance method on the singleton
`InGameText.get_Instance` (`0xB149C0`, `Instance` property, `_instance` at 0x0).
Signature `void ShowText(string text, float time, bool cover = false, float y =
-400)`, cats `sfbf`. Body: `GameObject.SetActive` (+0x07C), then `movss [rbx+0x24],
xmm1` (+0x085) - the float argument lands in the `timer` field the `Update`
counts down - then `SetAsLastSibling` and `Transform.set_localPosition` built
with the fourth argument.
- Proves: the arguments are seconds, replace-or-not and canvas height offset,
  and the game itself asks for a refusal notice with `time` 3.0 (twice) and 5.0
  and `y` -400 in `Mouse.TryToSetPlantByCard` (rva `0x763B80`) at +0x564, +0x5D1, +0x6FC. A room
  refusal can therefore be shown the way the game shows its own, with no mod UI.
- Repro: `--method 'Core.InGameText@ShowText'`, `--method 'Mouse@TryToSetPlantByCard'`
  plus the window decode of +0x4C0..+0x720 (rip-relative `movss` immediates).

**E37. A planting has no native money gate to replay** - `Mouse.SetPlantFromCard`
(rva `0x75E110`, 233 bytes) resolves exactly two calls: `TravelRogueCardData.Snapshot`
(+0x079) and `CreatePlant.SetPlant` (+0x0CA). The literal `"Insufficient Sun!"`
appears only in `GodManager.ClickCard` (`0x503C40`), `ScratchOffMenu.Refresh`
(`0x7F2410`) and `TowerUpgradeMenu.Select1/2/3` (`0x81E4F0/0x81E6E0/0x81E910`) -
no plant path at all.
- Proves: the game refuses an unaffordable plant *earlier*, when the card is
  taken, so there is no native call whose replay means "the pool cannot pay
  this". What is native and reusable is the line it is said on (E36), and the
  sentence itself belongs to the room.

**E38. The fusion refusal is a native answer with its own three texts** -
`CreatePlant.MixFail(int theBoxColumn, int theBoxRow, PlantType newPlantType)`
(rva `0x4534D0`, 3,824 bytes, 55 calls, returns bool, cats `iii`) reaches
`Core.InGameText.ShowText` and the terrain rules `PlaceRule.PlaceManager` /
`PlaceRule.OrdinaryTerrainRule` / `BoardGrid` / `GridSystem`, with the literals
"This cannot be fused on land.", "This cannot be fused in this location.",
"This cannot be fused on water." It has one caller: `CreatePlant.CheckMix` +`0xA09`.
- Proves: when two plants meet on one cell the game already has a refusal voice,
  and it is a *terrain* verdict, not a money verdict - so a room that declined a
  fusion because the merge is not synced must not borrow it, and the merge
  protocol still has to carry the decided value instead of the roll (E35).
- Repro: `--method 'CreatePlant@MixFail'`, `py -3 tools/re/pvz-xref.py
  'CreatePlant$$MixFail'`.

**E39. A victory cup has seventeen native doors, and most of them are this lawn's own
bookkeeping** - `Lawnf.SetAward(Board, Vector2, bool killZombie, bool fake)` (rva
`0x889550`, 969 bytes, 20 calls, returns `PrizeMgr`, cats `otbb`; `Resources.Load`
+`0x0B8`, `Object.Instantiate` +`0x15B`) is called from 17 methods:
`BejeweledManager.MatchPlantUpdate+0x9D5`, `BrickManager.Update+0x1A1`,
`GameLose.HandleGameLose+0x14C`, `GameLose.OnTriggerEnter2D+0x812`,
`GameLose.ProcessZombieEnter+0x6FE`, `Board.EnterNextRound+0x721`,
`Board.<SetVictoryCondition>b__137_0+0x6B`, `LookingStar.Update+0x2EB`,
`FlagGameManager.Update+0x371`, `Game2048Manager.MixPlant+0x20E`,
`HoverGardenManager.DropVictoryTrophy+0x12E`, `TreasureManager.TaskUpdate+0x2B9`,
`PVPRandomManager.DamagePlayer2+0xB0`, `PVPRandomManager.ZombieEnterHouse+0xCB`,
`TravelRogueManager.FinishRun+0x18A`, `GameLevel.BoardVictory.OnUpdate+0xCE`,
`GameLevel.BoardVictory.Win+0xDD`. Five of them are a manager's own `Update` body, one is
a round change, three are the *loss* path.
`PrizeMgr.Clicked` (`0x646120`, 1,900 bytes, 24 calls) is the door a click runs, and it
is the level-end door: `AdvantureManager.LevelComplete` +`0x4C2`, `SaveInfo.SavePlayerData`
+`0x704`, `Board.ClearTheBoard` +`0x721`, with `Object.Destroy` +`0x14D` and
`GameObject.SetActive` +`0x5B8` on its own children. Fields: `isLand` 0x3C, `isClicked`
0x3D, `selectAdv` 0x3E, `fake` 0x3F.
The lift itself is `PrizeMgr.<MoveAndScaleObject>d__17.MoveNext` (rva `0x6535D0`, 961
bytes, 15 calls), started by `Clicked` at `+0x6AC`: it writes `Transform.set_position`
+`0x20A` and `Transform.set_localScale` +`0x2A8` frame by frame on the cup's own object,
and a Unity coroutine on an inactive GameObject does not advance. So the cup whose click
is replayed here must not be hidden on the same frame - that is a cup that vanishes
instead of rising - while `SetActive(false)` stays the right end for every *other* cup.
- Proves: a cup is not one event, and "this lawn won" is a sentence every end's own
  zombie list can say. Measured live: a peer was asked for a cup at 11:53:13.075, 21.6 s
  before the host's cup arrived on that end at 11:53:34.700, and 45.8 s after the host's
  lift had been replayed there (11:56:14.806) the same end was clicking a cup again
  (11:57:00.563). So the room's cup is the host's door and one object at a time, and the
  way to take the rest down is the call the game itself uses inside `Clicked` -
  `GameObject.SetActive(false)` - not an object the mod writes.
- Repro: `py -3 tools/re/pvz-xref.py 'Lawnf\$\$SetAward'`, `--method 'Lawnf@SetAward'`,
  `py -3 tools/re/pvz-disasm.py 'PrizeMgr\$\$Clicked'`, and the two logs of the live run
  (`MelonLoader/Logs/26-11-12_11-42-25.log`, `26-11-12_11-44-10.log`).

**E40. A gift box grows its plant out of an animation door that takes no arguments** -
`Present.AnimEvent` (rva `0x56B3E0`, 416 bytes, 6 calls, `Plant T()`, no parameter,
virtual slot 73) reaches `ParticleManager.SetParticle` +`0x132`, `Present.RandomPlant`
+`0x165` and `Present.SuperRandomPlant` +`0x181`. `Present.RandomPlant` (`0x56BDA0`) has
exactly one caller, `AnimEvent+0x165`, and calls back into `SuperRandomPlant` at
+`0x995`; `SuperRandomPlant` (`0x56CB60`) has three callers: `AnimEvent+0x181`,
`RandomPlant+0x995` and `Invest_<CJK>.OnSelect+0x2CE`. `SnowPresent.AnimEvent`
(`0x576200`, 1,272 bytes, 15 calls) does not call them: it builds its own list with
`Lawnf.GetAllPlantTypes`, rolls `UnityEngine.Random.Range` at +`0x3E6` and grows a
`FreezedPlant` at +`0x468`. Neither `AnimEvent` has a static caller at all, which is the
shape of a Unity animation callback - the box opens when its own animation says so, under
a second after the placement (the live run's census caught the rolled plant 0.75 s later).
- Proves: the plant a gift holds is decided after the replicated placement by a door with
  no arguments to send and a fresh plant as its answer, so the rows can only be
  `Verdict.Never` and one end has to be the one that rolls. The live run says both ends
  did: a box the peer planted on cell 1,1 at 11:47:29.641 had grown `WaterShieldshroom`
  (1470) there while the same box on the host's lawn grew `Shieldshroom` (43), which the
  host's census minted at 11:47:30.390; the client's lawn agreed only when the layout
  retype landed at 11:47:31.426, 1.8 s after the box was planted, and that repair is
  `Plants.Kill` + `Plants.Spawn`, so the plant arrived with none of the health or level it
  had grown. The gift's own door is why `--arbiter` reads `Present.AnimEvent` as a `read`:
  the roll lives one call deeper than the door, exactly the trap E35 had to learn.
- Repro: `py -3 tools/re/pvz-disasm.py 'Present\$\$AnimEvent'`,
  `py -3 tools/re/pvz-xref.py 'Present\$\$RandomPlant'`, `--arbiter`, and the gift lines
  of the two logs between 11:47:29 and 11:47:31.

**E41. The fertilizer is a lawn object, and the body that changes the plant has no caller
in the image** - `Fertilize : Bucket` (dump.cs:67390) has six instance fields
(`zombieFertilizePrefab` 0x58, `zombie` 0x60, `targetFreezedPlant` 0x68, `theTargetPlant`
0x70, `anim` 0x78, `isUsing` 0x80) and **no static field**, so there is no
`Fertilize.Instance` for `NativeCalls.Singleton` to read.
- `Fertilize.Pick/0` (`0x737730`, 96 bytes, slot 8) -> `Bucket.Pick` +`0x2A`,
  `Animator.SetTrigger` +`0x47`. `Fertilize.Use/0` (`0x739130`, 48 bytes, slot 9) has one
  call: `Fertilize.Use(int,int)` at +`0x1B`.
- `Fertilize.Use/2` (`0x738AA0`, 1,680 bytes, 27 calls, `void T(int,int)`, cats `ii`,
  effect `config`, **no `UnityEngine.Random`**) is two near-identical blocks - the second
  one is the `zombie` branch: enumerate a `List<KeyValuePair<int,int>>`, read
  `Transform.get_position`, add to it (`Vector3Extensions.Add`), write it back,
  `Animator.CrossFade("use")`, `GameAPP.PlaySound`, and end with `Behaviour.set_enabled`.
  Its rip refs are `Board`, `FreezedPlant`, `GameAPP`.
- Three callers reach it: `Fertilize.Use+0x1B`, `Fertilize.Update+0x369` and
  `EndoFlame.OnFire+0x2CF` (which instantiates its `zombieFertilizePrefab` at +`0x292` and
  `GetComponent`s it at +`0x2AA` first).
- `Fertilize.Upgrade/0` (`0x737B30`, 3,952 bytes, effect `world`) is the body that changes
  the lawn: `FreezedPlant.SetEntity` +`0x146`, `Lawnf.Get1x1Plants` +`0x1EF`,
  `FreezedPlant.FreezePlant` +`0x2AE`, `Lawnf.HasZombie` +`0x2F5`,
  `Plant.set_AttributeCountdown` +`0x35D`, `Lawnf.SetDroppedCard` +`0x9E4`, **sixteen**
  `CreatePlant.SetPlant` from +`0x458` to +`0xCCB`, `Instantiate` +`0xE04`, `Destroy`
  sites. It has ZERO static callers, and `SendMessage` does not explain that either: the
  only methods whose literals carry the word `Upgrade` belong to the rogue-shooting
  minigame. So it is an animation event, the same shape E40 found for `Present.AnimEvent`.
- `Fertilize.Update/0` (`0x7377B0`, 896 bytes) is a frame callback that reads the lawn with
  `Lawnf.GetPlants` / `GetCertainZombies` / `GetAllPlants`, filters them with
  `Fertilize.<>c.<Update>b__8_0` (`0x7554F0`, whose only call is `FreezedPlant.CanFreeze`),
  rolls `ListExtensions.GetRandom` at +`0x33D` and then calls `Fertilize.Use` at +`0x369`.
- The supply is a scene object, not a board one: `GiveFertilize` (dump.cs:67428, fields
  `occurrences` 0x20, `pos` 0x24). `AnimGive/0` (`0x741AA0`, effect `side`: instantiate,
  asset_load, scene_query, sound) loads the asset `Items/Fertilize/Ferilize`,
  `AvaliableToGive/0` (`0x741DB0`, `bool T()`, `gameapp_static`) is static, and **both have
  zero static callers**.
- The game's own tutorial texts (`stringliteral.json`, printed from
  `TutorManager.TutorUpdate13`, `0x66E160`) say what it does: "You unlocked the Fertilizer,
  which appears periodically in the upper left corner of the map.", "In short, the
  Fertilizer always allows plants to progress to the next stage.", "Fertilizer can fully
  restore a plant's HP.", "Fertilizers can make the Sun-shroom grow immediately.",
  "Fertilizers can act as a purple card, directly upgrading certain plants."
- Not this item: the zen garden has its own fertilizer - `ZenGarden.GardenPlant.Fertized/1`
  (`0x8EA200`), `GardenPlant.GrowUp/0` (`0x8EA6D0`, "Used Super Fertilizer") and
  `AutoFertilizer.TimeUp/0` (`0x731990`, a `GardenEquipment`). The room stands on a lawn,
  and the garden is not the room's lawn (see `PauseSync`).
- Proves: the doors a hand can walk are `Use/0` and `Use/2`, both deterministic and both
  instance doors of an object that lives in no board collection - `Board` keeps a
  `List<GridItem> griditemArray` at 0x90, which covers graves, pots and ice but not a
  `Bucket`, and the fertilizer is a `Bucket`. So `NativeCalls.EntityTarget` has no lookup
  for it and `Singleton` has no `Instance` to read, which means a coverage row for
  `Fertilize.Use/2` would be refused on every peer as surely as `--arbiter` refuses a
  `Replicate` on a rolling body today. Either the room learns to name a lawn object, or the
  fertilizer travels as the plant change it made - and the plant layout `PlantSync` already
  sends every second can carry a replacement but not a heal.
- Repro: `py -3 tools/re/pvz-index.py --class Fertilize AutoFertilizer GiveFertilize`,
  `--method 'Fertilize@Use' 'Fertilize@Update'`,
  `py -3 tools/re/pvz-disasm.py 'Fertilize$$Upgrade' 'EndoFlame$$OnFire'`,
  `py -3 tools/re/pvz-xref.py 'Fertilize$$Upgrade' 'GiveFertilize$$AnimGive'` (both print
  "no static caller found"), and the `fert` lines of a live co-op run.

**E42. Every grave on a lawn is rolled, and the roll is the whole feature** - graves
are not plants or zombies: they are `GridItem`s, and the board keeps them in
`List<GridItem> griditemArray` at 0x90 (dump.cs:24567). `GridItem` has
`theItemType` 0x20, `theItemRow` 0x24, `theItemColumn` 0x28, `board` 0x30 and
`Grave : GridItem` adds `graveType` 0x48, `health` 0x4C, `ability` 0x50 - three fields,
so a grave holds **no zombie** (dump.cs:23305, 23371). `GridItemType` is
`{CraterDay 0, CraterNight 1, Ladder 3, ScaryPot 4, ScaryPot_plant 5, ScaryPot_zombie 6,
Grave 7, IceBlock 8, ScaryPot_hypnoZombie 9, ScaryPot_obsidian 10, ScaryPot_gold 11,
ScaryPot_red 12}` and `GraveType` is `{Default 0, Sunflower 1, Sunshroom 2}`
(dump.cs:23416, 23345).
- `GridItem.SetGridItem/4` (`0x467E30`, 2,016 bytes, `obj T(int,int,int,int)`, cats
  `iiii`, effect `world`) is the single mint door, and it is the door for **everything**:
  37 static call sites, among them `Board.SetGrave+0x210`, `Board.SupplyGrave+0x2A9`,
  `BrickManager.SetGraves+0x9B`, `Board.SetFreezedPlant+0x164`,
  `FreezedPlant.FreezePlant+0x1BF`, `BoardAction.SetPit+0x476` and `+0x4B1`,
  `Plant.Die+0x4D4`, `GoldScaryPot.Hit+0x1D1`, `ScaryPotManager.SetPots+0x66C`,
  `GameLevel.AutoChessLevel.FillGraves` (3 sites), and the level-script nodes
  `EventNodes.CreateGraveNode.Execute+0x85`, `CreateCraterNode.Execute+0xB8`,
  `CreateIceBlockNode.Execute+0x107`, `CreateLadderNode.Execute+0x85`. It rolls inside
  its own body:
  `UnityEngine.Random.Range` at +`0x2CE`, called as `Random.Range(0, [r13+0x18])` - a list
  count - behind a `test r13,r13` / `je +0x79F` at +`0x2AB`, then
  `List<KeyValuePair<int,int>>.get_Item` at +`0x2DF`, `Instantiate` +`0x387`,
  `GetComponent` +`0x39F`, `GridItem.SetLayer` +`0x648`. So `--arbiter` is right to refuse
  a `Replicate` on it: the row is `GridItem.SetGridItem/4 Never HostOnly` (23 rows, 0
  problems).
- `Board.SetGrave/0` (`0x47CDF0`, 601 bytes, 9 calls, effect `world`) is the level's own
  laying: `AdvantureData.GetResult` +`0x184`, `Random.Range` at +`0x1A0`, +`0x1CE` and
  +`0x232` around two `List<KeyValuePair<int,int>>.get_Item` and the `SetGridItem` at
  +`0x210`. It has exactly one caller, `Board.Start+0x287`.
- `Board.SupplyGrave/0` (`0x4852A0`, 726 bytes, effect `config`) is the wave's grave:
  `Random.Range` +`0x263`, two `get_Item`, `List.Remove` and `SetGridItem` +`0x2A9`. Two
  callers: `Board.HammerSummonZombie+0x237` and `Board.NewZombieUpdate+0x2EB`.
- `Grave.SetZombie/0` (`0x467010`, 752 bytes, effect `world`, `coroutine`) takes nothing,
  reads the grid cell (`GridSystem.GetGrid` +`0x0B3`, `BoardGrid.get_plants` +`0x0C6`),
  emits `CreateParticle.SetParticle` +`0x263` and starts `MoveAndSummonZombie` with
  `StartCoroutine` at +`0x2BD`. Its three callers are `Board.HammerSummonZombie+0x2D0`,
  `Board.NewZombieUpdate+0x909` and
  `GameLevel.RogueShooting.Shooting_<CJK>.OnWaveAdd+0x185` - it is *not* what happens when
  a grave is destroyed.
- `Grave.<MoveAndSummonZombie>d__9.MoveNext` (`0x473CF0`, 2,485 bytes, 41 calls) is where
  the zombie is decided and shown: `ListExtensions.GetRandom<int>` +`0x1F4`,
  `Lawnf.GetRandomBungiType` +`0x1FF`, `RogueShooting.ShootingManager.GetZombieType`
  +`0x214`, `AdvantureConfig.GetZombieTypes` +`0x394` and `GetRandom` again at +`0x3BB`,
  then `CreateZombie.CreateZombiePreview` +`0x45E` - the silhouette the player sees
  through the grave - `Transform.set_position` +`0x74D`, `Object.Destroy` +`0x7A9` (the
  grave itself), `CreateZombie.SetZombie` +`0x826`, `BoardEntity.AddWaveZombie` +`0x876`
  and two further `Random.Range` at +`0x8F5` and +`0x928`. No static caller names it: it
  is reached only through the `StartCoroutine` above, and its `MoveNext` is a generated
  body, which is the shape `--arbiter` refuses to replay.
- `Grave.Die/0` (`0x466A60`, slot 4, effect `world`) is the destruction: four
  `CreateItem.SetCoin` at +`0x0E8`, +`0x148`, +`0x191`, +`0x268`, `Board.GetPoint`
  +`0x2AE`, `ParticleManager.SetParticle` +`0x33E`, then `GridItem.Die` - so what a grave
  drops leaves through the coin door the room already covers, and `GridItem.Die/0`
  (`0x467B50`, `destroy`) is the door that takes the object off the lawn.
- Proves: the defect the user reported has two causes and both are rolls. Every end builds
  its own board, and `Board.Start` asks its own dice where the level's graves stand
  (`SetGrave`, one caller, three rolls), so the cells can only agree by luck; a grave's
  zombie - and the preview drawn over it - is rolled inside a generated coroutine body no
  frame can name. Nothing about a grave is a field the mod could copy (three fields, none
  of them a zombie), so the room's answer is the layout: the host's cells and kinds travel
  as numbers, a peer grows them with `SetGridItem` and takes down what the host does not
  name, and the peer keeps its graves shut because `Grave.SetZombie` is the roll. The
  zombie then needs no new machinery - the host's creator is already watched, and whatever
  the host's lawn gains while the room stands in a level is minted and sent
  (`ZombieSync.NoteZombieCreated`).
- Repro: `py -3 tools/re/pvz-index.py --class Grave GridItem`,
  `--method Board@SetGrave Board@SupplyGrave GridItem@SetGridItem`,
  `--method 'Grave.<MoveAndSummonZombie>d__9@MoveNext'`,
  `py -3 tools/re/pvz-disasm.py 'GridItem$$SetGridItem' 'Grave$$SetZombie' 'Grave$$Die'
  'Board$$SupplyGrave'`, `py -3 tools/re/pvz-xref.py 'GridItem$$SetGridItem'` (37 callers),
  `'Board$$SetGrave'` (1: `Board.Start`), `'Grave$$SetZombie'` (3), and `--arbiter`
  (rows 23, measured 23, problems 0).

**E43. The plant creator cannot be refused, because its callers read the pointer it hands
back** - "a peer mints nothing of its own" has to sit on a door that gives nothing back to the game.

- `CreatePlant.SetPlant` rva `0x455A00`, 5,712 bytes, 94 calls, arity 9, signature
  `obj T(int,int,int,obj,val,bool,bool,obj,obj)`, categories `iieotbboo`, effect `world`,
  `rolls=yes`. It is the one creator every plant comes through: `CreatePlant.CheckMix` has exactly one
  caller (`SetPlant+0x502`) and makes four nested `SetPlant` calls, so a fusion is the same door.
  `pvz-nulluse.py` counts **399 static call sites** in the image and reads what each body does with the
  `Plant` in `rax` before anything else touches it: `checked 44`, `dropped 116`, `returned 58`,
  `deref 142`, `passed 39`, `stored 0`. `deref` is a memory operand based on `rax` (a field read or
  write through the answer), `passed` is `mov rcx/rdx/r8/r9, rax` (the answer becomes the next call's
  `this` or its argument). **181 of the 399 sites therefore take the answer in hand before looking at
  it**, and they are not exotic: `Board.SetPrePlants` (54), `Fertilize.Upgrade` (16),
  `TutorManager.TutorUpdate*` (42), `SpreadFume.Start`, `SpreadScaredyShroom.Start`,
  `Present.RandomPlant`, `Present.SuperRandomPlant`, `Present.HappyRandomPlant`, `Mouse.ClickCard`,
  `Mouse.TurnPlantsToNewPlants`, `MixedPlant.<FusionAnimation>d__4.MoveNext`,
  `UltimatePumpkin.AttributeEvent`, `UltimateTorch.SummonPlant`, `LoonNut.HitEffect`,
  `PotatoSeed.<Start>d__6.MoveNext`, `SaveMgr.LoadPlant`, `Board.SetEvePlants`. A prefix that returns
  false on this door hands every one of them a null pointer, and an il2cpp field access through null is
  an access violation rather than a caught exception - so the mint door is watched (`MintGate`) and
  never gated. Three more reasons the same row cannot be `Replicate`: two parameters are objects a
  frame cannot name, one is a `Vector2`, and the body rolls.
- The gift boxes - the defect the user reported - come through two doors that return `void` and are
  therefore safe to skip:
  - `Paintingflower.AttributeEvent` rva `0x5C5520`, slot 41, 841 bytes, 11 calls - the artist
    sunflower's periodic ability (`Paintingflower : TwinFlower`, dump.cs:44889, field `pen` 0x270). At
    +`0x12C` it loads `41 B9 00 01 00 00` - `r9d = 0x100`, which is the third parameter, i.e.
    `PlantType.Present = 256` (dump.cs:84824) - and calls `CreatePlant.SetPlant`: what it puts in front
    of itself **is** a gift box. `pvz-nulluse.py --only=Paintingflower` gives one site, classified
    `dropped` - the ability never looks at what it minted. Its cadence survives the skip because the
    handler is not what keeps time - the *caller* is: `Plant.PlantUpdate` (`0x4D0380`, 3,472 bytes, 52
    calls) closes the round before dispatching (`mov byte [this+0x1FC],0` at +`0xA1B`,
    `SetAttributeCountdownValue(0)` at +`0xA2B`), takes `attributeCountdownRevision` (`0x200`) into a
    register at +`0xA30`, loads the slot's two-word invoke pair from the class (`mov rax,[klass+0x3C8]`
    +`0xA39`, `mov rdx,[klass+0x3D0]` +`0xA40`) and calls it at +`0xA4A`, then re-arms afterwards:
    `cmp ebx,[this+0x200]` / `jne +0x41` at +`0xA66` reads as "the handler did not re-arm itself, so arm
    the next round from `eventAttributeCountdownMaximum` (`0x1F4`)" (`SetAttributeCountdownValue` at
    +`0xAAA`) and `lastEventAttributeCountdown` (`0x1F8`) is copied from `attributeCountdown` at
    +`0xAB0`. A prefix that returns false never touches the revision, so it gets the ordinary interval
    back: a refused round costs one round instead of turning into a retry storm. The callee is that
    invoke pair and nothing else in the plant's per-frame body - `0x3C8 = 0x138 + 41*16`, and every
    `AttributeEvent` in the build carries `Slot: 41` (the empty base body `Plant.AttributeEvent` at
    `0x3ED8E0`, dump.cs:30397, and the artist's override, dump.cs:44899), which is the only slot 41 a
    body reading 0x1F0/0x1F4/0x1F8/0x1FC/0x200 can be dispatching. `Plant.SetAttributeCountdownValue`
    (`0x4D2C20`) is the writer behind every body measured here: `movss [this+0x1F0],xmm6` at +`0x38`
    (found with `pvz-pat.py "F30F11|1-2|F0010000"`, 14 sites, none of them inside `PlantUpdate`), plus
    the same ring drawn on the plant's own `HealthSlider` (`Plant.healthSlider` 0x80, through
    `set_ProgressFill` at +`0xC5`). The identical close/dispatch/re-arm block
    is repeated in `Plant.UpdateAttributeCountdown` (`0x4D4940`, +`0xC0`..+`0x134`), which exists for
    plants with their own clock and has exactly one caller, `BigWallNut.Update+0x69` - so it is *not*
    the artist's clock, which is what an earlier draft of this entry claimed. The build's own text
    agrees with the door being periodic - stringliteral.json 5344, "Artist Sunflower periodically plants
    random plants in front."
  - `Board.CreateRandomPresent` rva `0x476730`, 663 bytes, 10 calls, `void T()` - the level's own box,
    reached from `Board.LevelUpdate+0x319`. Same literal at +`0x15A`; its own `SetPlant` site is
    `checked` (+`0x188`), so even the mint it makes is looked at before use. The cadence is the
    *caller's*: the only two writers of `randomPresentTime` (`0x1C4`) with 60.0 are
    `Board.LevelUpdate+0x30C` - which re-arms it and calls the door nine bytes later - and
    `Board.SinglePresentUpdate+0x33`, which has no static caller. A refused round therefore costs one
    box and cannot turn into a call every frame.
  - The same literal appears in `CurseSunflower.Start`, `CursePeashooter.Start`, `CursePumpkin.Start`
    (all +`0xA9`), `DiamondImitater.AnimExplode+0x422`, `SnakeManager.SummonUpdate+0x3C1`,
    `PlayerShootingManager.Update+0x31B`, `PVPRandomManager.AISetPlant+0x4CF` and
    `PVPRandomManager.PlayerSetPlant+0x4AD`. Those place a box when something happens rather than on a
    countdown, so they are not the spam and are left alone; `MintGate` names them in a live room if one
    of them turns out to repeat.
- Proves: the defect has a shape, not a name. Every end runs its own plants' abilities, so an artist
  sunflower standing on a peer mints a box the host's lawn never had; the census takes that box down
  within a second and the box's own opening is already refused there (E40), which is exactly "spams
  gifts that only sometimes work". The room's rule is the one the graves and the dice already use - a
  peer in a level rolls nothing of its own - but it can only be *applied* where the game's body gives
  nothing back, so it is applied at the two doors above and measured at the mint door: the log names
  every plant a peer mints by itself, with its type and cell, once per type per level.
- Repro: `py -3 tools/re/pvz-index.py --method CreatePlant@SetPlant
  Paintingflower@AttributeEvent Board@CreateRandomPresent`,
  `py -3 tools/re/pvz-nulluse.py 'CreatePlant$$SetPlant'` (399 sites, 181 dangerous),
  `py -3 tools/re/pvz-nulluse.py 'CreatePlant$$SetPlant' --only=Paintingflower` (1 site, `dropped`),
  `py -3 tools/re/pvz-pat.py "41B900010000"` (30 hits, 11 of them game code),
  `py -3 tools/re/pvz-xref.py 'Plant$$UpdateAttributeCountdown' 'Plant$$SetAttributeCountdownValue'`
  (1 caller: `BigWallNut.Update`; 10 sites, 4 of them inside `PlantUpdate`),
  `py -3 tools/re/pvz-hex.py 'Plant$$PlantUpdate'` and reading +`0xA1B`..+`0xAB6`,
  `py -3 tools/re/pvz-hex.py 'Plant$$UpdateAttributeCountdown' 'Plant$$SetAttributeCountdownValue'`,
  `py -3 tools/re/pvz-pat.py "488B..C8030000" "488B..D0030000"` (the slot-41 invoke pair; the two sites
  that matter are `Plant$$PlantUpdate+0xA39` and `Plant$$UpdateAttributeCountdown+0xE1`),
  `py -3 tools/re/pvz-hex.py 'Paintingflower$$AttributeEvent'`,
  `py -3 tools/re/pvz-scan.py "1C4:0x42700000:4"` (the gift timer, written by its caller),
  `py -3 tools/re/pvz-disasm.py 'Board$$CreateRandomPresent' 'Board$$LevelUpdate'
  'Paintingflower$$AttributeEvent'`, and
  `--arbiter` (rows 26, measured 26, problems 0).

**E44. The chooser makes its own cards available again, and it flags its own preselection after
the card is already in the bank** - the seed draft greys cards out, and both rules it rests on
are writes the game makes itself.

- `CardUI` (dump.cs:21223) is `theSeedType` 0x20, `theSeedCost` 0x24, `isSelected` 0x28
  (21228), `thePlantLevel` 0x30, `parent` 0x38, `isAvailable` 0x40 (21232), `CD` 0x44,
  `fullCD` 0x48, `usedTimes` 0x4C, `maxUsedTimes` 0x50, `preSelected` 0x54 (21237), `safe`
  0x55, `fromSystem` 0x56, `isPickUp` 0x57, `isExtra` 0x58, `disabled` 0x59 (21242).
- Who writes `isAvailable`. The one-byte stores of 0 and 1 through the three registers a card
  pointer is held in, taken over the whole image: `C6434001` 47 sites, `C6434000` 42,
  `C6474001` 31, `C6474000` 16, `C6404001` 29, `C6404000` 3. Of those 168 sites, five methods
  of the seed chooser own six of them and no other chooser body appears at all:
  `CardUI.Update+0xE9` (1) and `+0x25B` (0), `CardUI.CDUpdate+0x24F` (1), `+0x141` (0) and
  `+0x2B3` (0), and `CardUI..ctor+0x43` (1). The same six patterns aimed at `disabled` (0x59)
  give 23 sites in the image and **none** of them in `CardUI`, `InitBoard`, `InGameUI` or
  `CardSlotManager`: nothing in the chooser clears the flag the mod sets.
- What those stores mean, read off the bodies. `CardUI.Update` rva `0x43F4F0`, 864 bytes:
  +`0x7B` is `80 7B 59 00` (`cmp byte [rbx+0x59],0` - the card's own `disabled`) and +`0x7F`
  `0F 85 D4 01 00 00` (`jne +0x259`), so a card is looked at before its cooldown is believed.
  The fall-through path reaches +`0xE9` as `8B 43 48 89 43 44 C6 43 40 01`: `fullCD` into
  `CD`, then available. The disabled branch lands on +`0x259` (`xor edx,edx`) and stores 0 at
  +`0x25B`. `CardUI.CDUpdate` rva `0x43D210`, 944 bytes, tests `disabled` nowhere in it and
  writes 1 at +`0x24F`.
  - Proves: the 250 ms re-mark in `PlantSelect` is required rather than cautious. A cooldown
    that runs out puts a greyed card back on the market through `CDUpdate+0x24F`, which never
    asks the mod's `disabled`; and a card of a page the player has not opened is created
    later, by `CardUI..ctor+0x43`, already available. Only coming back catches either.
- Who flags a preselection. `InitBoard.PreSelectCard` rva `0x46C7B0`, 3,888 bytes, is the
  level's own seed choice. Its first branch is `+0xC65 Lawnf.CreateCard`, `+0xC90
  InGameUI.AddCardToBank`, and only then `+0xCD2` and `+0xCE8` write `C6 47 54 01`
  (`preSelected = 1`) beside `C6 47 28 01` (`isSelected = 1`). Its zombie-card branch marks
  first (+`0xE09`) and calls `AddCardToBank` at `+0xE34`. `InitBoard.PreSelect` writes the
  flag at +`0x211` and +`0x2AB`, `InitBoard.CreateCard` at +`0x66`.
  - Proves: the mod's depth flag around `InitBoard`'s preselection cannot be replaced by the
    game's own `CardUI.preSelected`, for two independent reasons. On the branch the level's
    choice takes, the game writes its marker only after `AddCardToBank` has returned, so the
    mod's postfix on that door cannot see it; and the marker stays on the card for its whole
    life, so a player who takes a preselected card out and clicks it again would be taken for
    the game. `preSelected` says where a card came from, never who moved it last.
  - Correction: an early draft of this stage called `preSelected` a dead field, having looked
    for the write with `pvz-disasm.py`. That tool lists call targets only and never a memory
    operand, so it cannot answer a question about a field; `pvz-pat.py` and `pvz-hex.py` can,
    and above is what they say.
- Repro: `py -3 tools/re/pvz-pat.py "C6434001" "C6434000" "C6474001" "C6474000" "C6404001"
  "C6404000"`, and the same six with `59` in place of `40` for `disabled`;
  `py -3 tools/re/pvz-hex.py 'CardUI$$Update'` reading +`0x70`..+`0xF0` and +`0x250`..+`0x260`;
  `py -3 tools/re/pvz-hex.py 'CardUI$$CDUpdate' 'CardUI$$.ctor'` reading +`0x140`, +`0x24F`,
  +`0x2B0`, +`0x40`;
  `py -3 tools/re/pvz-pat.py "C6475401" "C6405401" "C6435401"` (9 + 5 + 14 sites, the
  chooser's own named above);
  `py -3 tools/re/pvz-disasm.py 'InitBoard$$PreSelectCard'` (82 calls, 42 of them
  `InitBoard.PreSelect`);
  `py -3 tools/re/pvz-hex.py 'InitBoard$$PreSelectCard'` reading +`0xC60`..+`0xCF0`; and
  `--arbiter` (rows 26, measured 26, problems 0 - no door of the chooser was touched).

**E45. The darkness of a card is the game's own overlay, and one byte the game reads but never
writes asks for it** - E44 measured who writes `disabled` (nobody in the chooser). What it left
unmeasured is who *reads* it, and that is the whole answer to a card that stays dark after the
room gave the plant back.

- Who reads `disabled` (0x59). `80..5900` (`cmp byte [r?+0x59],0`) gives 54 sites over the image
  and exactly two of them belong to the card: `CardUI.PutDown+0x9E` and `CardUI.Update+0x7B`.
  `0FB6..59` (the `movzx` form) gives 19 sites, none of them in `CardUI`. Two doors decide the
  life of the flag, and both are the card's own.
- What `CardUI.Update` does with it. rva `0x43F4F0`, 864 bytes: +`0x7B` is the test and +`0x7F`
  `0F 85 D4 01 00 00` (`jne +0x259`). At +`0x259` the body is `33 D2` (`xor edx,edx`) then
  +`0x25B` `C6 43 40 00` (E44's store of 0 into `isAvailable`), then +`0x262
  Component.get_gameObject`, +`0x27B Transform.GetChild` with `mov edx,3` at +`0x275`, +`0x28E
  Component.get_gameObject`, and +`0x2A4 GameObject.SetActive` with `mov dl,1` at +`0x29F`.
  - Proves: `disabled != 0` means *every frame* "not available, and show my own shadow child".
    The grey the draft puts on another seat's card is therefore the game's picture, not a mod's
    one - and since the game never writes the field (E44), a `disabled` this end leaves behind
    can only be taken back by this end. A card that is dark while the draft lets it be clicked
    is the mod forgetting its own byte, not the game having an opinion.
  - It also means writing `isAvailable` was never needed: `Update+0x25B` derives it from
    `disabled`, and E44 showed `CDUpdate+0x24F` writing the other value from the cooldown. A
    restored snapshot of `isAvailable` could only ever be a value from outside the draft - a
    card caught mid-cooldown was handed back as unavailable.
- What `CardUI.PutDown` does with it. rva `0x43E9A0`, 256 bytes: `mov edx,3` at +`0x41`, +`0x48
  Transform.GetChild`, +`0x60 Component.get_gameObject`, and the deciding `dl` is built by three
  tests in a row - +`0x96` `84 C0 75 04` (what the pointer test at +`0x8C` answered decides
  whether the rest is looked at; the no-answer path takes `xor edx,edx` at +`0x9A` and lands on
  `SetActive(false)`), then +`0x9E` the `disabled` test with `75 25` (`jne +0xC9`, where
  +`0xC9` is `mov edx,1`), then +`0xA4`/+`0xA9` `fullCD` against `CD` with `77 1A` (`ja +0xC9`,
  a cooldown that has not run out), and only then +`0xB8`/+`0xBE` `8B 43 24` (`theSeedCost`) and
  `39 81 10 01 00 00` (`cmp [rcx+0x110],eax` - `Board.theSun`, table 10) with `0F 9C C2`
  (`setl dl`, i.e. `theSun < cost`); +`0xD9 GameObject.SetActive`.
  - Proves: the shadow child of a card is on for any of three reasons - `disabled`, a cooldown
    still running, or a price this lawn cannot pay - and the player cannot tell them apart from
    the picture. So a draft mark must not be the *only* difference the mod makes to a card if a
    seat is to learn who holds it: E45 is why the owner's nickname goes onto the card as text.
- What a tool preview has to read. `Hammer` (dump.cs:75215) `Instance` 0x0 and
  `theHammerEffect` 0x60; `Wheel` (75775) `plantAxis` 0x60, `thePlantType` 0x68, `plant` 0x70 -
  and **no `Instance` field at all**, so the wheelbarrow has to be found in the scene the way
  `LevelSync` finds `StartGameBtn`. `Shovel.Instance` (75727) and `Glove.Instance` (75184) do
  exist, and `InGameTool.isPickUp` 0x2C is what all four toggle (E14: `Mouse.mouseItemType`
  never does). `Wheel.Use` rva `0x7E1CE0` calls `CreatePlant.CreatePlantPreview` at +`0x350`
  (E19) - the game already draws the wheelbarrow's ghost on a cell, so a preview of it borrows
  that door instead of inventing a picture.
- Repro: `py -3 tools/re/pvz-pat.py "80..5900" "0FB6..59"` (54 + 19 sites);
  `py -3 tools/re/pvz-hex.py 'CardUI$$Update'` reading +`0x75`..+`0x85` and +`0x255`..+`0x2A9`;
  `py -3 tools/re/pvz-hex.py 'CardUI$$PutDown'` reading +`0x3E`..+`0x65` and +`0x90`..+`0xE5`;
  `py -3 tools/re/pvz-disasm.py 'CardUI$$Update' 'CardUI$$CDUpdate' 'CardUI$$PutDown'` (the
  `GetChild`/`SetActive` pairs); `py -3 tools/re/pvz-disasm.py 'Wheel$$Use'`; and
  `--arbiter` (rows 26, measured 26, problems 0 - no door of the chooser was touched).

**E46. A card's picture is its first child's image, and the game fills it when the card is born**
- the roster's hover list needs one plant icon per seat, and the only honest source for it is the
  chooser's own picture.

- `CardUI.ChangeCardSprite`, rva `0x43D5C0`, 135 bytes: +`0x2A Component.get_transform`, `33 D2`
  (`xor edx,edx`) at +`0x28` before +`0x3C Transform.GetChild` - so the child the card paints is
  index **0** - then +`0x50 Component.GetComponent<object>` with the `UnityEngine.UI.Image` type
  token taken at +`0x43`, and +`0x77 UIExtend.SetIcon` with `8B 93 88 00 00 00` at +`0x68`
  (`mov edx,[rbx+0x88]` = `thePlantType`, dump.cs:21251) and two `movss` of float literals
  (`F3 0F 10 1D/15` at +`0x55` and +`0x5D`).
- Who calls it: `CardUI.Awake+0xC1` and `SeedLibrary.CreateCard+0x15B`, 9 sites in all. A card
  therefore carries its plant's picture from the moment it is made, whether or not the page it
  sits on was ever opened - which is what lets a lookup over every `CardUI` find the icon of a
  plant whose page is closed.
- `UIExtend.SetIcon(Image, PlantType, float, float)`, rva `0x8E0030`, 624 bytes: a
  `Dictionary<Int32Enum, object>.TryGetValue` at +`0x0FD` keyed by the plant type, whose answer
  reaches +`0x12E Image.set_sprite`; then `RectTransform.set_sizeDelta` at +`0x1D0` and
  `Transform.set_localScale` at +`0x249`, both worked out of the two float arguments by the
  `divss`/`mulss` arithmetic at +`0x190`..+`0x230`. So only the sprite is the game's own: the
  size is arithmetic whose literals are not named anywhere the tools resolve, and a copied
  picture that has to fit a row the mod laid out sets its own box and lets Unity's
  `Image.preserveAspect` letterbox the sprite inside it.
- The mouse is asked the way the game asks it: `UnityEngine.Input.get_mousePosition` (rva
  `0x2826A50`) has **58** static callers in this image, so the legacy input module is live here,
  and `RectTransformUtility.RectangleContainsScreenPoint(RectTransform, Vector2, Camera)` is in
  the same engine build (dump.cs:783283); the camera it wants is the canvas's `worldCamera`,
  which answers null for an overlay canvas and is then passed on as null.
- Repro: `py -3 tools/re/pvz-disasm.py 'CardUI$$ChangeCardSprite'`;
  `py -3 tools/re/pvz-hex.py 'CardUI$$ChangeCardSprite' 'UIExtend$$SetIcon'` reading
  +`0x28`..+`0x77` and +`0x0F0`..+`0x249`;
  `py -3 tools/re/pvz-xref.py 'CardUI$$ChangeCardSprite'` (9 callers);
  `py -3 tools/re/pvz-xref.py 'UnityEngine.Input$$get_mousePosition'` (58 callers).


**E47. What a hand holds is a scene object, and the layer a plant stands on is its own**
- four live reports about the preview of a tool: the hammer, the cart and the glove drew
  under the plants, the fertilizer drew nothing at all, and a plant that was fertilized kept
  the sprite of its old stage.
- The four tools are one family: `Shovel`, `Glove`, `Hammer` and `Bucket` all extend
  `InGameTool` (`isPickUp` 0x2C, E14), and `Fertilize : Bucket` (dump.cs:67390, E41). The bag
  a hand holds is therefore a `Bucket` standing in the lawn, not a card in the strip - so a
  preview of it has to read the object, exactly as the glove's carried plant is read.
- `Fertilize.Pick/0` (`0x737730`, 96 bytes, slot 8) is two calls: `Bucket.Pick` +`0x2A` and
  `Animator.SetTrigger` +`0x47`. `Bucket.Pick/0` (`0x4611B0`, 288 bytes) is the door that
  fills a hand, and the writes are direct memory stores on the `Mouse` this tool carries in
  its own `m` field (dump.cs:22612, `protected Mouse m; // 0x28`):
  - +`0x53` `C6 43 21 01` - `Bucket.isLand` (0x21) is written 1 by `mov byte [rbx+0x21],1`.
  - +`0x41` `48 8B 7B 28` reads `protected Mouse m; // 0x28` (dump.cs:22612) into rdi, +`0x57`
    is `Component.get_gameObject`, and +`0x65`/`0x69` `48 8D 4F 50` + `48 89 47 50` store that
    answer at `[m+0x50]` = `Mouse.theItemOnMouse`. **A scene object, not a card.**
  - +`0x82` `48 C7 41 70 00 00 00 00` clears `Mouse.thePlantOnGlove` (0x70) and +`0x9E`
    `C7 40 38 FF FF FF FF` writes `Mouse.thePlantTypeOnMouse` (0x38) = **-1**. That is why a
    preview that waits for a card type on the mouse sees no plant: the game puts no card
    there, it puts a scene object.
  - +`0x101` `Renderer.set_sortingLayerName` (after `GameObject.TryGetComponent` +`0xE4`):
    the game itself re-sorts the picture a hand holds, so a copied picture has to be told
    which layer to sit on.
- `MouseItemType` (dump.cs:86472) has `Bucket = 7` and `Bean = 8`, and **no byte in the image
  writes either of them, or 3, 4, 5 or 6, into the field**: `pvz-pat.py` finds 0 sites for
  `C7 <modrm> 60 00 00 00 <value> 00 00 00` and `C6 <modrm> 60 00 00 00 <value>` over modrm
  `80 81 82 83 86 87`. `Mouse.mouseItemType` (0x60) is only ever the four tool values E14
  measured, so a preview of the fertilizer that waits for `mouseItemType == 7` never draws -
  which is what the mod reads instead: `theItemOnMouse`'s own `Fertilize` component.
- Why the ghosts were under the plants: the body that stands a plant up,
  `CreatePlant.SetPlant/9` (`0x455A00`), hands it to `CreatePlant.SetLayer/3` (`0x4543D0`, 1,040
  bytes, dump.cs:22125), and that body paints through `UnityEngine.Rendering.SortingGroup` -
  `set_sortAtRoot` at +`0x184` and +`0x305`, `set_sortingLayerName` at +`0x257`, +`0x35D`,
  +`0x39A`, `set_sortingOrder` at +`0x26E`, +`0x31C`, +`0x3CA`. `Fertilize.Upgrade` calls
  `CreatePlant.SetPlant` 14 times (+`0x458`..+`0xCCB`). Unity compares `sortingOrder` only
  inside one sorting **layer**, so a ghost that borrowed the plants' layer and gave itself
  order 9000 sat under the lawn, and a ghost that stayed on the default layer sits under it
  however high its order. The mod therefore reads the layer off the first standing plant's
  `SortingGroup` and puts its tools on it (E19, E21: the tools have no preview method of
  their own to borrow a layer from).
- Why the fertilized plant kept its old picture: the body that changes it, `Fertilize.Upgrade`
  (`0x737B30`, 3,952 bytes, E41), re-mints stages through `CreatePlant.SetPlant` and destroys
  and instantiates objects (+`0xE04`, +`0xEF3` `Instantiate`, `0x172`, `0xE45`, `0x146`
  `FreezedPlant.SetEntity`, `0x35D` `Plant.set_AttributeCountdown`) but no `Image.sprite`,
  no `SpriteRenderer` and no `ReplaceSprite` call. `Plant.ReplaceSprite` (dump.cs:30316, rva
  `0x3ED8E0`, slot 30) is
  `protected virtual` with **zero** callers in the image (`pvz-xref.py` prints
  "looking for 0 target(s)"), so there is no game door here that repaints a plant in place -
  the fix needs the live `[fert]` lines first, because a re-minted plant may arrive through a
  body the watch already names.
- Repro: `py -3 tools/re/pvz-hex.py 'Fertilize$$Upgrade' 'Bucket$$Pick'` reading
  +`0x55`..+`0x101`; `py -3 tools/re/pvz-disasm.py 'Fertilize$$Pick' 'Fertilize$$Upgrade'
  'CreatePlant$$SetPlant'` (the `Pick` -> `Bucket$$Pick` tail call, the 6 `SortingGroup`
  setters, the `Instantiate`/`Destroy`/`SetEntity` sites); `py -3 tools/re/pvz-xref.py
  'Plant$$ReplaceSprite'` (0 callers); `py -3 tools/re/pvz-pat.py
  `C7 40 38 FF FF FF FF` (0 sites each) and `grep -n "enum MouseItemType" -A 14
  ref/dump-en/dump.cs`; `--arbiter` (26 rows, 26
  measured, 0 problems - no door of the game was touched, all four claims are reads).

**E48. A fertilizing is one lawn's own act, and `Plant.Recover` takes an amount and not a
level**
- three live reports asked the question: a fertilized plant that healed on one seat stayed damaged
  on the other, a tool's preview drew only on the seat holding it, and the hammer and the zombie
  glove drew bigger than a cell.
- `Fertilize.Use/2` (`0x738AA0`, 1,680 bytes) is a moving-and-sounding body, not a changing one.
  Its resolved calls are the lawn's own cell list (+`0x170`, +`0x416` `List<KeyValuePair<int,
  int>>.GetEnumerator`, +`0x1CE`/`+0x45F` `MoveNext`), the fertilizer's picture being carried
  (+`0x295`/`0x4EA` `Transform.get_position`, +`0x2E4`/`+0x539` `Vector3Extensions.Add`,
  +`0x30E`/`+0x563` `Transform.set_position`), an animation fade (+`0x32E`/`+0x58D`
  `Animator.CrossFade`), a sound (+`0x35F`/`+0x5BE` `GameAPP.PlaySound`), a wait of one frame
  (+`0x373`, +`0x386` `WaitForUpdate.MainThreadAwaiter.GetResult`) and the object being put away
  (+`0x60A` `GetComponent<object>`, +`0x61C` `Behaviour.set_enabled`). **No `Plant.Recover`, no
  `TakeDamage` and no `CreatePlant.SetPlant` is called by it**, and `Fertilize.Use/0` (`0x739130`,
  48 bytes) is nothing but the call of that door at +`0x1B`, so all three ways a cell gets a
  fertilizer - a click on the held one, `Fertilize.Update` +`0x369`, and the drop itself - walk
  one body.
- The body that does change the plant cannot be replayed either: `Fertilize.Upgrade` (`0x737B30`,
  3,952 bytes) re-mints through 14 `CreatePlant.SetPlant` sites (+`0x458`..+`0xCDB), and that
  door is `HostOnly`/`Never` in the coverage table - a mint travels as the layout and never as a
  call (E41). `Fertilize.Update` (`0x7377B0`, 896 bytes) rolls its own answer through
  `ListExtensions.GetRandom<object>` at +`0x33D`, and a body that reaches `UnityEngine.Random`
  cannot be `Replicate` at all. So a fertilizing has no door to send: what travels is the cell it
  happened on, and every lawn changes its own plant with its own numbers.
- The heal therefore goes through `Plant.Recover` (dump.cs:30304, rva `0x4D1260`, 2,496 bytes,
  slot 27, `public virtual void Recover(float health, DamageType damageType = 0, bool particle =
  True, bool continuous = False)`), and its float is **the amount to put back, not the level to
  reach**. Two overrides say which is which:
  - `UltimateBamboo.Recover` (`0x593C80`, 112 bytes) asks `Lawnf.TravelUltimate` (+`0x028`) and,
    when that answers true, doubles the float it was handed - `addss xmm6,xmm6` at +`0x0032` -
    and tail-calls `Plant.Recover` at +`0x0068`. Doubling an amount is a stronger heal.
  - `ThreeNut.Recover` (`0x58A8A0`, 192 bytes) calls `Plant.Recover` with the float unchanged
    (+`0x0025`) and *then* works the total out for itself: `max/3` at +`0x003E` (`mov eax,
    0x55555556`, `imul qword [rbx+0xFC]`) or `2*max/3` at +`0x005D` (`add ecx,ecx`, `imul ecx`),
    each clamped against the health already standing (`cmp`/`cmovl` at +`0x0050`/`+0x0052` and
    +`0x0074`/`+0x0076`) and written straight into the field - `mov [rbx+0x100],edx` at
    +`0x007B`. A subclass that wants a level writes the level; it does not hand one to `Recover`.
  Fields: `thePlantMaxHealth` 0xFC, `thePlantHealth` 0x100 (dump.cs:30137); `DamageType.Normal`
  is 0 (dump.cs:78667).
- The tool objects of the strip are on every lawn before any hand takes one: `Shovel.Awake`
  (`0x7DD070`, 96 bytes) runs the static-init guard (+`0x0006` `80 3D ... 00`, +`0x001F`
  `C6 05 ... 01`), reads the `Shovel` statics (+`0x0026` `mov rax,[rip+...]`, +`0x002D`
  `mov rdx,[rax+0xB8]`) and stores `this` there with `48 89 1A` (`mov [rdx],rbx`) at +`0x0033` -
  not inside a branch, so `public static Shovel Instance; // 0x0` (dump.cs:75727) names a live
  object from the moment the level's objects are born. That is why a preview no longer has to ask
  a hand: a seat that is holding nothing still has the tool standing in its own lawn.
- The four tools are one family - `Glove : InGameTool` (dump.cs:75181), `Hammer : InGameTool,
  IDamageMaker` (75215), `Shovel : InGameTool` (75724), `Wheel : InGameTool` (75775), with
  `InGameTool : MonoBehaviour, IClickable` (75271) and `Fertilize : Bucket` (E41) - so each of the
  four arrays `Resources.FindObjectsOfTypeAll` answers for them is also an `InGameTool[]`, and one
  line can hold any tool of the strip. The size a ghost has to fit is not in the image: it is
  `GridManager.GetGridCenter` one column and one row apart, which is what the game's own cells are.
- Repro: `py -3 tools/re/pvz-disasm.py 'Fertilize$$Use' 'Fertilize$$Update' 'Fertilize$$Upgrade'`
  (the two overloads, the `GetRandom`, the 14 `SetPlant` sites, and no recover or damage door);
  `py -3 tools/re/pvz-disasm.py 'UltimateBamboo$$Recover' 'ThreeNut$$Recover'` then
  `py -3 tools/re/pvz-hex.py 'UltimateBamboo$$Recover' 'ThreeNut$$Recover'` reading
  +`0x0032` and +`0x003E`..+`0x007B`; `py -3 tools/re/pvz-hex.py 'Shovel$$Awake'` reading
  +`0x0026`..+`0x0035`; `py -3 tools/re/pvz-xref.py 'Fertilize$$Use'` (3 callers);
  `grep -n "^public class \(Glove\|Hammer\|Shovel\|Wheel\|InGameTool\)" ref/dump-en/dump.cs`;
  `--arbiter` (26 rows, 26 measured, 0 problems - no door of the game was touched and no row was
  added, all five claims are reads).


**E49. A card is two images on two GameObjects: the frame it carries and the plant it paints**
- the roster's preview became the game's own seed card drawn small, so the frame needed a source as
  measured as the picture's (E46) rather than a border the mod would have drawn itself.

- `CardUI.SetImage(int)`, rva `0x43EAA0`, 192 bytes: `48 8B F9` (`mov rdi,rcx`, `this`) at +`0x19`,
  `48 8B 87 98 00 00 00` (`mov rax,[rdi+0x98]` = `sprites`, dump.cs:21254) at +`0x47`, the bounds
  `3B 58 18` and `7D 3F` at +`0x57`/+`0x5A`, then `48 8B CF` (`mov rcx,rdi`) at +`0x64` immediately
  before +`0x067 Component.GetComponent<object>` - the receiver is the card and not one of its
  children - and +`0x097 Image.set_sprite` with the answer of `List.get_Item` (+`0x084`) in `rdx`.
- `CardUI.SetImage(CardBgType)`, rva `0x43EB60`, 224 bytes: the same shape on a background chosen by
  type - `48 8B D9` (`mov rbx,rcx`) at +`0x14`, `48 8B CB` (`mov rcx,rbx`) at +`0xA5`,
  `GetComponent` at +`0x0A8`, `Image.set_sprite` at +`0x0BD` - and `89 BB 90 00 00 00` at +`0xC2`
  writing `cardBgType` (dump.cs:21253) after the sprite is already on the image.
- Both overloads therefore paint **the `Image` on the card's own GameObject**, while
  `CardUI.ChangeCardSprite` paints the image of `GetChild(0)` (E46). Two images, two objects, one
  card: a preview that copies the first pair of `sprite`/`material` and the second shows the game's
  own card and nothing of the mod's drawing.
- Who calls them: 13 sites, among them `CardUI.SetLevel+0x6`, `SeedLibrary.<SetNormalCards>b__15_0`
  at +`0x2EF` and +`0x545` and `SeedLibrary.<>c.<SetColorfulCards>b__16_0+0xD1` - the chooser paints
  a card's frame while it builds its pages, so a card on a page the player never opened carries both
  images just as it carries its picture (E46), which is what lets the lookup take the deck's card
  when there is one and any card of the menu when there is not.
- The shape needs no literal of the game's either: the face is
  `card.GetComponent<RectTransform>().rect` - the same measurement the holder's name is sized from
  (E45) - and the `divss`/`mulss` arithmetic of `UIExtend.SetIcon` (E46) reaches the picture child
  alone, so the frame keeps the card's own ratio at whatever box the list hands over.
- Repro: `py -3 tools/re/pvz-disasm.py 'CardUI$$SetImage'`;
  `py -3 tools/re/pvz-hex.py 'CardUI$$SetImage'` reading +`0x19`..+`0x97` and +`0xA5`..+`0xC2`;
  `py -3 tools/re/pvz-xref.py 'CardUI$$SetImage'` (13 callers);
  `grep -n "thePlantType\|sprites\|cardBgType" ref/dump-en/dump.cs | sed -n '1,40p'`;
  `--arbiter` (26 rows, 26 measured, 0 problems - nothing of the game is patched here, both claims
  are reads of the chooser's own painting, and no coverage row was added).


**E50. What a lawn plays and the room never said: `GameAPP.PlaySound`, and the doors that own a sound**
- Before this, no line of `Core` carried a sound. The only place the door appears in the mod is the
  probe spec `M("app-state", "GameAPP", "PlaySound", 3, "Void")` (`Core/GameAPI/NativeProbe.cs:438`),
  and the relay of `EffectSync` was gated twice over: only what a particle door made while a death was
  on the stack, and only when the sender was the host. Both halves of the customer's complaint - a
  fusion and a mower that present on one screen only, and zombies that fall silently - sit in those two
  gates, and the silence sat in the missing door.
- `GameAPP.PlaySound` is static with two overloads, rva `0x71FCA0` and `0x71FBE0`, given
  `(SoundType, float theVolume = 0.5, float pitch = 1)` at dump.cs:66535 and
  `(int theSoundID, float, float)` at dump.cs:66539, and the second carries
  `[AsyncStateMachine(typeof(GameAPP.<PlaySound>d__71))]` at 66537 while the first does not: the
  `SoundType` one converts and the id one is the body. Between them 1445 call sites, which is why the
  arguments are read by position in a prefix and why one patch class names the method and not the
  overload.
- The death sounds are per zombie type and not one place: 11 call sites sit in the `*Zombie.DieEvent`
  bodies (`BlackTrainZombie.DieEvent` at +`0x0F6` with its particle door next to it at +`0x1DC`,
  `CatapultZombie.DieEvent` and `SubmarineZombie.DieEvent` at +`0x085`,
  `SuperMachineNutZombie.DieEvent` at +`0x0AA`), so a room that wants the sound of a death has to ask
  the door and not the type.
- The mower's own sound is in `Mower.StartMove` (rva `0x4A70B0`, 256 bytes: +`0x087
  GameAPP.PlaySound`, +`0x0AB BoardSpawner.MowerStart`, +`0x0C7 Animator.SetTrigger`, +`0x0ED
  Rigidbody2D.set_velocity`). Its kill is not a name: `Mower.AttackZombie`, rva `0x4A6930`, 64 bytes,
  resolves to no calls at all because its tail is a jump through the zombie's own table with the
  reason already in `edx`, so which death door the row falls through cannot be read here and both
  mower doors are counted as the event.
- The fusion burst is made in `CreatePlant.MixEvent(PlantType, Plant, int)`, rva `0x452EF0`, 1504
  bytes: +`0x4E8 ParticleManager.SetParticle`, +`0x4FA Random.Range`, +`0x52B GameAPP.PlaySound`,
  +`0x582 BoardAction.CreateCherryExplode`, +`0x5C4 BoardAction.CreateFreeze`. Its table of callers is
  empty, and the recipe body `CreatePlant.CheckMix` (rva `0x44F2C0`, 8192 bytes with the resolved calls
  reaching +`0x1DF9`) names only `MixData.TryGetMix`/`TryGetDisMix`/`TryGetRandomMix` and
  `CreatePlant.MixFail` at +`0xA09` - so the burst is counted as an event where it is played rather
  than where it is decided, and both are watched because which extent the sound sits in is a thing the
  live log answers and the disassembly does not.
- The particle door has exactly one overload: `ParticleManager.SetParticle(ParticleType, Vector2,
  int layerRow = 11, bool lim = True, float scale = 0)`, rva `0x4CA480` (dump.cs:29792). The
  two-argument `SetParticle(ParticleType, Vector2)` at dump.cs:66963 belongs to `Garden` (dump.cs:66849),
  the zen garden, and is not on a lawn's table - the `__args.Length < 5` guard of the relay therefore
  skips nothing today, which removes the suspicion raised while this stage was being planned.
- A death is synchronous, and that is what lets a count say whether a sound belongs to one:
  `Zombie.Die(int reason = 0)` is slot 34 (dump.cs:64696) and `Zombie.DieEvent(int reason = 0)` slot 35
  (dump.cs:64711) and neither carries `[AsyncStateMachine]`. The async `DieEvent` state machines in the
  dump are plants' (`FlyingThreePeater_sp.<DieEvent>d__2` at dump.cs:46378,
  `UltimateImpKing.<DieEvent>d__12` at 63164), so a plant's own death presentation is a different
  question and belongs to the wheel's take (14d), not here.
- Repro: `py -3 tools/re/pvz-xref.py 'GameAPP$$PlaySound'` (two addresses, 1445 callers);
  `py -3 tools/re/pvz-disasm.py 'Zombie$$DieEvent'` and count the `GameAPP$$PlaySound` lines;
  `py -3 tools/re/pvz-disasm.py 'Mower$$StartMove'`; `py -3 tools/re/pvz-disasm.py
  'Mower$$AttackZombie'` (64 bytes, no resolved calls); `py -3 tools/re/pvz-disasm.py
  'CreatePlant$$MixEvent'`; `py -3 tools/re/pvz-disasm.py 'CreatePlant$$CheckMix'`;
  `py -3 tools/re/pvz-xref.py 'CreatePlant$$MixEvent'` (no static caller);
  `grep -n "PlaySound\|Slot: 3[45]\|SetParticle(ParticleType" ref/dump-en/dump.cs`;
  `--arbiter` (26 rows, 26 measured, 0 problems - every door named here is watched, none is called on
  another end's behalf, so no replication decision enters the table and no row was added).

**E51. `PlaySound` has two doors and one of them is a jump - and it is MelonLoader that patches them**
- The overload a caller of the game's own sound names is the `SoundType` one, rva `0x71FCA0`, 104 bytes, and
  its body is a wrapper: `8B CB` (`mov rcx,rbx`, the sound id) with the two floats restored from the stack,
  then `+0x063 E9 D8 FE FF FF` - a **tail jump**, `0x71FCA0 + 0x68 - 0x128 = 0x71FBE0`, which is
  `PlaySound(int theSoundID, float theVolume, float pitch)`, the async one (`[AsyncStateMachine]`, dump.cs:66537).
  So one door on the `int` overload hears every sound of the game, and a door on each of the two would say
  every `SoundType` sound twice - which is what the first shape of `SoundSync` would have done.
- Watch `GameAPP.PlaySound` by name alone and Harmony resolves nothing: the assembly's own pass over
  `PvzMP.Core.GameAPI.SoundSync+SoundDoor` threw `HarmonyLib.HarmonyException: Ambiguous match for
  HarmonyMethod[(class=Il2Cpp.GameAPP, methodname=PlaySound, type=Normal, args=undefined)]`, inner
  `System.Reflection.AmbiguousMatchException` through `System.Type.GetMethod(String, BindingFlags)` -
  `D:\PvzFusion2\MelonLoader\Logs\26-11-13_9-46-50.log:67-68`, same at :67-68 of the other seat's log.
  Naming the argument types, as `FertilizeGate` already does for the two `Fertilize.Use` overloads, is the
  answer that works.
- **Who installs an attribute door.** The exception came from `MelonBase.HarmonyInit()`, i.e. from the pass
  MelonLoader runs over the melon's own assembly at setup - and in that very run the mod's line
  `the game's sounds are watched` had been printed at :240 with no failure under it, while the log contains
  not one sound the door heard. Both facts fit a single reading: `new Harmony("PvzMP.sound")
  .PatchAll(typeof(SoundSync))` resolves nothing for the *nested* patch class, and the doors that
  demonstrably run - `the drop of a fertilizer on cell 1,0`, `placement asked for type 0 cell 0,0`, the
  death and mower counts - are installed by MelonLoader's pass, once, since no line of theirs is doubled
  anywhere. Consequences kept in the code: an "are watched" line is a request and not a proof, the `catch`
  around `PatchAll` cannot fire for a nested door, and what a door really did is measured by counting
  inside it (`this lawn heard N sound(s), said K effect(s) and played M of the room's`).
  `EffectSync.ForgetTheLawn` was itself missing from `LevelSync`'s cleanup list, which already calls nine
  others, so that count was printed by nobody until now.
- Repro: `py -3 tools/re/pvz-index.py --method GameAPP@PlaySound` (104 bytes, one resolved call);
  `py -3 tools/re/pvz-hex.py 'GameAPP$$PlaySound'` (the `E9 D8 FE FF FF` at +0x063 of the first block);
  `grep -n "PlaySound" ref/dump-en/dump.cs` (the two declarations at 66535 and 66539);
  `grep -n "Ambiguous match\|sounds are watched\|said sound" D:/PvzFusion2/MelonLoader/Logs/26-11-13_9-46-50.log`.

**E52. A fusion click walks `Mouse.LeftClickWithSomeThing` and nothing else**
- The question `MergeGate` was written to ask is answered by the two live logs of 2026-11-13: of its three
  watches, `the click found something under the pointer` fired 27 times on the peer's log and 14 on the
  host's, while `the game is reinforcing a plant in place` and `the game is turning plants into a new plant`
  fired **zero** times on both. The numbers printed with them agree: `held type 256, plants under the
  pointer 1`.
- So the fusion is not a named door of `Mouse` at all, and the only way to make it happen on a lawn is the
  way the host already does it: let that lawn run the game's own creator on the cell, which reaches
  `CreatePlant.CheckMix` -> `MixEvent` (measured in E50 as 8192 bytes wide with no static caller). What a
  seat does today instead is apply the host's diff, which is why a fusion looks to that seat like a plain
  replacement of two plants by one.
- Repro: `grep -c "the click found something under the pointer" D:/PvzFusion2/MelonLoader/Logs/26-11-13_9-46-50.log`
  (27), the same two greps for `reinforcing` and `turning plants into` (0 each), and
  `grep -o "while the room decides: cell.*"` for the cells and held types.

**E53. A fertilizer really grows the plant on the lawn that held it, and the host's layout then eats it**
- The live lines say it in the game's own numbers. On the **peer** three growths are printed by the watch
  around `Fertilize.Upgrade`, e.g.
  `- obj #-81638 type 3 cell 1,0 hp 2400/4000 + obj #-83076 type 1027 cell 1,0 hp 8000/8000`, and on the
  host four of them, one on cell 2,1. `type 3` is `PlantType.WallNut` and `1027` is `PlantType.TallNut`
  (dump.cs, `public enum PlantType`), so the growth is real on both ends, and the object id changes - a
  replacement of the plant and not a change in place, which is the question E48 left open.
- What follows is the room's fault and not the game's. Seventy milliseconds after that line the peer's log
  reads `SpawnPlant -> native creator returned no object`, `SpawnPlant(col=1,row=0,type=3) -> 0`,
  `KillPlant(#-83076,reason=0) -> ok`, `the host's plant layout arrived: 5 plants, 0 spawned, 0 moved,
  0 retyped, 1 removed here` - the cell ended empty. The reason is the id: the correction keys on the
  *bound* instance id, which the growth replaced, so it takes the "no plant there" branch, asks the
  creator to put a walnut on a cell that is not free, gets nothing, and the loop that removes what the
  layout did not name then kills the grown nut.
- Two consequences carried forward: what a fertilizer grew has to travel with the cell, because the layout
  only ever travels from the host and the host was told only to heal; and a wanted entry of a layout has to
  be answered by the plant standing on its **cell** before any creator is asked.
- Repro: `grep -n "the body that changes the plant left" <both logs>`;
  `sed -n '7490,7515p' D:/PvzFusion2/MelonLoader/Logs/26-11-13_9-46-50.log`;
  `awk '/^public enum PlantType/,/^}/' ref/dump-en/dump.cs | grep -E "WallNut = 3;|TallNut = 1027;"`.

**E54. The hammer hits zombies its own physics query found, and its blow body has no caller**
- `Hammer.Use(Mouse)` (rva `0x7D1840`, 1568 bytes, 19 resolved calls) is the whole click: `Mouse.MousePosition`
  -> `Camera.main` -> `Camera.ScreenToWorldPoint` -> `LayerMask.GetMask` -> `Physics2D.OverlapCircleAll` ->
  `Component.TryGetComponent<Zombie>`, and then for what came back `Collider2D.get_bounds` ->
  `BoardAction.CreateCherryExplode` -> `Zombie.AddfreezeLevel`; after that `CreateParticle.SetParticle`,
  `GameAPP.PlaySound`, `InGameTool.PutDown`, `Mouse.ClearItemOnMouse`. The pointer and the engine's query
  belong to this machine, so the click itself is not a call the room can pass on.
- The blow writes through one door: `Zombie.AddfreezeLevel(int level, int force = 0)` (rva `0x702A80`, 96
  bytes, zero resolved calls, one declaration in the dump). The mod witnesses that step while the tool body
  runs, which is the game's own answer to "which zombies did this click touch" - a hit test the mod wrote
  itself would only be a second answer that can disagree.
- `Hammer.EffectZombie(Zombie)` (rva `0x7D11A0`, 356 bytes, cats `o`, effect `world`, no `UnityEngine.Random`)
  is those same three calls with the zombie as its one argument, so it is the blow on a *named* zombie and
  the row the room repeats: `Hammer.EffectZombie/1`, `Replicate`, `AnyPeer`. `pvz-xref.py` finds **no static
  caller** for it, and the tool's other two doors - `Hammer.CrushZombie` (`0x7D1110`) and `Hammer.AnimCrush`
  (`0x7D0BA0`, an animation event with an instantiate and a sound in it) - are the callers a static scan
  cannot see. All three are therefore watched and only the effect body is covered. **E60 corrects this row:
  those three calls write no damage, and the covering row is `Zombie.TakeDamage/5`.**
- `Hammer` keeps a `public static Hammer Instance`, which is what lets the applier resolve the `~` target of
  the key without the mod holding a game object of its own.
- Still open, and the log has to say it: which of the three doors a live click walks, and whether the room's
  replay of `EffectZombie` leaves the same amount of damage the inlined body left on the lawn that clicked.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Hammer$$Use'` and the same for
  `'Hammer$$EffectZombie'`, `'Hammer$$CrushZombie'`, `'Zombie$$AddfreezeLevel'`;
  `py -3 tools/re/pvz-xref.py 'Hammer$$EffectZombie'` (no static caller found);
  `grep -n "public static Hammer Instance" ref/dump-en/dump.cs`;
  `grep -n "AddfreezeLevel" ref/dump-en/dump.cs` (one declaration).

**E55. The cart is one body holding both halves of a move, and its pocket is private**
- `Wheel` is an `InGameTool` with three methods of its own (`OnUpdate`, `Use(Mouse)`, `.ctor`) and four
  fields; the two that matter are `private GameObject plant` (offset `0x70`) and `private SavePlantData
  savedData` (offset `0x78`). Both are private, so the mod cannot read the pocket to tell a lift from a
  drop - it has to remember what *it* named on the previous click. Proof: see `ref/dump-en/dump.cs` line
  75775 (`grep -n "public class Wheel : InGameTool" ref/dump-en/dump.cs`).
- `Wheel.Use` (rva `0x7E1CE0`, 1873 bytes, 37 resolved calls, cats `o`, effect `world`) is the whole
  two-click tool in one body, in this order:
  `InGameTool.PutDown` +0x12E, `Mouse.ClearItemOnMouse` +0x144, `Mouse.GetPlantsOnMouse` +0x158,
  `List<object>.RemoveAll` +0x217, `GameAPP.PlaySound` +0x267, `SavePlantData..ctor` +0x284,
  `Lawnf.TravelAdvanced` +0x2CD, `CreatePlant.CreatePlantPreview` +0x350, `Transform.set_parentInternal`
  +0x470, `Object.Destroy` +0x4CF / +0x4EE / +0x5A0, `CreatePlant.SetPlant` +0x651, `Object.Destroy`
  +0x6B3, `Lawnf.TravelAdvanced` again +0x6BF, `SavePlantData.LoadData` +0x70B. One body, one click, and
  the click both takes a plant off a cell and puts one down.
- The cart therefore cannot travel as a universal call. Its drop half ends in `CreatePlant.SetPlant/9`,
  which is a `Never` row in the arbiter (an animation event whose 9 arguments this end cannot name), and
  its state half is `SavePlantData`, whose 20 public fields would need a codec the mod wrote itself.
- Nor can the mod watch the engine's own steps to find the cart: `pvz-xref.py` reports 8 static callers of
  `SavePlantData..ctor` and 6 of `SavePlantData.LoadData`, so a door on either would fire for every other
  user and could not say "this is the cart". The cart's own click is the door, and the click is measured.
- The room repeats the *move*, not the two halves of the body: one `PlantMove` frame with the room id of
  the plant the lift named, applied with `Plant.MoveBox` plus `GridManager.GetGridCenter` plus
  `AdjustPosition`. That is a deliberate deviation from the mechanism offered for fork 3 ("carry the saved
  state exactly through the game's own damage door"): despawning and respawning the plant would rebuild it
  from `CreatePlant` defaults, which resets `theLevel`, `thePlantStage` and the attributes, while the move
  keeps this lawn's own object and therefore every number on it by construction. Proof: `grep -n "MoveBox"
  ref/dump-en/dump.cs`; `grep -n "thePlantHealth\|theLevel\|starUp" ref/dump-en/dump.cs`.
- The lift is aimed by the game's own hit test, not by a cell the mod guessed: `Wheel.Use` reads
  `Mouse.GetPlantsOnMouse` at +0x158, which is the same query `Cursor.ReadPlantInstanceIdsUnderCursor`
  reads, and `ShovelRules.TrackedPlant` names the room plant from those instance ids. The hovered cell
  (`TrackedPlantAt`) is the fallback for a level whose mouse has no list yet.
- `WorldSync.ApplySpawn` gained the consequence: the plant standing on a spawn's cell is now the answer on
  **every** seat, including the seat that clicked, because a cart drop that reached its cell already moved
  an object there and a second creator on one cell is a difference every later layout must take down.
- Still open, and the live log has to say it: whether one live click is one `Use` call or two; whether the
  lift leaves the object standing (the log line reports "still stands on this lawn" or "is gone"); whether
  the drop rebuilds instead of moving; and whether a lift can be abandoned mid-tool.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Wheel$$Use'`;
  `py -3 tools/re/pvz-disasm.py 'Wheel$$Use'` (read the ordered call list);
  `py -3 tools/re/pvz-xref.py 'SavePlantData$$LoadData'`;
  `py -3 tools/re/pvz-index.py --class 'Wheel'`;
  `grep -n "the cart lifted plant \|the cart rebuilt plant \|the cart moved plant " D:/PvzFusion*/MelonLoader/Logs/*.log`.

**E56. A fertilizer really grows by destroying the old object and asking the creator with the free flag**
- `Fertilize.Upgrade` (rva `0x737F30`, 3946 bytes, 43 resolved calls) is the growth body the fertilizer
  runs a third of a second after its drop. Its first act on the plant it was aimed at is
  `Component.get_gameObject` +0x150 -> `Object.Destroy` +0x172. The kind that arrives is made later by
  `CreatePlant.SetPlant` (+0x458 and fifteen more call sites in the same body). The first of those calls
  sets `r9d = 0x431` (1073, the kind) and two stack booleans to 1 (`C6 44 24 30 01`, `C6 44 24 38 01`)
  after putting `null` in the target-plant slot: that is `SetPlant(column, row, type, null, puffV,
  isFreeSet=true, withEffect=true, hidplant=null, travelRogueData=null)`.
- Why the flag matters, measured live: a plain `SpawnPlant` on a cell the game still counts as occupied
  returns nothing. The 12:23 peer log reads `KillPlant(#-83076,reason=0) -> ok` followed by
  `SpawnPlant -> native creator returned no object`, and the cell ends empty (E53). The old body was killed
  by the mod before the creator was asked; the game's own growth destroys first and then asks with the
  free flag, so `Plants.Grow` does exactly those two native steps and nothing else.
- Consequence: `FertilizeSync.GrowTheCell` and the layout's retype branch both answer with
  `Plants.Grow(oldInstance, cell, newType)`, which returns the new object's instance id so the room id can
  be rebound to it. The animation, sound and effect of the game's own body are not repeated; the room
  already carries the kind and the heal (E53).
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Fertilize$$Upgrade'`;
  `py -3 tools/re/pvz-hex.py 'Fertilize$$Upgrade'` (read +0x150..+0x460);
  `grep -n "native creator returned no object" D:/PvzFusion*/MelonLoader/Logs/*.log`;
  `grep -n "GrowPlant ->" D:/PvzFusion*/MelonLoader/Logs/*.log`.

**E57. The hammer's kill is the animation event, and its damage door is an interface dispatch to `TakeDamage`**
- Live, every click runs two bodies a third of a second apart. The 12:23 peer log has
  `this lawn's hammer fell and touched nothing frame=7530` (the `Use` window) and then
  `the hammer door the animation event of a crush ran while the room decides frame=7565`, and the zombies
  died. `Use` asks the engine around `Mouse.MousePosition`; the crush asks around the tool's own transform.
  So the window that witnesses a live blow is `Hammer.AnimCrush`, not `Use` (E54).
- `Hammer.AnimCrush` (rva `0x7D0BA0`, 1220 bytes) has no resolved damage call. It computes the damage
  (`ShootingManager.get_Damage` + `Core.Lawnf.Multiply`), finds the zombies with
  `Physics2D.OverlapCircleAll` + `TryGetComponent<object>`, and then at +0x30F calls the IL2CPP interface
  dispatcher at rva `0x2D00` with `ecx = 0x0E`, `rdx` = the zombie, `r8d` = the damage, `r9` = the tool,
  and stack slots `[rsp+0x20] = 1` (`DamageType.NormalAll`), `[rsp+0x28] = 270` (`PlantType` of the
  hammer), `[rsp+0x30] = false`. The five arguments after the object are exactly
  `TakeDamage(int theDamage, IDamageMaker damageFrom, DamageType, PlantType, bool)`; `Hammer` is declared
  `IDamageMaker` and `Zombie` is the `IDamageable`.
- Therefore the mod witnesses `Zombie.TakeDamage` (rva `0x712210`; every type's override calls it, 13
  static callers in the dump) and keeps the older `Zombie.AddfreezeLevel` witness for the `Use` path,
  which calls it directly at +0x511. Both are prefixes only; the window is the crush body itself, so the
  usual plant damage of the lawn (which reaches `TakeDamage` too) is outside it.
- The room's repeat is unchanged: one `Hammer.EffectZombie/1` per named zombie, the tool's own per-zombie
  blow body (`Collider2D.get_bounds` -> `BoardAction.CreateCherryExplode` -> `Zombie.AddfreezeLevel`),
  with `Hammer.Instance` resolved from the class. The crush's sound and particles now travel too, because
  the crush body opens the same effect event `Use` did. **E60 replaces this repeat: that body writes no
  damage, and the room now repeats the `Zombie.TakeDamage/5` the crush actually called.**
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Hammer$$AnimCrush'`;
  `py -3 tools/re/pvz-hex.py 'Hammer$$AnimCrush'` (read +0x2E0..+0x320);
  `py -3 tools/re/pvz-index.py --method 'Zombie$$TakeDamage'`;
  `grep -n "hammer fell and touched nothing\|the hammer door the animation event" D:/PvzFusion*/MelonLoader/Logs/*.log`.

**E58. A cart lift is its own frame: every lawn hides its own copy and keeps the plant's numbers**
- The lift destroys the object on the clicking lawn. The 12:23 peer log:
  `the cart lifted plant #2 (type 1) off cell 0,2; its object #-18916 is gone from this lawn frame=8148`,
  and the drop rebuilds it: `the cart rebuilt plant #2 as obj -25544 type 1 on cell 0,0 frame=8865`.
  That is `Wheel.Use`'s `Object.Destroy` before `CreatePlant.SetPlant` (E55). A lawn other than the
  clicking one cannot follow that destruction: its object is the plant, with its own health and level,
  and destroying it would throw the numbers away.
- The 12:23 host log shows the other half of the failure:
  `the cart lifted plant #2 (type 1) off cell 0,0; its object #-19700 is gone from this lawn frame=8619`
  followed nine seconds later by
  `the cart's drop named no plant of this room on cell 0,2; this lawn keeps carrying plant #2 frame=9164`.
  The census ran between the two clicks, found no object for the record's instance id, and removed room id
  #2; the drop then could not name its plant. A lift therefore has to survive the census and the layout.
- The room's lift is `MsgAction.PlantLift` (`PlantLiftPayload {NetId, Column, Row}`), host-decided like the
  moves. Every lawn answers with the same object operation: `GameObject.SetActive(false)` on its own
  plant (`Plants.SetShown`), the room id kept in the ledger and in a local away set. Nothing is killed and
  no numbers are copied, so the drop's `PlantMove` moves the very plant each seat already had.
- While a plant is away, the host's census does not remove its record and the client's layout neither moves
  it, retypes it nor removes it; the drop - the usual `PlantMove` frame - shows the object again
  (`SetActive(true)`) and moves it with `Plant.MoveBox`. The seat that clicked has nothing to hide: its
  game already destroyed the object, and its drop finds the rebuilt object and rebinds the room id to it
  (already the glove's fallback).
- Repro: `grep -n "the cart lifted plant \|the cart rebuilt plant \|the cart's drop named no plant"
  D:/PvzFusion*/MelonLoader/Logs/*.log`;
  `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Wheel$$Use'`;
  `grep -n "PlantLift\|SetShown\|ShowPlant" Core/GameAPI/PlantSync.cs Core/GameAPI/Plants.cs`.

**E59. A spawned zombie walks at the speed its own creator rolled; the room has to carry that roll**
- `CreateZombie.SetZombie` (rva `0x45A5C0`, 2896 bytes, 52 resolved calls) uses `UnityEngine.Random`, and
  `Zombie.theOriginSpeed` (offset `0x110`) is one of the numbers it rolls. Every lawn that makes its own
  copy of one of the host's zombies therefore makes a different walk until the next state push.
- `Zombie.UpdateSpeed` (rva `0x713E50`, 320 bytes) is what turns a walk into motion: it multiplies
  `theOriginSpeed` (0x110) by the eight factors `freezeSpeed` (0x114), `coldSpeed` (0x118),
  `butterSpeed` (0x11C), `kelpSpeed` (0x120), `garlicSpeed` (0x124), `uniqueSpeed` (0x128),
  `holeSpeed` (0x12C), `maLiangSpeed` (0x130), stores the product in `theSpeed` (0x10C), sets the
  `Rigidbody2D` velocity (+0xE7) and the animator's `Speed` float (+0x12B). Writing the nine fields alone
  does not change the walk: the body keeps the velocity it was given last.
- The state push already writes those nine fields (`Zombies.ApplyState`), which is why the live logs read
  `zombie state from the host: N bound, 0 corrected` after the first correction - but the first push comes
  five seconds after the spawn, and without the recompute the body never takes the corrected numbers.
- The spawn frame now carries `SpawnZombiePayload.OriginSpeed`, read from the host's own zombie right after
  `CreateZombie.SetZombie` made it. The client writes it with `Zombies.SetBaseSpeed`, which calls the
  game's own `Zombie.UpdateSpeed` immediately, and the state push does the same whenever it corrects a
  speed field. The spawn log prints this lawn's roll and the host's base, so the live log says whether the
  two copies start alike.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'CreateZombie$$SetZombie'`;
  `py -3 tools/re/pvz-index.py --method 'Zombie$$UpdateSpeed'`;
  `py -3 tools/re/pvz-hex.py 'Zombie$$UpdateSpeed'` (read +0x40..+0x130);
  `grep -n "base speed this lawn rolled" D:/PvzFusion*/MelonLoader/Logs/*.log`.

**E60. A hammer blow is one `Zombie.TakeDamage` per zombie; `Hammer.EffectZombie` writes no damage**
- The room used to repeat `Hammer.EffectZombie/1` and the host's zombie did not die. Live 13:33: the peer
  (PvzFusion2, log `26-11-13_13-29-30.log`) says at frame 6982
  `this lawn's hammer touched 1 zombie(s) and the room is told 1 blow(s) of 1 named, the host takes them
  onto its own lawn first`; the host (PvzFusion3, log `26-11-13_13-29-37.log`) receives the frame at
  13:33:06.137 and applies it at 13:33:06.159: `applied Hammer.EffectZombie/1 ["#5"] seq 1 from peer 1`.
  The witness printed the target just before the body ran: `the hammer door the blow body of one named
  zombie ran on Zombie#-21050 type=0 row=2 hp=270/270 alive=True`. One second later the host's own state
  push reads `zombie state pushed: 2 room zombie(s), total hp 540` - #5 is still at full health. It left
  the host lawn only at frame 7540, fourteen frames after `the mower of row 2 started on this lawn,
  the room is told frame=7526`: the mower's bite took it, not the replayed blow.
- The body the room repeated is not a damage body. `py -3 tools/re/pvz-index.py --method
  'Hammer$$EffectZombie'` measures rva `0x7D11A0`, 356 bytes, cats `o`, effect `world`, no
  `UnityEngine.Random`, and its resolved calls are `Collider2D.get_bounds`,
  `BoardAction.CreateCherryExplode` and `Zombie.AddfreezeLevel`: bounds, an explosion and a freeze,
  and no damage step. A body that writes no damage cannot be the room's answer to a kill.
- The damage is an interface dispatch inside the crush, and E57 measured it: `Hammer.AnimCrush` (rva
  `0x7D0BA0`) computes the damage and at +0x30F calls the IL2CPP dispatcher at rva `0x2D00` with
  `ecx = 0x0E`, `rdx` = the zombie, `r8d` = the damage, `r9` = the tool, `[rsp+0x20] = 1`
  (`DamageType.NormalAll`), `[rsp+0x28] = 270` (the hammer's `PlantType`) and `[rsp+0x30] = false` - the
  five arguments of `Zombie.TakeDamage(int, IDamageMaker, DamageType, PlantType, bool)`.
  `py -3 tools/re/pvz-index.py --method 'Zombie$$TakeDamage'` measures rva `0x712210`, 1761 bytes,
  22 resolved calls, cats `ioeib`, effect `world`, no roll, so the row is `Zombie.TakeDamage/5`,
  `Replicate`, `AnyPeer` (`Core/Session/Coverage.cs`; `--arbiter` measures it).
- The maker travels by class, not by object: every lawn builds its own hammer, and `Hammer` declares
  `public static Hammer Instance` (E54). The frame therefore carries `!Hammer` (`CallArgs.Singleton`,
  `RefKind.Singleton`), and the applier resolves it by the class' static `Instance` and tests it with
  `ApiBase.Present`. A maker the game passed that this end cannot see travels as `~`.
- The mod witnesses that door with a prefix on `Zombie.TakeDamage` (`HammerGate.TakeDamage`), records
  the five arguments verbatim (`HammerSync.NoteBlow`), and after the crush body returns announces one
  `Zombie.TakeDamage/5` per zombie the ledger numbers (`HammerSync.EndBlow`). The window is the crush:
  `Use` and `AnimCrush` open and close it, `EffectZombie`, `CrushZombie` and `AddfreezeLevel` remain
  log-only doors, because a Unity animation event is the caller the static scan cannot see (E54, E57).
- Repro: `grep -n "applied Hammer.EffectZombie/1\|zombie state pushed: 2 room zombie(s)\|the mower of
  row 2 started" D:/PvzFusion3/MelonLoader/Logs/26-11-13_13-29-37.log`;
  `grep -n "hammer touched 1 zombie(s)" D:/PvzFusion2/MelonLoader/Logs/26-11-13_13-29-30.log`;
  `cd /d/PvzMP && py -3 tools/re/pvz-index.py --method 'Hammer$$EffectZombie'`;
  `py -3 tools/re/pvz-index.py --method 'Zombie$$TakeDamage'`;
  `py -3 tools/re/pvz-index.py --arbiter`.

**E61. A destroyed Unity object keeps its managed handle, so presence is Unity's own answer**
- A loss destroys the objects a level owns while their managed handles stay non-null with a non-zero
  native pointer. The host log of 13:34:19 (PvzFusion3, `26-11-13_13-29-37.log`) has five consecutive
  `ReadFertilizerPickedUp -> FAIL Il2CppException: System.NullReferenceException` at
  `UnityEngine.GameObject.GetComponent[T]` (frames 9989-9993), one millisecond apart, and then
  `capability disabled: input (5 consecutive failures in ReadFertilizerPickedUp)`. The pointer checks
  that were in `ApiBase.Present` passed; only the native get-component call threw.
- Once the `input` breaker is open every cursor query answers `default(T)`, and that answer is not a
  click. From 13:34:36.890 the host log repeats, frame after frame,
  `placement asked for type 0 cell 0,0 cost -1`, and each one reaches the plain creator:
  `SpawnPlant(col=0,row=0,type=0) -> -56852`. A blocked read of the held card answers 0, which is not
  `Cursor.NoCard` (-1), and a blocked read of the cell answers 0,0 - so the mod asked the room for a
  plant nobody clicked at, and the room's own creator grew it.
- The same null answer broke the merge watch: `ReadPlantInstanceIdsUnderCursor` returned null and the
  describe line took `.Length`, so the host log reads `the merge watch threw: NullReferenceException`
  one millisecond before the first phantom placement (13:34:36.889).
- The same stale pointer is what takes a tool's ghost away. `CreateToolPreview(glove)` failed at
  13:33:52.931 with `UnityEngine.Component.GetComponentInChildren` in its stack, and the peer's
  `CreateToolPreview(cart)` failed at 13:34:19.526. `Cursor.WheelObject` keeps the cart it found and
  only searches again when `Present` says it is gone, so a cart destroyed by the level change was never
  replaced and its ghost could not come back on the next lawn.
- The fix is Unity's own presence question, not a second pointer test: the interop proxy's implicit
  bool conversion (`(bool) unity`, beside `operator ==` with null) is `Object.op_Implicit`, the native
  alive check, and every il2cpp type the game hangs off a GameObject derives from `UnityEngine.Object`.
  `ApiBase.Present` now asks it, after its pointer and collection checks. `ReadPlantInstanceIdsUnderCursor`
  answers `NothingUnderCursor` when the query is blocked, and `TryCapturePlacement` refuses a click whose
  `CardUse.Held()` is not present - the game's own answer to a click with an empty hand - and takes the
  card's own `theSeedCost` instead of a bank fallback.
- Repro: `grep -n "ReadFertilizerPickedUp -> FAIL\|capability disabled: input" D:/PvzFusion3/MelonLoader/Logs/26-11-13_13-29-37.log`;
  `grep -n "placement asked for type 0 cell 0,0\|SpawnPlant(col=0,row=0,type=0)\|the merge watch threw"
  D:/PvzFusion3/MelonLoader/Logs/26-11-13_13-29-37.log`;
  `grep -n "CreateToolPreview" D:/PvzFusion*/MelonLoader/Logs/26-11-13_1*.log`;
  `grep -n "public static bool Present" -A 22 Core/GameAPI/ApiBase.cs`.

**E62. The damage door travels with the tool's class, so the maker is `!Hammer`, and the host pushes the state at once**
- Live 14:12: the peer (PvzFusion3, `26-11-13_14-9-46.log`) crushes
  `Zombie#-21196 type=0 row=4 hp=270/270 alive=True for 1000000 from IDamageMaker, kind 14, reports
  270 frame=5729`, announces `the room is told 1 damage step(s) of 1 named, the host takes them onto
  its own lawn first frame=5729`, and its own call reads `call Zombie.TakeDamage/5 -> Deferred`.
- The host (PvzFusion2, `26-11-13_14-9-41.log`) receives the frame at 14:12:58.428 and refuses it:
  `IDamageMaker.Instance: IDamageMaker has no static Instance, so this end cannot say which object to
  run the method on`, hence `call Zombie.TakeDamage/5 -> Refused frame=5918` and `the game said no to
  Zombie.TakeDamage/5: Refused`, again at 14:14:28 and twice at 14:15:38.
- The token was the proxy's own type name. `maker.GetType().Name` on an il2cpp parameter declared as
  an interface answers the interface (`IDamageMaker`), and no lawn has a static `Instance` on an
  interface. The native dispatch measured in E57 passes the tool itself (`r9` at `AnimCrush` +0x30F),
  and every lawn's tool is `Hammer.Instance` (E54), so the maker travels as the measured class:
  `CallArgs.Singleton("Hammer")` -> `!Hammer`, resolved by `NativeCalls.Singleton` through the static
  `Instance` and tested with `ApiBase.Present`. `HammerSync.NoteBlow` records `Hammer` and prints
  the interface name the object really carries ("the body hands it over as IDamageMaker").
- The kill needs no separate list. The door body is 1,000,000 damage of kind 14 against 270 hp (E57),
  so one applied `Zombie.TakeDamage/5` is the kill and the death is the game's own on every lawn.
  What was missing was visibility: the host's state push to peers waited for `ZombiePushMillis`.
  `NativeCalls.Run` now calls `ZombieSync.ForcePush()` right after the door returns on the host, so
  the despawn rides the next Tick instead of the next quarter second.
- Repro: `grep -n "crush touched\|the room is told 1 damage step\|TakeDamage/5 -> Deferred"
  D:/PvzFusion3/MelonLoader/Logs/26-11-13_14-9-46.log`;
  `grep -n "IDamageMaker has no static Instance\|TakeDamage/5 -> Refused\|the game said no to"
  D:/PvzFusion2/MelonLoader/Logs/26-11-13_14-9-41.log`;
  `cd /d/PvzMP && py -3 tools/re/pvz-index.py --arbiter`.

**E63. A fused plant has no cart drop preview, so a drop that lifts first has to rebuild with the creator's free flag**
- Live 14:14: the host (PvzFusion2, `26-11-13_14-9-41.log`) reports `the cart lifted plant #16 (type
  1027) off cell 5,0; its object #-47576 is gone from this lawn, every other lawn is told frame=12934`;
  the peer (`26-11-13_14-9-46.log`) answers `plant #16 (obj -30746) is in a cart by the host's cart,
  this lawn hides its copy frame=12755`. Two seconds later the host drops and the native body leaves
  nothing: `the cart's plant #16 is not on cell 4,1 after the native call, falling back to the lawn
  snapshot frame=13103`, then `the cart's drop named no plant of this room on cell 4,1; this lawn keeps
  carrying plant #16 frame=13103`. The room keeps `#16` bound to the object the lift destroyed, and
  the peer's copy stayed hidden for the rest of the level.
- A normal drop works in the same session: `the cart rebuilt plant #2 as obj -35380 type 1 on cell
  0,1` (14:12:53). The difference is the type. Type 1027 was made by fertilizer growth (E56): it has
  no seed and no preview, the native drop's `CreatePlant.CreatePlantPreview` path yields nothing on a
  clear cell, and the lift has already destroyed the old object, so there is nothing to move either.
- The repair is the creator call that made the fused plant: `CreatePlant.SetPlant(col, row,
  (PlantType)t, null, default, true, true, null, null)` - the free-set flag of E56 - wrapped as
  `Plants.SpawnFree`. `PlantSync.TryMoveCarriedPlant(tool, rebuild: true)` uses it only when the old
  object is gone, nothing stands on the target cell and the native drop named nothing; it then binds
  the room id to the new object and sends PlantMove. An occupied cell stays a refusal, not a loss:
  the cart keeps carrying.
- Repro: `grep -n "cart lifted plant #16\|cart's plant #16 is not on cell\|cart's drop named no plant"
  D:/PvzFusion2/MelonLoader/Logs/26-11-13_14-9-41.log`;
  `grep -n "plant #16 (obj" D:/PvzFusion3/MelonLoader/Logs/26-11-13_14-9-46.log`;
  `grep -n "the cart rebuilt plant" D:/PvzFusion2/MelonLoader/Logs/26-11-13_14-9-41.log`.

**E64. A lost lawn stays in a level, so the last cursor word keeps drawing; the lose menu is the door that clears it**
- Live: PvzFusion2 `26-11-13_13-29-30.log` draws `a ghost of the hammer takes its picture` at
  13:34:48.255 (frame 12781) and receives the last `recv Cosmetic/CursorIntent from peer 0` at
  13:34:48.639; nothing follows until the next level's `the host starts level type 0 level 2` at
  13:37:31. `LevelSync.TheRoomIsInALevel` stays true through the lose screen, so `PreviewSync.Tick`
  keeps drawing the last words and the seats keep showing a hand that no longer clicks.
- The symptom is a zombie ghost in the corner as if the glove held it, on the seat that does not own
  it, while the seat that really holds a glove hides its own (by design) - so the stale picture is
  the only preview left on screen.
- `EndGate` postfixes `UIMgr.EnterLoseMenu(string reason)` (dump: `EnterLoseMenu(string reason = "")`).
  `PreviewSync.TheLevelWasLost` destroys every ghost, clears `Local`, stops sending (`_ended`) and
  sends one empty intent (`Column`/`Row` = `Cursor.NoCell`, `PlantType`/`GloveZombieType` =
  `Cursor.NoCard`), so the other seats clear the same picture at once. `_ended` clears on the next
  `ForgetTheLawn`, i.e. at the next level start.
- Repro: `grep -n "a ghost of the hammer takes its picture\|recv Cosmetic/CursorIntent from peer 0\|the
  host starts level" D:/PvzFusion2/MelonLoader/Logs/26-11-13_13-29-30.log`;
  `grep -n "EnterLoseMenu" ref/dump-en/dump.cs`.

**E65. An il2cpp interface is not the proxy's base class, so the game's own class question decides the maker**
- Live 14:43 (host PvzFusion2, `26-11-13_14-43-3.log`): the peer's blow arrives as a whole frame and
  is still refused - `!Hammer: argument 1 names a Hammer where the game wants a IDamageMaker` at
  14:47:41.183, then `call Zombie.TakeDamage/5 -> Refused frame=12280` and `the game said no to
  Zombie.TakeDamage/5: Refused`; the same three lines repeat at 14:49:43 (twice), 14:52:58 and
  14:53:59. The peer keeps crushing (`the crush touched Zombie#-21624 ... for 1000000 from Hammer
  (the body hands it over as IDamageMaker), kind 14, reports 270 frame=12576`), so the zombie dies on
  the client's lawn only.
- The token is right and the check is what cannot answer. The decompiled proxy of the game's class is
  `public class Hammer : InGameTool` with no `IDamageMaker` in its bases, while the dump declares
  `Hammer : InGameTool, IDamageMaker` (E54/E57). Il2CppInterop models an il2cpp interface as a class of
  its own (`public class IDamageMaker : Il2CppObjectBase`, with the `(IntPtr)` constructor every proxy
  has), so `parameter.IsInstanceOfType(Hammer.Instance)` is false for a reason that has nothing to do
  with the game: the managed wrapper hierarchy is not the native one.
- The answer is the runtime's own: `Il2CppClassPointerStore.GetNativeClassPointer(parameter)` for the
  declared type, `IL2CPP.il2cpp_object_get_class` for the object, and
  `IL2CPP.il2cpp_class_is_assignable_from(wanted, actual)`. When the game says yes, the same native
  pointer is wrapped as the declared type with `Activator.CreateInstance(parameter, pointer)`, because
  reflection only accepts the wrapper the parameter names. `NativeCalls.Rewrap` is that path and only
  runs after the managed check refused, so every reference that already worked is unchanged. A
  reference the game also refuses keeps E62's refusal line.
- Repro: `grep -n "argument 1 names a Hammer\|TakeDamage/5 -> Refused\|the game said no to
  Zombie.TakeDamage/5" D:/PvzFusion2/MelonLoader/Logs/26-11-13_14-43-3.log`;
  `grep -n "crush touched" D:/PvzFusion3/MelonLoader/Logs/26-11-13_14-43-8.log`;
  `ilspycmd -t Il2Cpp.Hammer D:/PvzFusion2/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`;
  `ilspycmd -t Il2Cpp.IDamageMaker D:/PvzFusion2/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`;
  `grep -n "class Hammer" ref/dump-en/dump.cs`;
  `py -3 tools/re/pvz-index.py --method 'Zombie$$TakeDamage'`.

**E66. The cart's drop is one creator call, and a refused call leaves the picture and the cooldown behind**
- Live 14:48 (peer PvzFusion3, `26-11-13_14-43-8.log`): `the cart's click is a lift, the pointer is on
  cell 1,2 and names 1 plant(s) frame=16165`, then `the cart lifted plant #16 (type 1027) off cell 1,2;
  its object #-24620 is gone from this lawn frame=16165`; the drop on 1,1 is `a drop of plant #16 ...
  names 0 plant(s) frame=16283`, and the native body leaves the cell empty - `the cart's drop of plant
  #16 (type 1027) created nothing on cell 1,1, so the cell was rebuilt with the game's own creator as
  obj -27664 (the fused kind has no drop preview) frame=16283`. The rebuilt object is the plant, but
  the cart is still loaded: at 14:49:02 the same plant is reported `in a cart ... this lawn hides its
  copy`, at 14:49:05 a lift finds it `#-27664 was hidden here`, and the screen shows the picture in
  the cart and no recharge.
- `Wheel.Use` (rva 0x7E1CE0, 1888 bytes) holds both halves. `cmp dword ptr [rsi+0x68], -1` at +0x149
  sends a loaded cart to the drop branch at +0x5CC, which calls `CreatePlant.SetPlant(column, row,
  thePlantType, null, (0,0), false, true, null, null)` at +0x651 - the free flag is the stack byte set
  to zero at +0x62E. After the call, `op_Inequality(plant, null)` at +0x688 jumps to the plain epilogue
  at +0x5A5 when SetPlant returned null: the preview is not destroyed, `thePlantType` stays, the
  cooldown is not started and `savedData` is not cleared. Only the success path runs
  `Destroy([rsi+0x70])` at +0x6B3, `fullCD`/`CD` at +0x6E2/+0x6E7, `SavePlantData.LoadData` at +0x70B,
  `thePlantType = -1` at +0x710 and `savedData = null` at +0x717.
- `CreatePlant.SetPlant` (rva 0x455A00, 5712 bytes) is why the fused kind comes back null: when the
  free flag byte is zero it asks `CheckBox` at +0x4B2, and a false answer goes to `CheckMix` at +0x502;
  a null mix returns through +0x424 (`xor eax, eax; ret`). With the free flag set, the body jumps
  straight to the creation path at +0x5D2 - the same call the growth body of E56 makes.
- The fix is that one call at that one moment: `PlantSync.CartDropDoor` prefixes `CreatePlant.SetPlant`
  and sets `isFreeSet = true` only while this end's own cart drop is running, only for the carried
  type, only when the room's ledger has no other plant on the aimed cell, and only when the game's own
  `CheckBox` refuses it. The native body then finishes its own drop - preview destroyed, type reset,
  cooldown started, saved state loaded - and `TryMoveCarriedPlant` binds the new object by its cell as
  it already does for a normal drop. The cart's own state is now measured in the log:
  `the cart after the click holds type <t>, its preview is up/gone, its cooldown reads <CD> of <fullCD>`.
- Repro: `grep -n "cart lifted plant #16\|cart's drop of plant #16\|cart's click is a lift"
  D:/PvzFusion3/MelonLoader/Logs/26-11-13_14-43-8.log`;
  `grep -n "plant #16.*in a cart" D:/PvzFusion3/MelonLoader/Logs/26-11-13_14-43-8.log`;
  `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'Wheel$$Use'`;
  `py -3 tools/re/pvz-disasm.py 'CreatePlant$$SetPlant'`;
  `py -3 tools/re/pvz-hex.py 'Wheel$$Use'`.

**E67. A survival round transition destroys the peer's board before EnterGame is refused**
- Live 15:31 on a survival level (client PvzFusion2, `26-11-13_15-16-25.log`; host PvzFusion3,
  `26-11-13_15-16-21.log`): the host summons wave 20 at 15:31:15-18 and mints zombies #216-#263. The
  client's game decides the same moment is the end of the round and calls `UIMgr.EnterGame(3,5)`, which
  the level gate refuses: `level type 3 level 5 is not started here: the host starts the level, your own
  pick is ignored frame=48230`. From then on the client's picture is a lawn with no board behind it:
  `the host named 11 graves and this lawn cannot read its own` (20 times), `CreateParticle.SetParticle
  -> FAIL ... NullReferenceException` (5 times, which switches mp-effects off for the session), and 48
  `SpawnZombie(row=...,type=...,x=9.9) -> FAIL ... NullReferenceException` with the stack
  `CreateZombie.SetTransform (UnityEngine.GameObject zombie, UnityEngine.Vector3 position)` /
  `CreateZombie.SetZombie`, each followed by `the host's zombie #N did not appear on this lawn`.
- The refusal is the second half of the destructive sequence, not its start. `Board.EnterNextRound`
  (rva 0x477630) calls `UnityEngine.MonoBehaviour.Invoke` at +0x62B and +0x6CF with the string
  "StartNextRound"; its callers are `GameLose.OnTriggerEnter2D`, `GameLose.ProcessZombieEnter`,
  `GameLose.TryHandleHideActiveBuff` and `Board.<SetVictoryCondition>b__137_0`. `Board.StartNextRound`
  (rva 0x4846D0) saves the board (+0xD7), rolls the next zombie list (+0x1D2), schedules its own delay
  (+0x32C) and then calls `Board.Die` at +0x342 and `UIMgr.EnterGame` at +0x386. So by the time the
  level gate says "not here", the client's board is already destroyed - and `CreateZombie.SetTransform`
  (rva 0x459280) parents the new zombie through `Component.get_transform` at +0x2FE without a null
  check, which is the NullReferenceException above.
- The fix is `RoundGate`: a peer's `Board.EnterNextRound` and `Board.StartNextRound` are refused while
  the room decides (`the game's next round is not run here (EnterNextRound): the host decides when the
  lawn moves to it`). The host's own transition runs in full; its `EnterGame` is heard by the level gate
  and reaches the peers as `LevelStart`, which takes their old lawn down and loads the host's round.
- Repro: `grep -n "is not started here: the host starts the level\|cannot read its own\|SetParticle -> FAIL\|did not appear on this lawn" D:/PvzFusion2/MelonLoader/Logs/26-11-13_15-16-25.log`;
  `grep -n "wave 20" D:/PvzFusion3/MelonLoader/Logs/26-11-13_15-16-21.log`;
  `cd /d/PvzMP && py -3 tools/re/pvz-xref.py 'Board$$EnterNextRound'`;
  `py -3 tools/re/pvz-disasm.py 'Board$$StartNextRound'`;
  `py -3 tools/re/pvz-index.py --method 'Board$$EnterNextRound'`.

**E68. A room placement never reaches the click tail that clears the hand**
- `Mouse.TryToSetPlantByCard` (rva 0x763B80) is where a single-player click ends. Its success branch
  calls `CardUI.UseOnce` at +0x7D6, `CardUI.PutDown` at +0x7EA, `UnityEngine.Object.Destroy` on
  `theItemOnMouse` at +0x80D and `Mouse.ClearItemOnMouse(false)` at +0x81A - so the hand is emptied on
  every successful click, whatever the plant was.
- `Mouse.ClearItemOnMouse` (rva 0x756880) is that emptying: in a level it calls
  `HoverGardenManager.CancelHeldPlacement` (+0x05E), destroys `theItemOnMouse` when asked (+0x086), then
  writes `plantTowards=1` (+0x08D), `theIZECardOnMouse=null` (+0x098), `thePlantTypeOnMouse=-1`
  (+0x09E), `theZombieTypeOnMouse=-1` (+0x0A5), `theItemOnMouse=null` (+0x0B5), `theCardOnMouse=null`
  (+0x0C4), the three glove fields (+0x0D3..+0x0F4), `cannonPlant=null` (+0x109) and `mouseItemType=0`
  (+0x117).
- A room placement never runs that body: the gate turns the click into a `PlacePlantRequest`, and the
  plant appears later from the host's `SpawnPlant`. `CardUse.Spend` cleared the hand only when
  `theCardOnMouse` was the very `CardUI` the click captured, so a hand still carrying the planted type
  on another card object - or the type with no card behind it - stayed in the hand after the plant was
  out. The spend now finishes the native tail: the hand is cleared whenever it still carries the planted
  type, and a card of another plant (the player's next pick) is left alone. The line says which case it
  was: `SpendCard -> CD 0/7.5, hand carried type 42, cleared`.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'Mouse$$ClearItemOnMouse'`;
  `py -3 tools/re/pvz-disasm.py 'Mouse$$TryToSetPlantByCard'`;
  `py -3 tools/re/pvz-index.py --method 'Mouse$$ClearItemOnMouse'`;
  live: `grep -n "SpendCard ->" <log>` and read the `hand` word after every placement.

**E69. The host's zombie list is the room's list; a copy the host does not name cannot stay**
- Every room zombie is a ledger record bound to a lawn object: a peer adopts the host's net id, runs
  `CreateZombie.SetZombie` itself and binds the object (`ZombieSync.ApplySpawn`). The host pushes
  `ZombieState` every 500 ms naming every alive room zombie on its lawn; a bound host zombie missing
  from `Zombies.ReadAll` is removed from the ledger and sent as `DespawnZombie`. The death door used for
  both is `Zombie.Die(int reason = 0)` (rva 0x705D90, documented in the entry at line 334).
- The gap: a peer's `ApplyState` only counted zombies it could not apply (`missing`), and a zombie born
  on the peer's own lawn by the game's own code - with no `SpawnZombie` behind it - was never looked at
  by any frame. The newest peer session `D:/PvzFusion3/MelonLoader/Logs/26-11-13_15-58-15.log` has
  504 `from the host -> obj` lines and 504 `left this lawn by the host's word` lines, so that run has no
  extra object; the reported case was a zombie the host could only see through its effects, i.e. one the
  host's list never held.
- The fix: `ReconcileTheList` removes a bound zombie the host's frames have not named for 3 s;
  `SweepThisLawn` (peer only, once a second, in a level) takes down any lawn zombie with no room record
  after 3 s of grace; the host sends an empty `ZombieState` every 5 s in a level, so a lawn with no
  zombies still says so. Both removals go through `Zombie.Die(0)`, the same door the despawn uses.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'Zombie$$Die'`;
  `grep -c "from the host -> obj" D:/PvzFusion3/MelonLoader/Logs/26-11-13_15-58-15.log`;
  `grep -c "left this lawn by the host's word" D:/PvzFusion3/MelonLoader/Logs/26-11-13_15-58-15.log`;
  live: `grep -n "has not been in the host's list for\|the host never named it" <client log>`.

**E70. The lose screen is one door, and in a room the host is the one who opens it**
- `UIMgr.EnterLoseMenu(string reason = "")` (rva 0x8E2880) has 32 callers in the binary:
  `GameLose.HandleGameLose` +0xED, `GameLose.OnTriggerEnter2D` +0x7A7, `GameLose.ProcessZombieEnter`
  +0x694, `PVPRandomManager.ZombieEnterHouse` +0x1F8, `HoverGardenManager.HandleBreach` +0x426,
  `HoverGardenManager.FailRun` +0x6D, `Plant.Die` +0x495, `Player.Die` +0x2D, and the minigame managers
  (`MinesweeperManager.OnGameOver`, `SnakeManager.MoveBox`, `FruitNinjaManager.LoseScore`,
  `AutoChess.RoundManager.GameOver`) - one patch therefore covers every lose path.
- In `GameLose.OnTriggerEnter2D` the call is the last thing the body does (+0x7A7, after the level-data
  check at +0x73B..+0x79D). The board's own move to the next round is `Board.EnterNextRound` earlier at
  +0x52E, already refused for a peer by RoundGate (E67).
- The room: the host's postfix broadcasts `GameOver` once per lawn; a peer's prefix refuses its own
  menu and waits. When the frame arrives the peer calls `UIMgr.EnterLoseMenu(reason)` under `_applying`,
  so the game's own body runs on the peer - statistics, time scale, audio pause, the ghost cleanup - at
  the host's moment. A peer's own loss no longer opens a screen the room did not earn.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-xref.py 'UIMgr$$EnterLoseMenu'`;
  `py -3 tools/re/pvz-disasm.py 'GameLose$$OnTriggerEnter2D'`;
  `grep -n "EnterLoseMenu" ref/dump-en/dump.cs`;
  live: `grep -n "the lose screen is not opened here\|the host says the lawn has ended" <client log>`.

**E71. The main menu is a pushed UIType, and the coop button reads the mouse itself**
- `UIType.MainMenu = 0` and `UIType.MainMenu_travel = 1` (ref/dump-en/dump.cs:93165-93166).
  `UIMgr.EnterMainMenu` (rva 0x8E2B90) pushes UIType 0 at +0x239 and UIType 1 at +0x293, so the type
  of the stack's top names the screen the player is on - that is what the mod's coop button checks
  (`Menu.Top().uIType`, Core/GameAPI/CoopMenu.cs).
- `UIResourcesLoader.Push(UIType, Transform, bool group)` (rva 0x8E4990) instantiates
  `UIPrefabs[type]`, writes the type into `BaseMenu.uIType` (offset 0x38) and adds the instance to the
  stack `List` at [this+0x18]; `Peak`/`Pop`/`PopTo`/`PopAll` walk that same list.
- `MainMenu` carries `UIButton_mainMenu challengeLevel/izLevel/survivalLevel` (dump.cs:76134-76147); a
  `UIButton` has `clickEvent` at 0x20 and `rectTransform` at 0x48 (dump.cs:70221-70226). The mod only
  reads `survivalLevel`'s rectangle to put its own box in the strip under it - it never presses the
  game's button, so nothing on the main menu is re-entered.
- The box is the mod's own `GameObject` with an `Image` and a child `TextMeshProUGUI` on
  `GameAPP.canvasUp`, all with `raycastTarget = false`; hit testing is the mod's own -
  `Input.mousePosition` through `RectTransformUtility.RectangleContainsScreenPoint` and
  `Input.GetMouseButtonDown(0)` (CoopMenu.cs:129-130), the same press read ActionGate uses
  (Core/GameAPI/ActionGate.cs:177-178). HOST and CLIENT call `ConnectionEntry.HostFromMenu` /
  `JoinFromMenu`, which enter the same `Open` gate F1 and F2 use, so the button and the hotkeys cannot
  drift apart.
- Live proof is still pending: no session has run on this build yet.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'UIMgr$$EnterMainMenu'`;
  `py -3 tools/re/pvz-disasm.py 'UIResourcesLoader$$Push'`;
  `grep -n "MainMenu_travel\|survivalLevel" ref/dump-en/dump.cs`;
  live: `grep -n "the coop button stands\|the coop button offers\|the coop button asks" <log>`.

**E72. What a plant keeps between frames, and the doors that make a peer show the host's numbers**
- The fields a plant's behaviour and its bar read (ref/dump-en/dump.cs:30136-30200): `skinType` 0xF8,
  `thePlantMaxHealth` 0xFC, `thePlantHealth` 0x100, `theShieldHealth` 0x104, `magnetCount` 0x114,
  `theLevel` 0x118, `wheatType` 0x11C, `disableCount` 0x120, `attributeCount` 0x124, `shootingLevel`
  0x128, `shootingCurse` 0x12C, `killCount` 0x130, `puffPlace` 0x134, `butterP` 0x140,
  `thePlantStage` 0x144, `freezeLevel` 0x148, `theLilyType` 0x1B0, `firstParent` 0x1B4,
  `secondParent` 0x1B8, and the seven switches `isLily` 0x214, `imitatless` 0x215, `invincible`
  0x216, `melonSputter` 0x21B, `keepShooting` 0x21E, `uncrashable` 0x221, `undead` 0x223. One read
  (`Plants.FactsOf`) names all of them, and `PlantSnapRules` is the one mapping between that read and
  the layout entry, so the host's send and a peer's receive cannot disagree field by field.
- Health moves only by the gap the two lawns disagree by: `Plant.Recover(float, DamageType, bool,
  bool)` (rva 0x4D1260, slot 27) for a heal and `Plant.RealTakeDamage(int)` (rva 0x4D1170) for damage,
  with the amount chosen by `PlantFactsRules.Between`. A host number of zero or below is not a fact: a
  dead plant is an entry the layout stops naming, and that removal is the death (E73).
- Growth: `Plant.Upgrade(int targetLevel, bool free, bool abyss)` (rva 0x4D4FE0) is the game's own
  level change; a peer calls it with `free = true` because no sun is spent on this end. The bar is
  `HealthSlider.HealthValue`/`ShieldValue` (ref/dump-en/dump.cs:28187-28188) and is written only when
  something moved, so a lawn that already agrees costs no write.
- Deliberately left local: `Plant.theStatus` (0x1A8, the `PlantStatus` animation object) and the
  attack/produce countdowns (0x1E8, 0x1EC, 0x1F0, 0x204-0x210). They are one animation's progress;
  copying them would freeze or duplicate a peer's own animation, and the outcome a player sees is
  already carried by health, kind and level.
- Live proof is pending: no room has run on this build yet. The line to read next time is the layout
  summary - `aligned` counts the plants whose facts were corrected on a peer.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'Plant$$Recover'`;
  `py -3 tools/re/pvz-disasm.py 'Plant$$RealTakeDamage'`;
  `py -3 tools/re/pvz-disasm.py 'Plant$$Upgrade'`;
  `grep -n "thePlantMaxHealth\|theShieldHealth\|theLevel\|thePlantStage\|imitatless" ref/dump-en/dump.cs`;
  live: `grep -n "the host's plant layout arrived" <peer log>`.

**E73. A room plant dies on the host's word, and hypnosis is a zombie flag**
- `Plant.Die(DieReason)` is the one public death door: virtual slot 35, rva 0x4CC520
  (ref/dump-en/dump.cs:30346). Its body destroys the object (`UnityEngine.Object.Destroy` at +0x161)
  and can open the lose screen (+0x495, E70). Four kinds replace the body - `GoldSquash`
  (dump.cs:35197, TypeDefIndex 1206), `Leigon` (36728, 1271), `PortalNut` (38549, 1334) and
  `UltimatePortalNut` (42878, 1499) - so a gate on `Plant` itself would miss them. `PlantDeath`
  walks the interop assembly, finds every declared `Die` in the plant family and patches each; the
  count is logged at install.
- The sentence: the host's own lawn runs `Plant.Die` as it always did; a peer holds the death of a
  plant the `NetEntity` ledger numbers. The host's layout removal, the host's shovel and despawn, and
  the mod's own rebuild all call `Plants.Kill`, which sets `Plants.ForTheRoom` around `plant.Die`, so
  the host's word is exactly the call that gets through. A plant the ledger does not number, a lawn
  outside a level, and the host's own lawn are untouched. A held plant comes back through E72's heal
  door when the host still names it alive.
- Hypnosis is a zombie flag, not a side list: `Zombie.isMindControlled` (dump.cs:64477, offset 0x145),
  and the game itself spawns hypnotized zombies with it (`Board.SetZombie(..., bool isMindControlled,
  ...)`, dump.cs:22205). The host's copy travels in `ZombieHealth.MindControlled` and is written on a
  peer next to the armor and the speed factors; a hypnosis zombie a peer mints on its own is still
  swept by the host's list (E69).
- Live proof is pending: no room has run on this build yet. The lines to read next time are the held
  death on a peer and the aligned count from E72.
- Repro: `cd /d/PvzMP && py -3 tools/re/pvz-disasm.py 'Plant$$Die'`;
  `grep -n "override void Die(Plant.DieReason" ref/dump-en/dump.cs`;
  `grep -n "isMindControlled" ref/dump-en/dump.cs`;
  live: `grep -n "a room plant dies on the host's word\|was brought down on this lawn" <peer log>`.
