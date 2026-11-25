using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

// Godot calls GodotPlugins.Game.Main.InitializeFromGameProject in the assembly that the project setting
// dotnet/project/assembly_name names. override.cfg sets that name to ours, so the engine calls this method
// and we start the game ourselves, then the mods.
namespace GodotPlugins.Game
{
    internal static class Main
    {
        // Must not touch Godot or game types: their assemblies only resolve after Startup.Begin installs the resolver.
        [UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]
        private static byte InitializeFromGameProject(nint godotDll, nint outCallbacks, nint inCallbacks, int inCallbacksSize)
        {
            try
            {
                MelvorModLoader.Startup.Begin(godotDll, outCallbacks, inCallbacks, inCallbacksSize);
                return 1;
            }
            catch (Exception e)
            {
                MelvorModLoader.Log.Write("Could not start: " + e);
                return 0;
            }
        }
    }
}

namespace MelvorModLoader
{
    internal static class Startup
    {
        internal const string GameAssembly = "GamesByMalcs.Melvor2";

        internal static string Home = "", GameFolder = "", GameData = "", ModsFolder = "";
        internal static AssemblyLoadContext Context = null!;

        internal static string Version =>
            typeof(Startup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        static readonly List<string> s_lookIn = new();

        internal static void Begin(nint godotDll, nint outCallbacks, nint inCallbacks, int inCallbacksSize)
        {
            Assembly me = typeof(Startup).Assembly;
            Home = Path.GetDirectoryName(Path.GetFullPath(me.Location))!;
            GameFolder = Path.GetDirectoryName(Home)!;
            ModsFolder = Path.Combine(GameFolder, "mods");
            Log.Open(GameFolder);
            Log.Write($"{Loader.Name} {Version} on .NET {Environment.Version}");

            GameData = LocateGameData();
            Context = AssemblyLoadContext.GetLoadContext(me)!;
            LookIn(GameData);
            Context.Resolving += OnResolving;

            BootGodot(godotDll, outCallbacks, inCallbacks, inCallbacksSize);
            Log.Write("Game started from " + GameData);
            QueueLoader();
        }

        // What Godot's generated entry point does for every C# game, with the game's DLL as the script assembly.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void BootGodot(nint godotDll, nint outCallbacks, nint inCallbacks, int inCallbacksSize)
        {
            NativeLibrary.SetDllImportResolver(typeof(Godot.GodotObject).Assembly,
                new Godot.NativeInterop.GodotDllImportResolver(godotDll).OnResolveDllImport);
            Godot.NativeInterop.NativeFuncs.Initialize(inCallbacks, inCallbacksSize);
            Godot.Bridge.ManagedCallbacks.Create(outCallbacks);
            Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(typeof(GamesByMalcs.Melvor2.Game).Assembly);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void QueueLoader() => Loader.Queue();

        static string LocateGameData()
        {
            foreach (string dir in Directory.GetDirectories(GameFolder, "data_*"))
                if (File.Exists(Path.Combine(dir, GameAssembly + ".dll")))
                    return dir;
            throw new DirectoryNotFoundException($"No {GameAssembly}.dll next to the loader. Install the loader into the game folder.");
        }

        internal static void LookIn(string dir)
        {
            if (!s_lookIn.Contains(dir)) s_lookIn.Add(dir);
        }

        static Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
        {
            foreach (string dir in s_lookIn)
            {
                string file = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(file)) return ctx.LoadFromAssemblyPath(file);
            }
            return null;
        }
    }

    /// <summary>The loader's log file (AstrolabeLoader.log in the game folder), started fresh each run.</summary>
    public static class Log
    {
        static readonly object s_gate = new();
        public static string FilePath { get; private set; } = Path.Combine(Path.GetTempPath(), "AstrolabeLoader.log");

        internal static void Open(string folder)
        {
            string file = Path.Combine(folder, "AstrolabeLoader.log");
            try { File.WriteAllText(file, ""); FilePath = file; }
            catch { try { File.WriteAllText(FilePath, ""); } catch { } }
        }

        public static void Write(string message)
        {
            lock (s_gate)
            {
                try { File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss ") + message + Environment.NewLine); }
                catch { }
            }
        }
    }
}
