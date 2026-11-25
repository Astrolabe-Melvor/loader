using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Hold;

/// <summary>Astrolabe Hold: bank value by tab, keep-or-sell rules per item, and a log of what was sold.</summary>
public sealed class HoldMod : IMod
{
    Keeper? _keeper;
    HoldWindow? _window;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _keeper = new Keeper(ctx.Directory, ctx.Log);
        _entry = ctx.AddWindow("Hold", Toggle, Key.F9);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        ctx.Frame += () => _keeper.Tick(KitGame.Current, Time.GetTicksMsec() / 1000.0);
        // What each item is for (for the Cleanup tab); fetched fresh after a game update.
        Cleanup.Load(ctx.Directory, ctx.Log);
        Messages.Subscribe("loader.gameupdated", _ => Cleanup.Refresh(ctx.Directory, ctx.Log));
        ctx.Log("Ready. F9 opens Hold.");
    }

    void Toggle()
    {
        if (_window == null)
        {
            _window = new HoldWindow(_keeper!);
            _window.SetKeyLabel(_entry!.KeyLabel);
        }
        _window.Toggle();
    }
}
