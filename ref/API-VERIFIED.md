# PvzFusion 4.1 English build — verified native API surface

Источник: `ref/dump-en/dump.cs`, снят Il2CppDumper 6.7.46 с фактического билда в `D:\PvzFusion`.
Metadata Version 31, Il2Cpp Version 31, CodeRegistration `182b1fc50` — дамп чистый, без upx/шифрования.
Проверка: `grep` по 15 ключевым типам → все присутствуют ровно по одному объявлению.
Все строки ниже скопированы из дампа как есть; `[PRIVATE]` = метод приватный в билде.

## Живая верификация против загруженной сборки (Stage 1a)

`Core/GameAPI/NativeProbe.cs` при старте мода разрешает reflection-ом все 81 член из этой таблицы
на фактическом загруженном `Assembly-CSharp` (сгенерированные прокси), а не на дампе. Результат
реального запуска (MelonLoader log, `frame=0`, до входа в меню):

```
[PvzMP] [api] main thread captured: id=1 frame=0
[PvzMP] [probe] resolved 81/81, missing=0, kind-notes=0, non-public=0, features disabled=0, api failures=0
[PvzMP] [probe] DISABLED FEATURES: none
```

Что это доказало про форму прокси (важно для всего `Core/GameAPI`):

1. **Нативное поле = C# `public` свойство** с телом `il2cpp_field_get/set_value`, плюс служебное
   `private static readonly IntPtr NativeFieldInfoPtr_<name>` на том же типе. Обычного C#-поля у
   прокси нет, поэтому `Type.GetField` находит 0 из 81 и ранняя версия зонда ложно отключала все
   12 фич. Проверяем нативность поля именно по `NativeFieldInfoPtr_`.
2. **Нативный `private` метод = `public` метод прокси**: `Il2Cpp.Mouse.SetPlantFromCard` в декомпиляте
   объявлен `public unsafe Plant SetPlantFromCard(int column, int row, PlantType type)`, а указатель
   называется `NativeMethodInfoPtr_SetPlantFromCard_Private_Plant_Int32_Int32_PlantType_0`.
   То есть приватность в билде не блокирует вызов из Mono — блокирует только понимание порядка вызова.
3. Типы полей совпали с дампом, включая `Zombie.theHealth/theMaxHealth = Int64` против
   `Plant.thePlantHealth = Int32` (строка 4 таблицы ниже) — два разных маршаллинга подтверждены живым билдом.
4. `Board.CanStartNextWave` и `Zombie.Alive` — именно нативные свойства (get/set пары), а поля —
   `kind-notes=0`, т.е. ни одного расхождения «объявил field, а там property».

Чего этот прогон НЕ доказывает: ни один метод gameplay-по-прежнему не вызывался, тела пусты,
поведение не наблюдалось. Проверено только «член существует, публично вызываем, тип совпал».

Обновление счётчика: с Stage 3a таблица зонда — **91 член**, живой прогон даёт
`resolved 91/91, missing=0, non-public=0, features disabled=0, api failures=0`.

## Стек меню игры — родной путь диалога подключения (Stage 3a, LIVE)

```csharp
// GameAPP
public static UIResourcesLoader UIManager;              // заполняется сценой меню; на frame=0 ещё null

// UIResourcesLoader
public Dictionary<UIType, GameObject> UIPrefabs;        // приватное нативное поле => public свойство прокси
public BaseMenu Push(UIType type, bool changeOldToOffscreen = true);
public void Pop();
public List<BaseMenu> Peak();

// enum UIType: InputKeyMenu = 73   — экран «ключ уровня», единственный префаб с текстовым полем

// InputKeyMenu : BaseMenu
public TMP_InputField _InputField;
public static string key;
public void OnInputOver();
```

Живой прогон (`MelonLoader/Logs/26-11-11_0-36-10.log`): мод открыл собственный промпт, записал в поле
текст, прочитал его обратно и закрыл меню — всё внутри одного `Update`, то есть до отрисовки кадра, и
игрок диалог не видит.

```
[PvzMP] [menu] watch check 1 frame=30 UIManager=present
[PvzMP] [menu] menu stack is live frame=30 after 1 checks: prefabs=94 promptRegistered=True
[PvzMP] [menu] self-test: Push(InputKeyMenu) frame=30 managedWrapper=BaseMenu visible=True
[PvzMP] [menu] self-test: text roundtrip wrote '127.0.0.1' read '127.0.0.1' match=True committedKey='127.0.0.1'
[PvzMP] [menu] self-test: popped, loader still usable=True prefabs=94 frame=30
```

Что это доказало:

1. `GameAPP.UIManager` в сцене главного меню жив и заполнен (`prefabs=94`), `UIType.InputKeyMenu` среди
   зарегистрированных. На `frame=0` (`NativeSmoke`) он **null**, и это не вердикт, а порядок загрузки —
   поэтому проверка перенесена в `MenuWatch`, который ждёт реальную сцену.
2. `Push(UIType.InputKeyMenu, false)` действительно показывает меню (`activeInHierarchy=True`), `Pop()`
   его снимает, загрузчик после попа остаётся пригодным.
3. `_InputField.text` читается и пишется из мода ⇒ ip, порт и ник собираются родным полем игры; своего
   текстового UI нет вообще.
4. Строка `frame=30` заодно доказала то, чего лог загрузки не показывал: `MelonEvents.OnUpdate` реально
   дергается, а значит `MainThreadPump` работает.

Правило формы прокси — касается всего `Core/GameAPI`, не только меню:

5. **Нисходящее C#-преобразование к прокси неприменимо.** `Push` объявлен как `BaseMenu`, и interop
   оборачивает объект по *объявленному* типу: `prompt as InputKeyMenu` даёт `null` всегда (живой прогон
   до фикса: `pushed menu is BaseMenu`). Правильно — `prompt.Cast<InputKeyMenu>()`: он сверяет реальный
   нативный класс (`il2cpp_class_is_assignable_from`) и в исключении называет его, а не угадывает по
   managed-типу обёртки.

Поведение, которого дамп не показывает (тела пусты) и которое известно только из прогона:

6. **`InputKeyMenu.key` — это «что сейчас в поле», а не «игрок подтвердил».** Запись `_InputField.text`
   из кода сама по себе сделала `key == '127.0.0.1'`, хотя `OnInputOver()` никто не вызывал. Значит
   `key`/`text` годятся только как *значение*, а сигнал *подтверждения* надо брать у самого
   `OnInputOver` (Harmony-наблюдатель или `GameMassage`) — иначе мод начнёт подключаться до того, как
   игрок нажал OK.

### Примитив диалога заменён на `ReNameMenu` (Stage 3b)

`InputKeyMenu` годится как поле, но у него нет родной кнопки подтверждения: сигнал пришлось бы снимать
Harmony-наблюдателем с `OnInputOver`. В дампе есть строго лучший вариант — экран переименования, который
игра использует именно как «спросить строку»:

```csharp
// enum UIType: ReNameMenu = 75
// ReNameMenu : BaseMenu                    (TypeDefIndex 2502)
public TextMeshProUGUI title;              // вопрос сверху, родным шрифтом игры
public UIButton activeButton;              // кнопка подтверждения
public TMP_InputField input;               // то же поле, что и в InputKeyMenu
public void SetTitle(string text);
public void SetInputText(string text);

// UIButton : MonoBehaviour
public UnityEvent clickEvent;              // public поле, не приватное
public bool Interactable { get; set; }
```

`ReNameMenu` игра никогда не заполняет сама: каждый вызывающий вешает свой замыкание на `activeButton`
(`PlayerSaveMenu.<ResetName>b__0`, `LevelSaveEnter.<EditLevel>b__0`,
`GardenUI.<OnTurnPageButtonClicked>b__0`), то есть «повесить обработчик на кнопку промпта» — штатный
механизм игры, а не взлом. Harmony для подтверждения больше не нужен вовсе.

Что доказано сборкой (`clickEvent.AddListener(() => ...)` компилируется): у прокси
`UnityEngine.Events.UnityAction` есть `implicit operator` из `System.Action`, то есть managed-делегат
можно передать в нативное событие. **Живого доказательства, что обратный делегат реально дёргается при
клике, ещё нет** — это первый ручной тест после этого этапа.

`Core/GameAPI/NativePromptScreen.cs` поэтому делает в обработчике только два действия: читает `input.text`
и ставит флаг. `PromptDialog.Tick()` опрашивает флаг из `MainThreadPump` и принимает там все решения —
закрывать меню или открывать сокет внутри клика нельзя, событие ещё в стеке. Заодно `Menu.IsTop()`
(через `Peak()`) запрещает моду закрывать чужое меню: `Pop()` снимает верх стека независимо от того,
чей он.

Таблица `NativeProbe` после замены: **92** спецификации (было 91), из них `mp-menu` — 11: `UIManager`,
`UIPrefabs`, `Push/Pop/Peak`, `UIType.ReNameMenu`, `SetTitle`, `SetInputText`, `input`, `activeButton`,
`clickEvent`. Прогон выше (`resolved 91/91`) сделан до замены — новый вердикт будет на первом же запуске.

Что этот раздел сам по себе НЕ доказывал: что диалог видно и читаемо, что клавиатура долетает до
`TMP_InputField` внутри открытого меню и что обработчик клика срабатывает. Все три закрыты прогоном ниже.

### Диалог на ReNameMenu: живой прогон (Stage 3b-4, LIVE)

Логи `MelonLoader/Logs/26-11-11_1-26-5.log`. Заказчик в главном меню: F2 → адрес → OK, порт → OK,
ник → OK, затем F1 → ник → OK.

```
[PvzMP] [mp] dialog started for a join
[PvzMP] [api] Push(ReNameMenu) -> BaseMenu@0x27C9391B960 frame=14837
[PvzMP] [Il2CppInterop] Registered mono type Il2CppInterop.Runtime.DelegateSupport+Il2CppToMonoDelegateReference in il2cpp domain
[PvzMP] [api] clickEvent.AddListener -> ok frame=14837
[PvzMP] [api] SetTitle -> ok frame=14837
[PvzMP] [api] SetInputText -> ok frame=14837
[PvzMP] [mp] dialog asks address: Multiplayer address
[PvzMP] [mp] dialog confirm clicked, box = '26.225.12'
[PvzMP] [mp] dialog address = '26.225.12'
[PvzMP] [mp] dialog asks port: Multiplayer port
[PvzMP] [mp] dialog confirm clicked, box = '7777'
[PvzMP] [mp] dialog asks nick: Your nickname
[PvzMP] [mp] dialog confirm clicked, box = 'Playe123'
[PvzMP] [api] Pop -> ok frame=17120
[PvzMP] [mp] dialog complete: join 26.225.12:7777 as Playe123
[PvzMP] [mp] entry ready: join 26.225.12:7777 as Playe123 frame=17120
[PvzMP] [mp] dialog started for a host
[PvzMP] [mp] dialog confirm clicked, box = 'Player'
[PvzMP] [mp] dialog complete: host as Player
```

Три вопроса, на которые прогон до этого не имел ответа, закрыты:

1. **Диалог видно.** Заказчик подтвердил, что экран появился, и печатал в него.
2. **Клавиатура долетает до `TMP_InputField` внутри нативного меню**: в поле пришли `26.225.12` и
   `Playe123` — мод ничего не писал в это поле в момент ввода, `SetInputText` был только префилом.
3. **Обратный делегат работает.** `clickEvent.AddListener` зарегистрировал managed-замыкание (в логе
   строка Il2CppInterop про `Il2CppToMonoDelegateReference`), и три клика родной кнопкой его реально
   дёрнули: `dialog confirm clicked, box = ...`. Harmony не понадобился.

Плюс то, чего логи до этого не показывали: последовательность вопросов с префилом (`7777` принято
как есть), `Pop()` по завершении, отдельно хост-ветка (только ник) и `dialog complete` / `entry ready`
с cleaned-значениями.

### Поле диалога обрезало адрес: `TMP_InputField.characterLimit` (Stage 3c, FIX, требует прогона)

Заказчик вставил в вопрос про адрес полный `26.225.125.120`, а в `input.text` пришло `26.225.12` —
9 символов. Это не наши правила: `AddressRules.MaxHostLength` = 64 и `CleanHost` ничего из
дочетверичной записи не отбрасывает. Обрезало само нативное поле — `ReNameMenu` игрой сделан как поле
имени и приходит со своим лимитом длиной в несколько символов (тот же прогон потерял и букву в нике:
`Player123` пришёл как `Playe123`).

Проверка по дампу (`ref/dump-en/dump.cs`, класс `TMP_InputField`, TypeDefIndex 13657):

```csharp
public int characterLimit { get; set; }
```

Что делает мод ([Core/GameAPI/Menu.cs](Core/GameAPI/Menu.cs) +
[Core/GameAPI/NativePromptScreen.cs](Core/GameAPI/NativePromptScreen.cs)):

1. сразу после `Push(ReNameMenu)` читает `input.characterLimit` и тут же поднимает его до
   `AddressRules.MaxHostLength` (64) — на всё время, пока диалог держит экран;
2. в `Close()` возвращает прочитанное значение на место, и только потом `Pop()` — и только пока меню
   ещё своё (`IsTop`), иначе мод лезет в чужой экран;
3. открывание диалога печатает оба числа одной строкой, какой лимит был у префаба.

Что это доказывает само по себе: ничего. Живой прогон обязан показать строку
`the dialog box holds 64 characters, the menu came with N` и `dialog complete: join 26.225.125.120:...`.
Если `N` окажется ≥ 15, значит причина не в `characterLimit` и обрезку надо искать в
`characterValidation`/`contentType` того же поля.

`NativeProbe` эту пару членов не проверяет: `TMP_InputField` живёт не в `Assembly-CSharp`, и спека
упрала бы возможность `mp-menu` целиком. Подпись держится на компиляции (проект ссылается на
`Il2CppTMPro.dll`) и на строке из дампа выше.

### `GameStatus` — не признак боя (Stage 3b-4, LIVE, гейт переписан)

Тот же прогон сначала ничего не открывал, и отказ был наш, а не игры:

```
[PvzMP] [mp] ignored join: ... theGameStatus=0/InGame      (главное меню)
[PvzMP] [mp] ignored join: ... theGameStatus=3/Selecting   (экран выбора уровня/растений)
```

Значит `theGameStatus == InGame` **в главном меню**, и предикаты `IsInBattle/IsPaused/IsSelecting` по
этому полю ничего не различают — удалены, чтобы никто на них не оперся. Единственный измеренный признак
идущего боя — газон: `Board.Instance`. Гейт входа в комнату теперь смотрит на него и печатает оба факта
на каждое нажатие (`lawn=present|none theGameStatus=...`), так что расхождение видно сразу.

Чего прогон всё ещё НЕ доказал: почему нажатия после переключения игры в оконный режим закончились
строкой `the dialog is not the top menu any more, ... dialog stopped: the dialog was closed without an
answer`. Мод повёл себя правильно (свой `Pop` не сделал, сессию не начал), но кто закрыл меню — игра при
смене разрешения, клик мимо или Escape — не измерено.

## Клавиатура: вход в мультиплеер хоткеями возможен (Stage 3, LIVE)

Мобильная производная порта — а значит «физическая клавиатура доходит до Unity» надо было измерить, а
не предполагать. Измеряли read-only опросом `UnityEngine.Input` из `OnUpdate` (чистое чтение, ничего не
меняется; заказчик в это время держал F1/F2). Живой прогон `MelonLoader/Logs/26-11-11_0-50-42.log`:

```
[PvzMP] [keys] probe started frame=1, legacy input read ok; ...
[PvzMP] [keys] press #1 F1 anyKey=True inputString='' touches=0 frame=116
...
[PvzMP] [keys] press #20 F2 anyKey=True inputString='' touches=0 frame=429
[PvzMP] [keys] probe finished: 20 presses in 429 frames / 7s
```

Выводы:

1. Legacy Input Manager в билде **включён и читается** — ни одного исключения, `api failures=0`.
   `UnityEngine.Input` лежит не в `CoreModule`, а в `MelonLoader/Il2CppAssemblies/UnityEngine.InputLegacyModule.dll`
   (в `CoreModule` его нет вовсе) — это отдельная ссылка в csproj.
2. **F1 и F2 реально долетают** и различаются: 20 нажатий, все названы правильно. Пропущенных не было,
   интервал между событиями 0.2–0.5 с.
3. Темп опроса ≈ **57 fps** (`429` кадров за `7` с). `frame=30` наступил только через 15 с — это экран
   загрузки, а не меню; в меню кадры идут обычно, так что опрос раз в `Update` для хоткея достаточен.
4. `touches=0` при живой клавиатуре ⇒ тач-ввода в этом окне нет, он не маскирует клавиши.
5. `inputString=''` для F1/F2 — функц-клавиши символ не дают, так что для набора ip/порта/ника нужно
   именно поле `TMP_InputField`, а не `inputString`.

Чего прогон НЕ доказал: что клавиатура долетает до `TMP_InputField`, когда открыт нативный промпт (фокус
у поля — отдельный вопрос), и что игрок это окно видит. Это уже ручной тест Stage 3b.

Реализация входа: `Keyboard.WasPressed` = `Input.GetKeyDown` через `ApiBase.Query` (чистое чтение),
`ConnectionEntry.Tick` подписан на `MelonEvents.OnUpdate` после pump и `MenuWatch`. F1 ⇒ хост (только
ник), F2 ⇒ гость (ip → порт → ник), Escape ⇒ отмена. Клавиша игнорируется, если уже есть газон
(`Board.Instance` — единственный измеренный признак идущего боя, см. раздел выше), если `mp-menu` в
breakers или если префаб не зарегистрирован; каждое нажатие печатает `theGameStatus` и `lawn=...`, чтобы
отказ был виден как факт, а не как догадка. Ни один из этих путей ничего не меняет в игре, пока игрок
не нажал F1/F2.

## Соответствие брифингу: расхождения

| # | Утверждение брифа | Факт в английском билде | Чем грозит |
|---|---|---|---|
| 1 | `Mouse.SetPlantFromCard(int,int,PlantType)` — «локальная точка установки растения» | нативно **`private Plant SetPlantFromCard(int, int, PlantType)`**, но прокси Il2CppInterop выставил его **public** | вызывать из Mono **можно напрямую**; остаётся только семантический риск (`PRIVATE-RISK` = «тело не читал, порядок вызова внутри не знаю»), а не риск доступности |
| 2 | `Board.RequestNextWave()`, `Board.NewZombieUpdate()` как точки волн | оба нативно **`private`**, в прокси так же public | глушить волны клиента удобнее на **публичном `BoardSpawner.SummonZombies(int wave)`**; Harmony по приватному методу не требуется |
| 3 | `BoardEntity.plantHeads` = `Dictionary<int,List<Plant>>` | `Dictionary<PlantType, List<Plant>>` | типизация реестра растений |
| 4 | «здоровье `long`» | **асимметрия**: `Zombie.theHealth/theMaxHealth` = `long`, `Plant.thePlantHealth/thePlantMaxHealth` = `int` | два разных маршаллинга в HP-реконсиле |
| 5 | `InGameUI.cards` | `public HashSet<CardUI> cards` (не List) | обход без индексов |
| 6 | «нет `Board.isPause`» | подтверждено; пауза в `GameStatus.Pause` (`GameAPP.theGameStatus`) | гейт хуков берём от `theGameStatus` |
| 7 | нет `AddZombie/AddPlant/Spawn*` у Board | подтверждено | спавн только через `Create*` |
| 8 | — | **`CardSlotManager.MaxSlotCount = DefaultMaxCardCount = 14` (const)** | жёсткий потолок слотов: 4 игрока × 7 карт = 28 > 14, общий банк на Stage 5 надо считать от 14 |

## Creators — все `public static <Type> Instance`

```csharp
// CreateZombie
public Zombie SetZombie(int theRow, ZombieType theZombieType, float theX = 9.9f,
                        bool isMindControlled = false, bool ignoreTerrain = false);
public Zombie SetZombieWithMindControl(int theRow, ZombieType theZombieType,
                        float theX = 9.9f, bool withEffect = false);      // НОВОЕ, вне брифа
public static GameObject CreateZombiePreview(ZombieType theZombieType, Color color,
                        Transform parent, Vector2 position);              // НОВОЕ: превью-объект
// CreatePlant
public Plant SetPlant(int newColumn, int newRow, PlantType theSeedType, Plant targetPlant,
                        Vector2 puffV, bool isFreeSet = false, bool withEffect = true,
                        Plant hidplant = null, TravelRoguePlantData travelRogueData = null);
public bool CheckBox(int theBoxColumn, int theBoxRow, PlantType theSeedType);
public static GameObject CreatePlantPreview(PlantType theSeedType, Color color,
                        Transform parent, Vector2 position);              // НОВОЕ: призрак без спавна
// CreateBullet
public Bullet SetBullet(float x, float y, int theRow, BulletType theBulletType,
                        BulletMoveWay theMovingWay, bool fromEnermy = false);
// CreateItem
public GameObject SetCoin(int theColumn, int theRow, int theItemType, int theMoveType,
                        Vector3 pos = default, bool freeSet = false);     // theItemType/theMoveType = int, не enum
```

## Board / BoardEntity / BoardSpawner

```csharp
public static Board Instance;                       // есть
public int theSun; public int maxSun;               // есть, int
public void SetSun(int count); public void UseSun(float count);
public void GetSun(float count, bool save = true);
public int theWave; public int theMaxWave;
public float timeUntilNextWave; public float holdOnTime;
public bool CanStartNextWave { get; }               // + public bool get_CanStartNextWave()
public void StartNextWave();
[PRIVATE] private void RequestNextWave();
[PRIVATE] private void NewZombieUpdate();
[PRIVATE] private void Update();
public List<Zombie> zombieArray; public List<Zombie> zombieHead;
public Dictionary<ZombieType, List<Zombie>> zombieHeads;
public void OnPlantCreate(Plant plant); public void OnPlantDie(Plant, Plant.DieReason);
public void ClearTheBoard();
public BoardSpawner boardSpawner;                   // НОВОЕ: доступ к спавнеру волн
// BoardSpawner
public void SummonZombies(int wave);                // ПУБЛИЧНАЯ точка спавна волны
public void MowerStart(int row, int wave); public void ResetMowerProtection();
// BoardEntity
public List<Plant> plantArray, hiddenPlants, plantHead;
public Dictionary<PlantType, List<Plant>> plantHeads;
public List<Bullet> bulletArray;
public Dictionary<int, List<Zombie>> waveZombies; public void AddWaveZombie(int, Zombie);
```

## Сущности

```csharp
// Zombie
public long theHealth; public long theMaxHealth;   // LONG, готча брифа подтверждена
public int theFirstArmorHealth, theFirstArmorMaxHealth;    // 0xC0 / 0xC4
public int theSecondArmorHealth, theSecondArmorMaxHealth;  // 0xC8 / 0xCC
public ZombieType theZombieType; public ZombieStatus theStatus;
public Towards towards; public int theZombieRow; public float theSpeed;
public float theOriginSpeed;                                // 0x110, база
public float freezeSpeed, coldSpeed, butterSpeed;           // 0x114 / 0x118 / 0x11C
public float kelpSpeed, garlicSpeed, uniqueSpeed;           // 0x120 / 0x124 / 0x128
public float holeSpeed, maLiangSpeed;                       // 0x12C / 0x130
public void AdjustPosition(Vector3 position);                // 0x702AE0, родной телепорт (E21)
public virtual bool Alive { get; }
public virtual void Die(int reason = 0);            // Slot 34, 42 override в дампе (E18b)
// Mower — газонокосилка
public int theMowerRow; public MowerType theMowerType;     // 0x20 / 0x24
public bool started;                                        // 0x30
public void StartMove();                                    // public, не virtual
[PRIVATE] private void OnTriggerStay2D(Collider2D collision);
// GridManager — центр клетки для призрака
public static GridManager Instance { get; }
public Vector3 GetGridCenter(int col, int row);
// MixData — таблица слияний, чистое чтение (E20)
public static bool TryGetMix(PlantType a, PlantType b, out PlantType c, bool order = true);
// GameAPP.config — те же поля, что пишут ползунки меню паузы (E20)
public static GameConfig config;                            // 0x128
public int difficulty;                                      // 0x10
public float gameSpeed;                                     // 0x14
// Plant
public int thePlantHealth; public int thePlantMaxHealth;   // int, НЕ long
public int thePlantColumn; public int thePlantRow; public PlantType thePlantType;
public Plant.DieReason dieReason;
public virtual void Die(Plant.DieReason reason = 0);
public void RealTakeDamage(int damage);
public override void TakeDamage(int damage, IDamageMaker damageFrom, DamageType damageType = 0,
                                PlantType reportType = -1, bool fix = false);
// CoinSun (компонент на GameObject из CreateItem.SetCoin)
public void Die();  public int theMoveType;
```

## Ввод и курсор (закрывает Stage 4e без своей математики сетки)

```csharp
public static Mouse Instance;
public int theMouseRow; public int theMouseColumn;              // клетка под курсором — нативно
public float theBoxXofMouse; public float theBoxYofMouse;
public CardUI theCardOnMouse;                                   // что игрок держит — нативно
public MouseItemType mouseItemType;                             // 0x60; Shovel=3, Glove=4 (E19), но этот билд Shovel сюда не пишет: единственная запись — обнуление в Mouse.ClickOnCard+0x2B0 (E14). «Лопата в руке» — InGameTool.isPickUp (0x2C)
public Vector2 MousePosition { get; }
public int GetColumnFromX(float x); public int GetRowFromY(float x, float y);
public float GetBoxXFromColumn(int theColumn); public float GetBoxYFromRow(int theRow);
[PRIVATE] private Plant SetPlantFromCard(int column, int row, PlantType type);
[PRIVATE] private void TryToSetPlantByCard();
[PRIVATE] private void PreviewPositionUpdate();                 // своя нативная механика призрака у игры
// Shovel — точка сноса
public static Shovel Instance;                     // сам объект инструмента (InGameTool)
public override bool OnClick(Mouse mouse); public override void Use(Mouse mouse);
public void ReturnPlantToGarden(Plant plant);
[PRIVATE] private void PayBackSun(Plant plant);
// InGameTool.PickUp: set_enabled + set_parentInternal(this.transform) — 0x7D4470 (E21);
// и isPickUp (0x2C) = 1; PutDown (0x7D44D0) пишет 0 — это и есть сигнал «лопата в руке»;
// нативной превью-функции у инструмента нет, призрак лопаты берёт его спрайт в рантайме.
```

## Первый detour: gate на `Mouse.TryToSetPlantByCard` (Stage 4b, требует живого прогона)

Сигнатуры подтверждены `NativeProbe` в живом запуске (`non-public=0`, `resolved 92/92`), тело — пусто
в дампе, поэтому всё ниже — про вызываемость, а не про поведение:

```csharp
[PRIVATE] private void TryToSetPlantByCard();                                  // вход клика картой
[PRIVATE] private Plant SetPlantFromCard(int column, int row, PlantType type); // рабочий метод, params 3
public CardUI theCardOnMouse; public int theMouseColumn; public int theMouseRow;
```

Механизм: `MouseGate` ставит Harmony-prefix на `TryToSetPlantByCard` один раз при старте мода
(`MelonEvents.OnApplicationStart`). Префикс спрашивает `WorldSync.TryCapturePlacement()` и возвращает
`true` (пустить оригинал) всегда, когда комната не активна — то есть в одиночной игре код мода ничего
не делает, кроме одного bool. В комнате оригинал пропускается, а мод отправляет хосту
`Gameplay/PlacePlantRequest{PlantType, Column, Row}`; хост проверяет (`Roster` → `Entities.PlantAt` →
`Plants.CanPlace` → нативный `CreatePlant.SetPlant`) и рассылает `SpawnPlant{NetId,...}` + абсолютный
`SunState`. Отказ — `Gameplay/RequestDenied{Requested, Reason}`, где `Reason` обязан быть короче
`Protocol.MaxTextChars = 64` (иначе кадр ловится на decoding и отказ не доходит).

Что обязан напечатать прогон (иначе 4b не считается проверенным):

- `[hook] Mouse.TryToSetPlantByCard is gated frame=…` — Harmony собрал патч на IL2CPP-методе. Если
  вместо этого `capability disabled: mp-placement` — детур не встал, и это отдельная задача.
- Клик картой у клиента: `place request from peer 1: type=0 cell=2,3` на хосте, затем
  `plant #1 granted to peer 1 on cell 2,3` и `sun pushed as 25/500`.
- У клиента: `plant #1 from peer 1 cell 2,3 -> obj <instanceID>` — то есть растение появилось из чужого
  кадра, а не из локального клика. Локального `SpawnPlant` из `Plants.Spawn` **до** этой строки у клиента
  быть не должно.
- Одновременный клик двух игроков в одну клетку: второй получает `peer N's PlacePlantRequest refused:
  cell 2,3 already holds plant #1` (формуляр отказа общий для посадки и копки, `DenyPayload.Requested`
  говорит, о чём именно речь).

Гипотезы, которые прогон проверяет впервые: (1) метод вызывается ровно на клик, а не каждый кадр с
картой в руке (наш `RepeatAskingMillis = 2000` держит это в рамках, но частоту строк видно только в
логе); (2) других путей посадки в бою нет — если после gate растение всё равно появляется локально,
точек патча нужно больше; (3) `theCardOnMouse.thePlantType` и есть то, что игра собиралась сажать.

### Дырка Stage 4b, которую нашёл и закрыл Stage 4c: хост не читал чужие запросы

`WorldSync.HandleFrame` разбирал `SpawnPlant`, `SunState`, `RequestDenied` и всё про уровень, но `case`
для `Gameplay/PlacePlantRequest` в нём отсутствовал. Запрос клиента проваливался в
`LevelSync.HandleFrame` и печатал «no one handles … yet» — то есть клиент мог просить посадку сколько
угодно, хост молчал, и 4b end-to-end в паре не работал никогда (в одиночной игре и на хосте — работал,
поэтому прогон 4b это не поймал). Вместе с маршрутом заведены полномочия, потому что одна строка
`case` их не описывает:

- Прошения о газоне (`PlacePlantRequest`, `RemovePlantRequest`) принимает **только хост** (`IAmTheHost`).
  Клиент, которому прислали такое, отвечает себе `peer N sent a <Action> to this end: only the host
  decides what is on the lawn` и молчит — два конца, сажающих одну клетку, хуже одного молчаливого.
- Суждения хоста (`SpawnPlant`, `DespawnPlant`, `SunState`, `RequestDenied`) принимает только кадр,
  пришедший **от хоста** (`TheHostSaidIt` — по флагу `IsHost` в ростере; `MpHost` внутрь `Core/GameAPI`
  не заходит, проверка границ green).
- Право спрашивать комнату собрано в одно `MpSession.RoomDecides`: хост решает с момента, как
  слушает порт (уровень, начатый до первого входа, — уже комнатный), клиент — только получив место.
  Игрок, который ещё только добирается по IP, остаётся одиночкой и на посадке, и на лопате, и на уровне;
  раньше это было верно только для уровня.

## Второй detour: gate на `UIMgr.EnterGame` (Stage 5a, требует живого прогона)

Сигнатура в таблице зонда стоит с самого раннего этапа (`M("level-flow", "UIMgr", "EnterGame", 4, "Void")`),
живой прогон Stage 1a её резолвил; `theBoardLevel`/`theBoardType` — тоже (`app-state`). Тело пусто в дампе,
поэтому ниже — про вызываемость и про задуманное поведение:

```csharp
[STATIC][PUBLIC] public static void EnterGame(LevelType levelType, int levelNumber, int id = -1, string name);
public static int theBoardLevel; public static LevelType theBoardType;   // что игра считает текущим уровнем
[PRIVATE] private void Restart();                                          // PauseMenu_Btn, тело неизвестно
```

Механизм: `LevelGate` ставит Harmony-prefix на `EnterGame` один раз при старте. Префикс отдаёт аргументы
`LevelSync.TryCaptureStart` и пускает оригинал, кроме одного случая — **клиент, у которого уже есть место
в комнате**, сам кликнул уровень. Клиент, который ещё только соединяет, своих кликов не теряет: ему никто
уровень ещё не обещал. Хост: оригинал идёт как обычно, но перед ним в комнату летит `Gameplay/LevelStart{LevelType, LevelNumber,
LevelId, LevelName}`, а реестр сущностей и незакрытая просьба о посадке сбрасываются (номера `NetId`
начинаются с 1 вместе с новым газоном). Клиент: по этому кадру сам зовёт нативный `EnterGame` с теми же
аргументами под флагом `_applying`, и потому не блокируется собственным же gate.

Ограничение протокола, которое надо помнить: `LevelName` обязан быть короче `Protocol.MaxTextChars = 64`,
иначе кадр лопнет на декоде у клиента и читатель закроет линк — то есть одно длинное имя сохранения убило бы
комнату. Проверка сделана до отправки (`LevelAnswer.Unshareable`), и в этом случае хост играет один, а не
«как-нибудь синхронизируется».

Подтверждение загрузки: клиент шлёт `LevelReady` только когда появился `Board.Instance` **с другим
`GetInstanceID()`**, чем был в момент команды. Иначе перезаход в тот же уровень выглядел бы как «уже готово»,
ведь старый газон в момент команды ещё жив.

Что обязан напечатать прогон (иначе 5a не считается проверенным):

- `[hook] UIMgr.EnterGame is gated frame=…` и `the level is gated on the room: only the host starts one`.
- Хост: `the host starts level type 0 level 3 (id=-1 name=''), the room is told`.
- Клиент: `UIMgr.EnterGame(type 0 level 3) -> ok` (это строка `ApiBase` про родной вызов) → через
  несколько кадров `the host's level type 0 level 3 is up here as type 0 level 3`. Если вторая пара чисел
  не совпадает — отдельно `this lawn is not the level the host named`, и это правда, а не баг мода: поля
  читаются у игры.
- Клиент в комнате, свой клик: `level type 0 level 5 is not started here: the host starts the level, your
  own pick is ignored`, и уровень НЕ загрузился.
- Хост на `LevelReady`: `peer 1 (Client) is in type 0 level 3`.
- Перезаход хоста из меню паузы обязан дать те же строки `the host starts level …` — если их нет,
  `PauseMenu_Btn.Restart` не проходит через `EnterGame`, и это вторая точка патча (гипотеза).
- Одиночная игра: ни одной из этих строк, уровень запускается как раньше.

## Третий detour: gate на лопату (Stage 4c; установка гейта проверена запуском, сама копка — нет)

> UPDATED Stage 2 (11.11.2026): байтовая дизассемблерия показала, что копает только `Use`, а `OnClick` —
> выбор лопаты (`PickUp`). Патч с `OnClick` снят, гейт остался один — на `Use`; первая пара вызовов тела
> (`PutDown` + `ClearItemOnMouse`) повторяется модом на клиенте, иначе лопата остаётся в руке.
> См. `ref/EVIDENCE.md` E14/E14b. Всё, что ниже, — журнал Stage 4c.

```csharp
public class Shovel : InGameTool                       // dump.cs:75724
[PUBLIC][OVERRIDE] public override bool OnClick(Mouse mouse);  // 75738
[PUBLIC][OVERRIDE] public override void  Use(Mouse mouse);     // 75741
[PRIVATE]          private void PayBackSun(Plant plant);       // 75744 — есть, мод не зовёт
[PUBLIC]           public void  ReturnPlantToGarden(Plant plant); // 75747 — то же

public class Mouse                                     // 68736
[PUBLIC] public List<Plant> GetPlantsOnMouse();        // 68879 — хит-тест игры: клетка отвечает списком

public class Plant
[PUBLIC][VIRTUAL] public virtual void Die(Plant.DieReason reason = 0);  // DieReason.ByShovel = 7 (29929)
```

Все пять новых членов стоят в таблице `NativeProbe` (`input`: `GetPlantsOnMouse`; `shovel`: `OnClick`,
`Use`); на прогоне Stage 1a было `resolved 92/92`, теперь строк 94. **Живой запуск 11.11.2026
(`26-11-11_3-49-54.log`) закрыл именно это: `resolved 94/94, missing=0, non-public=0, features
disabled=0, api failures=0`, `DISABLED FEATURES: none`, и рядом три строки гейтов —
`Mouse.TryToSetPlantByCard is gated`, `Shovel.OnClick and Shovel.Use are gated`,
`UIMgr.EnterGame is gated`, плюс `ready; no session exists, single player is untouched` и ни одного
исключения.** Проверено только то, что Harmony собрал патч над IL2CPP-методами лопаты и что прокси их
видит; сама копка в комнате — ниже, её ещё не было. Тела пусты в дампе, поэтому ниже — про вызываемость
и про задуманное поведение, а не про то, что игра делает внутри.

Механизм: `ShovelGate` патчит **оба** метода лопаты (`prefix` + `postfix`), потому что из дампа не видно,
в каком из двух тел сама копка. Первый сработавший `prefix` решает за клик, второй в том же кадре
повторяет его ответ (`_shovelClickFrame`), поэтому один клик не может спросить комнату дважды; если же
вызовы приходят в разных кадрах, второй останавливает окно `RepeatAskingMillis = 2000` и в логе будет
`not asking twice`. У `OnClick` пропущенный оригинал обязан вернуть `false` — «инструмент этот клик не
взял», — и префикс так и делает через `ref bool __result`.

Растение для вопроса берётся не из клетки, а из родного хит-теста игры (`GetPlantsOnMouse`), потому что
клетка в момент клика по иконке ещё помнит прежнее наведение, а на одной клетке может стоять два
растения. Комната спрашивает то из них, которое знает (`ShovelRules.TrackedPlant`), и никогда не выбирает
между незнакомыми.

Правило (чистый `ShovelRules.Refusal`, покрыт тестами): выкопать можно **любое** растение комнаты, чьим
бы оно ни было. Отказ ровно один — «этого растения комната не выращивала», то ли потому, что уровень
начался с него, потому что его дала игра (`TurnPlantsToNewPlants`, слияния пшеницы), либо потому, что в
кадре прислали `NetId` не растения или уже мёртвого. Такое копает сама игра, и на других концах оно
останется — это осознанная граница среза, а не пропуск.

Хост копает себя: оригинал лопаты **не** блокируется (значит работают звук, анимация и `PayBackSun`), а
`postfix` того же кадра и пушит `SunState` — потому что `theSun` нативная лопата меняет уже после `prefix`,
а в комнату уходит абсолют. Клиент копает: `Gameplay/RemovePlantRequest{NetId}` → хост проверяет и зовёт
`Plant.Die(ByShovel)` на своём газоне (своей лопаты у хоста для чужого клика нет) → `DespawnPlant{NetId,
Reason}` всем → каждый клиент тоже `Plant.Die(Reason)`, где `Reason` — число из кадра, не из головы мода.

Что обязан напечатать прогон (иначе 4c не считается проверенным):

- `[hook] Shovel.OnClick and Shovel.Use are gated frame=…`; если вместо этого `capability disabled:
  mp-shovel` — Harmony не собрал патч над IL2CPP-методом, и копка в комнате остаётся локальной.
- Клик лопатой у клиента: `dig request from peer 1: plant #1 cell 2,3` на хосте и `plant #1 dug up by peer
  1, roster: …`; у клиента — `plant #1 from peer 1 cell 2,3 left this lawn`. Локального `KillPlant` **до**
  этой строки у клиента быть не должно: до неё растение ещё обязано стоять.
- Хост своей лопатой: те же строки с `peer 0` плюс `sun pushed as …` сразу после них. Число в этой строке
  и есть ответ на вопрос, возвращает ли лопата солнце: сверить `theSun` до копки и после.
- Растение, которого комната не знает: `the shovel is aimed at something the room did not grow, the game
  digs it alone`. Если такая строка появилась на растении, которое **комната и сажала**, — значит
  `GetPlantsOnMouse` отвечает не тем, чем игра копает, и точку поиска надо менять.
- Отказ: `peer 1's RemovePlantRequest refused: …` на хосте и `the host refused my RemovePlantRequest: …`
  у клиента.
- Одиночная игра: ни одной из этих строк, лопата работает как раньше.

Гипотезы, которые прогон проверяет впервые: (1) в каком из двух тел копка (патча два, поэтому промах не
даёт тихого локального копания — максимум «лопата не работает» + строка в логе); (2) укладываются ли оба
вызова в один кадр; (3) меняет ли нативная лопата `theSun` (`PayBackSun` приватен и модом не зовётся,
поэтому у клиента копка возврата не даёт — если у хоста солнце подскочило, это правило надо пересматривать
вместе с Stage 4d); (4) не приходит ли `OnClick` на взятие лопаты в руки (тогда `dig request …` появится
без клика по газону).

## Список лобби: нативные типы вне `Assembly-CSharp` (Stage 4d-HUD, собран, живого прогона ещё не было)

`NativeProbe` эти члены проверить не может: он резолвит только `Il2Cpp.<Type>` из `Assembly-CSharp`, а
TMP живёт в `Unity.TextMeshPro.dll` (в дампе `// Namespace: TMPro`, в прокси — `Il2CppTMPro`), движковые
типы — в `UnityEngine.CoreModule/UIModule`. Это тот же сознательный пробел, что и с `TMP_InputField` на
Stage 3c. Подтверждение здесь — **компиляция против сгенерированных прокси** (сборка проходит, 0
предупреждений), плюс строки дампа:

```csharp
// Il2Cpp (dump.cs:66454-66455) — родные родители UI, искать канвас по сцене не нужно
public static Transform canvas; public static Transform canvasUp;

// TMPro / Il2CppTMPro
public class TextMeshProUGUI : TMP_Text, ILayoutElement;            // dump.cs:677117
public virtual string text { get; set; }                            // TMP_Text, dump.cs:683463
public TMP_FontAsset font { get; set; }                             // dump.cs:683466
public float fontSize { get; set; }  public override Color color { get; set; }
public TextAlignmentOptions alignment { get; set; }                 // MidlineRight = 4100
public bool enableWordWrapping { get; set; }
public RectTransform rectTransform { get; }                         // dump.cs:683533
public static TMP_FontAsset defaultFontAsset { get; }               // TMP_Settings, dump.cs:681852

// UnityEngine
public GameObject(string name);  public T AddComponent<T>();        // dump.cs:577869
public Transform transform { get; }  public void SetActive(bool);
public void SetParent(Transform parent, bool worldPositionStays);   // dump.cs:580025
public Vector2 anchorMin/anchorMax/anchoredPosition/sizeDelta { get; set; } // RectTransform
public static void Destroy(Object obj);                             // dump.cs:578664
```

Что обязан напечатать прогон (иначе HUD не считается проверенным):

- `the lobby list is up under canvasUp with the font '<имя>'` — строка называет шрифт, который реально
  дал `TMP_Settings.defaultFontAsset`; если шрифт `''` или список не виден, причина в этом ассете, а не в
  позиционировании.
- На `F1` (host): `the lobby list shows 1 of 4: Lobby 1/4 #0 Player (host)`.
- На присоединении клиента: та же строка с `Lobby 2/4` и `#1 <ник>`, у хоста и у клиента **символ в
  символ** (это и есть тест `TheHostAndTheClientPrintTheSameList`).
- На `F3`/потере хоста: `the lobby list is removed: no room is running`, и в одиночной игре этих строк
  нет вовсе.
- Если вместо списка в логе `capability disabled: mp-hud (...)` — нативный путь отрисовки не работает, и
  мода обязана молчать (исключений в `OnUpdate` быть не должно).

Ширину бокса и отступ (`300x300`, `20 px` от правого края) мод выбрал сам: нативных констант для «свободного
места справа» в игре нет. Это проверяется только глазами, и это единственная цифра здесь, которую дамп не
подтверждает.

## Четвёртый detour: «в бой» только хостом (Stage 6; установка гейта проверена запуском, сам бой — нет)

```csharp
// InGameUI, dump.cs:75608 — ровно то, что делает родная кнопка старта боя
public void OnStartBattleButtonClick();   // public, 0 параметров, не статический: нужен InGameUI.Instance
public static InGameUI Instance;          // уже подтверждён живым запуском (seed-bank)
public GameObject StartBattle;            // сам объект кнопки; мод его не трогает

// вторая дверь, которую закрывает только прогон: тот же старт как функциональная карта
// enum FunctionCardType: StartBattle = 9 (dump.cs:21570) → зовётся из Mouse.ClickOnCard, мимо этого gate
```

Правило — в чистом `BattleAnswer.Decide(roomDecides, isHost, applyingTheHostsOrder)` (покрыт тестами):
хосту можно и комнату надо предупредить, сидящему клиенту нельзя, набиравшему IP — как в одиночной игре.
Третий аргумент нужен ровно для одного: клиент исполняет приказ хоста **тем же** нативным методом, и его
собственный гейт не имеет права отказать ему же. Приказ хоста принимает только кадр, пришедший от хоста
(`RosterEntry.IsHost`), чужой `BattleStart` печатает отказ и ничего не делает.

Что обязан напечатать прогон (иначе Stage 6 по этой части не считается проверенным):

- `[hook] InGameUI.OnStartBattleButtonClick is gated frame=…` — **уже LIVE** (запуск 11.11.2026,
  `26-11-11_4-18-55.log`, рядом `resolved 101/101`, `non-public=0`, `DISABLED FEATURES: none`). Если
  вместо неё `capability disabled: mp-level (...)` — Harmony не собрал патч, и клиент снова начнёт бой сам.
- Хост жмёт «в бой»: `the host starts the battle, the room is told frame=…`, у каждого клиента —
  `the host's battle start is played here: the game's own button ran frame=…`. Если вместо этого
  `the game gave no button to press`, значит `InGameUI.Instance` в этот момент пуст (`ApiBase.Present` не
  прошёл) и волны у клиента не пойдут.
- Клиент жмёт «в бой» сам: у него `the battle is not started here: the host starts the battle, your own
  ready is ignored frame=…`, и на хосте от этой кнопки не должно появиться **ни одной** строки. Волны у
  клиента обязаны остаться там, где были (`Board.theWave`).
- Чужой `BattleStart` (не от хоста): `peer 1 tried to start the battle: only the host says when the waves begin`.
- Одиночная игра: ни одной из этих строк, кнопка работает как раньше.
- Гипотеза про вторую дверь проверяется так: если клиент всё-таки начал бой, а строки отказа в его логе
  нет — старт пришёл через `FunctionCardType.StartBattle` (`Mouse.ClickOnCard`), и гейтировать надо и её.

## Экономика посадки: нативный путь карты после разрешения хоста (Stage 6; собран, живого прогона не было)

Замечание заказчика: «растения сажаются, но без лимитов на солнце и кулдаунов». Причина одна: до этого
среза сажал только `CreatePlant.SetPlant`, а он про карту ничего не знает. Исправление деньги не считает —
оно пускает ровно тот игровой путь, которым карта играет сама, и только после «да» хоста.

```csharp
// Mouse, dump.cs:68780 — приватный в дампе, публичный в прокси (живой запуск: non-public=0)
private Plant SetPlantFromCard(int column, int row, PlantType type);   // им же играет TryToSetPlantByCard
// CardUI, dump.cs:21227-21243 — цена и кулдаун, которые мод только читает
public int theSeedCost;   // 0x24
public float CD;          // 0x44 — остаток остывания
public float fullCD;      // 0x48
public bool onCardBank;   // 0x5A — «эта карта лежит в банке», фильтр в CardBank.Find
// InGameUI — банк этого газона, одним из двух известных путей
public List<CardUI> Cards { get; }   // публичное свойство; поле cards (HashSet) рядом
// Board — родное списание
public void UseSun(float theSun);  public int theSun;  public int maxSun;
```

Разделение полномочий в `WorldSync.Grant`/`ApplySpawn`: на том конце, где кликали, посадка идёт
`Plants.PlantFromCard` → то есть родным `Mouse.SetPlantFromCard`, и цена, кулдаун, звук и уровень растения
приходят от игры; на всех остальных концах — `Plants.Spawn`, потому что карты там не тратились. Если
игровой путь карты вернул пустоту, мод громко пишет это и сажает простым создателем: газон, обещанный
комнатой, не может остаться пустым из-за догадки.

Солнце хост не угадывает, а измеряет: `theSun` читается до и после создания, разница сверяется с ценой
карты (`PlantingRules.StillOwed` — чистый, покрыт тестами), недостающее добирается родным `Board.UseSun`.
Обе догадки дороже ошибки: «всегда доплачивать» = двойное списание, «никогда» = посадка за ноль.

Что обязан напечатать прогон:

- `place request from peer 1: type=8 cell=3,2 frame=…` — как и раньше.
- `plant #1 granted to peer 0 on cell 3,2, the card here reads cost 50, CD 0/75, roster: …` — числа
  взяты **с карты этого газона**. Если там `no card of type 8 is on this bank`, значит `InGameUI.Cards` в
  этом билде пуст либо `onCardBank` значит не то, и тогда экономику мод считает вслепую (он не откажет и
  не списшит лишнего — `UnknownCost`, см. строку ниже).
- `the pool went 100 -> 50 against a cost of 50: nothing left for the room to charge` — это и есть ответ на
  главный вопрос: карточный путь списил сам, доплата не нужна и её можно убирать.
- `the pool went 100 -> 100 against a cost of 50, so the room charges the remaining 50 and the pool is 50`
  — наоборот: нативный путь в этом кадре не списил ничего, доплата обязательна.
- `sun pushed as 50/5000` сразу после grant; у остальных концов `the host says the pool is 50/5000, this
  end had …`.
- Отказ по деньгам: `the pool holds 20, the card costs 50` на хосте + `the host refused my
  PlacePlantRequest: …` у клиента. **Такого отказа в самой игре для этого пути нет** — правило вводит мод,
  и это осознанное расширение, а не нативное поведение.
- types с неизвестной ценой: `nothing is charged for type 9: no card of type 9 is on this bank` — посадка
  разрешена, деньги не тронуты.
- Кулдаун проверяется глазами: карта должна посереть и остыть. Если после `PlantFromCard` она активна
  в том же кадре — метод кулдаун не делает, и тогда его придётся возвращать отдельно (`CardUI.CD`).
- Одиночная игра: ни одной из этих строк.

Гипотезы, которые прогон проверяет впервые: (1) `SetPlantFromCard` берёт клетку и тип **аргументами** и не
читает сам `theMouseColumn/theMouseRow` — иначе посадка придёт не туда; (2) он же списывает солнце и
запускает кулдаун, потому что это ровно тот метод, который игра зовёт изнутри `TryToSetPlantByCard`;
(3) `InGameUI.Cards` — тот же банк, что и поле `cards`, и `onCardBank` на карте означает «лежит в банке».

## Слияние растений: этот срез только слушает (Stage 6, диагностика)

Заказчик: «я не могу сливать растения (нужно ставить одно растение поверх другого)». Причина известна
ровно одна: хост отказывает по занятой клетке. Строить синхрон слияния вслепую нельзя — все три нативных
пути в дампе пусты, и какой из них делает слияние — неизвестно. Поэтому срез ограничивается диагностикой:
`MergeGate` ставит три log-only prefix, все возвращают `true`, ничего не блокируют; если патч не собрался —
только предупреждение, комната работает как раньше (эта проверка не владеет возможностью).

```csharp
// Mouse — три кандидата, все приватные в дампе, все с пустым телом
private void LeftClickWithSomeThing();                                            // dump.cs:68864, диспетчер клика «по чему-то»
private void ReinforcePlant();                                                    // dump.cs:68867, 0 параметров
private Plant[] TurnPlantsToNewPlants(CardUI card, PlantType newType, PlantType nextType); // dump.cs:68924
// рядом, ДРУГОЙ класс (ShootingData, dump.cs:90814): static List<Plant> с теми же именем и типами
// аргументов — то же имя метода не гарантирует тот же путь; смотреть надо на строки наблюдателя
```

Комната слияние **не** синхронизирует, но отказ по занятой клетке теперь печатает вердикт родного
`CheckBox`, потому что «игра считает клетку свободной, а мод отказывает сам» и «игра сама считает её
заблокированной» — это два разных следующих шага:

- `cell 3,2 holds plant #1 of type 8, asked type 8, the game's CheckBox calls the cell plantable frame=…`
  → игра пускает в эту клетку, а мод отказывает: значит слияние — отдельный нативный путь, и следующий
  срез обязан различать «то же растение поверх того же» и «занято чужим».
- тот же ряд со словами `calls the cell blocked` → мод отказывает правильно, и путь слияния лежит мимо
  `TryToSetPlantByCard`; искать его надо по строкам наблюдателя.

Строки наблюдателя (печатаются только пока комната решает — в одиночной игре их нет вовсе):

- `the click found something under the pointer while the room decides: cell 3,2 held type 8, plants under
  the pointer 1 frame=…`
- `the game is reinforcing a plant in place while the room decides: …`
- `the game is turning plants into a new plant while the room decides: …`

Что означает каждый ответ: `ReinforcePlant` — усиление на том же месте (одинаковое растение поверх
одинакового), синхронизируется одним событием «заменить растение»; `TurnPlantsToNewPlants` — «два разных
становятся третьим», тогда нужны два входа и массив на выходе, и это отдельный кадр; `LeftClickWithSomeThing`
без двух предыдущих — просто диспетчер, и тогда читать надо, что он зовёт внутри. Если не печатается ни
одна строка, а клик по занятой клетке в игре что-то делает — значит он отсекается ещё до `Mouse` (например
в UI), и точку наблюдения надо поднимать выше.

## Банк карт и старт боя

```csharp
public static InGameUI Instance;
public HashSet<CardUI> cards;
public bool AddCardToBank(CardUI card, bool quick = false);
public bool RemoveCardFromBank(CardUI card, bool quick = false);
public void OnStartBattleButtonClick();                 // публичный, = нажатие родной кнопки «в бой»
public GameObject StartBattle;
public static CardSlotManager ... ;  public void SetMaxCardCount(int count);
public const int MaxSlotCount = 14;  public const int DefaultMaxCardCount = 14;
// CardUI
public PlantType thePlantType; public int theSeedCost; public bool onCardBank; public List<Sprite> sprites;
```

## Навигация/загрузка уровня — ПУБЛИЧНЫЙ статический `UIMgr` (ключевая находка Stage 0b)

```csharp
public static void EnterGame(LevelType levelType, int levelNumber, int id = -1, string name);
public static void EnterTravelGame(LevelType levelType, int levelNumber);
public static SceneType GetSceneType(LevelType theLevelType, int theLevelNumber);
public static void EnterMainMenu();  public static void BackToMenu();
public static void EnterPauseMenu(); public static void BackToGame();
public static void EnterLoseMenu(string reason = "");
// + EnterIZGame/EnterChallengeMenu/EnterAlmanac/EnterGarden/... (все static public)
```

## Состояние приложения и сервисы переиспользования

```csharp
public static GameAPP Instance;
public static Transform canvas; public static Transform canvasUp;   // родные родители для UI мода
public static string playerName;                                     // нативный ник: есть, public static
public static int theBoardLevel; public static LevelType theBoardType;
public static GameStatus theGameStatus;                              // гейт хуков по состоянию игры
public static ResourcesManager resourcesManager;
public static void PlaySound(SoundType soundType, float theVolume = 0.5f, float pitch = 1f);
public static GameObject[] itemPrefab; public static Sprite[] spritePrefab;
public static long theMoneyCount; public static string version;

// ResourcesManager — источник спраутов/префабов БЕЗ создания сущности
public readonly Dictionary<PlantType, GameObject> plantPrefabs;
public readonly Dictionary<PlantType, GameObject> plantPreviews;      // готовые превью растения
public readonly Dictionary<ZombieType, GameObject> zombiePrefabs;
public readonly Dictionary<BulletType, GameObject> bulletPrefabs;
public readonly Dictionary<SceneType, GameObject> backgroundPrefabs;
public readonly List<PlantType> allPlants; public readonly List<ZombieType> allZombieTypes;

// GameMassage — нативная шина событий игры (кандидат вместо лишних Harmony-патчей)
public static GameMassage Instance { get; }
public void AddEventListener(string name, UnityAction action);
public void EventTrigger(string name);

// enum GameStatus: OpenOptions=-2, OutGame=-1, InGame=0, Pause=1, InInterlude=2,
//                  Selecting=3, Almanac=4, Car=5, Wating=6, BigGarden=7
// enum LevelType : Nothing=-1, Advanture=0, Challenge=1, IZ=2, Survival=3, Explore=4,
//                  TravelAdvanture=5, SkinLevel=6, AbyssRealm=7, NewAdvanture=8,
//                  TowerLevel=9, StarAdvanture=10, CustomLevel=11
```

## Банк карт: кто чем владеет (дополняет Stage 5)

```csharp
public static InitBoard Instance;               // ПУБЛИЧНЫЙ статический сервис инициализации поля
public bool ready;                              // нативный флаг готовности борда = гейт состояний сессии
public UniTask StartInit();                     // ВНИМАНИЕ: UniTask, см. оговорку ниже
public UniTask ShowSeedBank();
public UniTask ShowBottom(bool quick = false);  public UniTask HideBottom(bool quick = false);
public CardUI CreateCard(PlantType theSeedType, bool shadow = true, bool quick = false);
public CardUI CreateCard(FunctionCardType functionCardType, bool quick = false);   // совок/инструменты
public CardUI PreSelect(PlantType theSeedType);
public void InitMower();  public void LookLawn();
// доступ к слот-менеджеру — через публичное свойство, НЕ через поиск по сцене:
public CardSlotManager CardSlotManager { get; }      // на InGameUI.Instance
public IReadOnlyList<CardUI> Cards { get; }          // на InGameUI и на CardSlotManager
public bool IsFull { get; } public int ActiveCardCount { get; }
public int MaxCardCount { get; } public int TotalSlots { get; }
public void SetMaxCardCount(int count);
public static CardSlotManager EventCardOperations.ActiveManager();   // второй, статический путь
```

## Оговорка про UniTask (влияет на Stage 5/6, проверять на Stage 1)

Игра широко использует `Cysharp.Threading.Tasks.UniTask` (318 упоминаний в дампе, тип в билде есть).
Часть нативных методов, которые мы хотим звать, возвращают `UniTask` (`InitBoard.StartInit`,
`ShowSeedBank`, `CardSlotManager.MoveCardAsync`). Await-ить IL2CPP-ый UniTask из Mono-кода
через Il2CppInterop — отдельный источник падений, поэтому план по умолчанию: **вызвать без await
и определять готовность по нативным полям** (`InitBoard.Instance.ready`, `GameAPP.theGameStatus`,
`InGameUI.Instance.cards.Count`), а не по продолжению задачи. Проверить на Stage 1.

## Что по-прежнему НЕ verified

- Тела всех методов пусты (IL2CPP stubs): любой вывод «что делает метод» выше — это сигнатура, а не поведение.
- `BoardSpawner.SummonZombies` прочитан побайтово (E18): зовёт `GetRandomRow`,
  `AdjustZombieForTerrain`, `AdjustRowForSpecialZombies` и `CreateZombie.SetZombie`; её единственный
  игровой вызывающий — `Board.NewZombieUpdate`. Гипотеза «обходит пул» снята.
- Не найден нативный world-space текст для ника над клеткой: кандидаты `DamageHUD`, `GameMassage`-события, `Create*Preview(parent,...)` с Transform-родителем — проверка на Stage 1.
- `CreatePlantPreview` прочитан (E19): `Instantiate` + `Lawnf.GetSprites` + `SpriteRenderer.set_color`
  + `set_parentInternal`; тело не добавляет ни коллайдера, ни игрового компонента. Родитель задаётся
  вызывающим, поэтому призрак мода висит на доске и снимается её смертью; удаляет его мод сам
  (`Object.Destroy`, `Previews.Destroy`). Насколько призрак прозрачен — решает переданный `Color`.
- Stage 6, четыре вещи, которые не подтверждены ничем, кроме компиляции и установки патча: (1) списывает ли
  `Mouse.SetPlantFromCard` солнце и запускает ли кулдаун сам (меряется строкой `the pool went …`);
  (2) непустой ли `InGameUI.Instance.Cards` в бою и тот ли это банк, что и поле `cards`; (3) начинает ли
  приказ хоста бой у клиента и не проходит ли старт мимо гейта через `FunctionCardType.StartBattle`;
  (4) какой из трёх путей `Mouse` делает слияние — наблюдатель ещё ни разу не срабатывал в комнате.
- Прокси-ассембли: форма членов (поле⇒свойство, private⇒public, типы) подтверждена живым запуском — см.
  «Живая верификация» выше. Из нативного кода модом исполнены только чтения из `NativeSmoke`
  (`GameAPP.theGameStatus` = `-1`, `GameAPP.playerName` = `'Player'`, пути «газона ещё нет» во всех
  остальных). Не подтверждено живым запуском по-прежнему: любой спавн, любое изменение состояния,
  любое чтение в бою. Тела в дампе пусты, значит вывод «что делает метод» и «в каком порядке его
  звать» остаётся гипотезой до строк в MelonLoader log.
- Stage 9, ещё не проверено в игре: (1) есть ли у `Shovel.Instance` `SpriteRenderer` (запасной путь —
  `Image` + материал растения) и как призрак лопаты выглядит; (2) рендерится ли мир-текст
  `TMPro.TextMeshPro` над доской и на нужном слое; (3) обновляет ли `Board.Update` `timeScale` из
  `GameAPP.config.gameSpeed` сразу после записи поля; (4) достаточно ли восьми множителей скорости,
  чтобы зомби не расходились между снимками позиции.
