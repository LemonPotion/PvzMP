# Что подтверждено в эталонной реализации (pvz eco) — только архитектура, не код

Источник: `ref/eco-src/` — декомпиляция `D:\Games\PvzEco\...\Managed\Assembly-CSharp.dll`
(ilspycmd 8.2.0.7535, 453 файла, **реальные тела методов**). PvzEco — Mono-игра, другой проект:
его классов в PvzFusion нет, код отсюда не портируется.

## Транспорт и фрейминг (SocketServer.cs, 1112 строки)

- Фрейм: `[4-байтный length][byte type1][byte type2][UTF-8 JSON]`.
  Отправка: `list.InsertRange(0, BitConverter.GetBytes(list.Count))`, приём:
  `BitConverter.ToInt32(...)` + `Encoding.UTF8.GetString(buf, 2, len - 2)` — JSON идёт ПОСЛЕ двух байтов типа.
- Приём — отдельный `new Thread(Recevice).Start(socket)` на каждый сокет; отправка — `SendMsg(type1, type2, json)`
  с флагом `OutThis`, чтобы исключить автора рассылки.
- Heartbeat: `StartCoroutine(SendHeartbeat())`, интервал `0.5f` по `Time.realtimeSinceStartup`;
  проверка живости — окно `3f`, за его пределами флаг `Heartbeat=false` → разрыв.
  То есть ровно те числа, что названы в брифинге, подтверждены кодом.
- Сериализация — `JsonUtility.ToJson(bag)`, где bag — простой `[Serializable]` класс из папки `SocketSave` (48 файлов).

## Протокол: словарь сообщений эталона

`ConnectInfo`, `PlayerInfo`, `OnlinePlayerInfo`, `JoinTeamApply`, `JoinSpecApply` (spectator), `ReConnectInfo`,
`LoadLVBag` (загрузка уровня), `SelectCard`, `SelectPrepare`, `SlotMchBag`, `AddCardBag`, `UpdateCardCD`,
`PlantSpawn`, `ZombieSpawn`, `ZombieSpawnApply`, `SunSpawn`, `ClickedSun`, `SunNumBag`,
**`PlantPreview`, `ShovelPreview`**, `SynItem`, `SynGrid`, `SynMap`, `SynBooty`, `GameOver`,
`FlagMeterSyn`, `TimetableSyn`, `ToolApply`, `CommandBag`, `PrivateChatMsg`, `PvPModeSyn`, `PvPTeamList`, `SpectList`.

Канал намерений (то, что просит наш Stage 4b) в эталоне существует как отдельный тип сообщений:

```csharp
class PlantPreview  { string PlayerName; Vector2 GridPos; PlantType plantType; bool isImtor; }
class ShovelPreview { string PlayerName; Vector2 GridPos; bool isShow; }
class SunNumBag     { int sunNum; bool isSun; bool isBlTeam; }              // абсолютное значение солнца
class ZombieSpawn   { int OnlineId; string PlacePlayer; ZombieType Type; Vector2 SpawnPos; int UpdateLine; }
class ClickedSun    { int OnlineSunId; bool FlyPos; Vector2 pos; }
```

Подтверждаются три решения из брифинга: солнце — абсолютным числом; онлайновая идентичность —
целым `OnlineId`, который выдаёт хост (`SocketServer.Instance.ItemId` — auto-increment); сущность
несёт `PlacePlayer` (кто произвёл) отдельно от типа.

## Валидация входа в сессию (ветки в SocketServer)

Хост отказывает и шлёт текст причины: неверный пароль; `人数过多` — слишком много игроков;
**дубликат имени**; **«игра уже началась, отказ подключения»**; несовпадение набора команд;
**несовпадение версии** (`VersionCode`). Строка версии проверяется при коннекте — наш
`Hello{protocolVersion}` закрывает то же.

Наше расхождение с эталоном, зафиксированное пользователем: при дубликате ника eco отказывает,
мы добавляем суффикс `#2`.

## Чего в эталоне нет и что придётся решать самим

- Транспорт у eco — raw `Socket` с ручным буфером; никакого шифрования/сжатия. У нас так же (решение v1).
- КурSOR-поток: eco шлёт превью по смене клетки (полевой `GridPos`), частоту троттла в open-коде не подтверждал —
  свой троттл считаем от смены клетки и ставим верхнюю границу по времени.
- Реконсил HP: в `SocketSave` **нет ни одного поля здоровья** (проверено grep по всем 48 пакетам) — явного
  HP-пакета эталон не шлёт. Но есть `SynItem{ OnlineId, SynItemType Type, string AName, Vector2 Twofloat, int[] SynCode }`
  — универсальный дискретный патч состояния по `OnlineId`, где `SynCode` — четыре числа произвольного смысла.
  Наш HP-only реконсил 2–4 Hz — сознательное отступление от формы эталона, оговорённое брифингом как
  «decide and justify»; по духу он близок к `SynItem` (править состояние по id, а не стримить кадры).
