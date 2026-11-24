using System;
using System.Collections.Generic;

namespace MelvorModLoader;

/// <summary>
/// Lets mods talk to each other without referencing one another: one publishes a topic, others subscribe to it.
/// Handlers run on the game's main thread, right away, in the order they subscribed. A mod that is not installed
/// simply never answers. Example: Spyglass publishes "course.plan" with ("item id", amount); Course opens its planner.
/// </summary>
public static class Messages
{
    static readonly Dictionary<string, List<Action<object?>>> s_handlers = new();

    public static void Subscribe(string topic, Action<object?> handler)
    {
        if (!s_handlers.TryGetValue(topic, out var list)) s_handlers[topic] = list = new();
        list.Add(handler);
    }

    /// <summary>Sends a message. Returns true when at least one mod is listening.</summary>
    public static bool Publish(string topic, object? data = null)
    {
        if (!s_handlers.TryGetValue(topic, out var list) || list.Count == 0) return false;
        foreach (var h in list.ToArray())
        {
            try { h(data); }
            catch (Exception e) { Log.Write($"[{topic}] handler failed: {e.Message}"); }
        }
        return true;
    }

    public static bool HasListener(string topic) => s_handlers.TryGetValue(topic, out var list) && list.Count > 0;
}
