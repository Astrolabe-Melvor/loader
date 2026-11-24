using Godot;
using MelvorModLoader;

namespace Astrolabe.Logbook;

/// <summary>Astrolabe Logbook: XP, items and currencies per hour this session, notable drops, past sessions.</summary>
public sealed class LogbookMod : IMod
{
    Tracker? _tracker;
    LogWindow? _window;
    LogPanel? _panel;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _tracker = new Tracker(ctx.Directory, ctx.Log);
        _entry = ctx.AddWindow("Logbook", Toggle, Key.F7);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);
        _panel = new LogPanel(_tracker);
        _panel.Clicked += () => { EnsureWindow(); _window!.Toggle(); };
        _tracker.Changed += () => _panel.Update();

        ctx.Frame += () =>
        {
            _tracker.Tick(GameAccess.CurrentGame, Time.GetTicksMsec() / 1000.0);
            _window?.Tick();
            _panel.Update();
        };
        ctx.Log("Ready. F7 opens the Logbook.");
    }

    void EnsureWindow()
    {
        if (_window != null) return;
        _window = new LogWindow(_tracker!);
        _window.SetKeyLabel(_entry!.KeyLabel);
    }

    void Toggle()
    {
        EnsureWindow();
        _window!.Toggle();
    }
}
