using System;
using System.Reflection;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;

namespace MelvorModLoader;

// This file is the public surface mods compile against. Names and signatures are kept the same as the other
// Melvor Idle 2 loader so its mods load here unchanged. Add new things; never rename or remove.

/// <summary>Implement this in one public class of your mod. The loader creates it and calls OnLoad once.</summary>
public interface IMod
{
    void OnLoad(ModContext ctx);
}

/// <summary>Handed to each mod on load.</summary>
public sealed class ModContext
{
    internal ModContext(string name, string directory, SceneTree tree)
    {
        Name = name;
        Directory = directory;
        Tree = tree;
    }

    /// <summary>The mod's assembly name.</summary>
    public string Name { get; }

    /// <summary>The folder the mod's DLL sits in. Keep the mod's own files here.</summary>
    public string Directory { get; }

    /// <summary>Godot's scene tree. Add nodes under Tree.Root.</summary>
    public SceneTree Tree { get; }

    /// <summary>Writes "[ModName] message" to the loader log.</summary>
    public void Log(string message) => MelvorModLoader.Log.Write($"[{Name}] {message}");

    /// <summary>
    /// Registers a window in the Mods menu (F1). <paramref name="toggle"/> opens or closes it, from the menu or its key.
    /// The player can rebind or clear the key. Key.None means menu only.
    /// </summary>
    public ModWindow AddWindow(string title, Action toggle, Key defaultKey = Key.None) =>
        Hotkeys.Register(Name, title, toggle, new KeyChord(defaultKey));

    /// <summary>Once per rendered frame, on the main thread. Paused while offline progress is computed.
    /// A handler that throws is removed and logged.</summary>
    public event Action Frame
    {
        add => Loader.AddFrame(this, value);
        remove => Loader.RemoveFrame(value);
    }
}

/// <summary>Helpers for reaching the running game.</summary>
public static class GameAccess
{
    static PropertyInfo? s_gameProp;

    /// <summary>Depth-first search for the first node of type T, from <paramref name="root"/> or the scene root.</summary>
    public static T? FindNode<T>(Node? root = null) where T : Node
    {
        root ??= ((SceneTree)Engine.GetMainLoop()).Root;
        if (root is T match) return match;
        int count = root.GetChildCount(true);
        for (int i = 0; i < count; i++)
        {
            T? found = FindNode<T>(root.GetChild(i, true));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>True while the game simulates offline time on its own thread. Leave the game alone until it is false.</summary>
    public static bool IsComputingOffline => ((SceneTree)Engine.GetMainLoop()).Paused;

    static GameView? s_view;
    static ulong s_lastSearch;

    /// <summary>The in-game screen, or null on the main menu. Cached, so it is cheap to call every frame.</summary>
    public static GameView? FindGameView()
    {
        if (s_view != null && GodotObject.IsInstanceValid(s_view) && s_view.IsInsideTree()) return s_view;
        s_view = null;
        // Searching the whole scene tree is slow; on the main menu look again at most twice a second.
        ulong now = Time.GetTicksMsec();
        if (now - s_lastSearch < 500) return null;
        s_lastSearch = now;
        return s_view = FindNode<GameView>();
    }

    /// <summary>The open character's game state, or null on the main menu.</summary>
    public static GamesByMalcs.Melvor2.Game? CurrentGame
    {
        get
        {
            GameView? view = FindGameView();
            if (view == null) return null;
            s_gameProp ??= typeof(GameView).GetProperty("Game", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return s_gameProp?.GetValue(view) as GamesByMalcs.Melvor2.Game;
        }
    }
}
