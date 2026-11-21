using Godot;
using MelvorModLoader;

namespace Astrolabe.ActionQueue;

/// <summary>Astrolabe Action Queue: chains actions with goals so the game moves on by itself.</summary>
public sealed class ActionQueueMod : IMod
{
    Queue? _queue;
    QueueWindow? _window;
    QueueHud? _hud;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        _queue = new Queue(ctx.Directory, ctx.Log);
        _entry = ctx.AddWindow("Action Queue", Toggle, Key.F6);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);

        _hud = new QueueHud(_queue);
        _hud.Clicked += () => { EnsureWindow(); _window!.Open(); };
        _queue.Alert += text => _hud.Alert(GameAccess.CurrentGame, text);

        ctx.Frame += () =>
        {
            double now = Time.GetTicksMsec() / 1000.0;
            var game = GameAccess.CurrentGame;
            _queue.Tick(game, now);
            _window?.Tick();
            _hud.Update(game);
        };
        ctx.Log("Ready. F6 opens the queue.");
    }

    void EnsureWindow()
    {
        if (_window != null) return;
        _window = new QueueWindow(_queue!);
        _window.SetKeyLabel(_entry!.KeyLabel);
    }

    void Toggle()
    {
        EnsureWindow();
        _window!.Toggle();
    }
}
