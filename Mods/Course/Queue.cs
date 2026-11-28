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

/// <summary>Most GP and Fastest XP: what the picked actions may use from the bank.</summary>
public enum Funding
{
    Bank,      // anything whose ingredients are in the bank
    None,      // only actions that use nothing (gathering, thieving...)
    Self,      // gather the ingredients first, then make; your existing stock is left alone
}

/// <summary>Thieving and Agility: what a queue step does when the bank runs out of the healing food it may use.</summary>
public enum FoodOut
{
    KeepGoing,   // carry on; the game auto-eats the selected bonus food
    NextStep,    // stop the step and move on
    MakeMore,    // make more healing food in steps put before this one, then come back
    Idle,        // stop and do the idle action until there is healing food again, then come back
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
    /// <summary>Most GP and Fastest XP: what the picked actions may use from the bank.</summary>
    public Funding Funding { get; set; }
    /// <summary>Self-funded: bank stock of each ingredient when the step started using it. Making stops there.</summary>
    public Dictionary<string, int> Reserve { get; set; } = new();
    /// <summary>Put in by a self-funded step to gather its ingredients; removed once done, at the next round.</summary>
    public bool Gather { get; set; }
    /// <summary>Smithing and Beastcrafting: use the recipe's other mode (Sacrificial Anvil, Astral Offerings) where
    /// unlocked. Never for steps that count items. Most GP uses the anvil without asking.</summary>
    public bool UseRewardMode { get; set; }
    [JsonIgnore] public double FoodWindowStart { get; set; }
    [JsonIgnore] public int FoodRuns { get; set; }
    /// <summary>Stopped for want of healing food (<see cref="FoodOut.Idle"/>): starts again once there is some.</summary>
    [JsonIgnore] public bool FoodWait { get; set; }
    [JsonIgnore] public double GatherWindowStart { get; set; }
    [JsonIgnore] public int Gathers { get; set; }
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
        Action = Action, Goal = Goal, Amount = Amount, Skill = Skill, SkillName = SkillName, Item = Item, ItemName = ItemName, Gear = Gear, Weapon = Weapon, Stance = Stance, Belt = Belt, SellOutput = SellOutput, Funding = Funding, UseRewardMode = UseRewardMode,
    };

    public void Reset()
    {
        Begun = Done = PassedOver = PassedAfterStart = Waiting = Finished = false;
        State = "";
        Restarts = Tries = 0;
        RetryAt = LastRestart = BestCheckAt = 0;
        Resolved = null;
        FailedUntil.Clear();
        Reserve.Clear();
        Gathers = 0;
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
    /// <summary>Food (item ids) never used for thieving and agility: not as bonus food, not to heal.</summary>
    public HashSet<string> FoodSkip { get; set; } = new();
    /// <summary>Other skills' bonus food (combat) it may heal with; kept for its own skill otherwise.</summary>
    public HashSet<string> FoodAllow { get; set; } = new();
    /// <summary>Heal with the biggest heal first instead of the cheapest per HP.</summary>
    public bool HealBiggestFirst { get; set; }
    [JsonIgnore] internal Finesse.FoodRules FoodRules => new(FoodSkip, FoodAllow, HealBiggestFirst);
    /// <summary>What a thieving or agility step does when the healing food it may use runs out.</summary>
    public FoodOut FoodOut { get; set; }
    /// <summary>For <see cref="FoodOut.MakeMore"/>: the food to make (item id), "" = the cheapest per HP you can make.</summary>
    public string FoodMake { get; set; } = "";
    public int FoodMakeAmount { get; set; } = 500;
    /// <summary>For <see cref="FoodOut.MakeMore"/>: gather every ingredient instead of using what's in the bank.</summary>
    public bool FoodSelfFund { get; set; }
    /// <summary>History keeps this many lines (in the tab and on disk); older ones are dropped.</summary>
    public int HistoryLimit { get; set; } = 2000;
    /// <summary>Kinds of history line to highlight (see <see cref="Queue.HistoryKinds"/>).</summary>
    public HashSet<string> HistoryFlags { get; set; } = new() { "skipped", "paused", "failed" };
    /// <summary>Highlighted lines you've looked at ("time|text"), so they stop counting.</summary>
    public HashSet<string> HistorySeen { get; set; } = new();
    /// <summary>"entity|recipe" to the reward mode it had before a step changed it ("" = none), so it can be put back.</summary>
    public Dictionary<string, string> RewardRestore { get; set; } = new();
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
        int limit = HistoryLimit;
        if (History.Count > limit) History.RemoveRange(0, History.Count - limit);
        if (!HasCharacter) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.AppendAllText(HistoryFile, $"{e.Time:yyyy-MM-dd HH:mm:ss}\t{text}{Environment.NewLine}");
            // The file only grows by appending: now and then cut it back to what the tab keeps.
            if (++_historyLines > limit + 500) WriteHistory();
        }
        catch { }
    }

    int HistoryLimit => Math.Clamp(State.HistoryLimit, 100, 100_000);

    /// <summary>What History can highlight, how it spots each one, and whether it's on unless you change it.</summary>
    public static readonly (string Id, string Name, bool On)[] HistoryKinds =
    {
        ("skipped", "Skipped steps", true),
        ("paused", "The queue pausing (a full bank, say)", true),
        ("failed", "Something that didn't work (can't, couldn't, gear not changed, no way to make)", true),
        ("ranout", "Running out of food or a refill (Course makes more and carries on)", false),
        ("finished", "The queue finishing", false),
    };

    /// <summary>Which kind of line this is, by its wording; null for ordinary lines.</summary>
    public static string? KindOf(string text) =>
        text.StartsWith("Skipped") ? "skipped"
        : text.StartsWith("Paused") || text.StartsWith("Queue paused") ? "paused"
        : text.Contains("can't") || text.Contains("couldn't") || text.Contains("Could not") || text.Contains("not changed") || text.Contains("no way to make") ? "failed"
        : text.Contains(": out of ") ? "ranout"
        : text.StartsWith("Queue finished") || text.Contains("the queue finished") ? "finished"
        : null;

    static string SeenKey(HistoryEntry e) => $"{e.Time:yyyy-MM-dd HH:mm:ss}|{e.Text}";
    public bool Flagged(HistoryEntry e) => KindOf(e.Text) is string k && State.HistoryFlags.Contains(k);
    public bool Seen(HistoryEntry e) => State.HistorySeen.Contains(SeenKey(e));
    public bool NeedsLook(HistoryEntry e) => Flagged(e) && !Seen(e);

    public void MarkSeen(IEnumerable<HistoryEntry> lines)
    {
        var keys = lines.Where(Flagged).Select(SeenKey).ToList();
        if (keys.Count > 0) Edit(() => State.HistorySeen.UnionWith(keys));
    }

    public void SetHistoryFlag(string kind, bool on) => Edit(() => { if (on) State.HistoryFlags.Add(kind); else State.HistoryFlags.Remove(kind); });

    // Seen marks for lines no longer in the history are dropped, so the list stays small.
    void PruneSeen()
    {
        if (State.HistorySeen.Count == 0) return;
        var have = History.Select(SeenKey).ToHashSet();
        State.HistorySeen.RemoveWhere(k => !have.Contains(k));
    }
    int _historyLines;   // lines in the history file, roughly (to know when to cut it back)

    void WriteHistory()
    {
        if (!HasCharacter) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.WriteAllLines(HistoryFile, History.Select(h => $"{h.Time:yyyy-MM-dd HH:mm:ss}\t{h.Text}"));
            _historyLines = History.Count;
            PruneSeen();
        }
        catch { }
    }

    /// <summary>Clears history older than <paramref name="before"/>, or all of it.</summary>
    public void ClearHistory(DateTime? before = null)
    {
        if (before is DateTime b) History.RemoveAll(h => h.Time < b); else History.Clear();
        if (History.Count == 0) { try { File.Delete(HistoryFile); } catch { } _historyLines = 0; State.HistorySeen.Clear(); }
        else WriteHistory();
        Notify();
    }

    public void SetHistoryLimit(int lines)
    {
        Edit(() => State.HistoryLimit = lines);
        if (History.Count > HistoryLimit) { History.RemoveRange(0, History.Count - HistoryLimit); WriteHistory(); }
        else Notify();
    }

    void LoadHistory()
    {
        History.Clear();
        try
        {
            _historyLines = 0;
            if (!File.Exists(HistoryFile)) return;
            var lines = File.ReadAllLines(HistoryFile);
            _historyLines = lines.Length;
            foreach (string line in lines.TakeLast(HistoryLimit))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && DateTime.TryParse(line[..tab], out DateTime t)) History.Add(new HistoryEntry(t, line[(tab + 1)..]));
            }
            if (lines.Length > HistoryLimit + 500) WriteHistory();   // an old, long file: cut it back once
            PruneSeen();
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

    public bool SavedExists(string name) => Safe(name).Length > 0 && File.Exists(SavedFile(name));
    string SavedFile(string name) => Path.Combine(SavedFolder, Safe(name) + ".json");

    public void SaveAs(string name)
    {
        name = Safe(name);
        if (name.Length == 0) return;
        Write(SavedFile(name), new QueueState { Name = name, Loop = State.Loop, Idle = State.Idle, Steps = State.Steps.Select(s => s.Copy()).ToList() });
        _savedSig.Remove(name);
        Edit(() => State.Name = name);
    }

    /// <summary>Saves steps under a name without touching the current queue (a pasted share code).</summary>
    public void SaveStepsAs(string name, IEnumerable<Step> steps)
    {
        name = Safe(name);
        if (name.Length == 0) return;
        Write(SavedFile(name), new QueueState { Name = name, Steps = steps.Select(s => s.Copy()).ToList() });
        _savedSig.Remove(name);
        Notify();
    }

    /// <summary>A saved queue as it is on disk (null if it can't be read).</summary>
    public QueueState? ReadSaved(string name) => Read(SavedFile(name));

    public DateTime SavedTime(string name)
    {
        try { return File.GetLastWriteTime(SavedFile(name)); } catch { return default; }
    }

    public void RenameSaved(string from, string to)
    {
        to = Safe(to);
        if (to.Length == 0 || to == from || SavedExists(to)) return;
        QueueState? q = ReadSaved(from);
        if (q == null) return;
        q.Name = to;
        Write(SavedFile(to), q);
        try { File.Delete(SavedFile(from)); } catch { }
        _savedSig.Remove(from);
        if (State.Name == from) Edit(() => State.Name = to); else Notify();
    }

    /// <summary>Adds a saved queue's steps to the end of this one.</summary>
    public void AddSaved(string name)
    {
        QueueState? q = ReadSaved(name);
        if (q == null || q.Steps.Count == 0) return;
        Change($"add \"{q.Name}\"", () => State.Steps.AddRange(q.Steps.Select(s => s.Copy())));
    }

    /// <summary>Replaces the steps with these (a pasted share code); the queue is then unsaved.</summary>
    public void ReplaceSteps(IEnumerable<Step> steps)
    {
        var list = steps.Select(s => s.Copy()).ToList();
        Change("replace the queue", () => { State.Steps = list; State.Name = ""; State.Running = false; });
    }

    // What a queue looks like for "changed since you saved it": steps (without their progress), loop and idle action.
    readonly Dictionary<string, (DateTime Time, string Sig)> _savedSig = new();
    static string Signature(QueueState q) =>
        JsonSerializer.Serialize(new { q.Loop, q.Idle, Steps = q.Steps.Select(s => s.Copy()).ToList() }, s_json);

    /// <summary>The current queue differs from the saved one it was loaded from or saved as (or was never saved).</summary>
    public bool ChangedSinceSaved
    {
        get
        {
            if (State.Name.Length == 0) return State.Steps.Count > 0;
            DateTime t = SavedTime(State.Name);
            if (!_savedSig.TryGetValue(State.Name, out var c) || c.Time != t)
            {
                QueueState? q = ReadSaved(State.Name);
                if (q == null) return true;
                _savedSig[State.Name] = c = (t, Signature(q));
            }
            return c.Sig != Signature(State);
        }
    }

    public void LoadSaved(string name)
    {
        QueueState? q = Read(SavedFile(name));
        if (q == null) return;
        Snapshot($"load \"{q.Name}\"");
        Edit(() =>
        {
            QueueState o = State; // options and gear sets belong to the character, not the saved queue
            State = new QueueState
            {
                Name = q.Name, Loop = q.Loop, Idle = q.Idle, Steps = q.Steps.Select(s => s.Copy()).ToList(),
                Alerts = o.Alerts, FoodGuard = o.FoodGuard, BankGuard = o.BankGuard, Hud = o.Hud, AutoGear = o.AutoGear, SkillGear = o.SkillGear, WeaponRestore = o.WeaponRestore, OffhandRestore = o.OffhandRestore, SlotRestore = o.SlotRestore, SlayerGauntlet = o.SlayerGauntlet, Belts = o.Belts,
                SlayerSkip = o.SlayerSkip, SlayerExtend = o.SlayerExtend, SlayerReserve = o.SlayerReserve, SlayerRerollLocked = o.SlayerRerollLocked, SlayerAlways = o.SlayerAlways, FinesseGear = o.FinesseGear, FinesseFood = o.FinesseFood, FinesseFloor = o.FinesseFloor, HudX = o.HudX, HudY = o.HudY, HudMin = o.HudMin,
                FoodSkip = o.FoodSkip, FoodAllow = o.FoodAllow, HealBiggestFirst = o.HealBiggestFirst, FoodOut = o.FoodOut, FoodMake = o.FoodMake, FoodMakeAmount = o.FoodMakeAmount, FoodSelfFund = o.FoodSelfFund, HistoryLimit = o.HistoryLimit, HistoryFlags = o.HistoryFlags, HistorySeen = o.HistorySeen, RewardRestore = o.RewardRestore,
            };
            Status = $"Loaded \"{q.Name}\"";
        });
    }

    public void DeleteSaved(string name)
    {
        try { File.Delete(SavedFile(name)); } catch { }
        _savedSig.Remove(name);
        if (State.Name == name) Edit(() => State.Name = ""); else Notify();
    }

    void Edit(Action change)
    {
        change();
        Save();
        Notify();
    }

    readonly List<(string Steps, string Name, bool Loop, ActionRef? Idle, string What)> _undo = new();

    /// <summary>What Undo puts back: the steps, and the queue's name, loop and idle action (loading changes those).</summary>
    void Snapshot(string what)
    {
        _undo.Add((JsonSerializer.Serialize(State.Steps), State.Name, State.Loop, State.Idle, what));
        if (_undo.Count > 30) _undo.RemoveAt(0);
    }

    /// <summary>What Undo would put back, or "" when there is nothing to undo.</summary>
    public string UndoWhat => _undo.Count == 0 ? "" : _undo[^1].What;

    /// <summary>A change to the step list that Undo can take back.</summary>
    void Change(string what, Action change)
    {
        Snapshot(what);
        Edit(() => { change(); Settle(); });
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (json, name, loop, idle, what) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        List<Step>? steps = null;
        try { steps = JsonSerializer.Deserialize<List<Step>>(json); } catch { }
        if (steps == null) return;
        Edit(() =>
        {
            State.Steps = steps;
            State.Name = name;
            State.Loop = loop;
            State.Idle = idle;
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
    public void Insert(int at, IEnumerable<Step> steps)
    {
        var list = steps.ToList();
        Change(list.Count == 1 ? "add a step" : $"add {list.Count} steps", () => State.Steps.InsertRange(Math.Clamp(at, 0, State.Steps.Count), list));
    }

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

    /// <summary>Copies of the steps (their settings, not their progress) right after the last of them. Returns the copies' places.</summary>
    public List<int> Duplicate(IReadOnlyCollection<int> rows)
    {
        var picked = rows.Where(i => i >= 0 && i < State.Steps.Count).Distinct().OrderBy(i => i).ToList();
        var result = new List<int>();
        if (picked.Count == 0) return result;
        Change(picked.Count == 1 ? "duplicate a step" : $"duplicate {picked.Count} steps", () =>
        {
            int at = picked[^1] + 1;
            State.Steps.InsertRange(at, picked.Select(i => State.Steps[i].Copy()));
            result.AddRange(Enumerable.Range(at, picked.Count));
        });
        return result;
    }

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
    /// <summary>The food choices changed: pick the bonus food again on the next check.</summary>
    public void FoodChanged() => _buffAt = 0;


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
    double _nextSlayer, _nextFeed, _buffAt, _nextOfflineFeed;
    string _buffFor = "", _foodNote = "";
    GamesByMalcs.Melvor2.Data.Items.Item? _buff;
    readonly Dictionary<string, int> _offlineAte = new();

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
                _buff = Finesse.BuffFood(game, fe, cur, State.FoodRules);
        }
        string did = Finesse.Feed(game, cur, _buff, State.FinesseFloor, State.FoodRules);
        // During time away every heal would be its own history line; count them and say it once when the game is back.
        if (_offline && System.Text.RegularExpressions.Regex.Match(did, @"^ate (\d+) (.+?) \(") is { Success: true } m)
        {
            _offlineAte.TryGetValue(m.Groups[2].Value, out int n);
            _offlineAte[m.Groups[2].Value] = n + int.Parse(m.Groups[1].Value);
            return;
        }
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
        bool feeding = State.FinesseFood;
        if (!(State.Running || feeding) || !HasCharacter || game.CharacterName != _character || _game != game) return;
        if (!_wasOffline)
        {
            _wasOffline = _hadOffline = true;
            _simSeconds = _nextOfflineRun = _nextOfflineFeed = 0;
            _offlineSteps = State.Steps.Count(x => x.Done);
            // Time away starts when the game last ran: the last frame, or for a game just loaded, its save time.
            _virtualBase = _lastOnlineUtc != default ? _lastOnlineUtc : game.LoadedSaveTimestamp.ToUniversalTime();
        }
        _simSeconds += GamesByMalcs.Melvor2.Core.Constants.TickInterval / 1000.0;
        // Thieving and agility food during time away too: without this the game auto-eats the sushi all night.
        if (feeding && _simSeconds >= _nextOfflineFeed)
        {
            _nextOfflineFeed = _simSeconds + 1;
            lock (_gate)
            {
                _offline = true;
                _virtualUtc = _virtualBase.AddSeconds(_simSeconds);
                try { Feed(game, _lastNow + _simSeconds); }
                catch (Exception e) { _log("Food while the game was closed: " + e.Message); _nextOfflineFeed = double.MaxValue; }
                finally { _offline = false; }
            }
        }
        if (!State.Running || _simSeconds < _nextOfflineRun) return;
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
            if (_offlineAte.Count > 0)
            {
                Note("While the game was closed, healed with " + string.Join(", ", _offlineAte.Select(kv => $"{kv.Value:N0} {kv.Key}")) + " to keep the buff food for its bonus.");
                _offlineAte.Clear();
            }
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
        // Recipes a finished (or switched-away) step changed to the Sacrificial Anvil or Astral Offerings go back.
        if (State.RewardRestore.Count > 0)
        {
            int had = State.RewardRestore.Count;
            RewardModes.Restore(game, State, CurrentSteps().Where(x => !x.Done).Select(x => x.Effective));
            changed |= State.RewardRestore.Count != had;
        }

        // While the step waits for materials, the idle action uses the free time.
        List<Step> after = CurrentSteps().ToList();
        if (after.Count > 0 && after.All(s => s.Waiting) && State.Idle != null && !Starter.IsRunning(game, State.Idle))
            { WearIdle(game); Starter.Start(game, State.Idle); }

        Status = string.Join("  +  ", after.Select(s => $"{Label(s)}: {(s.State.Length > 0 ? s.State : "starting")}"));
        if (changed) { Save(); Notify(); }
    }

    /// <summary>"Woodcutting: Mahogany Tree", or for a train-a-skill step "Train Woodcutting (Mahogany Tree)".</summary>
    public static string Label(Step s) =>
        (s.IsBest || s.IsSlayer ? s.Resolved != null ? $"{s.Action.Label} ({Short(s.Resolved.Label)})" : s.Action.Label : s.Action.Label)
        + (!s.IsBest ? "" : s.Funding == Funding.None ? ", nothing from the bank" : s.Funding == Funding.Self ? ", self-funded" : "");

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
            ActionRef? top = Starter.BestFor(game, s.Action.Entity, s.Action.Target, s.Funding, s.UseRewardMode)
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
                s.State = PlanData.Loaded == null && (s.IsGold || s.Funding != Funding.Bank) ? "waiting for the planner data (" + PlanData.State + ")"
                    : s.Funding == Funding.None ? "waiting: everything here uses something from the bank"
                    : s.Funding == Funding.Self ? "waiting: nothing here can be gathered for at your levels"
                    : s.IsGold ? "waiting: nothing makes GP with what you have" : "waiting: nothing to train yet";
            }
            // Most GP: turn what it made into GP.
            if (s.IsGold && s.SellOutput && s.Resolved != null)
                foreach (Item it in Catalog.Outputs(game, s.Resolved))
                    if (Queue.BankCount(game, it.Id) > 0) Starter.Sell(game, it.Id, 0);
        }
        ActionRef act = s.Effective;
        if ((s.IsBest || s.IsSlayer) && s.Resolved == null) return switched;

        // Self-funded: out of what this step gathered, so gather more before making again.
        if (s.IsBest && s.Funding == Funding.Self && SelfFund(game, s, act, index, now)) return true;

        if (!s.Begun || s.Waiting)
        {
            if (now < s.RetryAt) return switched;
            // Waiting for healing food (the idle action runs meanwhile).
            if (s.FoodWait)
            {
                if (Finesse.Handles(game, act.Entity) && Finesse.OutOfHealing(game, State.FoodRules))
                {
                    s.RetryAt = now + RetryWait;
                    return switched;
                }
                s.FoodWait = false;
                Note($"{Label(s)}: there's healing food again; back to it");
            }
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
            // Smithing and Beastcrafting: the recipe's other mode on or off as the step wants (put back afterwards).
            try
            {
                if (RewardModes.For(game, act, out _) != null)
                {
                    string rm = RewardModes.Set(game, State, act, WantsRewardMode(s));
                    if (rm.Length > 0) Note($"{Label(s)}: {rm}");
                }
            }
            catch (Exception e) { Log("Reward mode: " + e.Message); }
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
                    if (Starter.BestFor(game, s.Action.Entity, s.Action.Target, s.Funding, s.UseRewardMode).Any(c => !s.FailedUntil.TryGetValue(c.Target, out double u) || now >= u)) return true;
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

        // Thieving and Agility: out of the healing food it may use. What happens is up to Options.
        if (State.FinesseFood && State.FoodOut != FoodOut.KeepGoing && Finesse.Handles(game, act.Entity)
            && Finesse.OutOfHealing(game, State.FoodRules))
            return OutOfHealingFood(game, s, act, index, now);

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
    /// Whether a step runs its recipe in the skill's other mode: Most GP always with the Sacrificial Anvil (it pays
    /// in GP straight away, a little more than selling); steps that count items never (the mode would destroy or
    /// replace them); anything else when the step asks for it.
    /// </summary>
    static bool WantsRewardMode(Step s) =>
        s.IsGold || (s.Goal is not (Goal.BankCount or Goal.MoreItems) && s.UseRewardMode);

    /// <summary>The bank has none of the healing food a thieving or agility step may use: Options decide what next.</summary>
    bool OutOfHealingFood(Game game, Step s, ActionRef act, int index, double now)
    {
        Starter.Component(game, act)?.TryStop(true);
        switch (State.FoodOut)
        {
            case FoodOut.NextStep:
                return Skip(s, $"{Label(s)}: stopped, out of healing food");
            case FoodOut.Idle:
                s.FoodWait = true;
                s.Waiting = true;
                s.RetryAt = now + RetryWait;
                s.State = "waiting: out of healing food";
                Note($"{Label(s)}: out of healing food; " + (State.Idle != null ? $"doing {State.Idle.Label}" : "waiting") + " until there's some, then back to this");
                return true;
            case FoodOut.MakeMore:
                if (now - s.FoodWindowStart > 1800) { s.FoodWindowStart = now; s.FoodRuns = 0; }
                if (++s.FoodRuns <= 3 && MakeFood(game, s, index)) return true;
                return Skip(s, $"{Label(s)}: stopped, out of healing food and none it can make");
        }
        return false;
    }

    /// <summary>Healing food planned the way Options say: what to make, how many, and its steps.</summary>
    internal sealed record FoodPlan(Item Food, int Amount, List<PlanLine> Lines, List<Step> Steps, double Hours);

    /// <summary>
    /// Plans the healing food from Options: the food picked there, or else the cheapest per HP you can make (food you
    /// keep out is passed over). Ingredients in the bank are used first unless Options say to gather them all.
    /// Null when nothing can be planned; <paramref name="why"/> says why for the first food tried.
    /// </summary>
    internal FoodPlan? PlanFood(Game game, out string why)
    {
        why = "";
        PlanData? data = PlanData.Get(Folder, Log);
        if (data == null) { why = "no rates to plan with"; return null; }
        IEnumerable<Item> foods = State.FoodMake.Length > 0
            ? new[] { Catalog.FindItem(game, State.FoodMake) }.OfType<Item>()
            : Finesse.AllFoods(game).Where(f => f.Kind == Finesse.FoodKind.Plain && !State.FoodSkip.Contains(f.Item.Id))
                .OrderBy(f => f.PerHp).ThenByDescending(f => f.Heal).Select(f => f.Item);
        int amount = Math.Max(1, State.FoodMakeAmount);
        var planner = new Planner(data, game, selfFunded: State.FoodSelfFund);
        foreach (Item food in foods)
        {
            List<PlanLine> lines = planner.Plan(Catalog.Local(food.Id), amount, more: true);
            PlanLine? bad = lines.FirstOrDefault(l => l.Problem.Length > 0);
            if (bad != null) { if (why.Length == 0) why = $"{food.DisplayName}: {bad.Problem}"; continue; }
            if (lines.Count == 0) continue;
            planner.ResolveIds(lines);
            List<Step> steps = planner.ToSteps(lines);
            if (steps.Count == 0) continue;
            return new FoodPlan(food, amount, lines, steps, lines.Where(l => !double.IsInfinity(l.Hours)).Sum(l => l.Hours));
        }
        return null;
    }

    /// <summary>Puts the planned healing food in steps before this one, which waits and carries on after them.</summary>
    bool MakeFood(Game game, Step s, int index)
    {
        if (PlanFood(game, out _) is not FoodPlan p) return false;
        Edit(() => State.Steps.InsertRange(index, p.Steps));
        s.Waiting = true;
        s.RetryAt = 0;
        s.State = $"waiting: making {p.Amount:N0} {p.Food.DisplayName}";
        Note($"{Label(s)}: out of healing food; making {p.Amount:N0} {p.Food.DisplayName} first ({p.Steps.Count} step{(p.Steps.Count == 1 ? "" : "s")}), then back to this");
        return true;
    }

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

    /// <summary>How much making each round of gathering is for, in hours.</summary>
    const double GatherHours = 0.25;

    /// <summary>
    /// Self-funded Most GP and Fastest XP: the ingredients of the picked action come from gathering, never from what
    /// was already in the bank. The bank count of each ingredient when the step first uses it is kept aside; when
    /// what is above it runs out, making stops and steps that gather about <see cref="GatherHours"/> of making's worth
    /// are put in front of this one. Returns true when it did something this tick.
    /// </summary>
    bool SelfFund(Game game, Step s, ActionRef act, int index, double now)
    {
        PlanData? data = PlanData.Loaded;
        if (data == null || Starter.RateFor(game, data, act) is not { } rate || rate.In.Count == 0) return false;
        var ids = Catalog.Items(game).Select(i => i.Id).ToList();
        var wants = new List<(string Local, string Full, double PerHour)>();
        foreach (var (local, perHour) in rate.In)
        {
            string? full = ids.FirstOrDefault(id => Catalog.Local(id) == local);
            if (full == null) return false;
            wants.Add((local, full, perHour));
        }
        bool low = false;
        foreach (var w in wants)
        {
            int have = BankCount(game, w.Full);
            if (!s.Reserve.TryGetValue(w.Full, out int keep)) s.Reserve[w.Full] = keep = have;   // what you had stays yours
            if (have <= keep) low = true;
        }
        if (!low) return false;

        if (Starter.IsRunning(game, act)) Starter.Component(game, act)?.TryStop(true);
        if (now - s.GatherWindowStart > 1800) { s.GatherWindowStart = now; s.Gathers = 0; }
        var planner = new Planner(data, game, selfFunded: true);
        List<PlanLine> lines = planner.PlanMany(wants.Select(w => (w.Local, s.Reserve[w.Full] + Math.Max(1, (int)Math.Ceiling(w.PerHour * GatherHours)))));
        planner.ResolveIds(lines);
        List<Step> steps = lines.Any(l => l.Problem.Length > 0) ? new() : planner.ToSteps(lines);
        // No way to gather it, or rounds of gathering that don't last (a gather step keeps being skipped): pick
        // another action for a while.
        if (steps.Count == 0 || ++s.Gathers > 4)
        {
            Note($"{Label(s)}: can't keep {act.Label} supplied by gathering; trying something else");
            s.FailedUntil[act.Target] = now + 1800;
            s.Resolved = null;
            s.BestCheckAt = 0;
            s.Gathers = 0;
            s.Waiting = true;
            s.RetryAt = 0;
            return true;
        }
        foreach (Step g in steps) g.Gather = true;
        Edit(() =>
        {
            // Last round's gathering steps are done: take them out so the queue doesn't fill up with them.
            var old = State.Steps.Take(State.Steps.IndexOf(s)).Where(x => x.Gather && x.Done).ToHashSet();
            State.Steps.RemoveAll(old.Contains);
            State.Steps.InsertRange(State.Steps.IndexOf(s), steps);
        });
        s.Waiting = true;
        s.RetryAt = 0;
        s.State = "waiting: gathering the ingredients first";
        Note($"{Label(s)}: gathering the ingredients for {act.Label} ({steps.Count} step{(steps.Count == 1 ? "" : "s")}), then making");
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
        RewardModes.Restore(game, State, Array.Empty<ActionRef>());
        if (State.AutoGear && CombatSetup.Restore(game, State) is { Length: > 0 } back) Note($"Queue finished: {back}");
        if (State.Idle != null) { { WearIdle(game); Starter.Start(game, State.Idle); } Status += $"; now {State.Idle.Label}"; }
        Raise("Queue finished" + (State.Idle != null ? $"; now {State.Idle.Label}" : ""));
        Save();
        Notify();
    }
}
