using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Astrolabe.UI;

namespace MelvorModLoader;

/// <summary>A mod DLL the loader found, and how it went.</summary>
internal sealed class ModInfo
{
    public ModInfo(string name, string version, string file) { Name = name; Version = version; File = file; }
    public string Name { get; }
    public string Version { get; }
    public string File { get; }
    /// <summary>Why it didn't load ("" when it did).</summary>
    public string Failed { get; set; } = "";
    /// <summary>Game types and members it calls that this game version doesn't have.</summary>
    public List<string> Missing { get; set; } = new();
    public int Errors { get; set; }
    public string LastError { get; set; } = "";
    /// <summary>Its frame handler was switched off after repeated errors.</summary>
    public bool Stopped { get; set; }
}

/// <summary>The loader's own settings and memory between sessions: mods\loader-state.json.</summary>
internal sealed class LoaderState
{
    public string GameVersion { get; set; } = "";
    public bool CheckUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string LatestRelease { get; set; } = "";

    static LoaderState? s_state;
    static string FilePath => Path.Combine(Startup.ModsFolder, "loader-state.json");

    public static LoaderState Get()
    {
        if (s_state != null) return s_state;
        if (Startup.ModsFolder.Length == 0) return new LoaderState();
        try { s_state = File.Exists(FilePath) ? JsonSerializer.Deserialize<LoaderState>(File.ReadAllText(FilePath)) : null; }
        catch (Exception e) { Log.Write("loader-state.json unreadable, starting fresh: " + e.Message); }
        return s_state ??= new LoaderState();
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Get(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("Could not save loader-state.json: " + e.Message); }
    }
}

/// <summary>
/// After a game update, finds the calls a mod makes into the game that no longer exist. .NET only notices a missing
/// method when the code using it first runs (a crash mid-play); resolving every member reference up front turns that
/// into a clear note in the Mods menu instead.
/// </summary>
internal static class Compat
{
    public static bool MentionsLoader(Assembly asm) =>
        asm.GetReferencedAssemblies().Any(n => n.Name == "MelvorModLoader");

    public static List<string> Missing(Assembly asm)
    {
        var found = new SortedSet<string>();
        Module m = asm.ManifestModule;
        // Metadata tables: 0x01 TypeRef, 0x0A MemberRef. Tokens run from row 1 until the table ends.
        foreach (int table in new[] { 0x01000000, 0x0A000000 })
            for (int row = 1; row < 100_000; row++)
            {
                try
                {
                    if (table == 0x01000000) m.ResolveType(table | row);
                    else m.ResolveMember(table | row);
                }
                catch (ArgumentOutOfRangeException) { break; }   // past the last row
                catch (Exception e) when (e is MissingMemberException or TypeLoadException or FileNotFoundException)
                {
                    // A member of the loader itself: the mod was built for a newer loader than this one.
                    bool ours = e.Message.Contains("MelvorModLoader") || e.Message.Contains("Astrolabe.UI");
                    found.Add((ours ? "newer loader needed: " : "") + Short(e.Message));
                }
                catch { }   // needs generic arguments to resolve, and the like: not a missing member
                if (found.Count >= 50) break;
            }
        return found.ToList();
    }

    // "Method not found: 'Void GamesByMalcs.Melvor2.X.Y(Int32)'." -> "X.Y(Int32)"
    static string Short(string msg)
    {
        Match q = Regex.Match(msg, "'([^']+)'");
        string s = q.Success ? q.Groups[1].Value : msg;
        s = Regex.Replace(s, @"(GamesByMalcs\.Melvor2|MelvorModLoader|Astrolabe\.UI)\.(\w+\.)*", "");
        return s.Length > 90 ? s[..87] + "..." : s;
    }
}

/// <summary>Notices when the game itself has updated since the last session.</summary>
internal static class GameWatch
{
    /// <summary>This game's version ("0.11.7").</summary>
    public static string Version { get; private set; } = "";
    /// <summary>The version before an update this session ("" when the game didn't update).</summary>
    public static string UpdatedFrom { get; private set; } = "";

    public static void Check()
    {
        try { Version = ProjectSettings.GetSetting("application/config/version").AsString(); } catch { }
        if (Version.Length == 0)
            try { Version = File.GetLastWriteTimeUtc(Path.Combine(Startup.GameData, Startup.GameAssembly + ".dll")).ToString("yyyy-MM-dd HH:mm"); } catch { }
        LoaderState st = LoaderState.Get();
        if (st.GameVersion.Length > 0 && st.GameVersion != Version)
        {
            UpdatedFrom = st.GameVersion;
            Log.Write($"Game updated: {UpdatedFrom} -> {Version}");
            // Mods that cache game data (Spyglass) refresh on this.
            Messages.Publish("loader.gameupdated", Version);
        }
        if (st.GameVersion != Version) { st.GameVersion = Version; LoaderState.Save(); }
        Log.Write("Game version " + Version);
    }
}

/// <summary>
/// Once a day, asks GitHub for the newest Astrolabe release and compares its zips (AstrolabeCourse-0.10.0.zip, ...)
/// with what is installed. Only the release list is read; nothing is downloaded or changed.
/// </summary>
internal static class Updates
{
    public const string Repo = "Astrolabe-Melvor/loader";
    public static string ReleasePage => $"https://github.com/{Repo}/releases/latest";

    /// <summary>Newer versions than installed: (name, installed, latest).</summary>
    public static List<(string Name, string Have, string Latest)> Newer { get; private set; } = new();
    public static string Status { get; private set; } = "";
    static bool s_running;
    static readonly object s_gate = new();
    static Action? s_done;

    /// <summary>Finishes a check on the game's thread (called every frame; cheap when nothing is waiting).</summary>
    internal static void Poll()
    {
        Action? a;
        lock (s_gate) { a = s_done; s_done = null; }
        a?.Invoke();
    }

    /// <summary>Raised on the game's thread when a check finishes.</summary>
    internal static event Action? Changed;

    public static void CheckSoon()
    {
        LoaderState st = LoaderState.Get();
        if (!st.CheckUpdates) { Status = "Update check is off."; return; }
        if (st.LatestRelease.Length > 0) Apply(st.LatestRelease, announce: false);
        if (DateTime.UtcNow - st.LastUpdateCheck > TimeSpan.FromHours(20)) CheckNow(announce: true);
    }

    public static void CheckNow(bool announce)
    {
        if (s_running) return;
        s_running = true;
        Status = "Checking for updates...";
        Task.Run(async () =>
        {
            string json = "", error = "";
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AstrolabeLoader/" + Loader.Version);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var res = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) error = "No release published yet.";
                else if (!res.IsSuccessStatusCode) error = $"GitHub answered {(int)res.StatusCode}.";
                else json = await res.Content.ReadAsStringAsync();
            }
            catch (Exception e) { error = "Couldn't reach GitHub: " + e.Message; }
            // Handed to the game's thread: Loader's frame tick calls Poll.
            lock (s_gate) s_done = () =>
            {
                s_running = false;
                LoaderState st = LoaderState.Get();
                st.LastUpdateCheck = DateTime.UtcNow;
                if (json.Length > 0) st.LatestRelease = json = Compact(json);
                LoaderState.Save();
                if (error.Length > 0) { Status = error; Log.Write("Update check: " + error); Changed?.Invoke(); return; }
                Apply(json, announce);
            };
        });
    }

    // Only the tag and asset names are needed; the full release answer is tens of KB.
    static string Compact(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            var assets = new List<Dictionary<string, string?>>();
            if (r.TryGetProperty("assets", out JsonElement a))
                foreach (JsonElement x in a.EnumerateArray()) assets.Add(new() { ["name"] = x.GetProperty("name").GetString() });
            return JsonSerializer.Serialize(new { tag_name = r.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : "", assets });
        }
        catch { return json; }
    }

    static bool IsNewer(string latest, string have) =>
        System.Version.TryParse(latest, out var lv) && System.Version.TryParse(have.Split('+', '-')[0], out var hv) && lv > hv;

    static void Apply(string json, bool announce)
    {
        var newer = new List<(string, string, string)>();
        string tag = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("assets", out JsonElement assets))
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    Match m = Regex.Match(a.GetProperty("name").GetString() ?? "", @"^Astrolabe([A-Za-z]+)-(\d+(?:\.\d+){1,3})\.zip$");
                    if (!m.Success || m.Groups[1].Value == "All") continue;
                    string name = m.Groups[1].Value, latest = m.Groups[2].Value;
                    string have = name == "Loader" ? Loader.Version : Loader.Mods.FirstOrDefault(x => x.Name == name)?.Version ?? "";
                    if (have.Length == 0) continue;   // not installed: not an update
                    if (IsNewer(latest, have)) newer.Add((name, have, latest));
                }
        }
        catch (Exception e) { Status = "Couldn't read the release list: " + e.Message; Changed?.Invoke(); return; }
        Newer = newer;
        Status = newer.Count == 0 ? $"Up to date (latest release {tag})." : $"Updates in {tag}: " + string.Join(", ", newer.Select(n => $"{n.Item1} {n.Item2} to {n.Item3}"));
        Changed?.Invoke();   // after Status: the Mods menu reads it
        if (announce && newer.Count > 0) Toast.Show($"Astrolabe updates: {string.Join(", ", newer.Select(n => $"{n.Item1} {n.Item3}"))}. Mods menu ({Hotkeys.MenuKey}) has the link.", Kit.Link);
    }
}

/// <summary>A short note in the top-left corner that goes away by itself.</summary>
internal static class Toast
{
    static VBoxContainer? s_box;

    public static void Show(string text, Color color, double seconds = 12)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        if (s_box == null || !GodotObject.IsInstanceValid(s_box))
        {
            var layer = new CanvasLayer { Layer = 121, Name = "AstrolabeToasts" };
            s_box = new VBoxContainer { Position = new Vector2(8, 70), MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(s_box);
            tree.Root.AddChild(layer);
        }
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 0) };
        card.AddThemeStyleboxOverride("panel", Kit.Box(Kit.Tooltip, 10, 6));
        Label l = Kit.Label(text, color, 14);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(420, 0);
        card.AddChild(l);
        s_box.AddChild(card);
        tree.CreateTimer(seconds).Timeout += () => { if (GodotObject.IsInstanceValid(card)) card.QueueFree(); };
    }
}
