# PvzMP - co-op for Plants vs Zombies Fusion

A mod that lets you play Plants vs Zombies Fusion with a friend (up to four people).
Every player has a lawn of their own on screen, but the events are shared: a plant placed by
one player shows up for everyone, and the zombies, the sun, the level and the end of the game
are the same for the whole room.

The host's computer is in charge: its lawn is the room's truth. If the two lawns ever disagree,
what the host sees wins.

Mod version: **0.1** (early). Every player must run the **same** mod version, or the room will
not come together.

## What you need

- Windows 10 or 11, 64-bit.
- The game **Plants vs Zombies Fusion 4.1 (English, PC)** - the file `PlantsVsZombiesRH.exe`.
  Other game versions have not been tested: the mod may fail to start or behave unpredictably.
- The mod loader **MelonLoader 0.7.3 (Open-Beta)**, the **.NET 6 (net6)** variant.
  Not .NET Framework and not net35 - the game runs on net6.
- **Radmin VPN** - a free program that connects two computers over the internet as if they were
  on the same home network. Every player needs it.
- One mod file, `PvzMP.dll` (it goes into the game folder, in the `Mods` subfolder).

## Install (on every computer)

1. Install the game and run it once to make sure it works without the mod.
2. Close the game.
3. Install MelonLoader 0.7.3:
   - download the MelonLoader 0.7.3 installer from its release page:
     <https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3>;
   - run it and point it at the game folder (the one with `PlantsVsZombiesRH.exe`);
   - pick the **.NET 6** variant and install.
4. Run the game once and close it. A `MelonLoader` folder appears in the game folder, and inside
   it a `Mods` folder.
5. Copy `PvzMP.dll` into the `Mods` folder.
   If there is no `Mods` folder, create one next to `MelonLoader`.
6. Start the game. A **COOP** button should appear in the upper right of the main menu.
   If the button is missing, open `MelonLoader\Logs\` (the newest `.log` file) and check that
   MelonLoader is 0.7.3/net6 and that `PvzMP.dll` really sits in `Mods`.

## How to connect over Radmin VPN

Radmin VPN gives every computer an address of the form `26.x.x.x` on a shared virtual network.
Those are the addresses used to connect - no real IP addresses and no router settings needed.

1. Both players install Radmin VPN: <https://www.radmin-vpn.com/> (the free version).
2. One player creates a network: **Create network**, picks a name and a password, and tells the
   other player both.
3. The other player joins that network: **Join network**, and enters the same name and password.
4. In the Radmin VPN window each player sees the other's address - `26.x.x.x`.
   That is the address to type into the game.
5. If Windows asks whether the game may use the network, allow it (the network should be
   "private"). The mod opens one TCP port, **7777** by default.

## How to start a game

1. The host starts the game first.
2. In the main menu the host clicks **COOP -> HOST**, types a nickname and confirms.
   The port stays at 7777 by default; there is no need to change it.
3. The client starts the game and clicks **COOP -> CLIENT**, types the host's Radmin VPN address
   (for example, `26.225.125.120`), the same port 7777, and a nickname.
4. Once everyone is connected, the host picks a level as usual. The client loads the same level.
5. On the seed chooser screen:
   - the player list is in the upper right, with a **ready / not ready** button under it.
     A client clicks it (or presses the game's **Let's Rock**) to say "ready"; clicking again
     takes the word back;
   - the host presses **Let's Rock** when nobody is still waiting.
6. Each player picks plant cards on their own screen. A plant can be held by one player at a
   time: on the other screens that card is dimmed and carries the holder's name.
7. When the level is left and the menu comes back, the ready button disappears with the level.

Hotkeys (if the buttons are out of reach):

- `F1` - create a room (host), `F2` - join a room (client);
- `F3` - leave the room;
- `F4` - sweep leftover objects off the lawn;
- `Escape` - close the connection dialog.

## What is synchronized

- The room, the player list, nicknames and readiness.
- Level start, pause, slow time and the end of the game (the host decides).
- Seed draft, plant placement and layout, the glove and the cart, plant fusions, fertilizer,
  the hammer, the shovel, graves and trophies.
- Plant health and facts: the host sends its layout once a second and corrects the client.
- A plant dies on the host's word.
- Zombies (including hypnotized ones), effects, sounds, and one shared sun bank.

## Known limitations in 0.1

- Plant status animations (bite, glow, sleep) stay local on purpose: they are one playing
  animation, and copying it would freeze or double the picture.
- Projectiles and sun created by a plant live on their own lawn; the shared sun bank converges
  to the host's number.
- A plant that runs out of health on a client's lawn stays up until the host's layout removes it
  or heals it back. That is the chosen "the host's word" model, not a lost message.

## If something does not work

- Check that everyone has the same `PvzMP.dll` (0.1) and the same MelonLoader 0.7.3/net6.
- Check that Radmin VPN is connected on both sides and the address has no typos.
- Allow the game through the Windows firewall (private networks) and open port 7777.
- The logs live in `MelonLoader\Logs\`. They carry `[mp]`, `[net]` and `[ui]` lines that show
  what the room and the mod are doing.

## For developers

```
dotnet build -c Release
dotnet test tests/PvzMP.NetTests/PvzMP.NetTests.csproj
```

`GameDir` in `PvzMP.csproj` points at a local game install; the game's DLLs are referenced for
the build only and are never redistributed with the mod.

`ref/` holds the measured game facts (`EVIDENCE.md`, each entry with a `Repro:` command) and the
reader tools in `tools/re/`. Decompiled game sources are not part of this repository.

## License

No license is granted yet. The repository is public for reading and testing; all rights reserved.
