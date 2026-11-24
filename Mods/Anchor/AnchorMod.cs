using Godot;
using MelvorModLoader;

namespace Astrolabe.Anchor;

/// <summary>Astrolabe Anchor: save backups on a timer and at launch, with notes, and restores through the game's own save code.</summary>
public sealed class AnchorMod : IMod
{
    Vault? _vault;
    AnchorWindow? _window;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _vault = new Vault(ctx.Directory, ctx.Log);
        _entry = ctx.AddWindow("Anchor", Toggle, Key.F4);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        ctx.Frame += () =>
        {
            try { _vault.Tick(Time.GetTicksMsec() / 1000.0); }
            catch (System.Exception e) { ctx.Log("Backup check failed: " + e.Message); }
        };
        ctx.Log("Ready. F4 opens Anchor.");
    }

    void Toggle()
    {
        if (_window == null)
        {
            _window = new AnchorWindow(_vault!);
            _window.SetKeyLabel(_entry!.KeyLabel);
        }
        _window.Toggle();
    }
}
