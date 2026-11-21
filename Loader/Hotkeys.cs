using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MelvorModLoader;

/// <summary>A key plus optional Ctrl / Shift / Alt. Key.None means no key.</summary>
public readonly record struct KeyChord(Key Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public static readonly KeyChord None = new(Godot.Key.None);
    public bool IsNone => Key == Godot.Key.None;

    /// <summary>"F8", "Ctrl+Shift+M", or "" when there is no key.</summary>
    public override string ToString()
    {
        if (IsNone) return "";
        string mods = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
        return mods + OS.GetKeycodeString(Key);
    }

    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        bool c = false, s = false, a = false;
        Key k = Godot.Key.None;
        foreach (string raw in text.Split('+'))
        {
            string p = raw.Trim();
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) c = true;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) s = true;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) a = true;
            else if (p.Length > 0) k = OS.FindKeycodeFromString(p);
        }
        return k == Godot.Key.None ? None : new KeyChord(k, c, s, a);
    }

    internal bool IsDown() =>
        !IsNone && Input.IsKeyPressed(Key)
        && Input.IsKeyPressed(Godot.Key.Ctrl) == Ctrl
        && Input.IsKeyPressed(Godot.Key.Shift) == Shift
        && Input.IsKeyPressed(Godot.Key.Alt) == Alt;

    internal bool IsFunctionKey => Key >= Godot.Key.F1 && Key <= Godot.Key.F35;
}

/// <summary>A mod window listed in the Mods menu. Made by <see cref="ModContext.AddWindow"/>.</summary>
public sealed class ModWindow
{
    readonly Action _toggle;
    internal bool Held;

    internal ModWindow(string mod, string title, Action toggle, KeyChord key)
    {
        Mod = mod;
        Title = title;
        _toggle = toggle;
        DefaultKey = key;
        Key = key;
    }

    /// <summary>"Mod:Title"; the name the key is saved under.</summary>
    public string Id => Mod + ":" + Title;
    public string Mod { get; }
    public string Title { get; }
    public KeyChord DefaultKey { get; }
    public KeyChord Key { get; internal set; }

    /// <summary>The current key as text, "" when cleared. Show this instead of a fixed key name.</summary>
    public string KeyLabel => Key.ToString();

    /// <summary>Raised on the main thread after the player changes the key.</summary>
    public event Action? Changed;

    internal void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Write($"[{Mod}] key change handler: {e.Message}"); }
    }

    /// <summary>Opens or closes the window.</summary>
    public void Press()
    {
        try { _toggle(); }
        catch (Exception e) { Log.Write($"[{Mod}] '{Title}' failed to open: {e}"); }
    }
}

/// <summary>Window keys for every mod. Saved in mods\keys.json. Checked once per frame.</summary>
public static class Hotkeys
{
    static readonly List<ModWindow> s_windows = new();
    static Dictionary<string, string> s_saved = new();
    static string File => Path.Combine(Startup.ModsFolder, "keys.json");

    /// <summary>The key that opens the Mods menu.</summary>
    public static KeyChord MenuKey { get; private set; } = new(Key.F1);

    public static IReadOnlyList<ModWindow> Windows => s_windows;

    /// <summary>Raised when a window is added or any key changes.</summary>
    public static event Action? ListChanged;

    internal static void Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                s_saved = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch (Exception e) { Log.Write("keys.json unreadable, using defaults: " + e.Message); }
        if (s_saved.TryGetValue("*menu", out string? m)) MenuKey = KeyChord.Parse(m) is { IsNone: false } k ? k : MenuKey;
    }

    internal static ModWindow Register(string mod, string title, Action toggle, KeyChord key)
    {
        var w = new ModWindow(mod, title, toggle, key);
        if (s_saved.TryGetValue(w.Id, out string? text)) w.Key = KeyChord.Parse(text);
        s_windows.RemoveAll(x => x.Id == w.Id);
        s_windows.Add(w);
        ListChanged?.Invoke();
        return w;
    }

    internal static void Rebind(ModWindow w, KeyChord key)
    {
        w.Key = key;
        if (key == w.DefaultKey) s_saved.Remove(w.Id); else s_saved[w.Id] = key.IsNone ? "None" : key.ToString();
        Save();
        w.NotifyChanged();
        ListChanged?.Invoke();
    }

    internal static void ResetAll()
    {
        s_saved.Clear();
        MenuKey = new KeyChord(Key.F1);
        Save();
        foreach (ModWindow w in s_windows) { w.Key = w.DefaultKey; w.NotifyChanged(); }
        ListChanged?.Invoke();
    }

    /// <summary>Other windows that use the same key.</summary>
    public static IEnumerable<ModWindow> Clashes(ModWindow w) =>
        w.Key.IsNone ? Enumerable.Empty<ModWindow>() : s_windows.Where(o => o != w && o.Key == w.Key);

    static void Save()
    {
        try { System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s_saved, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("Could not save keys.json: " + e.Message); }
    }

    static bool s_menuHeld;
    internal static bool Capturing;   // the Mods menu is waiting for a new key

    // Fires on the press edge only. While the Mods menu captures a new key nothing fires, but held state is still
    // tracked so the captured key does not open a window once capture ends.
    internal static void Poll(SceneTree tree)
    {
        Control? focus = tree.Root.GuiGetFocusOwner();
        bool typing = focus is LineEdit or TextEdit;

        bool menu = MenuKey.IsDown();
        if (menu && !s_menuHeld && !Capturing && (!typing || MenuKey.IsFunctionKey)) ModsMenu.Toggle();
        s_menuHeld = menu;

        for (int i = 0; i < s_windows.Count; i++)
        {
            ModWindow w = s_windows[i];
            bool down = w.Key.IsDown();
            if (down && !w.Held && !Capturing && (!typing || w.Key.IsFunctionKey)) w.Press();
            w.Held = down;
        }
    }

    internal static void SetMenuKey(KeyChord key)
    {
        MenuKey = key;
        s_saved["*menu"] = key.ToString();
        Save();
        ListChanged?.Invoke();
    }
}
