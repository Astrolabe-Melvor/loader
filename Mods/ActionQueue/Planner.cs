using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;

namespace Astrolabe.ActionQueue;

/// <summary>
/// Per-hour rates for every action, published by the Astrolabe wiki build (plan.json). Downloaded once a day and
/// cached in the mod folder, so the planner keeps working offline.
/// </summary>
sealed class PlanData
{
    public const string Url = "https://astrolabe-melvor.github.io/plan.json";

    public sealed record Rate(string Id, string Skill, string Name, int Level, double Sec, double Xp,
        List<(string Item, double PerHour)> Out, List<(string Item, double PerHour)> In);

    public string GameVersion { get; private set; } = "";
    public Dictionary<string, string> Items { get; } = new();
    public List<Rate> Rates { get; } = new();

    static PlanData? s_data;
    /// <summary>The data if it has been loaded, without starting a download.</summary>
    public static PlanData? Loaded => s_data;
    static Task? s_loading;
    public static string State { get; private set; } = "not loaded";

    /// <summary>The data if it is ready; starts loading it otherwise.</summary>
    public static PlanData? Get(string folder, Action<string> log)
    {
        if (s_data != null || s_loading != null) return s_data;
        string file = Path.Combine(folder, "plan.json");
        // Use the cached copy right away; refresh it in the background when it is a day old.
        if (File.Exists(file)) s_data = TryParse(File.ReadAllText(file), log);
        bool stale = !File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1);
        if (stale) s_loading = Task.Run(async () =>
        {
            try
            {
                State = "downloading";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                string json = await http.GetStringAsync(Url);
                PlanData? fresh = TryParse(json, log);
                if (fresh != null) { File.WriteAllText(file, json); s_data = fresh; }
                State = s_data != null ? "ready" : "download failed";
            }
            catch (Exception e) { State = s_data != null ? "ready (offline copy)" : "could not download: " + e.Message; log("plan.json: " + e.Message); }
        });
        else State = s_data != null ? "ready" : "unreadable";
        return s_data;
    }

    static PlanData? TryParse(string json, Action<string> log)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var d = new PlanData();
            JsonElement root = doc.RootElement;
            d.GameVersion = root.TryGetProperty("gameVersion", out var v) ? v.GetString() ?? "" : "";
            foreach (JsonProperty p in root.GetProperty("items").EnumerateObject()) d.Items[p.Name] = p.Value.GetString() ?? p.Name;
            foreach (JsonElement r in root.GetProperty("rates").EnumerateArray())
            {
                static List<(string, double)> Pairs(JsonElement e) =>
                    e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => (x[0].GetString() ?? "", x[1].GetDouble())).ToList() : new();
                string skill = r.GetProperty("skill").GetString() ?? "";
                var input = Pairs(r.GetProperty("in"));
                // Bait only speeds Fishing and Hunting up; the planner does not make you produce it.
                if (skill is "Fishing" or "Hunting") input.RemoveAll(x => x.Item1.EndsWith("Bait"));
                d.Rates.Add(new Rate(r.GetProperty("id").GetString() ?? "", skill, r.GetProperty("name").GetString() ?? "",
                    r.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 1,
                    r.TryGetProperty("sec", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0,
                    r.TryGetProperty("xp", out var xp) && xp.ValueKind == JsonValueKind.Number ? xp.GetDouble() : 0,
                    Pairs(r.GetProperty("out")), input));
            }
            return d;
        }
        catch (Exception e) { log("plan.json unreadable: " + e.Message); return null; }
    }
}

/// <summary>One line of a plan: gather or make an item, or bring it yourself.</summary>
sealed class PlanLine
{
    public string Item = "", ItemName = "";
    public double Need;              // how many to make for this plan
    public int Have;                 // in the bank now
    public int Target;               // "until the bank holds"
    public double Hours;
    public PlanData.Rate? Rate;      // null: no action makes it (bring it yourself)
    public ActionRef? Action;
    public int LevelNeeded;          // above your level: level up first
    public string Problem = "";      // why this line can't run as is
}

/// <summary>Works out the steps to get an item: the cheapest action for it, then the same for its ingredients.</summary>
sealed class Planner
{
    readonly PlanData _data;
    readonly Game _game;
    readonly Dictionary<string, int> _levels = new();
    readonly Dictionary<string, int> _bank = new();
    Dictionary<string, double> _costNow = new(), _costAny = new();
    readonly Dictionary<string, double> _used = new();   // bank stock this plan already uses

    public Planner(PlanData data, Game game)
    {
        _data = data;
        _game = game;
        foreach (GameEntity e in game.Entities.GetAllObjects())
        {
            ExperienceComponent? xp = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
            if (xp != null) _levels[Local(e.Id)] = xp.Level;
        }
        _costNow = Costs(anyLevel: false);
        _costAny = Costs(anyLevel: true);
    }

    static string Local(string id) => id.Contains(':') ? id[(id.LastIndexOf(':') + 1)..] : id;

    int Level(string skill) => _levels.TryGetValue(skill, out int l) ? l : 1;

    int Bank(string item)
    {
        if (_bank.TryGetValue(item, out int n)) return n;
        string? full = Catalog.Items(_game).Select(i => i.Id).FirstOrDefault(id => Local(id) == item);
        return _bank[item] = full == null ? 0 : Queue.BankCount(_game, full);
    }

    // Hours of play per unit of each item (gathering counted in), found by relaxing until nothing improves.
    Dictionary<string, double> Costs(bool anyLevel)
    {
        var cost = new Dictionary<string, double>();
        double InCost(List<(string Item, double PerHour)> ins) =>
            ins.Sum(x => x.PerHour * (cost.TryGetValue(x.Item, out double c) ? c : double.PositiveInfinity));
        for (int pass = 0; pass < 12; pass++)
        {
            bool changed = false;
            foreach (var r in _data.Rates)
            {
                if (!anyLevel && r.Level > Level(r.Skill)) continue;
                double ic = InCost(r.In);
                if (double.IsInfinity(ic)) continue;
                foreach (var (item, perHour) in r.Out)
                {
                    if (perHour <= 0) continue;
                    double h = (1 + ic) / perHour;
                    if (h < (cost.TryGetValue(item, out double old) ? old : double.PositiveInfinity) - 1e-12) { cost[item] = h; changed = true; }
                }
            }
            if (!changed) break;
        }
        return cost;
    }

    double UnitCost(string item) =>
        _costNow.TryGetValue(item, out double a) ? a : _costAny.TryGetValue(item, out double b) ? b : double.PositiveInfinity;

    /// <summary>The steps, ingredients first. <paramref name="more"/>: make N more; otherwise own N in total.</summary>
    public List<PlanLine> Plan(string item, int amount, bool more)
    {
        var lines = new Dictionary<string, PlanLine>();
        var order = new List<string>();
        _used.Clear();
        int have = Bank(item);
        double need = more ? amount : Math.Max(0, amount - have);
        if (need > 0) Expand(item, need, lines, order, new HashSet<string>(), 0, root: true);

        foreach (PlanLine l in lines.Values)
        {
            l.Have = Bank(l.Item);
            l.Target = (int)Math.Ceiling(l.Have + l.Need);
            if (l.Item == item) l.Target = more ? have + amount : amount;
        }
        return order.Select(k => lines[k]).ToList();
    }

    void Expand(string item, double need, Dictionary<string, PlanLine> lines, List<string> order, HashSet<string> seen, int depth, bool root)
    {
        // Ingredients already in the bank are used first (the item asked for is always made in full).
        if (!root)
        {
            double used = _used.TryGetValue(item, out double u) ? u : 0;
            double take = Math.Min(Math.Max(0, Bank(item) - used), need);
            _used[item] = used + take;
            need -= take;
        }
        if (need <= 0.0001 || depth > 8 || seen.Contains(item)) return;
        seen = new HashSet<string>(seen) { item };

        // Cheapest action you can do now; failing that, the cheapest at any level (flagged).
        PlanData.Rate? best = null;
        double bestH = double.PositiveInfinity, bestOut = 0;
        foreach (bool anyLevel in new[] { false, true })
        {
            foreach (var r in _data.Rates)
            {
                if (!anyLevel && r.Level > Level(r.Skill)) continue;
                double o = r.Out.FirstOrDefault(x => x.Item == item).PerHour;
                if (o <= 0) continue;
                double ic = r.In.Sum(x => x.PerHour * UnitCost(x.Item));
                double h = need * (1 + ic) / o;
                if (h < bestH) { best = r; bestH = h; bestOut = o; }
            }
            if (best != null) break;
        }

        if (!lines.TryGetValue(item, out PlanLine? line))
        {
            line = new PlanLine { Item = item, ItemName = _data.Items.TryGetValue(item, out string? n) ? n : item };
            lines[item] = line;
        }
        line.Need += need;

        if (best == null)
        {
            line.Problem = "no action makes this: bring it yourself";
            if (!order.Contains(item)) order.Add(item);
            return;
        }
        line.Rate = best;
        line.Hours += need / bestOut;
        line.Action ??= ToAction(best);
        if (best.Level > Level(best.Skill)) { line.LevelNeeded = best.Level; line.Problem = $"needs {best.Skill} level {best.Level}"; }
        if (line.Action == null && line.Problem.Length == 0) line.Problem = "action not found in this game version";

        foreach (var (input, perHour) in best.In)
            Expand(input, need * perHour / bestOut, lines, order, seen, depth + 1, root: false);

        order.Remove(item);
        order.Add(item);   // after its ingredients
    }

    ActionRef? ToAction(PlanData.Rate r)
    {
        string local = r.Id.Contains(':') ? r.Id[(r.Id.IndexOf(':') + 1)..] : r.Id;
        foreach (SkillChoice s in Catalog.Skills(_game))
        {
            if (Local(s.Id) != r.Skill && s.Name != r.Skill) continue;
            ActionChoice? a = s.Actions.FirstOrDefault(x => Local(x.Ref.Target) == local);
            if (a != null) return a.Ref;
        }
        return null;
    }

    /// <summary>
    /// Turns plan lines into queue steps ("until the bank holds N"). A line that needs a higher level gets a
    /// "train the skill" step in front of it, so the queue levels up on the way.
    /// </summary>
    public List<Step> ToSteps(IEnumerable<PlanLine> lines)
    {
        var steps = new List<Step>();
        var trained = new Dictionary<string, Step>();
        foreach (PlanLine l in lines)
        {
            if (l.Action == null) continue;
            if (l.LevelNeeded > 0 && l.Rate != null)
            {
                if (trained.TryGetValue(l.Rate.Skill, out Step? t)) t.Amount = Math.Max(t.Amount, l.LevelNeeded);
                else
                {
                    var train = TrainStep(l.Rate.Skill, l.LevelNeeded);
                    if (train == null) continue;
                    trained[l.Rate.Skill] = train;
                    steps.Add(train);
                }
            }
            steps.Add(new Step { Action = l.Action, Goal = Goal.BankCount, Amount = l.Target, Item = FullId(l), ItemName = l.ItemName });
        }
        return steps;
    }

    /// <summary>A "train this skill the fastest way until level N" step.</summary>
    public Step? TrainStep(string skillName, int level)
    {
        SkillChoice? skill = Catalog.Skills(_game).FirstOrDefault(s => Local(s.Id) == skillName || s.Name == skillName);
        if (skill == null) return null;
        GameEntity? leveled = _game.LeveledEntities.OfType<GameEntity>().FirstOrDefault(e => e.Id == skill.Id);
        return new Step
        {
            Action = new ActionRef { Entity = skill.Id, Target = Starter.BestTarget, Label = $"Train {skill.Name}" },
            Goal = Goal.Level, Amount = level, Skill = leveled?.Id ?? skill.Id, SkillName = skill.Name,
        };
    }

    static string FullId(PlanLine l) => s_fullIds.TryGetValue(l.Item, out string? f) ? f : l.Item;
    static readonly Dictionary<string, string> s_fullIds = new();

    /// <summary>Maps the plan's short item ids to the game's ids (needed by bank goals).</summary>
    public void ResolveIds(IEnumerable<PlanLine> lines)
    {
        var all = Catalog.Items(_game);
        foreach (PlanLine l in lines)
        {
            string? full = all.Select(i => i.Id).FirstOrDefault(id => Local(id) == l.Item);
            if (full != null) s_fullIds[l.Item] = full;
        }
    }
}
