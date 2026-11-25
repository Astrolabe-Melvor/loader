using System;
using Godot;
using MelvorModLoader;

namespace Astrolabe.Course;

/// <summary>Astrolabe Course: chains actions with goals so the game moves on by itself.</summary>
public sealed class CourseMod : IMod
{
    Queue? _queue;
    QueueWindow? _window;
    QueueHud? _hud;
    Inbox? _inbox;
    ModWindow? _entry;

    public void OnLoad(ModContext ctx)
    {
        Messages.Subscribe("course.plan", OnPlan);
        MoveOldData(ctx);
        _queue = new Queue(ctx.Directory, ctx.Log);
        _inbox = new Inbox(ctx.Directory, _queue, ctx.Log);
        _entry = ctx.AddWindow("Course", Toggle, Key.F6);
        _entry.Changed += () => _window?.SetKeyLabel(_entry.KeyLabel);

        _hud = new QueueHud(_queue);
        _hud.Clicked += () => { EnsureWindow(); _window!.Open(); };
        _queue.Alert += text => _hud.Alert(GameAccess.CurrentGame, text);

        ctx.Frame += () =>
        {
            double now = Time.GetTicksMsec() / 1000.0;
            var game = GameAccess.CurrentGame;
            _queue.Tick(game, now);
            _inbox.Tick(game, now);
            _window?.Tick();
            _hud.Update(game);
        };
        ctx.Log("Ready. F6 opens the queue.");
    }

    /// <summary>This mod was called Action Queue before 0.6; bring its queues, history and plan cache over once.</summary>
    static void MoveOldData(ModContext ctx)
    {
        try
        {
            string old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ctx.Directory.TrimEnd('\\', '/'))!, "ActionQueue");
            if (!System.IO.Directory.Exists(old)) return;
            foreach (string f in System.IO.Directory.GetFiles(old, "*", System.IO.SearchOption.AllDirectories))
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".dll" or ".pdb" or ".deps" || f.EndsWith(".deps.json")) continue;
                string to = System.IO.Path.Combine(ctx.Directory, System.IO.Path.GetRelativePath(old, f));
                if (System.IO.File.Exists(to)) continue;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                System.IO.File.Copy(f, to);
            }
            System.IO.Directory.Delete(old, true);
            ctx.Log("Moved queues and history over from the old Action Queue folder.");
        }
        catch (System.Exception e) { ctx.Log("Could not move the old Action Queue data: " + e.Message); }
    }

    /// <summary>Other mods ask Course to plan an item: Messages.Publish("course.plan", ("item id", amount)).</summary>
    void OnPlan(object? data)
    {
        if (data is not ValueTuple<string, int> (string item, int amount)) return;
        EnsureWindow();
        _window!.PlanFor(item, amount);
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
