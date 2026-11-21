using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.EventBus;

namespace Astrolabe.ActionQueue;

public enum Goal
{
    UntilStops,   // until the action ends by itself (out of materials, bank full)
    BankCount,    // until the bank holds N of an item
    MoreItems,    // until N more of an item have been added since the step began
    Level,        // until a skill reaches a level
    Minutes,      // for N minutes
    Times,        // until the action has been done N times
}

public sealed class Step
{
    public ActionRef Action { get; set; } = new();
    public Goal Goal { get; set; }
    public int Amount { get; set; }
    public string Skill { get; set; } = "";
    public string SkillName { get; set; } = "";
    public string Item { get; set; } = "";
    public string ItemName { get; set; } = "";
    /// <summary>Side by side: steps in different lanes run at the same time; each lane runs its steps in order.</summary>
    public int Lane { get; set; } = 1;

    // Progress, saved so a game restart picks up where it was.
    public bool Begun { get; set; }
    public bool Done { get; set; }
    public int StartCount { get; set; }
    public long StartTimes { get; set; }
    public DateTime StartedUtc { get; set; }

    [JsonIgnore] public string State { get; set; } = "";
    [JsonIgnore] public bool Waiting { get; set; }
    [JsonIgnore] public double RetryAt { get; set; }
    [JsonIgnore] public double LastRestart { get; set; }
    [JsonIgnore] public int Restarts { get; set; }
    [JsonIgnore] public int Tries { get; set; }

    [JsonIgnore] public ActionRef? Resolved { get; set; }      // "train a skill": the action picked right now
    [JsonIgnore] public double BestCheckAt { get; set; }
    [JsonIgnore] public Dictionary<string, double> FailedUntil { get; } = new();

    [JsonIgnore] public bool IsSell => Action.Entity == Starter.SellEntity;
    [JsonIgnore] public bool IsBest => Action.Target == Starter.BestTarget;
    /// <summary>The action to run: the step's own, or for "train a skill" the one picked for now.</summary>
    [JsonIgnore] public ActionRef Effective => IsBest ? Resolved ?? Action : Action;

    public string GoalText() => Goal switch
    {
        _ when IsSell => $"keep {Amount:N0}, sell the rest",
        Goal.UntilStops => "until it stops by itself",
        Goal.Level => $"until {SkillName} level {Amount}",
        Goal.BankCount => $"until {Amount:N0} {ItemName} in the bank",
        Goal.MoreItems => $"until {Amount:N0} more {ItemName}",
        Goal.Minutes => $"for {Amount} minute{(Amount == 1 ? "" : "s")}",
        Goal.Times => $"{Amount:N0} time{(Amount == 1 ? "" : "s")}",
        _ => "",
    };

    public Step Copy() => new()
    {
        Action = Action, Goal = Goal, Amount = Amount, Skill = Skill, SkillName = SkillName, Item = Item, ItemName = ItemName, Lane = Lane,
    };

    public void Reset()
    {
        Begun = Done = Waiting = false;
        State = "";
        Restarts = Tries = 0;
        RetryAt = LastRestart = BestCheckAt = 0;
        Resolved = null;
        FailedUntil.Clear();
    }
}

public sealed class QueueState
{
    public string Name { get; set; } = "";
    public List<Step> Steps { get; set; } = new();
    public bool Running { get; set; }
    public bool Loop { get; set; }
    /// <summary>How many lanes run at once (1 = one step at a time).</summary>
    public int Lanes { get; set; } = 1;
    /// <summary>Done while every lane waits for materials and after the queue ends. Null: leave the game as it is.</summary>
    public ActionRef? Idle { get; set; }

    public bool Alerts { get; set; } = true;      // sound and taskbar flash when it finishes or gets stuck
    public bool FoodGuard { get; set; } = true;   // stop a combat step when the selected food runs out
    public bool BankGuard { get; set; } = true;   // pause when the bank is full
    public bool Hud { get; set; } = true;         // show the current step in a corner of the screen
}

public sealed record HistoryEntry(DateTime Time, string Text);

/// <summary>Runs the steps, lane by lane. Checked twice a second from the loader's frame event.</summary>
sealed class Queue
{
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    const double RetryWait = 20;   // seconds between tries while a step waits

    readonly string _folder;
    readonly Action<string> _log;
    string _character = "";
    double _nextCheck;
    Game? _game;

    // Finished actions per skill entity, counted from the game's own "action performed" event (Times goal).
    static readonly Dictionary<string, long> s_performed = new();

    public QueueState State { get; private set; } = new();
    public string Status { get; private set; } = "Not running";
    public event Action? Changed;
    /// <summary>Something worth a sound: finished, a step skipped, the bank full.</summary>
    public event Action<string>? Alert;

    public List<HistoryEntry> History { get; } = new();
    string HistoryFile => Path.Combine(_folder, "queues", Safe(_character) + ".history.txt");

    /// <summary>Writes a line to the history (shown in the History tab, kept on disk) and the loader log.</summary>
    public void Note(string text)
    {
        _log(text);
        var e = new HistoryEntry(DateTime.Now, text);
        History.Add(e);
        if (History.Count > 500) History.RemoveRange(0, History.Count - 500);
        if (!HasCharacter) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.AppendAllText(HistoryFile, $"{e.Time:yyyy-MM-dd HH:mm:ss}\t{text}{Environment.NewLine}");
        }
        catch { }
    }

    public void ClearHistory()
    {
        History.Clear();
        try { File.Delete(HistoryFile); } catch { }
        Changed?.Invoke();
    }

    void LoadHistory()
    {
        History.Clear();
        try
        {
            if (!File.Exists(HistoryFile)) return;
            foreach (string line in File.ReadLines(HistoryFile).TakeLast(500))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && DateTime.TryParse(line[..tab], out DateTime t)) History.Add(new HistoryEntry(t, line[(tab + 1)..]));
            }
        }
        catch { }
    }

    void Raise(string text)
    {
        Note(text);
        if (State.Alerts) Alert?.Invoke(text);
    }

    public Queue(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
    }

    public bool HasCharacter => _character.Length > 0;
    public string Folder => _folder;
    public void Log(string message) => _log(message);

    /// <summary>The step each lane is on, in lane order.</summary>
    public IEnumerable<Step> CurrentSteps() =>
        State.Steps.Where(s => !s.Done).GroupBy(LaneOf).OrderBy(g => g.Key).Select(g => g.First());

    /// <summary>The lane a step runs in now (steps in lanes above the lane count run in the last lane).</summary>
    public int LaneOf(Step s) => Math.Clamp(s.Lane, 1, Math.Max(1, State.Lanes));

    // ---- files ----

    static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    string LiveFile => Path.Combine(_folder, "queues", Safe(_character) + ".json");
    string SavedFolder => Path.Combine(_folder, "saved");

    void LoadFor(string character)
    {
        _character = character;
        State = Read(LiveFile) ?? new QueueState();
        foreach (Step s in State.Steps) if (s.Lane < 1) s.Lane = 1;
        if (State.Lanes < 1) State.Lanes = 1;
        LoadHistory();
        Status = State.Running ? "Resuming" : "Not running";
        Changed?.Invoke();
    }

    QueueState? Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<QueueState>(File.ReadAllText(file), s_json) : null; }
        catch (Exception e) { _log($"Could not read {Path.GetFileName(file)}: {e.Message}"); return null; }
    }

    void Write(string file, QueueState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(state, s_json));
        }
        catch (Exception e) { _log($"Could not save {Path.GetFileName(file)}: {e.Message}"); }
    }

    public void Save() { if (HasCharacter) Write(LiveFile, State); }

    public IEnumerable<string> SavedNames() =>
        Directory.Exists(SavedFolder)
            ? Directory.EnumerateFiles(SavedFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n)
            : Enumerable.Empty<string>();

    public void SaveAs(string name)
    {
        name = Safe(name);
        if (name.Length == 0) return;
        Edit(() => State.Name = name);
        Write(Path.Combine(SavedFolder, name + ".json"),
            new QueueState { Name = name, Loop = State.Loop, Lanes = State.Lanes, Idle = State.Idle, Steps = State.Steps.Select(s => s.Copy()).ToList() });
    }

    public void LoadSaved(string name)
    {
        QueueState? q = Read(Path.Combine(SavedFolder, Safe(name) + ".json"));
        if (q == null) return;
        Edit(() =>
        {
            State = new QueueState { Name = q.Name, Loop = q.Loop, Lanes = Math.Max(1, q.Lanes), Idle = q.Idle, Steps = q.Steps.Select(s => s.Copy()).ToList() };
            Status = $"Loaded \"{q.Name}\"";
        });
    }

    public void DeleteSaved(string name)
    {
        try { File.Delete(Path.Combine(SavedFolder, Safe(name) + ".json")); } catch { }
        if (State.Name == name) Edit(() => State.Name = ""); else Changed?.Invoke();
    }

    void Edit(Action change)
    {
        change();
        Save();
        Changed?.Invoke();
    }

    // ---- editing ----

    public void Add(Step s) => Edit(() => State.Steps.Add(s));
    public void AddRange(IEnumerable<Step> steps) => Edit(() => State.Steps.AddRange(steps));

    public void Remove(int i) => Edit(() =>
    {
        if (i < 0 || i >= State.Steps.Count) return;
        State.Steps.RemoveAt(i);
        if (State.Steps.Count == 0) State.Running = false;
    });

    public void Move(int i, int by) => Edit(() =>
    {
        int j = i + by;
        if (i < 0 || j < 0 || i >= State.Steps.Count || j >= State.Steps.Count) return;
        (State.Steps[i], State.Steps[j]) = (State.Steps[j], State.Steps[i]);
    });

    public void MoveTo(int from, int to) => Edit(() =>
    {
        if (from < 0 || from >= State.Steps.Count) return;
        Step s = State.Steps[from];
        State.Steps.RemoveAt(from);
        State.Steps.Insert(Math.Clamp(to, 0, State.Steps.Count), s);
    });

    public void Replace(int i, Step s) => Edit(() => { if (i >= 0 && i < State.Steps.Count) State.Steps[i] = s; });

    public void SetOption(Action<QueueState> set) => Edit(() => set(State));

    public void SetLane(int i, int lane) => Edit(() => { if (i >= 0 && i < State.Steps.Count) State.Steps[i].Lane = Math.Clamp(lane, 1, 4); });

    public void Clear() => Edit(() => { State.Steps.Clear(); State.Running = false; State.Name = ""; Status = "Not running"; });
    public void SetLoop(bool on) => Edit(() => State.Loop = on);
    public void SetIdle(ActionRef? a) => Edit(() => State.Idle = a);
    public void SetLanes(int n) => Edit(() => State.Lanes = Math.Clamp(n, 1, 4));

    /// <summary>Starts the queue; with <paramref name="from"/>, steps above it count as done.</summary>
    public void Start(int from = -1) => Edit(() =>
    {
        if (State.Steps.Count == 0) return;
        for (int i = 0; i < State.Steps.Count; i++)
        {
            State.Steps[i].Reset();
            if (from >= 0 && i < from) State.Steps[i].Done = true;
        }
        State.Running = true;
        Status = "Starting";
        Note(from > 0 ? $"Queue started from step {from + 1}" : "Queue started");
    });

    public void Pause() => Edit(() => { State.Running = false; Status = "Paused"; foreach (Step s in State.Steps) s.Waiting = false; Note("Queue paused"); });

    // ---- reading the game ----

    public static int Level(Game game, string skillId)
    {
        if (!game.Entities.TryGetObjectById(skillId, out GameEntity? e) || e == null) return 0;
        return e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault()?.Level ?? 0;
    }

    public static int BankCount(Game game, string itemId)
    {
        ActionComponent? any = Catalog.AnyAction(game);
        if (any == null) return 0;
        Item? item = Catalog.FindItem(game, itemId);
        if (item != null) return any.Inventory.GetQuantity(item);
        foreach (var stack in any.Inventory.Stacks)
            if (stack.Item.Id == itemId) return any.Inventory.GetQuantity(stack.Item);
        return 0;
    }

    static long Performed(string entityId)
    {
        lock (s_performed) return s_performed.TryGetValue(entityId, out long n) ? n : 0;
    }

    void Hook(Game game)
    {
        if (_game == game) return;
        _game = game;
        try
        {
            var bus = typeof(Game).GetProperty("EventBus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(game) as GameEventBus;
            bus?.ActionPerformed.Subscribe((sender, e) =>
            {
                // Raised for every finished action (also on the offline thread); sub-steps like hunting's tracking are skipped.
                if (e.IsSubAction || sender is not ActionComponent c) return;
                lock (s_performed) s_performed[c.Entity.Id] = (s_performed.TryGetValue(c.Entity.Id, out long n) ? n : 0) + 1;
            });
        }
        catch (Exception ex) { _log("Times goal unavailable: " + ex.Message); }
    }

    static int Count(Game game, Step s) => s.Goal is Goal.BankCount or Goal.MoreItems ? BankCount(game, s.Item) : 0;

    /// <summary>How far the step is, e.g. "312 / 1,006".</summary>
    public string Progress(Game? game, Step s)
    {
        if (game == null || s.IsSell) return "";
        switch (s.Goal)
        {
            case Goal.Level: return $"level {Level(game, s.Skill)} / {s.Amount}";
            case Goal.BankCount:
                {
                    int n = BankCount(game, s.Item);
                    return $"{n:N0} / {s.Amount:N0}{Eta(s, n - s.StartCount, s.Amount - n)}";
                }
            case Goal.MoreItems:
                {
                    int got = s.Begun ? BankCount(game, s.Item) - s.StartCount : 0;
                    return $"{got:N0} / {s.Amount:N0}{Eta(s, got, s.Amount - got)}";
                }
            case Goal.Times: return $"{(s.Begun ? Performed(s.Action.Entity) - s.StartTimes : 0):N0} / {s.Amount:N0}";
            case Goal.Minutes:
                if (!s.Begun) return $"{s.Amount} min";
                double left = s.Amount - (DateTime.UtcNow - s.StartedUtc).TotalMinutes;
                return left > 0 ? $"{Math.Ceiling(left)} min left" : "done";
            default: return "";
        }
    }

    // "  ~12 min left", from the pace since the step began (shown after a minute, once the pace means something).
    static string Eta(Step s, int gained, int left)
    {
        if (!s.Begun || s.Done || gained <= 0 || left <= 0) return "";
        double minutes = (DateTime.UtcNow - s.StartedUtc).TotalMinutes;
        if (minutes < 1) return "";
        double eta = left / (gained / minutes);
        return eta < 1 ? "  ~1 min left" : eta < 120 ? $"  ~{Math.Ceiling(eta)} min left" : $"  ~{eta / 60:0.#} h left";
    }

    static bool Reached(Game game, Step s) => s.Goal switch
    {
        Goal.Level => Level(game, s.Skill) >= s.Amount,
        Goal.BankCount => BankCount(game, s.Item) >= s.Amount,
        Goal.MoreItems => s.Begun && BankCount(game, s.Item) - s.StartCount >= s.Amount,
        Goal.Minutes => s.Begun && (DateTime.UtcNow - s.StartedUtc).TotalMinutes >= s.Amount,
        Goal.Times => s.Begun && Performed(s.Action.Entity) - s.StartTimes >= s.Amount,
        _ => false,
    };

    // ---- running ----

    bool _bankFullNoted;

    public void Tick(Game? game, double now)
    {
        if (game == null) { _character = ""; return; }
        Hook(game);
        if (game.CharacterName != _character) LoadFor(game.CharacterName);
        if (!State.Running || now < _nextCheck) return;
        _nextCheck = now + 0.5;

        if (State.BankGuard && BankFull(game))
        {
            if (!_bankFullNoted)
            {
                _bankFullNoted = true;
                State.Running = false;
                Status = "Paused: the bank is full";
                Raise("Paused: the bank is full. Make room (or add a Sell step) and press Start.");
                Save();
                Changed?.Invoke();
            }
            return;
        }
        _bankFullNoted = false;

        List<Step> current = CurrentSteps().ToList();
        if (current.Count == 0) { Finish(game); return; }

        bool alongside = State.Lanes > 1 && current.Count > 1;
        bool changed = false;
        foreach (Step s in current) changed |= Run(game, s, now, alongside);

        // When every lane is waiting, the idle action uses the free time.
        List<Step> after = CurrentSteps().ToList();
        if (after.Count > 0 && after.All(s => s.Waiting) && State.Idle != null && !Starter.IsRunning(game, State.Idle))
            Starter.Start(game, State.Idle);

        Status = string.Join("  +  ", after.Select(s => $"{Label(s)}: {(s.State.Length > 0 ? s.State : "starting")}"));
        if (changed) { Save(); Changed?.Invoke(); }
    }

    /// <summary>"Woodcutting: Mahogany Tree", or for a train-a-skill step "Train Woodcutting (Mahogany Tree)".</summary>
    public static string Label(Step s) =>
        s.IsBest ? s.Resolved != null ? $"{s.Action.Label} ({s.Resolved.Label[(s.Resolved.Label.IndexOf(':') + 1)..].Trim()})" : s.Action.Label : s.Action.Label;

    static bool BankFull(Game game)
    {
        ActionComponent? any = Catalog.AnyAction(game);
        return any != null && any.Inventory.MaxStacks > 0 && any.Inventory.StackCount >= any.Inventory.MaxStacks;
    }

    // One step's turn. Returns true when something visible changed.
    bool Run(Game game, Step s, double now, bool alongside)
    {
        int index = State.Steps.IndexOf(s);
        string StartIt(ActionRef r) => alongside ? Starter.StartAlongside(game, r) : Starter.Start(game, r);

        if (s.IsSell)
        {
            string sold = Starter.Sell(game, s.Item, s.Amount);
            if (sold.Length == 0) return Done(s, $"Sold {s.ItemName} down to {s.Amount:N0}", "done");
            return Skip(s, $"Sell {s.ItemName}: {sold}");
        }

        if (!s.Begun && s.Goal is Goal.Level or Goal.BankCount && Reached(game, s))
            return Done(s, $"{s.Action.Label}: goal already met", "skipped");

        // Train a skill: every 10 seconds, look for a better action than the one running (new unlocks as you level).
        bool switched = false;
        if (s.IsBest && now >= s.BestCheckAt)
        {
            s.BestCheckAt = now + 10;
            ActionRef? top = Starter.BestFor(game, s.Action.Entity)
                .FirstOrDefault(c => !s.FailedUntil.TryGetValue(c.Target, out double until) || now >= until);
            if (top != null && (s.Resolved == null || s.Resolved.Target != top.Target))
            {
                if (s.Resolved != null) Note($"{s.Action.Label}: switching to {top.Label}");
                s.Resolved = top;
                switched = true;
                if (s.Begun) { s.Waiting = true; s.RetryAt = 0; }
            }
            if (top == null && s.Resolved == null) { s.Waiting = true; s.State = "waiting: nothing to train yet"; }
        }
        ActionRef act = s.Effective;
        if (s.IsBest && s.Resolved == null) return switched;

        if (!s.Begun || s.Waiting)
        {
            if (now < s.RetryAt) return switched;
            string why = StartIt(act);
            if (why.Length > 0)
            {
                if (s.IsBest)
                {
                    // That action cannot run (usually materials); try the next best one right away.
                    s.FailedUntil[act.Target] = now + 120;
                    s.Resolved = null;
                    s.BestCheckAt = 0;
                    if (Starter.BestFor(game, s.Action.Entity).Any(c => !s.FailedUntil.TryGetValue(c.Target, out double u) || now >= u)) return true;
                }
                // Cannot start (usually missing materials or a level). Wait and retry; give up after about 10 minutes,
                // or after a minute when nothing else could use the time.
                s.Tries++;
                if (s.Tries > 30 || (State.Idle == null && State.Lanes == 1 && s.Tries > 3))
                    return Skip(s, $"{Label(s)}: {why}");
                s.Waiting = true;
                s.State = $"waiting: {why}";
                s.RetryAt = now + RetryWait;
                return true;
            }
            if (!s.Begun)
            {
                s.Begun = true;
                s.StartedUtc = DateTime.UtcNow;
                s.StartCount = Count(game, s);
                s.StartTimes = Performed(s.Action.Entity);
                Note($"Step {index + 1}: {Label(s)}, {s.GoalText()}");
            }
            s.Waiting = false;
            s.Restarts = s.Tries = 0;
            s.State = "running";
            return true;
        }

        if (Reached(game, s)) return Done(s, $"{Label(s)}: done ({s.GoalText()})", "done");

        // Combat: stop before running out of food leaves the character fighting with nothing to eat.
        if (State.FoodGuard && OutOfFood(game, act))
        {
            Starter.Component(game, act)?.TryStop(true);
            return Skip(s, $"{Label(s)}: stopped, out of food");
        }

        if (!Starter.IsRunning(game, act))
        {
            if (s.Goal == Goal.UntilStops) return Done(s, $"{Label(s)}: stopped by itself", "done");

            // It ended early (out of materials, or something else took over). Restart a few times, then wait.
            if (now - s.LastRestart > 60) s.Restarts = 0;
            s.LastRestart = now;
            string why = ++s.Restarts <= 3 ? StartIt(act) : "stopped too often";
            if (why.Length > 0) { s.Waiting = true; s.RetryAt = 0; s.Tries = 0; s.State = $"waiting: {why}"; return true; }
        }
        return switched;
    }

    static bool OutOfFood(Game game, ActionRef act)
    {
        ActionComponent? comp = Starter.Component(game, act);
        if (comp is not GamesByMalcs.Melvor2.Entities.Combat.CombatActionComponent) return false;
        var food = comp.Entity.Components.Values.OfType<GamesByMalcs.Melvor2.Components.CombatFood.FoodComponent>().FirstOrDefault();
        if (food == null) return false;
        var selected = food.SelectedFood;
        return selected == null || food.Inventory.GetQuantity(selected.Item) <= 0;
    }

    bool Done(Step s, string note, string how)
    {
        Note(note);
        s.Done = true;
        s.Waiting = false;
        s.State = how;
        return true;
    }

    bool Skip(Step s, string note)
    {
        Raise("Skipped " + note);
        s.Done = true;
        s.Waiting = false;
        s.State = "skipped";
        return true;
    }

    void Finish(Game game)
    {
        if (State.Loop && State.Steps.Count > 0)
        {
            foreach (Step x in State.Steps) x.Reset();
            Status = "Starting over";
            Note("Queue finished; starting over");
            Save();
            Changed?.Invoke();
            return;
        }
        State.Running = false;
        Status = "Finished";
        if (State.Idle != null) { Starter.Start(game, State.Idle); Status += $"; now {State.Idle.Label}"; }
        Raise("Queue finished" + (State.Idle != null ? $"; now {State.Idle.Label}" : ""));
        Save();
        Changed?.Invoke();
    }
}
