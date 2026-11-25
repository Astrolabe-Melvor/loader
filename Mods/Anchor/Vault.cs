using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2.Saves;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Anchor;

/// <summary>One backup: a save string (the same text the game's own Export gives) and what it held.</summary>
public sealed class Backup
{
    public string File { get; set; } = "";
    public DateTime Time { get; set; }
    public string Character { get; set; } = "";
    public string Mode { get; set; } = "";
    public int TotalLevel { get; set; }
    public long Gp { get; set; }
    /// <summary>"timer", "launch", "manual" or "restore" (taken just before a restore).</summary>
    public string Kind { get; set; } = "";
    public string Note { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Bytes { get; set; }
    public bool Auto => Kind is "timer" or "launch";
}

public sealed class Options
{
    public int EveryMinutes { get; set; } = 30;
    public int KeepAuto { get; set; } = 20;
    public int KeepDays { get; set; } = 7;
}

/// <summary>A character Anchor knows: in the game's save list, or only in Anchor's backups (deleted from the game).</summary>
public sealed record Character(Guid Id, string Name, bool InGame);

/// <summary>
/// Backs saves up into mods\Anchor\backups\&lt;save id&gt;\ and restores them through the game's own save code
/// (so Steam Cloud gets the restored save too). Automatic backups: every save when the game starts, and the
/// open character on a timer. Only automatic backups are pruned; ones you make are kept until you delete them.
/// </summary>
public sealed class Vault
{
    readonly string _folder;
    readonly Action<string> _log;
    readonly Dictionary<Guid, List<Backup>> _lists = new();
    bool _launchDone;
    double _next = -1;

    public Options Options { get; }
    public event Action? Changed;

    public Vault(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
        Options = Read<Options>(Path.Combine(folder, "options.json")) ?? new Options();
    }

    public string Root => Path.Combine(_folder, "backups");
    string Dir(Guid id) => Path.Combine(Root, id.ToString());

    public void SaveOptions()
    {
        Options.EveryMinutes = Math.Clamp(Options.EveryMinutes, 5, 720);
        Options.KeepAuto = Math.Clamp(Options.KeepAuto, 3, 500);
        Options.KeepDays = Math.Clamp(Options.KeepDays, 0, 365);
        Write(Path.Combine(_folder, "options.json"), Options);
    }

    public void Tick(double now)
    {
        // Every save once at launch, as soon as the game has read its save list (skipped when unchanged).
        if (!_launchDone && SaveManager.StoredSaves.Count > 0)
        {
            _launchDone = true;
            int n = 0;
            foreach (Guid id in SaveManager.StoredSaves.Keys.ToList())
                if (TakeBackup(id, "launch", "", out _) == null) n++;
            if (n > 0) { _log($"Backed up {n} save{(n == 1 ? "" : "s")} at launch"); Changed?.Invoke(); }
        }

        // The open character, on the timer. The save file is what the game last wrote (it saves every few seconds).
        if (KitGame.Current == null || ActiveId() is not Guid active || GameAccess.IsComputingOffline) { _next = -1; return; }
        if (_next < 0) { _next = now + Options.EveryMinutes * 60; return; }
        if (now < _next) return;
        _next = now + Options.EveryMinutes * 60;
        if (TakeBackup(active, "timer", "", out _) == null) Changed?.Invoke();
    }

    static System.Reflection.PropertyInfo? s_activeProp;

    /// <summary>The open character's save id (the game keeps it private), or by name if that fails.</summary>
    public static Guid? ActiveId()
    {
        if (!SaveManager.HasActiveSaveId) return null;
        try
        {
            s_activeProp ??= typeof(SaveManager).GetProperty("ActiveSaveId",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (s_activeProp?.GetValue(null) is Guid g && g != Guid.Empty) return g;
        }
        catch { }
        string? name = KitGame.Current?.CharacterName;
        foreach (var (id, saves) in SaveManager.StoredSaves)
        {
            try { if (saves.GetMostRecentSave().Save.Header.CharacterName == name) return id; } catch { }
        }
        return null;
    }

    /// <summary>Characters in the game's save list, then any that only have backups here.</summary>
    public List<Character> Characters()
    {
        var list = new List<Character>();
        foreach (var (id, saves) in SaveManager.StoredSaves)
        {
            string name = "";
            try { name = saves.GetMostRecentSave().Save.Header.CharacterName; } catch { }
            list.Add(new Character(id, name.Length > 0 ? name : id.ToString()[..8], true));
        }
        if (Directory.Exists(Root))
            foreach (string d in Directory.EnumerateDirectories(Root))
                if (Guid.TryParse(Path.GetFileName(d), out Guid id) && list.All(c => c.Id != id))
                {
                    var l = Backups(id);
                    if (l.Count > 0) list.Add(new Character(id, l[0].Character, false));
                }
        return list;
    }

    /// <summary>Backups of a save, newest first.</summary>
    public List<Backup> Backups(Guid id)
    {
        if (_lists.TryGetValue(id, out var l)) return l;
        l = Read<List<Backup>>(Path.Combine(Dir(id), "index.json")) ?? new();
        l.RemoveAll(b => !System.IO.File.Exists(Path.Combine(Dir(id), b.File)));
        l.Sort((a, b) => b.Time.CompareTo(a.Time));
        return _lists[id] = l;
    }

    void SaveList(Guid id) => Write(Path.Combine(Dir(id), "index.json"), Backups(id));

    /// <summary>Backs up a save. Returns null when a backup was written, otherwise why not
    /// (automatic backups skip a save that has not changed since the last backup).</summary>
    public string? TakeBackup(Guid id, string kind, string note, out Backup? made)
    {
        made = null;
        if (!SaveManager.StoredSaves.TryGetValue(id, out StoredSaves? saves) || !saves.HasSave) return "the game has no save for this character";
        string text;
        SaveHeader header;
        try
        {
            var (source, save) = saves.GetMostRecentSave();
            text = source.GetSaveStringForId(id);
            header = save.Header;
        }
        catch (Exception e) { return "could not read the save: " + e.Message; }
        if (string.IsNullOrEmpty(text)) return "the save is empty";
        if (SaveManager.TryGetHeaderFromSaveString(text, out SaveHeader? fresh)) header = fresh;

        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        var list = Backups(id);
        if (kind is "timer" or "launch" && list.Count > 0 && list[0].Hash == hash) return "unchanged since the last backup";

        DateTime now = DateTime.Now;
        string file = now.ToString("yyyyMMdd-HHmmss") + ".txt";
        try
        {
            Directory.CreateDirectory(Dir(id));
            string path = Path.Combine(Dir(id), file);
            if (System.IO.File.Exists(path)) { file = now.ToString("yyyyMMdd-HHmmss-fff") + ".txt"; path = Path.Combine(Dir(id), file); }
            System.IO.File.WriteAllText(path, text);
        }
        catch (Exception e) { return "could not write the backup: " + e.Message; }

        made = new Backup
        {
            File = file, Time = now, Character = header.CharacterName, Mode = header.Gamemode, TotalLevel = header.TotalLevel,
            Gp = header.GP, Kind = kind, Note = note.Trim(), Hash = hash, Bytes = text.Length,
        };
        list.Insert(0, made);
        Prune(id);
        SaveList(id);
        return null;
    }

    /// <summary>Keeps the newest automatic backups, plus the newest one of each of the last few days.
    /// Backups you made (and the ones taken before a restore) are never pruned.</summary>
    void Prune(Guid id)
    {
        var list = Backups(id);
        var autos = list.Where(b => b.Auto).ToList();   // newest first
        var keep = new HashSet<Backup>(autos.Take(Options.KeepAuto));
        DateTime since = DateTime.Today.AddDays(-Options.KeepDays);
        foreach (var day in autos.Where(b => b.Time >= since).GroupBy(b => b.Time.Date)) keep.Add(day.First());
        foreach (Backup b in autos.Where(b => !keep.Contains(b)).ToList()) Remove(id, b);
    }

    void Remove(Guid id, Backup b)
    {
        try { System.IO.File.Delete(Path.Combine(Dir(id), b.File)); } catch { }
        Backups(id).Remove(b);
    }

    public void Delete(Guid id, Backup b)
    {
        Remove(id, b);
        SaveList(id);
        Changed?.Invoke();
    }

    public void SetNote(Guid id, Backup b, string note)
    {
        b.Note = note.Trim();
        SaveList(id);
        Changed?.Invoke();
    }

    public string? Text(Guid id, Backup b)
    {
        try { return System.IO.File.ReadAllText(Path.Combine(Dir(id), b.File)); }
        catch { return null; }
    }

    /// <summary>Puts a backup back as the character's save. Only from the title screen: an open character would save
    /// over it within seconds. The current save is backed up first, so a restore can be undone.</summary>
    public async Task<string> Restore(Guid id, Backup b)
    {
        if (KitGame.Current != null) return "Return to the title screen first: the open character would save over it.";
        string? text = Text(id, b);
        if (text == null) return "The backup file is missing.";
        if (!SaveManager.TryGetHeaderFromSaveString(text, out SaveHeader? h) || h.IsCorrupt) return "The backup can't be read as a save.";
        if (h.UUID != id) return "That backup belongs to a different character.";
        if (!SaveManager.StoredSaves.ContainsKey(id) && SaveManager.IsAtMaxSaves) return "The game already has the most characters it allows. Delete one first.";

        if (SaveManager.StoredSaves.ContainsKey(id))
        {
            string? why = TakeBackup(id, "restore", $"before restoring {b.Time:MMM d HH:mm}", out _);
            if (why != null) return "Not restored: could not back up the current save first (" + why + ").";
        }
        try
        {
            var errors = await SaveManager.WriteSaveStringToAllSourcesAsync(id, text, backup: true);
            if (errors.Count > 0) return "The game reported: " + string.Join("; ", errors.Select(e => e.Message));
        }
        catch (Exception e) { return "Restore failed: " + e.Message; }
        _log($"Restored {b.Character} from {b.Time:yyyy-MM-dd HH:mm}");
        Changed?.Invoke();
        return "";
    }

    void Write<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { _log("Could not save: " + e.Message); }
    }

    static T? Read<T>(string file) where T : class
    {
        try { return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(file)) : null; }
        catch { return null; }
    }
}
