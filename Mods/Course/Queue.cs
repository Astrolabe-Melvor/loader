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
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

public enum Goal
{
    UntilStops,   // until the action ends by itself (out of materials, bank full)
    BankCount,    // until the bank holds N of an item
    MoreItems,    // until N more of an item have been added since the step began
    Level,        // until a skill reaches a level
    Minutes,      // for N minutes
    Times,        // until the action has been done N times
    Gold,         // until you have N GP (Most GP steps)
    Technique,    // until an Agility technique (Item) reaches level N
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
    /// <summary>Loadout to wear: "" = the skill's gear set from Options, "*keep" = leave it, else a game loadout id.</summary>
    public string Gear { get; set; } = "";
    /// <summary>Combat only. Weapon: "" = the loadout's own, "*best" = best you own against the monster, else an item id.</summary>
    public string Weapon { get; set; } = "";
    /// <summary>Combat only. Stance: "" = leave as is, "*best" = best for the weapon, else a stance id (Slash, Crush...).</summary>
    public string Stance { get; set; } = "";
    /// <summary>Skills: a toolbelt set (its id) to put on when the step starts; "" = leave the toolbelt as it is.</summary>
    public string Belt { get; set; } = "";

    // Progress, saved so a game restart picks up where it was.
    public bool Begun { get; set; }
    public bool Done { get; set; }
    /// <summary>Set by "Run from here": the queue went past it without running it. Still counts as behind the queue.</summary>
    public bool PassedOver { get; set; }
    /// <summary>It was running (or had run) when "Run from here" passed over it: stopped early, not "not run".</summary>
    public bool PassedAfterStart { get; set; }
    public int StartCount { get; set; }
    public long StartTimes { get; set; }
    public DateTime StartedUtc { get; set; }

    /// <summary>Reached its goal (not skipped, not passed over by "Run from here").</summary>
    [JsonIgnore] public bool Finished { get; set; }
    [JsonIgnore] public string State { get; set; } = "";
    [JsonIgnore] public bool Waiting { get; set; }
    [JsonIgnore] public double RetryAt { get; set; }
    [JsonIgnore] public double BeltCheckAt { get; set; }
    [JsonIgnore] public double RefillWindowStart { get; set; }
    [JsonIgnore] public int Refills { get; set; }
    [JsonIgnore] public double LastRestart { get; set; }
    [JsonIgnore] public int Restarts { get; set; }
    [JsonIgnore] public int Tries { get; set; }

    [JsonIgnore] public ActionRef? Resolved { get; set; }      // "train a skill": the action picked right now
    [JsonIgnore] public double BestCheckAt { get; set; }
    [JsonIgnore] public Dictionary<string, double> FailedUntil { get; } = new();

    [JsonIgnore] public bool IsSell => Action.Entity == Starter.SellEntity;
    /// <summary>Buy from the shop (Action.Target is the purchase id) until the bank holds Amount of Item.</summary>
    [JsonIgnore] public bool IsShop => Action.Entity == ShopSource.Entity;
    /// <summary>Fight slayer tasks, master after master (Action.Entity is the combat skill).</summary>
    [JsonIgnore] public bool IsSlayer => Action.Target == Slayer.Target;
    [JsonIgnore] public bool IsBest => Action.Target == Starter.BestTarget || Action.Target == Starter.GoldTarget || IsTech;
    /// <summary>Train an Agility technique: the course and path giving it the most XP, switching as it levels.</summary>
    [JsonIgnore] public bool IsTech => Action.Target.StartsWith(Finesse.TechPrefix);
    [JsonIgnore] public bool IsGold => Action.Target == Starter.GoldTarget;
    /// <summary>Most GP steps: sell what the action makes as it comes in (never locked bank stacks).</summary>
    public bool SellOutput { get; set; } = true;
    /// <summary>The action to run: the step's own, or for "train a skill" the one picked for now.</summary>
    [JsonIgnore] public ActionRef Effective => IsBest || IsSlayer ? Resolved ?? Action : Action;
    /// <summary>What the Times goal counts: finished tasks for a Slayer tasks step, else the skill's actions (kills in combat).</summary>
    [JsonIgnore] public string CountKey => IsSlayer ? Slayer.DoneKey : Action.Entity;

    public string GoalText() => Goal switch
    {
        _ when IsSell => $"keep {Amount:N0}, sell the rest",
        Goal.UntilStops => "until it stops by itself",
        Goal.Level => $"until {SkillName} level {Amount}",
        Goal.BankCount => $"until {Amount:N0} {ItemName} in the bank",
        Goal.MoreItems => $"until {Amount:N0} more {ItemName}",
        Goal.Minutes => $"for {Amount} minute{(Amount == 1 ? "" : "s")}",
        Goal.Times when IsSlayer => $"{Amount:N0} task{(Amount == 1 ? "" : "s")} done",
        Goal.Times => $"{Amount:N0} time{(Amount == 1 ? "" : "s")}",
        Goal.Gold => $"until you have {Amount:N0} GP",
        Goal.Technique => $"until {ItemName} reaches level {Amount}",
        _ => "",
    };

    public Step Copy() => new()
    {
        Action = Action, Goal = Goal, Amount = Amount, Skill = Skill, SkillName = SkillName, Item = Item, ItemName = ItemName, Gear = Gear, Weapon = Weapon, Stance = Stance, Belt = Belt, SellOutput = SellOutput,
    };

    public void Reset()
    {
        Begun = Done = PassedOver = PassedAfterStart = Waiting = Finished = false;
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
    /// <summary>Done while a step waits for materials and after the queue ends. Null: leave the game as it is.</summary>
    public ActionRef? Idle { get; set; }

    public bool Alerts { get; set; } = true;      // sound and taskbar flash when it finishes or gets stuck
    public bool FoodGuard { get; set; } = true;   // stop a combat step when the selected food runs out
    public bool BankGuard { get; set; } = true;   // pause when the bank is full
    public bool Hud { get; set; } = true;         // show the current step in a corner of the screen
    public bool PageButton { get; set; } = true;   // a Queue button on the game's skill pages
    public bool RemoveFinished { get; set; }      // take steps off the list once they reach their goal (not while looping)
    public float HudX { get; set; } = -1;          // panel position, -1 = default spot (top-left, beside the logo)
    public float HudY { get; set; } = -1;
    public bool HudMin { get; set; }              // folded to one line
    public bool AutoGear { get; set; } = true;    // swap loadouts when a combat step starts
    /// <summary>Entity id to game loadout id; only the combat entity is used (steps left on "combat default").</summary>
    public Dictionary<string, string> SkillGear { get; set; } = new();
    /// <summary>Loadout id to the weapon it held before a step swapped it ("" = none), so it can be put back.</summary>
    public Dictionary<string, string> WeaponRestore { get; set; } = new();
    /// <summary>Same for the offhand (a two-handed weapon pushes it into the bank).</summary>
    public Dictionary<string, string> OffhandRestore { get; set; } = new();
    /// <summary>"loadout|slot" to the item that slot held before a step changed it (the slayer gauntlet's gloves).</summary>
    public Dictionary<string, string> SlotRestore { get; set; } = new();
    public bool SlayerGauntlet { get; set; } = true;   // wear a slayer gauntlet when the monster is a slayer task
    /// <summary>Monsters (enemy ids) to reroll whenever a master gives them as a task.</summary>
    public HashSet<string> SlayerSkip { get; set; } = new();
    /// <summary>Masters (ids) whose tasks are extended as soon as they are given.</summary>
    public HashSet<string> SlayerExtend { get; set; } = new();
    /// <summary>Rerolls and extensions never take slayer coins below this.</summary>
    public long SlayerReserve { get; set; }
    /// <summary>Reroll tasks in areas you can't enter yet.</summary>
    public bool SlayerRerollLocked { get; set; }
    /// <summary>Apply the reroll and extend rules all the time, not only while a Slayer tasks step runs.</summary>
    public bool SlayerAlways { get; set; }
    /// <summary>Thieving and Agility steps (with no toolbelt set of their own) wear the best gear you own for them.</summary>
    public bool FinesseGear { get; set; } = true;
    /// <summary>While thieving or doing agility: keep the skill's buff food selected, and eat healing food when HP gets low.</summary>
    public bool FinesseFood { get; set; } = true;
    /// <summary>Also heal when HP drops below this percent (always when one failure could take the rest).</summary>
    public int FinesseFloor { get; set; } = 30;
    /// <summary>Toolbelt sets per skill, made in the Toolbelts tab.</summary>
    public List<BeltSet> Belts { get; set; } = new();
}

public sealed record HistoryEntry(DateTime Time, string Text);

/// <summary>Runs the steps one at a time, in order. Checked twice a second from the loader's frame event.</summary>
sealed class Queue
{
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    const double RetryWait = 20;   // seconds between tries while a step waits

    readonly string _folder;
    readonly Action<string> _log;
    string _character = "";
    double _nextCheck;
    Game? _game;

    // ---- while the game is closed ----
    // The game catches up on time away by running its ticks on a worker thread with the screen paused. A tick
    // listener (OfflineTicker) runs the queue then too, every two game seconds, on that thread. Anything that would
    // touch the screen (alerts, window refreshes) waits until the game is back; times use the game time being
    // simulated, so "for 30 minutes" and history times come out right.
    int _mainThread = -1;   // the thread frames run on; set from the first frame or the game screen appearing
    readonly object _gate = new();
    bool _offline, _wasOffline, _hadOffline, _pendingChanged;
    readonly List<string> _pendingAlerts = new();
    DateTime _virtualBase, _virtualUtc, _lastOnlineUtc;
    double _simSeconds, _nextOfflineRun, _lastNow;
    int _offlineSteps;
    Game? _tickerFor;

    DateTime UtcNow => _offline ? _virtualUtc : DateTime.UtcNow;

    void Notify()
    {
        if (_offline) _pendingChanged = true;
        else Changed?.Invoke();
    }

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
        var e = new HistoryEntry(_offline ? _virtualUtc.ToLocalTime() : DateTime.Now, text);
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
        Notify();
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

    public void Raise(string text)
    {
        Note(text);
        if (_offline) { _pendingAlerts.Add(text); return; }   // sounds and Beacon once the game is back
        if (State.Alerts) Alert?.Invoke(text);
        Messages.Publish("course.alert", text);   // Beacon can show it or post it to Discord
    }

    public Queue(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
    }

    public bool HasCharacter => _character.Length > 0;
    public string Folder => _folder;
    public void Log(string message) => _log(message);

    /// <summary>The step the queue is on (the game runs one action at a time).</summary>
    public IEnumerable<Step> CurrentSteps() => State.Steps.Where(s => !s.Done).Take(1);

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
        LoadHistory();
        Status = State.Running ? "Resuming" : "Not running";
        Notify();
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
            new QueueState { Name = name, Loop = State.Loop, Idle = State.Idle, Steps = State.Steps.Select(s => s.Copy()).ToList() });
    }

    public void LoadSaved(string name)
    {
        QueueState? q = Read(Path.Combine(SavedFolder, Safe(name) + ".json"));
        if (q == null) return;
        Edit(() =>
        {
            QueueState o = State; // options and gear sets belong to the character, not the saved queue
            State = new QueueState
            {
                Name = q.Name, Loop = q.Loop, Idle = q.Idle, Steps = q.Steps.Select(s => s.Copy()).ToList(),
                Alerts = o.Alerts, FoodGuard = o.FoodGuard, BankGuard = o.BankGuard, Hud = o.Hud, AutoGear = o.AutoGear, SkillGear = o.SkillGear, WeaponRestore = o.WeaponRestore, OffhandRestore = o.OffhandRestore, SlotRestore = o.SlotRestore, SlayerGauntlet = o.SlayerGauntlet, Belts = o.Belts,
                SlayerSkip = o.SlayerSkip, SlayerExtend = o.SlayerExtend, SlayerReserve = o.SlayerReserve, SlayerRerollLocked = o.SlayerRerollLocked, SlayerAlways = o.SlayerAlways, FinesseGear = o.FinesseGear, FinesseFood = o.FinesseFood, FinesseFloor = o.FinesseFloor, HudX = o.HudX, HudY = o.HudY, HudMin = o.HudMin,
            };
            Status = $"Loaded \"{q.Name}\"";
        });
    }

    public void DeleteSaved(string name)
    {
        try { File.Delete(Path.Combine(SavedFolder, Safe(name) + ".json")); } catch { }
        if (State.Name == name) Edit(() => State.Name = ""); else Notify();
    }

    void Edit(Action change)
    {
        change();
        Save();
        Notify();
    }

    readonly List<(string Steps, string What)> _undo = new();

    /// <summary>What Undo would put back, or "" when there is nothing to undo.</summary>
    public string UndoWhat => _undo.Count == 0 ? "" : _undo[^1].What;

    /// <summary>A change to the step list that Undo can take back.</summary>
    void Change(string what, Action change)
    {
        _undo.Add((JsonSerializer.Serialize(State.Steps), what));
        if (_undo.Count > 30) _undo.RemoveAt(0);
        Edit(() => { change(); Settle(); });
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (json, what) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        List<Step>? steps = null;
        try { steps = JsonSerializer.Deserialize<List<Step>>(json); } catch { }
        if (steps == null) return;
        Edit(() =>
        {
            State.Steps = steps;
            if (steps.Count == 0) State.Running = false;
            foreach (Step st in steps.Where(x => x.Begun && !x.Done)) { st.Waiting = true; st.RetryAt = 0; }
            Settle();
            Note($"Undid: {what}");
        });
    }

    /// <summary>After the order changes: a step that was going but is no longer first shows as paused, and starts
    /// again properly (gear, toolbelt) when its turn comes back.</summary>
    void Settle()
    {
        var current = CurrentSteps().ToHashSet();
        foreach (Step st in State.Steps)
        {
            if (st.Done || current.Contains(st)) continue;
            if (st.Begun) { st.Waiting = true; st.RetryAt = 0; st.State = "paused: a step above runs first"; }
            else st.State = "";
        }
    }

    // ---- editing ----

    public void Add(Step s) => Change("add a step", () => State.Steps.Add(s));
    public void AddRange(IEnumerable<Step> steps) => Change("add steps", () => State.Steps.AddRange(steps));

    public void Remove(int i) => Change("remove a step", () =>
    {
        if (i < 0 || i >= State.Steps.Count) return;
        State.Steps.RemoveAt(i);
        if (State.Steps.Count == 0) State.Running = false;
    });

    public void Move(int i, int by) => Change("move a step", () =>
    {
        int j = i + by;
        if (i < 0 || j < 0 || i >= State.Steps.Count || j >= State.Steps.Count) return;
        (State.Steps[i], State.Steps[j]) = (State.Steps[j], State.Steps[i]);
    });

    /// <summary>Several steps at once, by index.</summary>
    public void RemoveMany(IReadOnlyCollection<int> rows) => Change(rows.Count == 1 ? "remove a step" : $"remove {rows.Count} steps", () =>
    {
        foreach (int i in rows.Where(i => i >= 0 && i < State.Steps.Count).OrderByDescending(i => i)) State.Steps.RemoveAt(i);
        if (State.Steps.Count == 0) State.Running = false;
    });

    public void RemoveDone() => Change("remove finished steps", () => State.Steps.RemoveAll(x => x.Done && !x.PassedOver));

    /// <summary>Moves the steps (kept in their order) so the first lands at <paramref name="to"/> in the list without them.
    /// Returns where they ended up.</summary>
    public List<int> MoveManyTo(IReadOnlyCollection<int> rows, int to)
    {
        var picked = rows.Where(i => i >= 0 && i < State.Steps.Count).Distinct().OrderBy(i => i).ToList();
        var result = new List<int>();
        if (picked.Count == 0) return result;
        Change(picked.Count == 1 ? "move a step" : $"move {picked.Count} steps", () =>
        {
            var steps = picked.Select(i => State.Steps[i]).ToList();
            foreach (int i in Enumerable.Reverse(picked)) State.Steps.RemoveAt(i);
            int at = Math.Clamp(to, 0, State.Steps.Count);
            State.Steps.InsertRange(at, steps);
            result.AddRange(Enumerable.Range(at, steps.Count));
        });
        return result;
    }

    /// <summary>Up (-1) or down (+1) one place, as a block.</summary>
    public List<int> MoveMany(IReadOnlyCollection<int> rows, int by)
    {
        var picked = rows.Where(i => i >= 0 && i < State.Steps.Count).OrderBy(i => i).ToList();
        if (picked.Count == 0) return picked;
        int first = picked[0];
        int to = Math.Clamp(first + by, 0, State.Steps.Count - picked.Count);
        if (to == first && picked[^1] - first == picked.Count - 1) return picked;
        return MoveManyTo(picked, to);
    }

    public void MoveTo(int from, int to) => Change("move a step", () =>
    {
        if (from < 0 || from >= State.Steps.Count) return;
        Step s = State.Steps[from];
        State.Steps.RemoveAt(from);
        State.Steps.Insert(Math.Clamp(to, 0, State.Steps.Count), s);
    });

    public void Replace(int i, Step s) => Change("edit a step", () => { if (i >= 0 && i < State.Steps.Count) State.Steps[i] = s; });

    public void SetOption(Action<QueueState> set) => Edit(() => set(State));


    public void Clear() => Change("remove all steps", () => { State.Steps.Clear(); State.Running = false; State.Name = ""; Status = "Not running"; });
    public void SetLoop(bool on) => Edit(() => State.Loop = on);
    public void SetIdle(ActionRef? a) => Edit(() => State.Idle = a);

    /// <summary>Starts the queue; with <paramref name="from"/>, steps above it are passed over (finished ones stay done).</summary>
    public void Start(int from = -1) => Edit(() =>
    {
        if (State.Steps.Count == 0) return;
        for (int i = 0; i < State.Steps.Count; i++)
        {
            Step s = State.Steps[i];
            bool finished = s.Done && !s.PassedOver;                   // reached its goal (or was skipped) earlier: stays done
            bool started = s.Begun && !s.Done || s.PassedAfterStart;   // running now, or stopped early by an earlier "Run from here"
            s.Reset();
            if (from >= 0 && i < from) { s.Done = true; s.PassedOver = !finished; s.PassedAfterStart = !finished && started; }
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

    public static int LevelCap(Game game, string skillId)
    {
        if (!game.Entities.TryGetObjectById(skillId, out GameEntity? e) || e == null) return 120;
        int cap = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault()?.LevelCap ?? 120;
        return cap > 0 ? cap : 120;
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
        // The game's tick list is read by the offline worker, so only join it while the game is running normally.
        if (_tickerFor != game && !GameAccess.IsComputingOffline) { _tickerFor = game; OfflineTicker.Register(game, this, _log); }
        if (_game == game) return;
        _game = game;
        try
        {
            var bus = typeof(Game).GetProperty("EventBus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(game) as GameEventBus;
            bus?.ActionPerformed.Subscribe((sender, e) =>
            {
                // Raised for every finished action (also on the offline thread); sub-steps like hunting's tracking are skipped.
                if (e.IsSubAction || sender is not ActionComponent c || c.Entity is GamesByMalcs.Melvor2.Entities.Combat.CombatEntity) return;
                lock (s_performed) s_performed[c.Entity.Id] = (s_performed.TryGetValue(c.Entity.Id, out long n) ? n : 0) + 1;
            });
            // Combat does not report "action performed"; a kill is its action.
            var combat = game.Entities.GetAllObjects().OfType<GamesByMalcs.Melvor2.Entities.Combat.CombatEntity>().FirstOrDefault();
            if (combat != null)
                combat.Enemy.Killed += (_, _) => { lock (s_performed) s_performed[combat.Id] = (s_performed.TryGetValue(combat.Id, out long n) ? n : 0) + 1; };
            // Finished slayer tasks, for "Slayer tasks: N tasks done".
            var tasks = Slayer.Component(game);
            if (tasks != null)
                tasks.TaskCompleted += (_, _) => { lock (s_performed) s_performed[Slayer.DoneKey] = (s_performed.TryGetValue(Slayer.DoneKey, out long n) ? n : 0) + 1; };
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
            case Goal.Technique:
                return $"level {Finesse.Technique(game, s.Item)?.Experience.Level ?? 0} / {s.Amount}";
            case Goal.Gold:
                {
                    long gp = ShopSource.Gp(game);
                    return $"{gp:N0} / {s.Amount:N0} GP";
                }
            case Goal.Times: return $"{(s.Begun ? Performed(s.CountKey) - s.StartTimes : 0):N0} / {s.Amount:N0}";
            case Goal.Minutes:
                if (!s.Begun) return $"{s.Amount} min";
                double left = s.Amount - (UtcNow - s.StartedUtc).TotalMinutes;
                return left > 0 ? $"{Math.Ceiling(left)} min left" : "done";
            default: return "";
        }
    }

    // "  ~12 min left", from the pace since the step began (shown after a minute, once the pace means something).
    string Eta(Step s, int gained, int left)
    {
        if (!s.Begun || s.Done || gained <= 0 || left <= 0) return "";
        double minutes = (UtcNow - s.StartedUtc).TotalMinutes;
        if (minutes < 1) return "";
        double eta = left / (gained / minutes);
        return eta < 1 ? "  ~1 min left" : eta < 120 ? $"  ~{Math.Ceiling(eta)} min left"
            : eta < 48 * 60 ? $"  ~{eta / 60:0.#} h left" : $"  ~{eta / 1440:0.#} days left";
    }

    bool Reached(Game game, Step s) => s.Goal switch
    {
        Goal.Level => Level(game, s.Skill) >= s.Amount,
        Goal.BankCount => BankCount(game, s.Item) >= s.Amount,
        Goal.MoreItems => s.Begun && BankCount(game, s.Item) - s.StartCount >= s.Amount,
        Goal.Minutes => s.Begun && (UtcNow - s.StartedUtc).TotalMinutes >= s.Amount,
        Goal.Times => s.Begun && Performed(s.CountKey) - s.StartTimes >= s.Amount,
        Goal.Gold => ShopSource.Gp(game) >= s.Amount,
        Goal.Technique => (Finesse.Technique(game, s.Item)?.Experience.Level ?? 0) >= s.Amount,
        _ => false,
    };

    // ---- running ----

    bool _bankFullNoted;
    double _nextSlayer, _nextFeed, _buffAt;
    string _buffFor = "", _foodNote = "";
    GamesByMalcs.Melvor2.Data.Items.Item? _buff;

    void Feed(Game game, double now)
    {
        ActionRef? cur = Starter.Current(game);
        if (cur == null || !Finesse.Handles(game, cur.Entity)) return;
        // The best buff food for what is running, worked out again every few minutes (levels and gear change it).
        string key = cur.Entity + "|" + cur.Target;
        if (key != _buffFor || now >= _buffAt)
        {
            _buffFor = key;
            _buffAt = now + 180;
            if (game.Entities.TryGetObjectById(cur.Entity, out GamesByMalcs.Melvor2.Entities.GameEntity? e) && e is GamesByMalcs.Melvor2.Components.CombatFood.IFoodEntity fe)
                _buff = Finesse.BuffFood(game, fe, cur);
        }
        string did = Finesse.Feed(game, cur, _buff, State.FinesseFloor);
        if (did.Length > 0 && did != _foodNote) Note($"{cur.Label}: {did}");
        _foodNote = did.StartsWith("ate") ? "" : did;   // repeat "ate" notes, not "no healing food" ones
    }

    /// <summary>
    /// The game screen was just created for a character, before any frame: get ready now, because the game may
    /// start catching up on time away straight after (frames are paused while it does).
    /// </summary>
    public void Attach(Game game)
    {
        lock (_gate)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            _lastOnlineUtc = default;   // a game just loaded: time away counts from its save
            Hook(game);
            if (game.CharacterName != _character) LoadFor(game.CharacterName);
        }
    }

    /// <summary>Once per game tick, from <see cref="OfflineTicker"/>: online ticks only note the time; offline ticks
    /// run the queue every two game seconds.</summary>
    public void GameTick(Game game)
    {
        if (_mainThread == -1 || Environment.CurrentManagedThreadId == _mainThread)
        {
            _lastOnlineUtc = DateTime.UtcNow;
            return;
        }
        if (!State.Running || !HasCharacter || game.CharacterName != _character || _game != game) return;
        if (!_wasOffline)
        {
            _wasOffline = _hadOffline = true;
            _simSeconds = _nextOfflineRun = 0;
            _offlineSteps = State.Steps.Count(x => x.Done);
            // Time away starts when the game last ran: the last frame, or for a game just loaded, its save time.
            _virtualBase = _lastOnlineUtc != default ? _lastOnlineUtc : game.LoadedSaveTimestamp.ToUniversalTime();
        }
        _simSeconds += GamesByMalcs.Melvor2.Core.Constants.TickInterval / 1000.0;
        if (_simSeconds < _nextOfflineRun) return;
        _nextOfflineRun = _simSeconds + 2;
        lock (_gate)
        {
            _offline = true;
            _virtualUtc = _virtualBase.AddSeconds(_simSeconds);
            try { RunQueue(game, _lastNow + _simSeconds); }
            catch (Exception e) { _log("While the game was closed: " + e.Message); _nextOfflineRun = double.MaxValue; }
            finally { _offline = false; }
        }
    }

    // Back from time away: the step timers ran on game time, so start them fresh; then the alerts and the window.
    void AfterOffline()
    {
        _wasOffline = false;
        if (_hadOffline)
        {
            _hadOffline = false;
            _nextCheck = _nextSlayer = _nextFeed = _buffAt = 0;
            foreach (Step x in State.Steps) { x.RetryAt = x.BestCheckAt = x.BeltCheckAt = x.LastRestart = 0; x.FailedUntil.Clear(); }
            int moved = State.Steps.Count(x => x.Done) - _offlineSteps;
            if (moved > 0) Note($"While the game was closed, the queue finished {moved} step{(moved == 1 ? "" : "s")}.");
            foreach (string a in _pendingAlerts)
            {
                if (State.Alerts) Alert?.Invoke(a);
                Messages.Publish("course.alert", a);
            }
            _pendingAlerts.Clear();
            _pendingChanged = true;
        }
        if (!_pendingChanged) return;
        _pendingChanged = false;
        Save();
        Changed?.Invoke();
    }

    public void Tick(Game? game, double now)
    {
        if (game == null) { _character = ""; return; }
        lock (_gate) TickOnline(game, now);
    }

    void TickOnline(Game game, double now)
    {
        if (GameAccess.IsComputingOffline) return; // a loader whose frames keep running during time away
        _mainThread = Environment.CurrentManagedThreadId;
        _lastNow = now;
        _lastOnlineUtc = DateTime.UtcNow;
        Hook(game);
        if (game.CharacterName != _character) LoadFor(game.CharacterName);
        if (_wasOffline || _hadOffline || _pendingChanged) AfterOffline();

        // Slayer rules all the time (Options), unless a Slayer tasks step is applying them already.
        if (State.SlayerAlways && now >= _nextSlayer && !GameAccess.IsComputingOffline
            && !(State.Running && CurrentSteps().Any(x => x.IsSlayer)))
        {
            _nextSlayer = now + 5;
            try { string did = Slayer.ApplyRules(game, State); if (did.Length > 0) Note("Slayer: " + did); } catch { }
        }

        // Thieving and agility food, whatever started the action: buff food on, heal before a failure can hurt.
        if (State.FinesseFood && now >= _nextFeed && !GameAccess.IsComputingOffline)
        {
            _nextFeed = now + 0.5;
            try { Feed(game, now); } catch (Exception e) { Log("Food: " + e.Message); }
        }

        RunQueue(game, now);
    }

    // The steps' turn: twice a second while playing, every two game seconds while the game catches up on time away.
    void RunQueue(Game game, double now)
    {
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
                Notify();
            }
            return;
        }
        _bankFullNoted = false;

        List<Step> current = CurrentSteps().ToList();
        if (current.Count == 0) { Finish(game); return; }

        bool changed = false;
        foreach (Step s in current) changed |= Run(game, s, now);
        if (State.RemoveFinished && !State.Loop && State.Steps.RemoveAll(x => x.Finished) > 0) changed = true;

        // While the step waits for materials, the idle action uses the free time.
        List<Step> after = CurrentSteps().ToList();
        if (after.Count > 0 && after.All(s => s.Waiting) && State.Idle != null && !Starter.IsRunning(game, State.Idle))
            { WearIdle(game); Starter.Start(game, State.Idle); }

        Status = string.Join("  +  ", after.Select(s => $"{Label(s)}: {(s.State.Length > 0 ? s.State : "starting")}"));
        if (changed) { Save(); Notify(); }
    }

    /// <summary>"Woodcutting: Mahogany Tree", or for a train-a-skill step "Train Woodcutting (Mahogany Tree)".</summary>
    public static string Label(Step s) =>
        s.IsBest || s.IsSlayer ? s.Resolved != null ? $"{s.Action.Label} ({Short(s.Resolved.Label)})" : s.Action.Label : s.Action.Label;

    // "Agility: Jump 3 (best path) (Simple Path)" -> "Jump 3: Simple Path"
    static string Short(string label)
    {
        string t = label[(label.IndexOf(':') + 1)..].Replace(" (best path)", "").Trim();
        int p = t.LastIndexOf(" (", StringComparison.Ordinal);
        return p > 0 && t.EndsWith(')') ? t[..p] + ": " + t[(p + 2)..^1] : t;
    }

    void WearIdle(Game game)
    {
        if (!State.AutoGear || State.Idle == null || !Gear.IsCombat(game, State.Idle.Entity)) return;
        if (State.SkillGear.TryGetValue(State.Idle.Entity, out string? id) && id.Length > 0) Gear.Swap(game, id);
    }

    static bool BankFull(Game game)
    {
        ActionComponent? any = Catalog.AnyAction(game);
        return any != null && any.Inventory.MaxStacks > 0 && any.Inventory.StackCount >= any.Inventory.MaxStacks;
    }

    // One step's turn. Returns true when something visible changed.
    bool Run(Game game, Step s, double now)
    {
        int index = State.Steps.IndexOf(s);
        string StartIt(ActionRef r) => Starter.Start(game, r);

        if (s.IsSell)
        {
            string sold = Starter.Sell(game, s.Item, s.Amount);
            if (sold.Length == 0) return Done(s, $"Sold {s.ItemName} down to {s.Amount:N0}", "done");
            return Skip(s, $"Sell {s.ItemName}: {sold}");
        }

        if (s.IsShop)
        {
            if (now < s.RetryAt) return false;
            string why = ShopSource.Buy(game, s);
            if (why.Length == 0) return Done(s, $"{s.Action.Label}: {s.Amount:N0} {s.ItemName} in the bank", "done");
            s.RetryAt = now + 10;
            string state = "waiting: " + why;
            if (s.State == state) return false;
            s.State = state;
            s.Waiting = true;
            return true;
        }

        if (!s.Begun && s.Goal is Goal.Level or Goal.BankCount or Goal.Gold or Goal.Technique && Reached(game, s))
            return Done(s, $"{s.Action.Label}: goal already met", "already met");

        bool switched = false;
        // Slayer tasks: every few seconds apply the reroll/extend rules and fight the top task (a finished task moves on).
        if (s.IsSlayer && now >= s.BestCheckAt)
        {
            s.BestCheckAt = now + 3;
            string did = Slayer.ApplyRules(game, State);
            if (did.Length > 0) Note("Slayer: " + did);
            ActionRef? top = Slayer.Pick(game, State, s.FailedUntil, now);
            if (top != null && (s.Resolved == null || s.Resolved.Target != top.Target || s.Resolved.Area != top.Area))
            {
                if (s.Resolved != null) Note($"Slayer tasks: next, {top.Label}");
                s.Resolved = top;
                switched = true;
                if (s.Begun) { s.Waiting = true; s.RetryAt = 0; }
            }
            if (top == null)
            {
                if (s.Resolved != null) Starter.Component(game, s.Resolved)?.TryStop(true);
                s.Resolved = null;
                s.Waiting = true;
                s.State = "waiting: no slayer task you can fight (locked areas or monsters you skip)";
            }
        }

        // Train a skill: every 10 seconds, look for a better action than the one running (new unlocks as you level).
        if (s.IsBest && now >= s.BestCheckAt)
        {
            s.BestCheckAt = now + 10;
            // Most GP and Fastest XP rank actions by the planner's rates. The Course window starts loading them, but
            // a queue that runs from the start of a session may never have had the window open.
            PlanData.Get(Folder, Log);
            ActionRef? top = Starter.BestFor(game, s.Action.Entity, s.Action.Target)
                .FirstOrDefault(c => !s.FailedUntil.TryGetValue(c.Target, out double until) || now >= until);
            if (top != null && (s.Resolved == null || s.Resolved.Target != top.Target || s.Resolved.Area != top.Area))
            {
                if (s.Resolved != null) Note($"{s.Action.Label}: switching to {top.Label}");
                s.Resolved = top;
                switched = true;
                if (s.Begun) { s.Waiting = true; s.RetryAt = 0; }
            }
            if (top == null && s.Resolved == null)
            {
                s.Waiting = true;
                s.State = s.IsGold ? (PlanData.Loaded == null ? "waiting for the planner data (" + PlanData.State + ")" : "waiting: nothing makes GP with what you have") : "waiting: nothing to train yet";
            }
            // Most GP: turn what it made into GP.
            if (s.IsGold && s.SellOutput && s.Resolved != null)
                foreach (Item it in Catalog.Outputs(game, s.Resolved))
                    if (Queue.BankCount(game, it.Id) > 0) Starter.Sell(game, it.Id, 0);
        }
        ActionRef act = s.Effective;
        if ((s.IsBest || s.IsSlayer) && s.Resolved == null) return switched;

        if (!s.Begun || s.Waiting)
        {
            if (now < s.RetryAt) return switched;
            // Combat loadout first, so the fight starts with the right gear. Only one combat step runs at a time.
            if (State.AutoGear && Gear.Wanted(game, State, s) is string gear)
            {
                string? before = Gear.CurrentId(game);
                string gerr = Gear.Swap(game, gear);
                if (gerr.Length > 0) Note($"{Label(s)}: gear not changed, {gerr}");
                else if (before != gear) Note($"{Label(s)}: wearing {Gear.Name(game, gear)}");
            }
            if (State.AutoGear && Gear.IsCombat(game, s.Action.Entity))
            {
                string setup = CombatSetup.Apply(game, State, s);
                if (setup.Length > 0) Note($"{Label(s)}: {setup}");
            }
            // Toolbelt items the action needs (Warped Cacti: a Filtered Vial in the Woodcutting toolbelt).
            string belt = Toolbelt.Prepare(game, act);
            if (belt.Length > 0 && (s.Tries == 0 || belt.StartsWith("put"))) Note($"{Label(s)}: {belt}");
            // Thieving and Agility: the best path, and the gear you own that gives the most XP for this target.
            if (Finesse.Handles(game, act.Entity))
            {
                Finesse.PickPath(game, act);
                if (State.FinesseGear && SetOf(s) == null)
                {
                    string worn = Finesse.BestGear(game, act);
                    if (worn.Length > 0) Note($"{Label(s)}: {worn}");
                }
                _buffAt = 0;   // choose the buff food again with the new gear
            }
            // The step's toolbelt set (refilled stock goes back on when a step resumes after a refill).
            if (State.AutoGear && SetOf(s) is BeltSet set)
            {
                string put = Belts.Apply(game, set);
                if (put.Length > 0 && (s.Tries == 0 || put.Contains("put on") || put.Contains("topped"))) Note($"{Label(s)}: {set.Name}: {put}");
            }
            string why = StartIt(act);
            if (why.Length > 0)
            {
                if (s.IsSlayer)
                {
                    // That task can't be fought now; try another master's task for a couple of minutes.
                    s.FailedUntil[act.Target] = now + 120;
                    s.Resolved = null;
                    s.BestCheckAt = 0;
                    if (Slayer.Pick(game, State, s.FailedUntil, now) != null) return true;
                }
                if (s.IsBest)
                {
                    // That action cannot run (usually materials); try the next best one right away.
                    s.FailedUntil[act.Target] = now + 120;
                    s.Resolved = null;
                    s.BestCheckAt = 0;
                    if (Starter.BestFor(game, s.Action.Entity, s.Action.Target).Any(c => !s.FailedUntil.TryGetValue(c.Target, out double u) || now >= u)) return true;
                }
                // Cannot start (usually missing materials or a level). Wait and retry; give up after about 10 minutes,
                // or after a minute when nothing else could use the time.
                s.Tries++;
                if (s.Tries > 30 || (State.Idle == null && s.Tries > 3))
                    return Skip(s, $"{Label(s)}: {why}");
                s.Waiting = true;
                s.State = $"waiting: {why}";
                s.RetryAt = now + RetryWait;
                return true;
            }
            if (!s.Begun)
            {
                s.Begun = true;
                s.StartedUtc = UtcNow;
                s.StartCount = Count(game, s);
                s.StartTimes = Performed(s.CountKey);
                Note($"Step {index + 1}: {Label(s)}, {s.GoalText()}");
            }
            s.Waiting = false;
            s.Restarts = s.Tries = 0;
            s.State = "running";
            return true;
        }

        if (Reached(game, s)) return Done(s, $"{Label(s)}: done ({s.GoalText()})", "done");

        // The state text is not saved: after a game restart a step that is already going has none yet.
        if (s.State != "running" && Starter.IsRunning(game, act)) { s.State = "running"; return true; }

        // A consumable in the step's toolbelt set ran out: refill it, then carry on.
        if (State.AutoGear && now >= s.BeltCheckAt && SetOf(s) is BeltSet bset)
        {
            s.BeltCheckAt = now + 2;
            if (Refill(game, s, bset, index, now)) return true;
        }

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

    public BeltSet? SetOf(Step s) => s.Belt.Length == 0 ? null : State.Belts.FirstOrDefault(b => b.Id == s.Belt);

    /// <summary>
    /// Refills a used-up consumable from the step's toolbelt set: straight from the bank when it has some, otherwise
    /// by planning the item (making or gathering what it needs) up to the set amount, in steps put before this one.
    /// This step waits and resumes after them with its progress kept, putting the new stock on as it starts again.
    /// </summary>
    bool Refill(Game game, Step s, BeltSet set, int index, double now)
    {
        // Some in the bank: put it straight back.
        foreach (BeltSlot bs in set.Slots.Where(b => b.Refill > 0))
        {
            if (Queue.BankCount(game, bs.Item) <= 0) continue;
            var only = new BeltSet { Skill = set.Skill, Slots = new() { bs } };
            if (Belts.OutOf(game, only) != null) { Note($"{Label(s)}: {Belts.Apply(game, only)}"); return true; }
        }
        BeltSlot? need = Belts.OutOf(game, set);
        if (need == null || Queue.BankCount(game, need.Item) > 0) return false;

        if (now - s.RefillWindowStart > 600) { s.RefillWindowStart = now; s.Refills = 0; }
        if (s.Refills >= 3) return false;   // keeps failing: leave the step running without it
        s.Refills++;
        string name = Catalog.FindItem(game, need.Item)?.DisplayName.ToString() ?? need.Item;

        PlanData? data = PlanData.Get(Folder, Log);
        var steps = new List<Step>();
        if (data != null)
        {
            var planner = new Planner(data, game);
            List<PlanLine> lines = planner.Plan(need.Item, need.Refill, more: false);
            planner.ResolveIds(lines);
            steps = planner.ToSteps(lines);
        }
        if (steps.Count == 0)
        {
            Note($"{Label(s)}: out of {name}, and the planner has no way to make it; carrying on without it");
            s.Refills = 3;
            return true;
        }
        Edit(() => State.Steps.InsertRange(index, steps));
        s.Waiting = true;
        s.RetryAt = 0;
        s.State = $"waiting: making {need.Refill:N0} {name}";
        Note($"{Label(s)}: out of {name}; making {need.Refill:N0} first ({steps.Count} step{(steps.Count == 1 ? "" : "s")}), then back to this");
        return true;
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
        s.Finished = true;
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
            Notify();
            return;
        }
        State.Running = false;
        Status = "Finished";
        if (State.AutoGear && CombatSetup.Restore(game, State) is { Length: > 0 } back) Note($"Queue finished: {back}");
        if (State.Idle != null) { { WearIdle(game); Starter.Start(game, State.Idle); } Status += $"; now {State.Idle.Label}"; }
        Raise("Queue finished" + (State.Idle != null ? $"; now {State.Idle.Label}" : ""));
        Save();
        Notify();
    }
}
