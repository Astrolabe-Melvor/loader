using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Deckhand;

/// <summary>Astrolabe Deckhand: pets and repairs in Ranching, keeps the grain mill stocked and running, and passes farming notices to Beacon.</summary>
public sealed class DeckhandMod : IMod
{
    Hand? _hand;
    DeckhandWindow? _window;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _hand = new Hand(ctx.Directory, ctx.Log);
        _entry = ctx.AddWindow("Deckhand", Toggle, Key.F3);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        ctx.Frame += () => _hand.Tick(KitGame.Current, Time.GetTicksMsec() / 1000.0);
        ctx.Log("Ready. F3 opens Deckhand.");
    }

    void Toggle()
    {
        if (_window == null)
        {
            _window = new DeckhandWindow(_hand!);
            _window.SetKeyLabel(_entry!.KeyLabel);
        }
        _window.Toggle();
    }
}
