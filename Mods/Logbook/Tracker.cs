using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.EventBus;
using MelvorModLoader;

namespace Astrolabe.Logbook;

public sealed class SkillLine
{
    public string Name { get; set; } = "";
    public double Xp { get; set; }
    public int StartLevel { get; set; }
    public int Level { get; set; }
    public long Actions { get; set; }
}

public sealed class DropEvent
{
    public DateTime Time { get; set; }
    public string Item { get; set; } = "";
    public long Qty { get; set; }
    public string Why { get; set; } = "";   // "first this session" or "rare: about 1 in 230 kills"
    public bool Rare { get; set; }
}

/// <summary>What changed while the game caught up on time you were away.</summary>
public sealed class Away
{
    public DateTime EndedUtc { get; set; }
    public Dictionary<string, double> Xp { get; set; } = new();
    public Dictionary<string, long> Items { get; set; } = new();
    public Dictionary<string, long> Currency { get; set; } = new();
    public bool IsEmpty => Xp.Count == 0 && Items.Count == 0 && Currency.Count == 0;
}

public sealed class Session
{
    public string Character { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime LastUtc { get; set; }
    /// <summary>Seconds of play counted (time the game was open; catching up offline time is not counted).</summary>
    public double Seconds { get; set; }
    public Dictionary<string, SkillLine> Skills { get; set; } = new();
    public Dictionary<string, long> Gained { get; set; } = new();
    public Dictionary<string, long> Used { get; set; } = new();
    public Dictionary<string, long> CurrencyGained { get; set; } = new();
    public Dictionary<string, long> CurrencySpent { get; set; } = new();
    /// <summary>How many separate times an item came in (for spotting rare drops).</summary>
    public Dictionary<string, int> Times { get; set; } = new();
    /// <summary>Display names for every id above, so past sessions read well without the game data.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
    /// <summary>Sell price of one of each item (in Gold Pieces), for "worth per hour".</summary>
    public Dictionary<string, long> Values { get; set; } = new();
    public List<DropEvent> Drops { get; set; } = new();
    public Away? LastAway { get; set; }
    /// <summary>XP, bank and currencies at the last save: compared after the next load to show what happened while away.</summary>
    public Snapshot? Snap { get; set; }

    public long ValueOf(string id, long qty) => Values.TryGetValue(id, out long v) ? v * qty : 0;
    public long GainedValue => Gained.Sum(kv => ValueOf(kv.Key, kv.Value));

    public double Hours => Math.Max(Seconds, 1) / 3600.0;
    public string NameOf(string id) => Names.TryGetValue(id, out string? n) ? n : id[(id.LastIndexOf(':') + 1)..];
}

public sealed class Snapshot
{
    public Dictionary<string, double> Xp { get; set; } = new();
    public Dictionary<string, int> Levels { get; set; } = new();
    public Dictionary<string, long> Items { get; set; } = new();
    public Dictionary<string, long> Currency { get; set; } = new();
}

public sealed class Options
{
    public bool Panel { get; set; } = true;
    public float PanelX { get; set; } = -1;
    public float PanelY { get; set; } = -1;
    public bool PanelMin { get; set; }
    public bool RareOnly { get; set; }
}

/// <summary>
/// Watches XP, the bank and currencies once a second and adds up what changed. Time spent catching up while you were
/// away is kept apart (as <see cref="Session.LastAway"/>) so it does not inflate the live per-hour rates.
/// </summary>
public sealed class Tracker
{
    const int RareActions = 25;     // need this many actions before calling anything rare
    const int RareRatio = 40;       // rare: came in at most once per this many actions
    const int MaxDrops = 400;
    const int KeepSessions = 60;

    readonly string _folder;
    readonly Action<string> _log;
    readonly Dictionary<string, Queue<(double T, double Xp)>> _recent = new();
    static readonly object s_lock = new();
    static readonly Dictionary<string, long> s_actions = new();
    Snapshot? _snap;
    Game? _hooked;
    volatile bool _offline;
    bool _fromDisk;   // _snap came from the last save, before the game was closed
    double _next, _lastNow, _nextSave, _nextSample, _nextHistory;

    public Session Session { get; private set; } = new();
    public Options Options { get; private set; } = new();
    public event Action? Changed;
    public string CurrentEntity { get; private set; } = "";

    public Tracker(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
        Options = ReadJson<Options>(Path.Combine(folder, "options.json")) ?? new Options();
    }

    // ---- snapshots ----


    Snapshot Take(Game game)
    {
        var s = new Snapshot();
        foreach (GameEntity e in game.LeveledEntities.OfType<GameEntity>())
        {
            ExperienceComponent? x = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
            if (x == null) continue;
            s.Xp[e.Id] = x.Xp;
            s.Levels[e.Id] = x.Level;
            Session.Names.TryAdd(e.Id, e.DisplayName.ToString());
        }
        ActionComponent? any = game.ActionManager.ActiveActions.FirstOrDefault()
            ?? game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<ActionComponent>().FirstOrDefault();
        if (any != null)
        {
            foreach (var st in any.Inventory.Stacks)
            {
                if (st.IsPlaceholder || s.Items.ContainsKey(st.Item.Id)) continue;
                s.Items[st.Item.Id] = any.Inventory.GetQuantity(st.Item);
                Session.Names.TryAdd(st.Item.Id, st.Item.DisplayName.ToString());
                if (st.Item.SellsFor != null) Session.Values[st.Item.Id] = st.Item.SellsFor.Quantity;
            }
            foreach (var (cur, stack) in any.Wallet.Stacks)
            {
                s.Currency[cur.Id] = stack.Quantity;
                Session.Names.TryAdd(cur.Id, cur.DisplayName.ToString());
            }
        }
        return s;
    }

    // ---- every frame ----

    public void Tick(Game? game, double now)
    {
        if (game == null) { _snap = null; return; }
        if (game.CharacterName != Session.Character) Begin(game);
        Hook(game);

        if (GameAccess.IsComputingOffline) { _offline = true; return; }
        if (_offline)
        {
            // The game just caught up on time away: whatever changed since the last look happened offline.
            _offline = false;
            CatchUp(game, now);
            return;
        }
        if (_fromDisk) { CatchUp(game, now); return; }

        if (now < _next) return;
        _next = now + 1;
        CurrentEntity = game.ActionManager.ActiveActions.FirstOrDefault()?.Entity.Id ?? "";

        Snapshot cur = Take(game);
        if (_snap == null) { _snap = cur; _lastNow = now; return; }
        // Only time the game was really running counts (a stall or a breakpoint does not).
        Session.Seconds += Math.Clamp(now - _lastNow, 0, 5);
        _lastNow = now;
        Session.LastUtc = DateTime.UtcNow;

        foreach (var (id, x) in cur.Xp)
        {
            int level = cur.Levels.TryGetValue(id, out int lv) ? lv : 0;
            SkillLine line = Line(id, level);
            line.Level = level;
            if (_snap.Xp.TryGetValue(id, out double before) && x > before) line.Xp += x - before;
        }
        foreach (var (id, q) in cur.Items)
        {
            long d = q - (_snap.Items.TryGetValue(id, out long b) ? b : 0);
            if (d > 0) Gained(id, d);
            else if (d < 0) Add(Session.Used, id, -d);
        }
        foreach (var (id, b) in _snap.Items)
            if (!cur.Items.ContainsKey(id) && b > 0) Add(Session.Used, id, b);   // the whole stack went
        foreach (var (id, q) in cur.Currency)
        {
            long d = q - (_snap.Currency.TryGetValue(id, out long b) ? b : 0);
            if (d > 0) Add(Session.CurrencyGained, id, d);
            else if (d < 0) Add(Session.CurrencySpent, id, -d);
        }
        lock (s_lock)
        {
            foreach (var (id, n) in s_actions)
            {
                if (!Session.Names.ContainsKey(id) && game.Entities.TryGetObjectById(id, out GameEntity? ent) && ent != null)
                    Session.Names[id] = ent.DisplayName.ToString();
                Line(id, 0).Actions += n;
            }
            s_actions.Clear();
        }
        _snap = cur;
        if (now >= _nextHistory) { _nextHistory = now + HistoryMinutes * 60; History(cur); }

        if (now >= _nextSample)
        {
            _nextSample = now + 15;
            foreach (var (id, line) in Session.Skills)
            {
                if (!_recent.TryGetValue(id, out var q)) _recent[id] = q = new();
                q.Enqueue((now, line.Xp));
                while (q.Count > 41) q.Dequeue();   // 10 minutes of samples
            }
        }
        if (now >= _nextSave) { _nextSave = now + 30; Save(); }
        Changed?.Invoke();
    }

    /// <summary>Whatever changed since the last look happened while away (offline catch-up, or the game was closed).</summary>
    void CatchUp(Game game, double now)
    {
        _fromDisk = false;
        Snapshot after = Take(game);
        if (_snap != null)
        {
            var away = new Away { EndedUtc = DateTime.UtcNow };
            foreach (var (id, x) in after.Xp)
                if (_snap.Xp.TryGetValue(id, out double b) && x > b) away.Xp[id] = x - b;
            foreach (var (id, q) in after.Items)
            {
                long d = q - (_snap.Items.TryGetValue(id, out long b) ? b : 0);
                if (d != 0) away.Items[id] = d;
            }
            foreach (var (id, q) in after.Currency)
            {
                long d = q - (_snap.Currency.TryGetValue(id, out long b) ? b : 0);
                if (d != 0) away.Currency[id] = d;
            }
            if (!away.IsEmpty) Session.LastAway = away;
        }
        _snap = after;
        _lastNow = now;
        _nextHistory = 0;   // record where the time away left things
        Save();
        Changed?.Invoke();
    }

    SkillLine Line(string id, int level)
    {
        if (!Session.Skills.TryGetValue(id, out SkillLine? line))
            Session.Skills[id] = line = new SkillLine { Name = Session.NameOf(id), StartLevel = level, Level = level };
        return line;
    }

    static void Add(Dictionary<string, long> d, string id, long n) => d[id] = (d.TryGetValue(id, out long v) ? v : 0) + n;

    void Gained(string id, long qty)
    {
        bool first = !Session.Gained.ContainsKey(id);
        Add(Session.Gained, id, qty);
        int times = Session.Times[id] = (Session.Times.TryGetValue(id, out int t) ? t : 0) + 1;
        long actions = CurrentEntity.Length > 0 && Session.Skills.TryGetValue(CurrentEntity, out SkillLine? l) ? l.Actions : 0;
        bool rare = actions >= RareActions && (long)times * RareRatio <= actions;
        if (!rare && !first) return;
        string per = Session.NameOf(CurrentEntity) == "Combat" ? "kills" : "actions";
        Session.Drops.Add(new DropEvent
        {
            Time = DateTime.Now, Item = id, Qty = qty, Rare = rare,
            Why = rare ? $"rare: about 1 in {actions / Math.Max(1, times):N0} {per}" : "first this session",
        });
        if (Session.Drops.Count > MaxDrops) Session.Drops.RemoveRange(0, Session.Drops.Count - MaxDrops);
        // Beacon can alert on these; once per item per session so a lucky streak doesn't spam.
        if (rare && s_rareSent.Add(Session.Character + "|" + id))
            Messages.Publish("logbook.rare", $"Rare drop: {Session.NameOf(id)} x{qty:N0} (about 1 in {actions / Math.Max(1, times):N0} {per})");
    }

    static readonly HashSet<string> s_rareSent = new();

    void Hook(Game game)
    {
        if (_hooked == game) return;
        _hooked = game;
        try
        {
            var bus = typeof(Game).GetProperty("EventBus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(game) as GameEventBus;
            bus?.ActionPerformed.Subscribe((sender, e) =>
            {
                // Also raised on the offline thread; those are not part of the live session.
                if (e.IsSubAction || sender is not ActionComponent c || _offline || c.Entity is CombatEntity) return;
                lock (s_lock) s_actions[c.Entity.Id] = (s_actions.TryGetValue(c.Entity.Id, out long n) ? n : 0) + 1;
            });
            // Combat does not report "action performed"; count kills instead.
            CombatEntity? combat = game.Entities.GetAllObjects().OfType<CombatEntity>().FirstOrDefault();
            if (combat != null)
                combat.Enemy.Killed += (_, _) =>
                {
                    if (_offline) return;
                    lock (s_lock) s_actions[combat.Id] = (s_actions.TryGetValue(combat.Id, out long n) ? n : 0) + 1;
                };
        }
        catch (Exception ex) { _log("Action counts unavailable: " + ex.Message); }
    }

    // ---- rates ----

    /// <summary>XP per hour over the last 10 minutes (or the session so far when shorter).</summary>
    public double RecentXpPerHour(string id)
    {
        if (!Session.Skills.TryGetValue(id, out SkillLine? line)) return 0;
        if (_recent.TryGetValue(id, out var q) && q.Count >= 3)
        {
            var (t0, x0) = q.Peek();
            double secs = Time() - t0;
            if (secs > 30) return (line.Xp - x0) / secs * 3600;
        }
        return line.Xp / Session.Hours;
    }

    static double Time() => Godot.Time.GetTicksMsec() / 1000.0;

    /// <summary>Hours to the next level at the recent pace, or null when not leveling.</summary>
    public double? HoursToLevel(Game? game, string id)
    {
        if (game == null || !game.Entities.TryGetObjectById(id, out GameEntity? e) || e == null) return null;
        ExperienceComponent? x = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        double rate = RecentXpPerHour(id);
        if (x == null || x.IsAtLevelCap || rate <= 0) return null;
        return x.XpToNextLevel / rate;
    }

    // ---- long-term history ----

    const int HistoryMinutes = 30;
    static string Local(string id) => id[(id.LastIndexOf(':') + 1)..];

    /// <summary>
    /// One line per half hour of play in history\&lt;character&gt;.jsonl: XP and level per skill, currencies, and the bank's sell value.
    /// Astrolabe Tools charts these (History) and uses them for goal estimates.
    /// </summary>
    void History(Snapshot s)
    {
        if (Session.Character.Length == 0) return;
        try
        {
            long bank = s.Items.Sum(kv => Session.Values.TryGetValue(kv.Key, out long v) ? v * kv.Value : 0);
            var line = new Dictionary<string, object>
            {
                ["t"] = DateTime.UtcNow.ToString("o"),
                ["xp"] = s.Xp.ToDictionary(kv => Local(kv.Key), kv => Math.Round(kv.Value)),
                ["lv"] = s.Levels.ToDictionary(kv => Local(kv.Key), kv => kv.Value),
                ["cur"] = s.Currency.ToDictionary(kv => Local(kv.Key), kv => kv.Value),
                ["bank"] = bank,
                ["items"] = s.Items.Values.Sum(),
            };
            string dir = Path.Combine(_folder, "history");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, Safe(Session.Character) + ".jsonl"), JsonSerializer.Serialize(line) + "\n");
        }
        catch (Exception e) { _log("History: " + e.Message); }
    }

    // ---- sessions on disk ----

    string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    string CurrentFile => Path.Combine(_folder, "current", Safe(Session.Character) + ".json");
    string PastFolder(string character) => Path.Combine(_folder, "sessions", Safe(character));

    /// <summary>A character was loaded: the last session file for it ended when the game closed; keep it and start fresh.</summary>
    void Begin(Game game)
    {
        s_rareSent.Clear();
        if (Session.Character.Length > 0) { Save(); Archive(Session); }
        Session = new Session { Character = game.CharacterName, StartedUtc = DateTime.UtcNow, LastUtc = DateTime.UtcNow };
        Session? last = ReadJson<Session>(CurrentFile);
        _snap = last?.Snap;
        if (last != null) Archive(last);
        _fromDisk = _snap != null;
        _recent.Clear();
        lock (s_lock) s_actions.Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Ends this session (keeps it under Past sessions) and starts a new one.</summary>
    public void Restart()
    {
        Game? game = GameAccess.CurrentGame;
        Save();
        Archive(Session);
        Session = new Session { Character = Session.Character, StartedUtc = DateTime.UtcNow, LastUtc = DateTime.UtcNow };
        _snap = game == null ? null : Take(game);
        _recent.Clear();
        Save();
        Changed?.Invoke();
    }

    void Archive(Session s)
    {
        if (s.Seconds < 60 && s.LastAway == null) return;   // too short to be worth keeping
        try
        {
            string dir = PastFolder(s.Character);
            Directory.CreateDirectory(dir);
            Snapshot? snap = s.Snap;
            s.Snap = null;   // only the live session needs it
            File.WriteAllText(Path.Combine(dir, s.StartedUtc.ToString("yyyyMMdd-HHmmss") + ".json"), JsonSerializer.Serialize(s));
            s.Snap = snap;
            foreach (string old in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).Skip(KeepSessions)) File.Delete(old);
        }
        catch (Exception e) { _log("Could not keep the session: " + e.Message); }
    }

    public List<Session> Past()
    {
        var list = new List<Session>();
        string dir = PastFolder(Session.Character);
        if (!Directory.Exists(dir)) return list;
        foreach (string f in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f))
            if (ReadJson<Session>(f) is Session s) list.Add(s);
        return list;
    }

    public void Save()
    {
        if (Session.Character.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentFile)!);
            if (_snap != null && !_offline) Session.Snap = _snap;
            File.WriteAllText(CurrentFile, JsonSerializer.Serialize(Session));
        }
        catch (Exception e) { _log("Could not save the session: " + e.Message); }
    }

    public void SetOption(Action<Options> set)
    {
        set(Options);
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "options.json"), JsonSerializer.Serialize(Options));
        }
        catch { }
        Changed?.Invoke();
    }

    static T? ReadJson<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null; }
        catch { return null; }
    }
}
