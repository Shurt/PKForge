<p align="center"><img src="docs/images/logo-transparent.png" alt="PKForge" width="140" /></p>

<p align="center"><b>A Pokémon save editor and bank for Android, support dual-screen handhelds.</b></p>

<p align="center">
  <a href="https://discord.gg/bMtzZmTDfu"><img src="https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?logo=discord&logoColor=white" alt="Discord" /></a>
  <a href="https://github.com/Shurt/PKForge/releases"><img src="https://img.shields.io/badge/Download-APK-2B4E95" alt="Download" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue" alt="License" /></a>
</p>

---


A Pokémon save editor and cross-generation Bank for Android, tuned for dual-screen
handhelds like the AYN Thor.
Built on [PKHeX.Core](https://github.com/kwsch/PKHeX) with the
[Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins) compiled in-process, so
legalizing a Pokémon works fully offline, on device.

Not affiliated with Nintendo, Game Freak, or The Pokémon Company.

## Features

- **Manage your Pokémon.** Open any save from a linked emulator or a single file and
  take full control of your team and boxes. Edit stats, IVs, EVs, moves, nature,
  ability, held item, Met/Origin, Tera type, Hyper Training, shininess, friendship,
  Pokérus, and more, with a live legality check as you go.
- **One-tap legalize.** The Auto Legality Mod is compiled in-process, so legalizing a
  Pokémon works fully offline, on device.
- **Move Pokémon between games.** Grab a Pokémon from one save and drop it into another,
  across generations, from a Gen 3 cartridge to a Gen 9 one. PKForge handles the
  transfer smoothly, so your team can travel with you from game to game.
- **A Bank like Pokémon Home.** Store your whole collection in a cross-game Bank with
  unlimited themed boxes. Organize with the gamepad, keep living dexes, and pull
  anything back out into any save whenever you need it.
- **Track your collection.** The Living Dex tracker follows all 1025 Pokémon across
  every generation, including shiny variants, so you always know what you still need.
  Select a species and choose **Find copies** to see its current save or Bank box and
  slot, including party positions. Results follow the tracker's form and shiny view;
  the normal view includes both normal and shiny copies. The finder rescans the local
  Bank, open session and visible shelf saves, and reports unreadable saves. Select a
  result for its trainer/file details. It is read-only and does not switch saves.
- **Browse and inject events.** The full Mystery Gift database is bundled and works
  offline. Browse Wonder Cards from old distributions and inject them straight into
  your save.
- **Edit the bag.** The bag editor handles any pouch, any item, any quantity, with
  presets and one-tap refills.
- **Edit the trainer.** Change your name, IDs, money, and gender on the trainer card,
  and manage trainer profiles and records.
- **RNG tools.** Inspect PID, IVs, and nature, and reroll a nature while keeping
  shininess.
- **Batch edit.** The organizer handles multi-select operations: copy, move, duplicate,
  delete, release, and send to the Bank or Poképark in bulk.
- **Breed and hatch.** The egg factory and Day Care / Nursery tools generate eggs and
  hatch Pokémon, including one egg of every species.
- **Complete the Pokédex.** Mark everything seen, generate a living dex, and track
  what is missing.
- **Play with your Pokémon.** The Poképark is a living habitat where your Pokémon
  wander while you're away. The Nuzlocke report tracks first encounters and dupes per
  route, and the encounter browser shows every way to catch a species in each game.
- **Share your teams.** Import and export Showdown sets, and generate QR codes to move
  a set between devices.
- **Game-specific editors.** Grand Underground, Poké Beans, Fashion, and more, tailored
  to each game.
- **Keep restore points.** Before each changed save is written, PKForge checks that the
  file still matches the open session and keeps a backup. It verifies the bytes after
  writing and retains 20 restore points per save. Close your emulator before editing:
  Android document providers cannot guarantee atomic replacement or lock out another
  app. Keep independent copies of important saves and export your Bank regularly.

## Send a save to a 3DS

Digital Pokémon X/Y saves can be sent to Checkpoint 5.2.0 over the local network.
In **Settings > 3DS connection**, save the IP address shown by Checkpoint. You can
also enter or change it during a transfer; PKForge remembers it across restarts.

1. Close the game on the 3DS and use Checkpoint to back up its current save.
2. Open Checkpoint's **Receive** screen for the matching game.
3. Open the save in PKForge and choose **Save data > Send to 3DS**.
4. Choose **Send backup** and enter the four-digit PIN shown by Checkpoint.
5. After Checkpoint confirms receipt, select the new `PKForge` backup on the 3DS
   and choose **Restore**. Keep your original backup.

Each upload gets a new backup name. PKForge does not restore the save automatically
or modify the local source file during this transfer. The PIN is not remembered.
If a transfer is interrupted, check Checkpoint's backup list before retrying;
receipt may be uncertain. Other games and receiving saves into PKForge are not
supported by this first version.

## Screenshots

<p align="center">
  <img src="docs/screenshots/home.png" alt="Home" width="49%" />
  <img src="docs/screenshots/editor.png" alt="Editor" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/bank.png" alt="Bank" width="49%" />
  <img src="docs/screenshots/living-dex.png" alt="Living Dex" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/mystery-gift.png" alt="Mystery Gift" width="49%" />
  <img src="docs/screenshots/bag.png" alt="Bag" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/rng.png" alt="RNG tools" width="49%" />
  <img src="docs/screenshots/pokepark.png" alt="Poképark" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/trainer-card.png" alt="Trainer card" width="49%" />
  <img src="docs/screenshots/showdown-qr.png" alt="Showdown QR" width="49%" />
</p>

<p align="center">
  <img src="docs/screenshots/nuzlocke.png" alt="Nuzlocke report" width="49%" />
  <img src="docs/screenshots/encounters.png" alt="Encounter browser" width="49%" />
</p>

## Optional pokedex connection

Open **Settings → Pokedex connection**, enable it, and choose **Sync now**. The default
server is `https://pokedex.thren.dev/`, reachable over Kyle's LAN or Tailscale. A custom
HTTPS URL can be configured. The connection is disabled by default and makes no requests
until you sync; save editing and the Bank work without it.

Sync sends collection metadata from the local Bank, open session and detected shelf saves (including hidden saves),
including forms, shininess and storage positions. Save files and Pokémon binaries stay on
the device. The server displays these observations separately from manually confirmed HOME
ownership. Neither HOME checkboxes nor acquisition/transfer confirmations are changed.

The cached HOME checklist, entry notes and transfer plan remain readable when offline.
Failed or cancelled uploads keep their exact pending snapshot for **Retry pending sync**,
including across app restarts. After retrying, sync once more for a fresh scan. Requests
time out after 15 seconds and do not retry in the background. Source scans can be cancelled.
Unreadable or omitted sources retain their previous server observations; complete sources
replace their prior snapshot, including removing copies no longer present. Open-session
observations may include unsaved edits. Eggs are excluded from recognized entities.

The server requires the companion integration from
[pokedex issue #24](https://github.com/Home-Lab-Org/pokedex/issues/24). Deploy that backend
and frontend before enabling the connection. An older/unreachable server leaves the local
cache and pending snapshot intact. The personal LAN/Tailnet deployment requires no login.
Do not expose the service publicly without adding access control.

Mapping v1 identifies supported forms conservatively. Unrecognized forms, uncertain legacy
Bank formats and unsupported save layouts appear as unmatched observations rather than
being counted as a different form. Changing the server URL or resetting the local connection
starts a new device identity and clears its local cache/pending request; retained remote
observations keep their timestamps. Check the server's **PKForge** tab for incomplete scans
and the last observation time of each source.

## Supported games

PKForge reads and edits saves from every mainline generation, plus the GameCube side
games and popular romhacks:

- **Generation I:** Red, Blue, Green (JP), Yellow
- **Generation II:** Gold, Silver, Crystal
- **Generation III:** Ruby, Sapphire, Emerald, FireRed, LeafGreen, Pokémon Box: Ruby & Sapphire, Colosseum, XD: Gale of Darkness
- **Generation IV:** Diamond, Pearl, Platinum, HeartGold, SoulSilver
- **Generation V:** Black, White, Black 2, White 2
- **Generation VI:** X, Y, Omega Ruby, Alpha Sapphire
- **Generation VII:** Sun, Moon, Ultra Sun, Ultra Moon, Let's Go Pikachu, Let's Go Eevee
- **Generation VIII:** Sword, Shield, Brilliant Diamond, Shining Pearl, Legends: Arceus
- **Generation IX:** Scarlet, Violet
- **Romhacks:** Pokémon Unbound, Radical Red, GS Chronicles, Luminescent Platinum, Pokémon Compass

## Supported emulators

Link a storage unit and PKForge finds your saves automatically:

- **Game Boy / Game Boy Color:** RetroArch, Linkboy, Pizza Boy C
- **Game Boy Advance:** RetroArch, Linkboy, Pizza Boy A
- **Nintendo DS:** melonDS, DraStic, RetroArch
- **GameCube:** Dolphin
- **Nintendo 3DS:** Azahar, Lime3DS, Citra MMJ
- **Nintendo Switch:** Eden

You can also open a single save file directly.

## Installation

This is Kyle's personal fork of [PKForge](https://github.com/sofianeelhor/PKForge).
Download the APK from [this fork's Releases](https://github.com/Shurt/PKForge/releases) and
allow installs from unknown sources. First run walks you through linking an emulator
(RetroArch, melonDS, Azahar/Lime3DS, Citra MMJ, Eden) or opening a single save file.

## 💬 Discord

Join for updates, support, bug reports, feature requests, or just to chat about the project.

👉 **[Join the PKForge Discord](https://discord.gg/bMtzZmTDfu)**

[![Discord](https://discordapp.com/api/guilds/1542192456018427926/widget.png?style=banner3&time-)](https://discord.gg/XkeD2vKJCZ)

## Building

.NET 10 SDK with the `maui-android` workload, Android SDK (API 36).

```bash
git submodule update --init --recursive
dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj
dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj
dotnet build src/PKForge.App/PKForge.App.csproj -f net10.0-android
```

Pull requests and pushes to `main` run both test suites and build a diagnostic APK.
These temporary CI APKs expire after three days and use disposable signing keys.
They are separate from the permanent personal app and are not an update channel.

### Personal releases and Obtainium

Releases are manual: after merging a change, open **Actions > Personal release >
Run workflow** and select `main`. Pushing a tag does not publish a release.
The workflow tests the code, signs one ARM64 Release APK, verifies its certificate,
and publishes it to this fork's GitHub Releases. It uses the exact commit selected
when the workflow starts.

Version names are `1.0.N`, where `N` is the release workflow's run number; Android
version codes are `1000 + N`. Failed runs can leave gaps. Start a new workflow run
for a new release; an existing release tag cannot be overwritten by rerunning it.
Keep this numbering scheme when changing the workflow so installed versions can
continue to update.

Configure these repository **Actions secrets** before the first release:

| Secret | Value |
| --- | --- |
| `PKFORGE_ANDROID_KEYSTORE_BASE64` | Base64-encoded personal PKCS12 keystore |
| `PKFORGE_ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `PKFORGE_ANDROID_KEY_ALIAS` | `pkforge` |
| `PKFORGE_ANDROID_KEY_PASSWORD` | Key password, the same as the keystore password |

Also set the repository **Actions variable** `PKFORGE_ANDROID_CERT_SHA256` to the
certificate's SHA256 fingerprint. Keep an independent backup of the keystore and
password; GitHub secrets cannot serve as a recoverable backup.

In Obtainium, add `https://github.com/Shurt/PKForge` as the app source. Each release
has one APK, so no architecture filter is needed. The permanent app is named
**PKForge Personal** and uses package ID `org.pkforge.shurt`. Its built-in updater
also checks this fork. It installs separately from upstream PKForge and the
previous diagnostic APK; export/import Bank data when moving to it. App-private
backups and settings do not transfer automatically. External save files are still
shared when you select the same files.

### Storage recovery and personal builds

Close the emulator before editing a save. If the file changes while it is open,
PKForge refuses the write and disconnects it; reopen the latest save to continue.
If a write fails or is interrupted, open **Restore points** from Home. New restore
points record their source file and can recover it even when it no longer opens.
Older restore points lack that identity and require an open, compatible save plus
an explicit warning before restoring.

Android's Storage Access Framework does not provide a portable atomic replacement
or a lock shared with emulators. Writes are backed up, flushed and read back, but a
process crash or simultaneous emulator write can still require manual recovery.
Keep independent save copies. Export the Bank before switching to an older or
unmodified build: the revised Bank index refers to versioned data files that older
builds do not understand. Archive imports honor the latest manifest; an interrupted
export must be rerun before it can be imported.

Personal APKs need the same permanent signing key for updates. Local builds using
the debug key cannot replace signed personal releases. `-p:DiagnosticBuild=true`
creates a separate test package and disables its built-in update check.

### Layout

```
src/PKForge.Domain          contracts and DTOs, no engine or Android dependencies
src/PKForge.Engine          adapters over pinned PKHeX.Core
src/PKForge.AutoMod         compiles the Auto Legality Mod against our Core
src/PKForge.Infrastructure  bank, backups, verified save writer
src/PKForge.App             MAUI app, SkiaSharp UI, gamepad and second screen
src/PKForge.Chrome          the design system: tokens and painters, pure Skia
tools/ChromePreview         renders the design system off-device
docs/                       architecture, bank model, development, art direction
```

## Credits

- [PKHeX](https://github.com/kwsch/PKHeX), the engine everything runs on
- [PKHeX-Plugins / Auto Legality Mod](https://github.com/santacrab2/PKHeX-Plugins), the
  offline legalizer compiled in-process
- [PKSM](https://github.com/FlagBrew/PKSM), the pixel chrome this UI builds on (GPL-3,
  see [UI attribution](src/PKForge.App/Resources/UI/ATTRIBUTION.md))
- [SteamGridDB](https://www.steamgriddb.com), cartridge icons, second-screen logos, and
  hero banners for the game library (community submissions; see
  [game art attribution](src/PKForge.App/Resources/GameArt/ATTRIBUTION.md))
- [PokeAPI](https://pokeapi.co), item art fetched at runtime and cached on device
- [game-icons.net](https://game-icons.net) (CC-BY 3.0, © Lorc, Delapouite, Guard13007,
  Carl Olsen and other contributing artists), UI symbols
- [Bulbagarden Archives](https://archives.bulbagarden.net), Pokérus status sprites
- Sprites and Pokémon names © Nintendo, Creatures Inc., GAME FREAK inc.

## License

GPLv3 or later, inherited from PKHeX.Core. See [LICENSE](LICENSE).
