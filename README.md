# Astrolabe

Mods for Melvor Idle 2 that look like part of the game: queue your actions, see what your bank is worth and sell by rules, get alerts, look up any item, keep save backups. Unofficial; no game files are changed.

Tested on game version 0.11.7 (Steam, Windows).

## Download

**[Latest release](https://github.com/Astrolabe-Melvor/loader/releases/latest)**, [Nexus Mods](https://www.nexusmods.com/melvoridle2/mods/55), or the [Mods page on the Astrolabe wiki](https://astrolabe-melvor.github.io/#/mods). All three have the same files.

1. Close the game.
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract `AstrolabeAll-<version>.zip` there, so `override.cfg` sits next to `Melvor2.exe`. (Or the Loader zip plus only the mods you want.)
4. Start the game. A green Astrolabe tag shows top left, **F1** opens the Mods menu, and every mod window has Help and a Tour.

To update, close the game and extract the new zips over the old ones; your settings are kept. The Mods menu says when there is an update.
To remove, delete `override.cfg` and the folder `data_MelvorModLoader_windows_x86_64`. The `mods` folder can stay.

**Already using Melvor Mod Loader?** Keep it: extract only the mod zips you want (not the Loader zip). Astrolabe's mods run on it too; Astrolabe Loader adds the update check, Report a bug and mod health to the Mods menu.

| Mod | Key | What it does |
|---|---|---|
| Course | F6 | Queue actions one after another, each until a goal: a level, an amount, a time. Plans whole items, runs slayer tasks, puts on each step's gear. |
| Logbook | F7 | XP, items and gold this session, per hour, with rare drops and past sessions. |
| Beacon | F8 | A sound, a note, a taskbar flash or a Discord message when something needs you. |
| Hold | F9 | What your bank is worth, sell by rules, a cleanup list of what's safe to sell, and shop buying by rules. |
| Spyglass | F10 | Where any item comes from and what it is for; plan it in Course. |
| Anchor | F4 | Save backups on a timer, with notes; restore from inside the game. |
| Deckhand | F3 | Ranching petting and repairs, the grain mill kept running, farming warnings. |

Found a bug? F1 > **Report a bug** copies everything needed; paste it into [a new issue](https://github.com/Astrolabe-Melvor/loader/issues/new).

## The Mods menu (F1)

- Every mod's version and health: loaded, did not load, paused after repeated errors, or calling game code that a game update removed. Hover a status for the details.
- Every mod window with its key. Click a key to change it.
- **Updates**: once a day the loader reads the newest release on GitHub and lists the mods that have a newer version. Nothing is downloaded or changed; **Get updates** opens the release page. Untick **Check daily** to turn it off.
- **Report a bug**: puts together your versions, mods, errors and the end of the log (with your Windows user name taken out), then copies it, saves it to `mods\bug-reports\`, or opens a GitHub issue to paste it into.

After a game update the loader says so in its top-left tag, checks every mod for game calls that no longer exist, and tells mods that cache game data to refresh.

Every mod window has **Help** and a **Tour** in its title bar. The tour points at each part of the window in turn; until you've opened and closed a window once, its Tour button is green and says "Take the tour". Windows remember where you drag them and how big you make them. When a window is taller than the screen, its tables get shorter first, so you scroll the table, not the window.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Close the game, then run `Install.cmd`.

It builds the loader into the game folder, writes `override.cfg` next to `Melvor2.exe`, and builds every mod under `Mods\`. If the game is not at `F:\SteamLibrary\steamapps\common\Melvor Idle 2`, set `MI2_GAME_DIR` first.

Start the game. A green tag in the top-left confirms the loader, and **F1** opens the Mods menu.

`Uninstall.cmd` removes `override.cfg` and the loader folder. The game then runs untouched.

## Releases

`Package.cmd` builds ready-to-share zips into `dist\`: `AstrolabeLoader-<version>.zip`, one `Astrolabe<Mod>-<version>.zip` per mod, and `AstrolabeAll-<version>.zip` with everything. Each extracts straight into the game folder. `Package.cmd release` also uploads them to a GitHub Release of this repo (needs the [gh CLI](https://cli.github.com), logged in), with `RELEASE-NOTES.md` as the release text. The build needs the game installed, because mods compile against its DLLs. The update check reads the asset names, so keep the `Astrolabe<Mod>-<version>.zip` pattern.

## How it works

Godot reads `override.cfg` before the project settings. Ours sets `dotnet/project/assembly_name` to `MelvorModLoader`, so the engine starts .NET from `data_MelvorModLoader_windows_x86_64` and calls our entry point instead of the game's. The loader boots the game exactly as Godot's own entry point would, then loads every DLL in `mods\` and `mods\<Mod>\` that has a public `IMod` class.

No game files are changed or shipped.

## Writing a mod

```csharp
using MelvorModLoader;
using Astrolabe.UI;

public sealed class Hello : IMod
{
    KitWindow? _win;

    public void OnLoad(ModContext ctx)
    {
        ctx.AddWindow("Hello", () =>
        {
            _win ??= new KitWindow("Hello", "my first mod");
            _win.Body.AddChild(Kit.Label("It works."));
            _win.Toggle();
        }, Godot.Key.F9);
    }
}
```

Put the project in `Mods\Hello\Hello.csproj` and `Install.cmd` builds it into the game. `Mods\Directory.Build.props` sets up the references and compiles the UI kit (`Kit\*.cs`) into the mod.

### API

The loader API (`MelvorModLoader` namespace) is the same as falorfrozen's Melvor Mod Loader:

| | |
|---|---|
| `ModContext` | `Name`, `Directory`, `Tree`, `Log()`, `Frame` event, `AddWindow(title, toggle, key)` |
| `GameAccess` | `CurrentGame`, `FindGameView()`, `FindNode<T>()`, `IsComputingOffline` |
| `ModWindow` | `Title`, `Key`, `KeyLabel`, `Changed` |

The UI kit (`Astrolabe.UI`) is source code compiled into each mod, not part of the loader. So a mod built with it runs on Astrolabe Loader and on Melvor Mod Loader alike, and needs no other file. Copies of the kit in different mods share their state (messages, window settings, which window is on top) through named app-domain data, see `Kit\Shared.cs`.

| | |
|---|---|
| `Kit` | `Theme`, `Box()`, `Label()`, `Heading()`, `Btn()`, `Row()`, `Rows()`, `Icon()`, palette colours, game fonts |
| `KitWindow` | game-framed window with title ribbon, drag, `Body`, `HeaderExtras`, `Open/Close/Toggle`, `SetHelp(bbcode)`, `SetTour(steps)` |
| `TourStep` | `(title, text, target, before)`: one stop on a window's tour; `target` returns the control to outline |
| `Messages` | `Subscribe(topic, handler)`, `Publish(topic, data)`: mods talk without referencing each other |
| `KitGame` | `Current`: the open character's game, cached (cheap every frame on either loader) |

Astrolabe Loader topics: `loader.gameupdated` (the game's new version, once at start after an update).

Theme variations: `Heading`, `Dim`, `ActionButton`, `Card`, `Ribbon`.

### Compatibility

The `MelvorModLoader` namespace types (`IMod`, `ModContext`, `GameAccess`, `ModWindow`, `KeyChord`) keep the same names and signatures as falorfrozen's Melvor Mod Loader, so mods built for it load on Astrolabe Loader, and Astrolabe's mods load on Melvor Mod Loader. Only one loader can be installed at a time. On Melvor Mod Loader, the Astrolabe Loader extras (mod health in the Mods menu, update check, Report a bug, the game-update check) are not there; the mods themselves work the same.

## License

MIT. See [LICENSE](LICENSE). Not affiliated with Games by Malcs or Jagex.
