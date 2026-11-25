# Astrolabe Loader

A mod loader for Melvor Idle 2, with a shared UI kit that draws mod windows using the game's own textures and fonts.

Tested on game version 0.11.7.

## Install (from source)

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Close the game, then run `Install.cmd`.

It builds the loader into the game folder, writes `override.cfg` next to `Melvor2.exe`, and builds every mod under `Mods\`. If the game is not at `F:\SteamLibrary\steamapps\common\Melvor Idle 2`, set `MI2_GAME_DIR` first.

Start the game. A green tag in the top-left confirms the loader, and **F1** opens the Mods menu.

`Uninstall.cmd` removes `override.cfg` and the loader folder. The game then runs untouched.

## The Mods menu (F1)

- Every mod's version and health: loaded, did not load, paused after repeated errors, or calling game code that a game update removed. Hover a status for the details.
- Every mod window with its key. Click a key to change it.
- **Updates**: once a day the loader reads the newest release on GitHub and lists the mods that have a newer version. Nothing is downloaded or changed; **Get updates** opens the release page. Untick **Check daily** to turn it off.
- **Report a bug**: puts together your versions, mods, errors and the end of the log (with your Windows user name taken out), then copies it, saves it to `mods\bug-reports\`, or opens a GitHub issue to paste it into.

After a game update the loader says so in its top-left tag, checks every mod for game calls that no longer exist, and tells mods that cache game data to refresh.

Every mod window has **Help** and a **Tour** in its title bar. The tour points at each part of the window in turn; until you've opened and closed a window once, its Tour button is green and says "Take the tour". Windows remember where you drag them and how big you make them. When a window is taller than the screen, its tables get shorter first, so you scroll the table, not the window.

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

Put the project in `Mods\Hello\Hello.csproj` and `Install.cmd` builds it into the game. `Mods\Directory.Build.props` sets up the references.

### API

| | |
|---|---|
| `ModContext` | `Name`, `Directory`, `Tree`, `Log()`, `Frame` event, `AddWindow(title, toggle, key)` |
| `GameAccess` | `CurrentGame`, `FindGameView()`, `FindNode<T>()`, `IsComputingOffline` |
| `Astrolabe.UI.Kit` | `Theme`, `Box()`, `Label()`, `Heading()`, `Btn()`, `Row()`, palette colours, game fonts |
| `Astrolabe.UI.KitWindow` | game-framed window with title ribbon, drag, `Body`, `HeaderExtras`, `Open/Close/Toggle`, `SetHelp(bbcode)`, `SetTour(steps)` |
| `Astrolabe.UI.TourStep` | `(title, text, target, before)`: one stop on a window's tour; `target` returns the control to outline |
| `Messages` | `Subscribe(topic, handler)`, `Publish(topic, data)`: mods talk without referencing each other |

Loader topics: `loader.gameupdated` (the game's new version, once at start after an update).

Theme variations: `Heading`, `Dim`, `ActionButton`, `Card`, `Ribbon`.

### Compatibility

The `MelvorModLoader` namespace types (`IMod`, `ModContext`, `GameAccess`, `ModWindow`, `KeyChord`) keep the same names and signatures as the other Melvor Idle 2 mod loader, so mods built for it load here. Only one loader can be installed at a time.

## License

MIT. See [LICENSE](LICENSE). Not affiliated with Games by Malcs or Jagex.
