# HKMP Sincronizado

A version of [HKMP](https://github.com/Extremelyd1/HKMP) 3.0.0 for [Sincroniza](https://github.com/vinicius-paraujo/sincroniza), a mod that balances and synchronizes multiplayer events in Hollow Knight.

If you don't use Sincroniza, use the [original HKMP](https://github.com/Extremelyd1/HKMP).

## Why it exists

Sincroniza runs on top of HKMP. Some problems we found while testing it can't be fixed from an addon, because they're inside HKMP:

- a player was disconnected after 5 seconds without packets;
- when the player simulating a room died or left, the boss could freeze for everyone else;
- an enemy killed by one player stayed dead for the whole group until the end of the save;
- a player entering a room could miss the initial state of its enemies.

Each change below says what broke, what we changed and how we tested it.

## Upstream

HKMP 3.0.0 is on the main branch of the original repository, but it was never released. The latest release is 2.4.3, and the repository hasn't had commits since October 2025. Sincroniza needs 3.0.0 working now, so we keep this version.

Bugs that also exist in the original HKMP are marked **upstream**. We plan to report them and offer the fixes to the HKMP author, so this fork gets smaller over time.

The fork changes when Sincroniza needs something that only HKMP can do. HKMP addons should keep working, but we haven't tested any yet.

## Base

The first commit of this repository is an exact copy of upstream commit [`f7c35d9`](https://github.com/Extremelyd1/HKMP/commit/f7c35d96f84c3d7b671fc4283795a07b4df97227) (HKMP 3.0.0). Every change since then is a separate commit that explains why it was made. The assembly keeps the version number 3.0.0, so the Sincroniza installer tells builds apart by their SHA-256.

## Changes

### Connection drop tolerance

**Without it.** A player whose connection stalled for five seconds was disconnected. Behind a tunnel or a busy Wi-Fi this happened several times per session. When the player who simulated a room (the *scene host*) dropped, every enemy in that room froze for everyone else.

**Changes.**

- A silent peer is disconnected after 30 seconds instead of 5; the DTLS handshake waits up to 15 seconds.
- Both sides report when the other side has been silent for 5 seconds and when it answers again. The client exposes these as events, so an addon can pause the game and reconnect from inside it.
- On the server, a silent scene host hands its rooms to another player in the room, preferring one who is still answering.
- **Upstream:** when a player disconnects, the scene host is transferred before the player is removed. Before, the removal came first, the transfer found no player and the room was left without a host.
- **Upstream:** logging in again with the same name and the same authentication key replaces the stale session instead of being refused as "name in use". A clear message explains when a name really is taken.
- **Upstream:** the chunk sender resets its slice index and acknowledgements at the start of each chunk. Before, a second large transfer in the same process overflowed the index and the reconnection stayed in `Connecting` forever.

**Verified** with two game copies: silence in both directions on the local network, a tunnel interrupted for 12 seconds followed by an automatic reconnection, and a silent scene host replaced by the other player.

### Scene host handover

When the scene host leaves a room, another player takes over the simulation of every entity in it. Several things went wrong in that moment:

- **Upstream:** an entity whose original object had already been destroyed threw an exception that stopped the handover, so the following entities, often the boss, never got a host. Each entity is now handed over on its own, and a missing object is skipped.
- **Upstream:** the same exception while becoming scene host on entering a room left later entities inactive. Hornet in Greenpath stayed invisible for the whole fight.
- The new host restores the entity's health from the last authoritative value, instead of the health its own copy had when the room loaded.
- The new host restores the position from the last position it received, or from the room's initial position when none was ever sent. Before, it used its replica's position, and a replica can fall while the real boss stands still during an intro.
- A body is made dynamic only when its collider is enabled. Before, the handover made a collider-less boss dynamic, and it fell through the floor.
- The active state of child objects, such as damage boxes, moves with the entity.
- A dead entity's state is kept on the server, so a player entering the room later finds it dead instead of alive.

**Verified** by unit tests for the position choice and the child state, and in game: the scene host dies during the Gruz Mother, Broken Vessel, Hornet and False Knight fights, and the other player finishes the fight.

### Shared world

These changes are mostly data, in `Resource/save-data.json`, which says for each save field whether it belongs to the server (shared) or to each player.

- 158 inventory fields are shared, such as abilities, charms, masks, vessels, notches, upgrades, keys and consumables. Seven fields HKMP did not sync are added, such as the vessel fragments and notches sold by Sly and the broken fragile charms.
- Area maps, placed markers and the NPC progress that changes the world are shared; the journal's "new entry" notices stay per player.
- **Upstream:** 832 room objects that reset when resting at a bench are marked semi-persistent, and the server can forget their state on request. Before, an enemy killed by anyone stayed dead for the whole group until the end of the save.
- The two-stop lifts of the Stag stations and Crystal Peak are synced entities, like the City lifts. Before, each player simulated their own lift, and whoever stayed behind saw the other player float.

### Thread safety

- **Upstream:** when a player enters a room, the server builds the "already in scene" packet with a copy of each entity's state-machine data, taken under a lock. Before, the packet referenced the live data, and the send thread could read it while the scene host's updates were being merged into it. The packet then failed with "Collection was modified", and the player entering the room got no initial state for that room's entities.
- The list of loggers is replaced rather than modified, so a logger can be added while network threads are logging.

**Verified** by unit tests that read thousands of copies while another thread keeps merging into the original.

### Logging

- `Hkmp.Logging.LogSinks.Add` lets an addon receive HKMP's own messages. Sincroniza uses it to keep one log file per game copy and per session; the Modding API log is shared by every copy installed on the same machine.
- Repetitive detail moved from the information level to the debug level: hooked state-machine actions, animations, components added to entities, collider changes and save-sync updates. A ten-minute two-player session went from about 1.5 MB to about 130 kB of log, without losing room, host or entity events.

## Known issues

- **Two scene hosts after a one-way drop.** The server moves the scene host role after 5 seconds of silence but only tells the new host. If the old host never noticed the drop, both players keep simulating the room. Other players then see enemies that don't die or move twice. This comes from the connection drop tolerance above. It has not been reproduced in game yet; a scenario to reproduce it is prepared.
- Compatibility with other HKMP addons has not been tested.

## Building

Requirements: the .NET SDK and a Hollow Knight 1.5 install with the [Modding API](https://github.com/hk-modding/api).

1. Copy `HKMP/LocalBuildProperties_example.props` to `HKMP/LocalBuildProperties.props` and point it at the game's `Managed` folder. That file is ignored by git, and the game's assemblies are never part of this repository.
2. Build in Release:

   ```
   dotnet build HKMP/HKMP.csproj -c Release
   ```

3. The output is `HKMP/bin/Release/net472/HKMP.dll`. Install it in place of HKMP in the game's `Mods/HKMP` folder.

## Tests

`HKMP.Tests` covers the parts of the fork that can run without the game: the handover position, the child state of entities and the copy of state-machine data across threads. It compiles those source files directly against small doubles of the Unity types, so it needs no game assemblies.

```
dotnet test HKMP.Tests
```

Behaviour that needs the game, such as handovers, reconnections and shared saves, is verified with the automated multiplayer scenarios of the Sincroniza development mod, which drive two or three game copies at once.

## License

HKMP is licensed under the [GNU Lesser General Public License v2.1](LICENSE), and so is this fork. All credit for HKMP goes to [Extremelyd1](https://github.com/Extremelyd1) and the HKMP contributors.

Hollow Knight is a trademark of Team Cherry. This project is not affiliated with or endorsed by Team Cherry.
