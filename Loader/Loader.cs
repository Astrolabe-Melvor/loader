using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using Astrolabe.UI;

namespace MelvorModLoader;

/// <summary>Finds and starts the mods, and runs their frame handlers.</summary>
public static class Loader
{
    public const string Name = "Astrolabe Loader";

    public static string Version => Startup.Version;

    /// <summary>Assembly names of the mods that loaded.</summary>
    public static IReadOnlyList<string> LoadedMods => s_mods;

    static readonly List<string> s_mods = new();
    static readonly List<(ModContext Owner, Action Run)> s_frame = new();
    static SceneTree s_tree = null!;

    // The scene tree does not exist yet when Startup runs; start on the first idle frame.
    internal static void Queue() => Callable.From(Start).CallDeferred();

    static void Start()
    {
        try
        {
            s_tree = (SceneTree)Engine.GetMainLoop();
            Hotkeys.Load();
            Kit.Init();
            LoadAll();
            ModsMenu.Install(s_tree);
            s_tree.ProcessFrame += Tick;
            Banner();
        }
        catch (Exception e) { Log.Write("Loader failed: " + e); }
    }

    static void LoadAll()
    {
        if (!Directory.Exists(Startup.ModsFolder))
        {
            Directory.CreateDirectory(Startup.ModsFolder);
            Log.Write("Created the mods folder: " + Startup.ModsFolder);
            return;
        }

        // mods\*.dll and mods\<Mod>\*.dll
        List<string> files = Directory.EnumerateFiles(Startup.ModsFolder, "*.dll")
            .Concat(Directory.EnumerateDirectories(Startup.ModsFolder).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Every mod folder is searched for dependencies, so a mod can ship its own libraries.
        foreach (string f in files) Startup.LookIn(Path.GetDirectoryName(f)!);

        foreach (string file in files)
        {
            Assembly asm;
            try { asm = Startup.Context.LoadFromAssemblyPath(file); }
            catch (BadImageFormatException) { continue; }   // a native library shipped with a mod
            catch (Exception e) { Log.Write($"Could not open {Path.GetFileName(file)}: {e.Message}"); continue; }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                Log.Write($"FAILED {Path.GetFileName(file)}: {e.LoaderExceptions.FirstOrDefault(x => x != null)?.Message ?? e.Message}");
                continue;
            }

            string name = asm.GetName().Name ?? Path.GetFileNameWithoutExtension(file);
            foreach (Type t in types)
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IMod).IsAssignableFrom(t)) continue;
                try
                {
                    var mod = (IMod)Activator.CreateInstance(t)!;
                    mod.OnLoad(new ModContext(name, Path.GetDirectoryName(file)!, s_tree));
                    s_mods.Add(name);
                    Log.Write($"Loaded {name} {asm.GetName().Version?.ToString(3)}");
                }
                catch (Exception e) { Log.Write($"FAILED {name}: {(e as TargetInvocationException)?.InnerException ?? e}"); }
            }
        }
        Log.Write($"{s_mods.Count} mod(s) loaded");
    }

    internal static void AddFrame(ModContext owner, Action run) => s_frame.Add((owner, run));
    internal static void RemoveFrame(Action run) => s_frame.RemoveAll(f => f.Run == run);

    static void Tick()
    {
        if (s_tree.Paused) return;   // offline progress is running on the game's thread
        Hotkeys.Poll(s_tree);
        for (int i = 0; i < s_frame.Count; i++)
        {
            try { s_frame[i].Run(); }
            catch (Exception e)
            {
                s_frame[i].Owner.Log("Frame handler removed after an error: " + e);
                s_frame.RemoveAt(i--);
            }
        }
    }

    static void Banner()
    {
        var layer = new CanvasLayer { Layer = 120, Name = "AstrolabeBanner" };
        var tag = new PanelContainer { Position = new Vector2(8, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        tag.AddThemeStyleboxOverride("panel", Kit.Box(Kit.Tooltip, 10, 6));
        var text = Kit.Label($"{Name} {Version}   {s_mods.Count} mod{(s_mods.Count == 1 ? "" : "s")}   F1 Mods", Kit.Good, 14);
        tag.AddChild(text);
        layer.AddChild(tag);
        s_tree.Root.AddChild(layer);
        s_tree.CreateTimer(12.0).Timeout += layer.QueueFree;
    }
}
