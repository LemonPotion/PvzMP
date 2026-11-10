# NATIVE-INDEX - what the game's own methods are, and what each one is for

Derived from `ref/native-index.json` by `py -3 tools/re/pvz-index.py`. The JSON is
generated, not edited, and not in git (`.git/info/exclude`); this file is the part
a human reads. Re-derive the tables with the commands in section 1.

Inputs: `ref/dump-en/script.json` (204,331 methods with a body), `ref/dump-en/dump.cs`
(15,326 distinct types over 72 assemblies) and `D:\PvzFusion\GameAssembly.dll` (the
only place real bodies exist). Build en-4.1, Unity 2022.3, il2cpp metadata 31.

---

## 1. Reproduce

| command | output |
| --- | --- |
| `py -3 tools/re/pvz-index.py --stats` | every count in sections 2 and 7 |
| `py -3 tools/re/pvz-index.py --candidates 30` | arbiter shortlist, most verbs first |
| `py -3 tools/re/pvz-index.py --arbiter write` | prove `Core/Session/Coverage.cs` and emit its facts |
| `py -3 tools/re/pvz-index.py --class Board Zombie` | one class, all methods |
| `py -3 tools/re/pvz-index.py --method Board@SetSun` | one method: bytes, calls, refs, strings |
| `py -3 tools/re/pvz-index.py --calibrate` | noise floor + the 13 known-answer rows |
| `py -3 tools/re/pvz-index.py --write` | `ref/native-index.json` (71 MB, 204,331 records) |

## 2. Definitions, so the tables cannot be read two ways

- **game code** = `image == Assembly-CSharp.dll`. `script.json` carries no image
  field, so a class token is matched against dump.cs by name (generics opened,
  nested separators normalised) and the body address settles every case where two
  assemblies declare the same name. An instantiation counts as the assembly that
  defines the generic, which is where its compiled body lives:
  `List<ContourVertex>` is mscorlib even though `ContourVertex` is a game type.
  Section 7 is the proof of the whole scheme: all 17,843 game bodies dump.cs
  declares are attributed, with 0 address disagreements.
  `Core.dll` is the only other first-party assembly: 20 types, 138 methods, 0
  verbs - `Lawnf`, `InGameText`, `GameMaterial`, collection/math extensions.
- **verb** = a game method whose name starts with a command word (`Set`, `Add`,
  `Create`, `Die`, `Move`, `Use`, `Take`, `Save`, `Load`, `Capture`, `Init`, ...)
  and that is not an accessor, an engine callback or compiler-generated. 2,681
  verbs. This is the candidate pool for the arbiter allow-list.
- **frame** = something Unity calls, not something a player commands: the exact
  names `Update`, `FixedUpdate`, `LateUpdate`, `Awake`, `Start` on a MonoBehaviour,
  plus any `On*` message. 2,051 in game code. Never sent.
- **blocked** = a parameter cannot travel (delegate, `IntPtr`, shared generic,
  unrecognized): 32 verbs, all listed in section 6.
- **cats** = what a parameter is on the wire: `int 1110`, `obj 959`, `bool 284`,
  `float 277`, `enum 159`, `struct-by-value 148`, `string 132`, `hard 39`,
  `array 15`, and 931 verbs taking nothing at all.
- **effect** = one word for what a call does, first rule that matches:
  `gen` 4,556 / `frame` 2,050 / `blocked` 267 / `world` 3,904 / `config` 1,851 /
  `side` 597 / `pure` 2,650 / `read` 9,988. Over the verbs: world 956, read 1,070,
  config 338, side 136, pure 149, blocked 32.
- Signature format of a verb: `void T()` 32.5%, `void T(obj)` 14.9%,
  `void T(int)` 13.8%, `void T(int,obj,int,int,bool)` 3.3% (that is damage), then
  a long tail; 8 formats cover 72.2% and 30 cover 87.3% of all verbs. `T` stands
  for the method, `obj` for a reference, `val` for a struct passed by value.

Body features and how many game methods carry them:
`store_any` 19,288, `string_lit` 4,779, `calls_world` 3,406, `gameapp_static`
2,157, `wr_config` 4,169, `wr_zombie` 1,659, `sound` 1,294, `random` 1,044,
`scene_query` 895, `wr_board` 416, `instantiate` 479, `destroy` 495,
`asset_load` 202, `event` 166, `wr_own` 237, `coroutine` 127, `wr_plant` 61.

## 3. The fourteen classes

Only decisive rows. Full tables: `--class <name>`. Verdict column: **replicate**
= the room must repeat this call; **mirror** = run it locally, the state arrives
as a snapshot; **local** = presentation or this player's own view; **never** =
engine callback.

### Board (`MonoBehaviour`, 135 instance fields, 91 methods, 38 verbs)

The lawn, and the only object that owns sun, money and waves.

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `SetSun` | `0x481250` | `void T(int)` | world, wr_own | replicate - 7 bytes, one store, no call |
| `UseSun` | `0x486E00` | `void T(float)` | world, wr_own | replicate |
| `SetMoney` | `0x47D220` | `void T(int)` | world, wr_own | replicate |
| `UseMoney` | `0x486DD0` | `void T(int)` | world, wr_own | replicate |
| `GetSun` | `0x478BF0` | `void T(float,bool)` | world, calls_world | replicate |
| `GetMoney` | `0x4783D0` | `void T(float)` | world, wr_own | replicate |
| `GetPoint` | `0x478920` | `void T(float,bool,bool,val,bool)` | world | replicate |
| `SunUpdate` | `0x484DC0` | `void T()` | world, random | mirror - rolls 8 sky suns per call |
| `RequestNextWave` | `0x47C0B0` | `void T()` | world, wr_board | replicate |
| `StartNextWave` | `0x484A70` | `void T()` | world, wr_board | mirror |
| `ToggleAutoNextWave` | `0x485930` | `void T()` | world, wr_board | replicate |
| `ClearTheBoard` | `0x475C60` | `void T()` | world, calls_world | replicate - ends in `SaveMgr.ClearBoard` |
| `Die` | `0x476FA0` | `void T()` | world, event, destroy | replicate |
| `ChangeMap` | `0x475A20` | `void T(obj)` | world, destroy | replicate |
| `GetBoxType` | `0x478070` | `int T(int,int)` | world, calls_world | read-only, use for checksums |
| `Update` | `0x486790` | `void T()` | frame | never |
| `Awake` / `Start` | `0x475590` / `0x484AC0` | `void T()` | frame | never |

### BoardSpawner (4 fields: board, theWave, rowsSpawns, mowerSpawns)

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `SummonZombies` | `0x48DE00` | `void T(int)` | world, wr_zombie, random | replicate - the wave |
| `ApplyGameModeEffects` | `0x48CF90` | `void T(obj,int)` | world, calls_world | mirror |
| `MowerStart` | `0x48DD40` | `void T(int,int)` | read | mirror |
| `ResetMowerProtection` | `0x48DDB0` | `void T()` | read | mirror |
| `GetRandomRow` | `0x48D490` | `int T()` | world, calls_world | local - consumes randomness |
| `AdjustRowForSpecialZombies` | `0x48C8D0` | `void T(hard,hard)` | blocked | enum params, see section 6 |

`GetRandomRow`, `GetRandomLandRow`, `GetRandomWaterRow` and `GetUnprotectedRows`
all read `UnityEngine.Random`, so a peer must never evaluate them; the row has to
arrive in the packet that spawns.

### CreatePlant (2 fields: board, bulletLayer) / CreateZombie (1 field: board)

The two factories. Every plant and zombie in a room is created through them.

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `CreatePlant.SetPlant` | `0x455A00` | `obj T(int,int,int,obj,val,bool,bool,obj,obj)` | world, random, instantiate, event, sound | replicate - the placement call |
| `CreatePlant.SetPlantInGarden` | `0x454BD0` | `obj T(int,int,int,obj,bool,bool,bool)` | world, instantiate | replicate (garden) |
| `CreatePlant.CreatePlantPreview` | `0x4516B0` | `obj T(int,val,obj,val)` | world, instantiate | local - ghost only |
| `CreatePlant.MixEvent` | `0x452EF0` | `void T(int,obj,int)` | world, random, sound | mirror |
| `CreatePlant.DestoryParticle` | `0x452020` | `void T(obj)` | world, destroy | local |
| `CreatePlant.CheckBox` | `0x44EFB0` | `bool T(int,int,int)` | world, calls_world | read-only, use for legality |
| `CreateZombie.SetZombie` | `0x45A5C0` | `obj T(int,int,float,bool,bool)` | world, random, instantiate, event | replicate - the spawn call |
| `CreateZombie.SetZombieWithMindControl` | `0x459DC0` | `obj T(int,int,float,bool)` | world, instantiate | replicate |
| `CreateZombie.CreateZombiePreview` | `0x4582B0` | `obj T(int,val,obj,val)` | world, instantiate | local |
| `CreateZombie.GetZombieData` | `0x458C50` | `void T(obj,int,bool)` | world, calls_world | mirror |

### Mouse (`MonoBehaviour`, 21 instance fields, 58 methods, 12 verbs)

The input layer: what the local player is holding and where the click lands.

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `TryToSetPlantByCard` | `0x763B80` | `void T()` | world, sound, destroy | replicate the *intent*, 4,385 bytes, 84 calls |
| `TryToPickPlant` | `0x7630C0` | `void T()` | world, sound | replicate the intent |
| `TryToSetPlantByGlove` | `0x764CB0` | `void T()` | world, event, sound | replicate the intent |
| `TryToSetZombieByCard` | `0x765340` | `void T()` | world, random, destroy | IZ mode only |
| `SetPlantFromCard` | `0x75E110` | `obj T(int,int,int)` | world, calls_world | replicate |
| `PutDownItem` | `0x75D330` | `void T()` | world, event, destroy | replicate |
| `ClearItemOnMouse` | `0x756880` | `void T(bool)` | world, destroy | local |
| `MoveZombie` | `0x75C930` | `bool T(obj,float,int,bool)` | world, calls_world | replicate (IZ drag) |
| `PlantPreviewUpdate` | `0x75CBC0` | `void T()` | world, instantiate | local |
| `get_MousePosition` | `0x766F60` | `val T()` | pure | local - the cosmetic channel |
| `Update` / `FixedUpdate` | `0x7665C0` / `0x7584B0` | `void T()` | frame | never |

Every entry point here reads `UnityEngine.Input` directly, so none of them can be
called on a peer. The room sends the resolved intent (which card, which cell) and
each client runs its own copy of the entry point - or, better, calls the same
`CreatePlant`/`CardSlotManager` method the entry point would have reached.

### InGameTool (`MonoBehaviour, IClickable`, 11 fields) / Shovel (`: InGameTool`, 0 own fields)

The tool cooldown lives on the tool, so the shovel is per-player state.

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `Shovel.Use` | `0x7DD710` | `void T(obj)` | world, sound, calls_world | replicate - the dig |
| `Shovel.PayBackSun` | `0x7DD250` | `void T(obj)` | world, calls_world | replicate |
| `Shovel.ReturnPlantToGarden` | `0x7DD360` | `void T(obj)` | world, calls_world | replicate |
| `Shovel.OnClick` / `InGameTool.OnClick` | `0x7DD0D0` / `0x7D4300` | `bool T(obj)` | side, sound | local |
| `InGameTool.PickUp` / `PutDown` | `0x7D4470` / `0x7D44D0` | `void T()` | config, store | local |
| `InGameTool.SetEventCooldown` | `0x7D4590` | `void T(float,float)` | config, store | local |
| `InGameTool.Update` / `CDUpdate` | `0x7D4670` / `0x7D41A0` | `void T()` | frame / config | never |
| `InGameTool.Use` | `0x3ED8E0` | `void T(obj)` | pure | the virtual no-op (section 6) |

### TutorManager (`MonoBehaviour`, 6 fields)

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `TutorOver` | `0x66DD80` | `void T()` | world, wr_board, destroy | replicate - ends the tutorial |
| `SetMissionCard` | `0x66DA60` | `void T(int,int,int)` | world, calls_world | replicate |
| `TutorUpdate1..42` | `0x66ED20` .. | `void T()` | world | mirror - a scripted step, keyed by `progress` |
| `TowerUpdate` | `0x66DB10` | `void T()` | world, calls_world | mirror |
| `Update` | `0x672960` | `void T()` | frame | never |

`TutorUpdate<N>` is a state machine driven by the two fields `id` and `progress`
(offsets `0x20`, `0x24`): sync `progress`, do not sync the step methods.

### InitBoard (`MonoBehaviour`, 11 fields) / CardSlotManager / DrawCardManager

The seed bank: what cards exist, which are picked, what a pull gives.

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `InitBoard.AddCard` | `0x469EB0` | `void T()` | world, 120 calls | replicate the result, not the loop |
| `InitBoard.CreateCard` | `0x46B500` | `obj T(int,bool,bool)` | world, calls_world | replicate (3 overloads, `0x46B500`, `0x46B630`, `0x46B3D0`) |
| `InitBoard.PreSelectCard` | `0x46C7B0` | `void T()` | world, random, destroy | replicate the pick |
| `InitBoard.QuickInGame` | `0x46DA10` | `void T()` | world, calls_world | replicate - the pre-battle scenario |
| `InitBoard.InitMower` | `0x46BDC0` | `void T()` | side, coroutine | mirror |
| `InitBoard.CanSelectCard` | `0x46B330` | `bool T()` | config | read-only |
| `CardSlotManager.AddCard` | `0x7CA2A0` | `bool T(obj,bool)` | world, calls_world | replicate |
| `CardSlotManager.AddCardAtIndex` | `0x7CA0F0` | `bool T(obj,int,bool)` | world, calls_world | replicate |
| `CardSlotManager.RemoveCard` | `0x7CB0E0` | `bool T(obj,bool)` | world, calls_world | replicate |
| `CardSlotManager.DeleteCard` | `0x7CA820` | `bool T(obj)` | world, destroy | replicate |
| `CardSlotManager.SynchronizeCards` | `0x7CB500` | `void T(obj)` | world, calls_world | replicate - already a bulk setter |
| `CardSlotManager.ShiftCardsForward` | `0x7CB2D0` | `void T(int,bool)` | world, calls_world | mirror |
| `CardSlotManager.ClearAll` | `0x7CA4D0` | `void T()` | config | replicate |
| `DrawCardManager.Pull` | `0x4F8F50` | `obj T(int)` | world, random, calls_world | host only - returns `List<PlantType>` |
| `DrawCardManager.PerformSinglePull` | `0x4F8E60` | `int T()` | world, random | host only |
| `DrawCardManager.InitializePools` | `0x4F8BE0` | `void T()` | world, calls_world | mirror |
| `DrawCardManager.Update` / `UpdateText` | `0x4F97E0` / `0x4F95C0` | `void T()` | frame / config | never / mirror |

### Plant (`Entity`, 105 instance fields, 120 methods, 19 verbs)

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `TakeDamage` | `0x4D3500` | `void T(int,obj,int,int,bool)` | world, wr_plant, calls_world | replicate - `(amount, source, col, row, crit)` |
| `RealTakeDamage` | `0x4D1170` | `void T(int)` | world, event | mirror |
| `DecreaseHealth` | `0x4CC350` | `void T(int)` | world, event | mirror |
| `ModifyHealth` | `0x4CF610` | `void T(int,float,bool)` | world, calls_world | mirror |
| `Die` | `0x4CC520` | `void T(int)` | world, event, destroy | replicate - 2,476 bytes, 46 calls |
| `RemoveFromList` | `0x4D1D10` | `void T()` | world, wr_own | mirror - `Die` already calls it |
| `MovePosition` | `0x4CFE50` | `void T(val)` | world, calls_world | replicate (glove) |
| `MoveBox` | `0x4CFD00` | `void T(int,int)` | world, calls_world | replicate (glove) |
| `Upgrade` | `0x4D4FE0` | `bool T(int,bool,bool)` | world, instantiate, destroy | replicate |
| `UseItem` | `0x4D57E0` | `bool T(int,obj)` | world, destroy | replicate |
| `Recover` | `0x4D1260` | `void T(float,int,bool,bool)` | world, event | mirror |
| `UpdateAttackCountDown` | `0x4D48D0` | `void T()` | world, calls_world | mirror - per-frame bookkeeping |
| `PlantUpdate` | `0x4D0380` | `void T()` | world, wr_plant, event | mirror |
| `Update` / `FixedUpdate` / `Start` / `Awake` / `OnDestroy` | - | `void T()` | frame | never |
| `TryUseCard` | `0x4D48B0` | `bool T(int,hard)` | blocked | enum parameter |

### Zombie (`Entity`, 115 instance fields, 148 methods, 40 verbs)

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `TakeDamage` | `0x712210` | `void T(int,obj,int,int,bool)` | world, wr_zombie, event | replicate - same shape as `Plant.TakeDamage` |
| `ApplyDamage` | `0x702FB0` | `void T(int,int,int)` | world, calls_world | mirror |
| `BodyTakeDamage` | `0x704340` | `void T(int)` | world, wr_zombie, sound | mirror |
| `FirstArmorTakeDamage` / `SecondArmorTakeDamage` | `0x70AB50` / `0x70FBF0` | `int T(int)` | world, destroy | mirror |
| `Die` | `0x705D90` | `void T(int)` | world, random, instantiate, event, sound, destroy | replicate |
| `OverRangeDie` | `0x70ECF0` | `void T()` | world | replicate |
| `DestoryZombie` | `0x705D10` | `void T()` | world, coroutine | replicate |
| `SetCold` | `0x710360` | `void T(float,int,bool)` | world, destroy | replicate - status |
| `SetFreeze` | `0x7108D0` | `void T(float,int)` | world | replicate |
| `AddPoisonLevel` / `AddfreezeLevel` | `0x7028C0` / `0x702A80` | `void T()` / `void T(int,int)` | world | replicate |
| `Garliced` / `DeLayGarliced` | `0x70B070` / `0x705BF0` | `void T(bool,bool)` / `obj T(float,bool,bool)` | world, random | replicate |
| `Charred` / `ForceCarred` / `SetCarred` | `0x7050B0` / `0x70AF00` / `0x70FD30` | - | world | replicate |
| `Buttered` / `Scared` / `BeSmall` / `SetKelped` / `SetEmbered` | - | - | world | replicate |
| `SetMindControl` | `0x711030` | `void T(int)` | world, instantiate, sound | replicate |
| `ChangeRow` | `0x704FB0` | `void T(int,bool)` | world, calls_world | replicate |
| `ModifyArmor` / `ModifyFragile` / `InitArmor` | `0x70D450` / `0x70D550` / `0x70C4C0` | - | world | replicate |
| `ModifySpeed` (Plant) / `UpdateSpeed` | `0x4CFB30` / `0x713E50` | `void T(int,float)` / `void T()` | world | mirror |
| `DropItem` / `DropCard` / `DropWheat` / `DropGardenPlant` / `DropKilledAward` | `0x707660` .. | `void T()` | world, random, instantiate | mirror - rewards |
| `MoveUpdate` / `MoveXUpdate` / `MoveYUpdate` / `AttackUpdate` / `AttributeUpdate` | - | `void T()` | world | mirror - per-frame chain |
| `Update` / `FixedUpdate` / `Start` / `Awake` / `OnTriggerEnter2D` / `OnTriggerExit2D` | - | - | frame | never |
| `UpdateShooting` | `0x713CC0` | `void T(hard,int)` | blocked | enum parameter |

Status effects are the single biggest replicate family on `Zombie`, and they all
take plain `int`/`float`/`bool`, so one `MethodCall` adapter covers them.

### SaveMgr (`MonoBehaviour`, 0 instance fields, 3 static) - the state carrier

| method | rva | format | effect | verdict |
| --- | --- | --- | --- | --- |
| `CaptureBoard` | `0x793070` | `obj T(int,int,string)` | world, calls_world | host snapshot - returns `SaveInfo` |
| `CaptureBoardState` | `0x792ED0` | `void T(int)` | world | the actual per-entity capture |
| `SaveBoard` | `0x794770` | `obj T(int,int,string)` | world, calls_world | host snapshot |
| `SavePlants` | `0x794A60` | `void T()` | config | mirror |
| `LoadBoard` | `0x7932A0` | `void T(int,int)` | world, calls_world | client rebuild, not yet byte-verified |
| `LoadPlant` | `0x793A30` | `void T(obj)` | world | client rebuild - takes one plant record |
| `LoadLily` / `LoadPot` | `0x7935F0` / `0x794060` | `void T(obj)` | world | client rebuild |
| `LoadTravelData` | `0x794490` | `void T(obj)` | world, wr_zombie | travel-rogue mode |
| `ClearBoard` | `0x793130` | `void T(int,int)` | config | replicate |
| `SaveTravelRogueCheckpoint` | `0x794E40` | `void T(int,bool)` | world | host only |

`GridSystem` (`AddPit`, `AddGrave`, `AddPot`, `AddLily`, all 4 verbs `world`) is
the other half of the pre-battle scenario: what is on the lawn before a plant is.

## 4. What is already serializable, so no field list has to be written

- `SaveMgr.CaptureBoard(int, int, string) -> SaveInfo` and `SaveMgr.LoadPlant /
  LoadLily / LoadPot (record)` round-trip the lawn through the game's own model.
- `Board.SaveJiasawToList` (`0x47C0D0`), `Board.SetPrePlants` (`0x47D230`),
  `Board.SetRandomZombies` (`0x480E90`) and `InitBoard.InitZombieFromList`
  (`0x46C060`) are the game's own bulk setup calls.
- Field offsets for `Board` (135), `Plant` (105), `Zombie` (115) and `GameConfig`
  (39) are in the JSON under `types`, taken from dump.cs, and are what the `wr_*`
  features are built from.

## 5. Input and per-frame: never on the wire

- `OnMouse*`, `OnPointer*`, `OnDrag`/`OnBeginDrag`/`OnEndDrag`, `OnSelect`/
  `OnDeselect`, `OnTrigger*`, `OnCollision*`, `OnAnimator*` are UI and physics
  callbacks. They are marked `frame`, they read only local input, and a peer must
  not run them.
- `Update`, `FixedUpdate`, `LateUpdate`, `Awake` and `Start` count as `frame` only
  on a MonoBehaviour; a plain class with a `Start()` is a command and stays a verb.
- The per-frame *chains* are the trap: `Board.SunUpdate`, `Board.LevelUpdate`,
  `Zombie.MoveUpdate`, `Plant.PlantUpdate`, `BoardSpawner.SummonZombies` and
  friends are ordinary methods that the engine calls indirectly from `Update`. The
  atlas classifies them `world`, not `frame`, because it cannot see the indirect
  call (`FF 15` thunks are not resolvable from a direct `E8`/`E9` scan). Nothing
  in this file may be re-sent per frame: send the snapshot, let each client run
  its own chain.

## 6. What the atlas cannot answer

- **32 blocked verbs** carry a delegate, `IntPtr` or shared generic:
  `EventManager.AddListener(int, hard, bool)`, `BoardAction.CreateFireLine`,
  `BoardAction.SetDoom`, `Doom.SetDoom`, `LaserBullet.SetLaser`,
  `SeedLibrary.CreateNewTypeofCards`, `TimePeriod.StartPeriod`,
  `DelayAction.SetAction(hard,float)`, `TravelRogueCraftingOverlay.ShowConfirmation`
  and 24 more, all printed by `--candidates`. Coverage must reach the state they
  produce, not the call.
- **Folded bodies.** 25,863 game methods share 18,832 body addresses, because
  il2cpp emits one stub for many methods: `0x3ED8E0` is a bare `ret` used by 508
  methods, `0x3ED870` is `xor edx,edx; jmp` used by 1,423 empty `.ctor`s,
  `0x49E6A0` tail-jumps into `Plant..ctor` (`0x4D6150`) for 138 derived
  constructors, `0x3F1980` is `mov eax,2; ret` for 124 constant getters. 242 verbs
  sit on a folded address, 138 of them on the bare `ret` at `0x3ED8E0`
  (`InGameTool.Use`, `TravelRogueTooltip.Hide`,
  `Bullet_cabbageMelon.SetPenetrationTime`), and for those the override provably
  does nothing, so gating it is free. The real verbs do not:
  `Plant.Die` (`0x4CC520`, 2,476 bytes) and `Zombie.TakeDamage` (`0x712210`) have
  their own bodies.
- **Call yield.** 112,505 of 297,950 `E8`/`E9` byte positions resolve to a known
  method name (38%); the rest are engine thunks and vtable dispatches, so
  "this body calls nothing" does not mean "this body does nothing".
- **Field-offset evidence is only as good as its uniqueness.** `Board`, `Plant`
  and `Zombie` share 85-89 offsets because both entities extend `Entity`; the
  `wr_board`/`wr_plant`/`wr_zombie` bits therefore use the offsets that are unique
  to one class, which is why `wr_plant` fires on only 61 methods.
- **2,723 class tokens (17,338 methods) have no dump.cs type at all** - async
  builders, `UniTask`, `Unity.Collections` shared generics. None is game code.

## 7. Measurement quality

- `script.json` has 204,331 methods with a body; the index holds all of them.
- dump.cs declares 17,843 game-image bodies; the index attributes 17,843 of them,
  0 missing.
- Where both sources give an address for the same method they agree 97,236 times,
  with **0 disagreements** once 11 shared-generic instantiations are excluded
  (script.json names one instantiation, dump.cs declares the open generic once).
- Noise floor, 1,653 game getters with a body of 32 bytes or less, which must not
  write anything: `wr_board` 0, `wr_plant` 0, `wr_zombie` 0, `wr_own` 0,
  `wr_config` 0. Constructors: 2 of 3,017 for `wr_board`, 29 for `wr_zombie`, 46
  for `wr_config`, so the world bits are right to about 1%.
- `--calibrate` re-checks 13 rows against `ref/EVIDENCE.md` (sun, coins, spawns,
  cards, save, die) and every one still lands on the documented RVA and effect.
  Body sizes in `ref/EVIDENCE.md` written before 2026-11-11 measured the gap to
  the next method; the atlas now cuts at the first `int3` padding run, which is
  the real code length and up to 15 bytes shorter.
- The counts remembered from the first pass (31,719 game methods / 4,909 types /
  4,541 verbs / `void T(obj)` 36.9%) are not reachable with these definitions and
  the tool says why: they counted a method as game code when the *last* component
  of its class name matched a game class, whatever namespace that class really
  lived in - 3,742 of those 29,605 methods belong to
  `Unity.VisualScripting.Core.dll`, `UnityEngine.UIElementsModule.dll`,
  `mscorlib.dll` and `Unity.RenderPipelines.Universal.Runtime.dll` - and they
  printed the instance pointer as the first parameter, which moves the format
  family to 40.5% / 12.5% / 12.1%. The atlas keeps the strict, provable version.
