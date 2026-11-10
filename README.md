# PvzMP

Co-op multiplayer for **Plants vs Zombies Fusion** (IL2CPP, `LanPiaoPiao/PlantsVsZombiesRH`).
Every player has a lawn of their own; the host's lawn is the room's truth for placement, zombies,
the level and the end of the game.

Status: **0.1** (early). Every end must run the same DLL.

## Requirements

- Windows, the game installed
- MelonLoader 0.7.3 (net6)
- Same mod version on every end: the handshake refuses a different one

## Install

1. Install MelonLoader 0.7.3 for the game.
2. Copy `PvzMP.dll` into the game's `Mods` folder.
3. Start the game. Logs are written to `MelonLoader/Logs/`.

## Play

- **Host**: in the main menu press `COOP` and then `HOST` (or press `F1` before a level starts),
  type a nickname, confirm. The dialog shows the port, 7777 by default.
- **Client**: press `COOP` and then `CLIENT` (or `F2`), type the host's address
  (a LAN address such as `192.168.1.10`) and the same port, confirm.
- Up to four seats.
- `Escape` closes the dialog, `F3` leaves the room, `F4` sweeps leftover objects off the local lawn.
- In the seed chooser the bottom line says `ready` or `not ready`. The host's `Let's Rock` starts
  the level only when no seat is still waiting, and a peer can press it again to take the word back.
- Each player picks cards on their own screen. A plant belongs to one seat at a time; the same card
  on the others is dimmed and carries the holder's name.
- The slow-mode button is shared: a press on any lawn toggles the room.

## What is synchronized

- Session, roster, nicknames, readiness
- Level start, pause, slow time, and the end of the game (the host decides)
- Seed draft, plant placement and layout, glove and cart moves, fusion, fertilizer, hammer, shovel,
  graves, trophies
- Plant facts: health and shield through the game's own heal and damage doors, level through its own
  upgrade, stage and skin, and the counters and switches its behaviour reads; the host's layout
  corrects a peer every second
- A room plant dies on the host's word: a peer holds the death until the host's layout stops naming
  the plant
- Zombies (the host's list, including which are hypnotized), effects and sounds, the sun bank

## Known limitations (0.1)

- Plant status animations (bite, glow, sleep) and their countdowns stay local on purpose: they are
  one playing animation, and copying them would freeze or duplicate it on the other lawn.
- Objects a plant creates (sun, projectiles) follow the local lawn; the sun bank itself is shared,
  so the totals converge.
- Stacked plants travel as separate layout entries; the overlay (pot, pumpkin, ladder) still looks
  as each lawn's own game draws it.
- A plant a peer's own game brings to zero stays up until the host's layout removes it or heals it
  back. That is the chosen model - the host's word - not a lost frame.

## Network

The room is one TCP port (7777 by default) opened by the host. A firewall or NAT must allow it;
clients need the host's reachable address. Nicknames and room state are sent only to the seats that
joined. The mod stores nothing outside the MelonLoader logs.

## Build

```
dotnet build -c Release
```

`GameDir` in `PvzMP.csproj` points at a local game install; the game's DLLs are referenced, never
redistributed.

Tests compile the mod's own protocol and session sources and run on loopback sockets:

```
dotnet test tests/PvzMP.NetTests
```

## Research notes

`ref/` holds the measured game facts (`EVIDENCE.md` entries, each with a `Repro:` command) and the
reader tools in `tools/re/`. Decompiled game sources are not part of this repository.

## License

No license is granted yet. The repository is public for reading and testing; all rights reserved.
