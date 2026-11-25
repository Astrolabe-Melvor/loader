using Godot;
using MelvorModLoader;

namespace Astrolabe.Spyglass;

/// <summary>Astrolabe Spyglass: look up any item's sources, uses and unlocks in game, and plan it in Course.</summary>
public sealed class SpyglassMod : IMod
{
    SpyglassWindow? _window;
    ModWindow? _entry;
    string _folder = "";
    System.Action<string> _log = _ => { };

    public void OnLoad(ModContext ctx)
    {
        _folder = ctx.Directory;
        _log = ctx.Log;
        _entry = ctx.AddWindow("Spyglass", Toggle, Key.F10);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        // Other mods can open an item here: Messages.Publish("spyglass.show", "item id").
        Messages.Subscribe("spyglass.show", d => { if (d is string id) { Ensure(); _window!.LookUp(id); } });
        Lookup.Get(_folder, _log);   // start the download early
        // The game updated: items may have changed, so fetch fresh data instead of waiting a day.
        Messages.Subscribe("loader.gameupdated", _ => Lookup.Refresh(_folder, _log));
        ctx.Frame += () => _window?.Tick();
        ctx.Log("Ready. F10 opens Spyglass.");
    }

    void Ensure()
    {
        if (_window != null) return;
        _window = new SpyglassWindow(_folder, _log);
        _window.SetKeyLabel(_entry!.KeyLabel);
    }

    void Toggle()
    {
        Ensure();
        _window!.Toggle();
    }
}
