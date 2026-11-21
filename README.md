# Astrolabe Loader

A mod loader for Melvor Idle 2, with a shared UI kit that draws mod windows using the game's own textures and fonts.

Tested on game version 0.11.6-alpha.1.

## Install (from source)

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Close the game, then run `Install.cmd`.

It builds the loader into the game folder, writes `override.cfg` next to `Melvor2.exe`, and builds every mod under `Mods\`. If the game is not at `F:\SteamLibrary\steamapps\common\Melvor Idle 2`, set `MI2_GAME_DIR` first.

Start the game. A green tag in the top-left confirms the loader, and **F1** opens the Mods menu.

`Uninstall.cmd` removes `override.cfg` and the loader folder. The game then runs untouched.

## How it works

Godot reads `override.cfg` before the project settings. Ours sets `dotnet/project/assembly_name` to `MelvorModLoader`, so the engine starts .NET from `data_MelvorModLoader_windows_x86_64` and calls our entry point instead of the game's. The loader boots the game exactly as Godot's own entry point would, then loads every DLL in `mods\` and `mods\<Mod>\` that has a public `IMod` class.

No game files are changed or shipped.

## Writing a mod

```csharp
using MelvorModLoader;
using Mi2Kit;

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
| `Mi2Kit.Kit` | `Theme`, `Box()`, `Label()`, `Heading()`, `Btn()`, `Row()`, palette colours, game fonts |
| `Mi2Kit.KitWindow` | game-framed window with title ribbon, drag, `Body`, `HeaderExtras`, `Open/Close/Toggle` |

Theme variations: `Heading`, `Dim`, `ActionButton`, `Card`, `Ribbon`.

### Compatibility

The `MelvorModLoader` namespace types (`IMod`, `ModContext`, `GameAccess`, `ModWindow`, `KeyChord`) keep the same names and signatures as the other Melvor Idle 2 mod loader, so mods built for it load here. Only one loader can be installed at a time.

## License

MIT. See [LICENSE](LICENSE). Not affiliated with Games by Malcs or Jagex.
