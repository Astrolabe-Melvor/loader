using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using GamesByMalcs.Melvor2.GameMenus;

namespace Astrolabe.UI;

// The kit is compiled into every Astrolabe mod (and into Astrolabe Loader), so a mod needs nothing but itself and a
// loader: Astrolabe Loader or Melvor Mod Loader. Each mod has its own copy of these classes, so anything the copies
// must agree on (messages between mods, window settings, which window is on top) lives in one place every copy can
// reach: the app domain's named data slots, holding only .NET and Godot types. Keys and types never change.

/// <summary>Values every copy of the kit shares, created by whichever copy asks first.</summary>
static class Shared
{
    public static T Get<T>(string key, Func<T> make) where T : class
    {
        AppDomain d = AppDomain.CurrentDomain;
        string slot = "astrolabe.kit/" + key;
        lock (d)
        {
            if (d.GetData(slot) is T found) return found;
            T made = make();
            d.SetData(slot, made);
            return made;
        }
    }
}

/// <summary>Writes to the loader's log (both loaders have MelvorModLoader.Log.Write).</summary>
static class KitLog
{
    public static void Write(string message)
    {
        try { MelvorModLoader.Log.Write(message); } catch { }
    }
}

/// <summary>Where things are on disk.</summary>
static class KitPaths
{
    static string? s_mods;

    /// <summary>The game's mods folder, next to Melvor2.exe.</summary>
    public static string Mods => s_mods ??= Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "mods");
}

/// <summary>
/// The open character's game, found once and remembered (cheap to call every frame on either loader).
/// </summary>
static class KitGame
{
    static GameView? s_view;
    static ulong s_lastSearch;
    static PropertyInfo? s_prop;

    /// <summary>The open character's game state, or null on the main menu.</summary>
    public static GamesByMalcs.Melvor2.Game? Current
    {
        get
        {
            GameView? view = View();
            return view == null ? null : Of(view);
        }
    }

    /// <summary>The game state a given game screen shows.</summary>
    public static GamesByMalcs.Melvor2.Game? Of(GameView view)
    {
        s_prop ??= typeof(GameView).GetProperty("Game", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return s_prop?.GetValue(view) as GamesByMalcs.Melvor2.Game;
    }

    /// <summary>The in-game screen, or null on the main menu.</summary>
    public static GameView? View()
    {
        if (s_view != null && GodotObject.IsInstanceValid(s_view) && s_view.IsInsideTree()) return s_view;
        s_view = null;
        // Searching the whole scene tree is slow; on the main menu look again at most twice a second.
        ulong now = Time.GetTicksMsec();
        if (now - s_lastSearch < 500) return null;
        s_lastSearch = now;
        return s_view = Find(((SceneTree)Engine.GetMainLoop()).Root);
    }

    static GameView? Find(Node n)
    {
        if (n is GameView v) return v;
        int count = n.GetChildCount(true);
        for (int i = 0; i < count; i++)
            if (Find(n.GetChild(i, true)) is GameView found) return found;
        return null;
    }
}

/// <summary>
/// Lets mods talk to each other without referencing one another: one publishes a topic, others subscribe to it.
/// Handlers run on the game's main thread, right away, in the order they subscribed. A mod that is not installed
/// simply never answers. Example: Spyglass publishes "course.plan" with ("item id", amount); Course opens its planner.
/// </summary>
static class Messages
{
    static Dictionary<string, List<Action<object?>>> Handlers =>
        Shared.Get("messages", () => new Dictionary<string, List<Action<object?>>>());

    public static void Subscribe(string topic, Action<object?> handler)
    {
        var all = Handlers;
        if (!all.TryGetValue(topic, out var list)) all[topic] = list = new();
        list.Add(handler);
    }

    /// <summary>Sends a message. Returns true when at least one mod is listening.</summary>
    public static bool Publish(string topic, object? data = null)
    {
        if (!Handlers.TryGetValue(topic, out var list) || list.Count == 0) return false;
        foreach (var h in list.ToArray())
        {
            try { h(data); }
            catch (Exception e) { KitLog.Write($"[{topic}] handler failed: {e.Message}"); }
        }
        return true;
    }

    public static bool HasListener(string topic) => Handlers.TryGetValue(topic, out var list) && list.Count > 0;
}
