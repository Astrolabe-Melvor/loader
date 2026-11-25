using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using Astrolabe.UI;
// ModInfo, Compat, GameWatch, Updates and Toast live in Status.cs.

namespace MelvorModLoader;

/// <summary>Finds and starts the mods, and runs their frame handlers.</summary>
public static class Loader
{
    public const string Name = "Astrolabe Loader";

    public static string Version => Startup.Version;

    /// <summary>Assembly names of the mods that loaded.</summary>
    public static IReadOnlyList<string> LoadedMods => s_mods;

    /// <summary>Every mod DLL found: its version, and whether it loaded, failed, or calls game code that is gone.</summary>
    internal static IReadOnlyList<ModInfo> Mods => s_info;

    static readonly List<string> s_mods = new();
    static readonly List<ModInfo> s_info = new();
    static readonly List<FrameHandler> s_frame = new();

    sealed class FrameHandler
    {
        public ModContext Owner = null!;
        public Action Run = null!;
        public int Errors;
        public double FirstError;
    }
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
            GameWatch.Check();
            Updates.CheckSoon();
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

            string name = asm.GetName().Name ?? Path.GetFileNameWithoutExtension(file);
            var info = new ModInfo(name, asm.GetName().Version?.ToString(3) ?? "", file);

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                string why = e.LoaderExceptions.FirstOrDefault(x => x != null)?.Message ?? e.Message;
                Log.Write($"FAILED {Path.GetFileName(file)}: {why}");
                if (Compat.MentionsLoader(asm)) { info.Failed = why; s_info.Add(info); }
                continue;
            }
            if (!types.Any(t => !t.IsAbstract && !t.IsInterface && typeof(IMod).IsAssignableFrom(t))) continue;   // a library, not a mod

            // Game calls the mod makes that this game version no longer has (renamed or removed in an update).
            info.Missing = Compat.Missing(asm);
            if (info.Missing.Count > 0) Log.Write($"{name}: {info.Missing.Count} game call(s) not found in this game version: {string.Join(", ", info.Missing.Take(8))}");
            s_info.Add(info);

            foreach (Type t in types)
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IMod).IsAssignableFrom(t)) continue;
                try
                {
                    var mod = (IMod)Activator.CreateInstance(t)!;
                    mod.OnLoad(new ModContext(name, Path.GetDirectoryName(file)!, s_tree));
                    s_mods.Add(name);
                    Log.Write($"Loaded {name} {info.Version}");
                }
                catch (Exception e)
                {
                    Exception inner = (e as TargetInvocationException)?.InnerException ?? e;
                    info.Failed = inner.Message;
                    Log.Write($"FAILED {name}: {inner}");
                }
            }
        }
        Log.Write($"{s_mods.Count} mod(s) loaded");
    }

    internal static void AddFrame(ModContext owner, Action run) => s_frame.Add(new FrameHandler { Owner = owner, Run = run });
    internal static void RemoveFrame(Action run) => s_frame.RemoveAll(f => f.Run == run);

    const int MaxErrors = 5;          // errors within ErrorWindow seconds before a frame handler is switched off
    const double ErrorWindow = 60;

    static void Tick()
    {
        if (s_tree.Paused) return;   // offline progress is running on the game's thread
        Hotkeys.Poll(s_tree);
        Updates.Poll();
        double now = Time.GetTicksMsec() / 1000.0;
        for (int i = 0; i < s_frame.Count; i++)
        {
            FrameHandler f = s_frame[i];
            try { f.Run(); }
            catch (Exception e)
            {
                // One bad frame (a game list changing under it, say) shouldn't stop a mod for the rest of the session;
                // a handler that keeps failing is switched off, and the player is told.
                if (f.Errors == 0 || now - f.FirstError > ErrorWindow) { f.Errors = 0; f.FirstError = now; }
                f.Errors++;
                ModInfo? info = s_info.Find(m => m.Name == f.Owner.Name);
                if (info != null) { info.Errors++; info.LastError = e.Message; }
                if (f.Errors == 1) f.Owner.Log("Error (it keeps running): " + e);
                if (f.Errors >= MaxErrors)
                {
                    f.Owner.Log($"Switched off after {MaxErrors} errors in a minute. Last: {e.Message}");
                    if (info != null) info.Stopped = true;
                    s_frame.RemoveAt(i--);
                    Toast.Show($"{f.Owner.Name} hit repeated errors and paused itself. Mods menu ({Hotkeys.MenuKey}) > Report a bug sends the details.", Kit.Bad);
                }
            }
        }
    }

    static void Banner()
    {
        var layer = new CanvasLayer { Layer = 120, Name = "AstrolabeBanner" };
        var tag = new PanelContainer { Position = new Vector2(8, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        tag.AddThemeStyleboxOverride("panel", Kit.Box(Kit.Tooltip, 10, 6));
        var lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        lines.AddChild(Kit.Label($"{Name} {Version}   {s_mods.Count} mod{(s_mods.Count == 1 ? "" : "s")}   {Hotkeys.MenuKey} Mods", Kit.Good, 14));
        // Anything the player should know about: mods that didn't load, a game update, game calls that are gone.
        foreach (string n in Notices()) lines.AddChild(Kit.Label(n, Kit.Gold, 13));
        tag.AddChild(lines);
        layer.AddChild(tag);
        s_tree.Root.AddChild(layer);
        s_tree.CreateTimer(lines.GetChildCount() > 1 ? 20.0 : 12.0).Timeout += layer.QueueFree;
    }

    /// <summary>Short lines for the banner and the Mods menu about what needs attention.</summary>
    internal static List<string> Notices()
    {
        var list = new List<string>();
        if (GameWatch.UpdatedFrom.Length > 0) list.Add($"The game updated ({GameWatch.UpdatedFrom} to {GameWatch.Version}).");
        foreach (ModInfo m in s_info.Where(m => m.Failed.Length > 0)) list.Add($"{m.Name} did not load: {Trim(m.Failed)}");
        foreach (ModInfo m in s_info.Where(m => m.Failed.Length == 0 && m.Missing.Count > 0))
            list.Add(m.Missing.Any(x => x.StartsWith("newer loader"))
                ? $"{m.Name} needs a newer {Name}: update the loader too."
                : $"{m.Name} uses {m.Missing.Count} game feature{(m.Missing.Count == 1 ? "" : "s")} this game version doesn't have; parts of it may not work.");
        return list;
    }

    static string Trim(string s) => s.Length > 120 ? s[..117] + "..." : s;
}
