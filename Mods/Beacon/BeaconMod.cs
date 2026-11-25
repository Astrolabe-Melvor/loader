using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Beacon;

/// <summary>Astrolabe Beacon: alerts for level ups, pets, new items, food, bank space and an idle character, on screen and on Discord.</summary>
public sealed class BeaconMod : IMod
{
    Watcher? _watcher;
    Toasts? _toasts;
    BeaconWindow? _window;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _watcher = new Watcher(ctx.Directory, ctx.Log);
        _toasts = new Toasts(ctx.Directory);
        _watcher.Raised += (a, r) => _toasts.Show(a, r, _watcher.Options, KitGame.Current);
        // Hold publishes these; Beacon delivers them like its own alerts.
        Messages.Subscribe("hold.bought", d => { if (d is string t) _watcher.FromMod(Kind.ShopBought, t); });
        Messages.Subscribe("hold.unlocked", d => { if (d is string t) _watcher.FromMod(Kind.ShopUnlocked, t); });
        Messages.Subscribe("deckhand.alert", d => { if (d is string t) _watcher.FromMod(Kind.Farming, t); });
        Messages.Subscribe("course.alert", d => { if (d is string t) _watcher.FromMod(Kind.Queue, t); });
        Messages.Subscribe("logbook.rare", d => { if (d is string t) _watcher.FromMod(Kind.RareDrop, t); });
        _entry = ctx.AddWindow("Beacon", Toggle, Key.F8);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        ctx.Frame += () => _watcher.Tick(KitGame.Current, Time.GetTicksMsec() / 1000.0);
        ctx.Log("Ready. F8 sets up alerts.");
    }

    void Toggle()
    {
        if (_window == null)
        {
            _window = new BeaconWindow(_watcher!, _toasts!);
            _window.SetKeyLabel(_entry!.KeyLabel);
        }
        _window.Toggle();
    }
}
