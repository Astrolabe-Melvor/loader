using System;
using System.Collections.Generic;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Views;
using GamesByMalcs.Melvor2.Localization;
using Godot;

namespace Astrolabe.Course;

/// <summary>
/// Listens to the game's own tick, so the queue can move on while the game catches up on time away (then the game
/// runs its ticks on a worker thread and no frames come, so the loader's Frame event is silent). It joins the
/// game's list of ticking entities, after the real ones; it is not a real entity and has nothing to show.
/// </summary>
sealed class OfflineTicker : ITickingEntity
{
    readonly Game _game;
    readonly Queue _queue;
    readonly Action<string> _log;
    bool _failed;

    OfflineTicker(Game game, Queue queue, Action<string> log) { _game = game; _queue = queue; _log = log; }

    /// <summary>Adds a ticker to this game (once). Only call while the game is not catching up on time away.</summary>
    public static void Register(Game game, Queue queue, Action<string> log)
    {
        try
        {
            if (typeof(Game).GetProperty("TickingEntities", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(game)
                is not List<ITickingEntity> list)
            {
                log("The queue can't run while the game is closed: this game version has no tick list to join.");
                return;
            }
            if (list.Exists(t => t is OfflineTicker)) return;
            list.Add(new OfflineTicker(game, queue, log));
        }
        catch (Exception e) { log("The queue can't run while the game is closed: " + e.Message); }
    }

    public void Tick()
    {
        if (_failed) return;
        try { _queue.GameTick(_game); }
        catch (Exception e) { _failed = true; _log("Stopped following game ticks: " + e.Message); }
    }

    // ITickingEntity asks for a whole entity; nothing reads these for a ticker.
    public string Id => "astrolabe:CourseTicker";
    public string LocalId => "CourseTicker";
    public DataNamespace Namespace => null!;
    public LocalizedString DisplayName => null!;
    public string IconPath => "";
    public Texture2D Icon => null!;
    public Texture2D Background => null!;
    public bool RequiresAttention => false;
    public event EventHandler<ValueChangedEventArgs<bool>>? RequiresAttentionChanged { add { } remove { } }
    public BaseActiveEntityView? GetActiveView() => null;
    public void SelectCurrentAction() { }
}
