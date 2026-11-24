using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Core.CostTracking;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Farming;
using GamesByMalcs.Melvor2.Entities.Ranching;
using GamesByMalcs.Melvor2.EventLog;
using MelvorModLoader;

namespace Astrolabe.Deckhand;

/// <summary>Mill stock rule for one grain: fill the mill with it, but leave this many in the bank.</summary>
public sealed class MillItem
{
    public bool Fill { get; set; } = true;
    public long LeaveInBank { get; set; }
}

/// <summary>Per character settings.</summary>
public sealed class Chores
{
    public bool Pet { get; set; } = true;
    public bool Repair { get; set; } = true;
    public int RepairBelow { get; set; } = 75;      // percent maintenance
    public long GpReserve { get; set; }             // repairs never take GP below this
    public bool MillFill { get; set; }
    public bool MillRestart { get; set; } = true;
    public Dictionary<string, MillItem> Mill { get; set; } = new();
    public bool FarmAlerts { get; set; } = true;
}

public sealed record Done(DateTime Time, string Text);

/// <summary>
/// The passive chores: pets animals that want it, repairs enclosures below a set maintenance, keeps the grain mill
/// stocked and running, collects its output, and passes the game's farming notices (automation can't be paid for,
/// seeds can't be bought, the bank is full at harvest, compost ran out) to Beacon. Nothing runs during offline catch-up.
/// </summary>
public sealed class Hand
{
    readonly string _folder;
    readonly Action<string> _log;
    readonly ConcurrentQueue<string> _notices = new();
    readonly Dictionary<string, DateTime> _lastNotice = new();
    string _character = "";
    Game? _hooked;
    double _next;
    bool _millWasActive, _millStoppedEmpty;

    public Chores Chores { get; private set; } = new();
    public List<Done> Log { get; } = new();
    public event Action? Changed;

    public Hand(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
    }

    public static RanchingPassiveComponent? Ranch(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<RanchingPassiveComponent>().FirstOrDefault();

    public static FarmingGrainMillComponent? Mill(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<FarmingGrainMillComponent>().FirstOrDefault();

    public void Tick(Game? game, double now)
    {
        if (game == null) { _character = ""; return; }
        if (game.CharacterName != _character) Load(game.CharacterName);
        Hook(game);

        bool said = false;
        while (_notices.TryDequeue(out string? n))
        {
            // The game repeats these every cycle: pass each one on at most every 10 minutes.
            if (_lastNotice.TryGetValue(n, out DateTime t) && DateTime.Now - t < TimeSpan.FromMinutes(10)) continue;
            _lastNotice[n] = DateTime.Now;
            Note(n);
            if (Chores.FarmAlerts) Messages.Publish("deckhand.alert", n);
            said = true;
        }

        if (GameAccess.IsComputingOffline || now < _next) { if (said) Changed?.Invoke(); return; }
        _next = now + 5;
        try
        {
            said |= DoRanch(game, false);
            said |= DoMill(game);
        }
        catch (Exception e) { _log("Chores failed: " + e.Message); }
        if (said) Changed?.Invoke();
    }

    void Hook(Game game)
    {
        if (_hooked == game) return;
        _hooked = game;
        try
        {
            // May be raised on the offline thread: queue the text, handle it on the next check.
            game.EventLog.EntryAdded += (_, e) => Notice(e.Item);
            game.EventLog.EntryMoved += (_, e) => Notice(e.Item);
        }
        catch (Exception e) { _log("Farming notices unavailable: " + e.Message); }
    }

    void Notice(IEventLogEntry entry)
    {
        if (entry is CantAffordAutomationEventLogEntry or CantAffordAutoPlantEventLogEntry or HarvestBankFullEventLogEntry or NotEnoughCompostEventLogEntry)
        {
            try { _notices.Enqueue(Plain(entry.GetPlainDescription())); } catch { }
        }
    }

    static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[^\]]*\]", "").Trim();

    /// <summary>Something the player needs to do: logged, and sent to Beacon (at most every 10 minutes per message).</summary>
    void Warn(string text)
    {
        if (_lastNotice.TryGetValue(text, out DateTime t) && DateTime.Now - t < TimeSpan.FromMinutes(10)) return;
        _lastNotice[text] = DateTime.Now;
        Note(text);
        if (Chores.FarmAlerts) Messages.Publish("deckhand.alert", text);
    }

    /// <summary>Enclosures below the repair line that can't be repaired now, and why.</summary>
    public List<string> Blocked { get; } = new();

    void Note(string text)
    {
        Log.Insert(0, new Done(DateTime.Now, text));
        if (Log.Count > 100) Log.RemoveAt(Log.Count - 1);
    }

    // ---- ranching ----

    public sealed record RanchView(int Enclosures, double LowestMaintenance, int WantPets, string RepairCost, bool CanRepair);

    public RanchView? Ranching(Game game)
    {
        RanchingPassiveComponent? r = Ranch(game);
        if (r == null) return null;
        var pens = r.ActiveLand.Values.Where(l => l.IsPurchased && l.ActiveEnclosure != null).Select(l => l.ActiveEnclosure!).ToList();
        if (pens.Count == 0) return new RanchView(0, 1, 0, "", false);
        string cost = "";
        bool can = false;
        using (CostTracker t = r.GetTotalRepairCostTracker())
        {
            cost = string.Join(", ", t.Currencies.Select(c => $"{c.Quantity:N0} {c.Currency.DisplayNameShort}").Concat(t.Items.Select(i => $"{i.Quantity:N0} {i.Item.DisplayName}")));
            can = t.Currencies.Any() || t.Items.Any();
        }
        return new RanchView(pens.Count, pens.Min(p => p.Maintenance), r.GetAnimalsRequiringPets(), cost, can);
    }

    /// <summary>Pets and repairs by the rules (or, with <paramref name="force"/>, repairs every enclosure now).</summary>
    public bool DoRanch(Game game, bool force)
    {
        RanchingPassiveComponent? r = Ranch(game);
        if (r == null) return false;
        bool did = false;
        if (Chores.Pet && !force)
        {
            int want = r.GetAnimalsRequiringPets();
            if (want > 0)
            {
                r.PetAllAnimals();
                Note($"Petted {want} animal{(want == 1 ? "" : "s")}");
                did = true;
            }
        }
        if (Chores.Repair || force)
        {
            Blocked.Clear();
            foreach (ActiveRanchingLand land in r.ActiveLand.Values)
            {
                ActiveRanchingEnclosure? pen = land.ActiveEnclosure;
                if (!land.IsPurchased || pen == null || pen.Maintenance >= 1.0) continue;
                if (!force && pen.Maintenance * 100 >= Chores.RepairBelow) continue;
                string cost, missing;
                string pname = pen.CurrentEnclosure.DisplayName.ToString();
                using (CostTracker t = r.GetRepairCostTracker(pen.CurrentEnclosure.Costs, pen.Maintenance))
                {
                    cost = string.Join(", ", t.Currencies.Select(c => $"{c.Quantity:N0} {c.Currency.DisplayNameShort}").Concat(t.Items.Select(i => $"{i.Quantity:N0} {i.Item.DisplayName}")));
                    // What is short: items or currency you don't have, or GP that would go under the reserve.
                    missing = string.Join(", ",
                        t.Items.Where(i => i.CurrentQuantity < i.Quantity).Select(i => $"{i.Quantity - i.CurrentQuantity:N0} more {i.Item.DisplayName}")
                        .Concat(t.Currencies.Where(c => c.CurrentQuantity < c.Quantity).Select(c => $"{c.Quantity - c.CurrentQuantity:N0} more {c.Currency.DisplayNameShort}"))
                        .Concat(t.Currencies.Where(c => c.Currency.Id == "MelvorBase:GoldPieces" && c.CurrentQuantity >= c.Quantity && c.CurrentQuantity - c.Quantity < Chores.GpReserve)
                            .Select(_ => "GP above your reserve")));
                }
                if (missing.Length > 0)
                {
                    Blocked.Add($"{pname} ({pen.Maintenance:P0}) needs {missing}");
                    Warn($"Can't repair {pname} ({pen.Maintenance:P0} maintenance): needs {missing}");
                    continue;
                }
                double before = pen.Maintenance;
                pen.Repair();
                if (pen.Maintenance > before)
                {
                    Note($"Repaired {pname} from {before:P0} for {cost}");
                    did = true;
                }
                else
                {
                    Blocked.Add($"{pname} ({pen.Maintenance:P0}): the game refused the repair");
                    Warn($"Can't repair {pname}: the game refused the repair");
                }
            }
        }
        return did;
    }

    // ---- grain mill ----

    public sealed record MillRow(Item Item, int InMill, int InBank, int Room);

    public (string Recipe, bool Active, List<MillRow> Rows)? MillState(Game game)
    {
        FarmingGrainMillComponent? m = Mill(game);
        if (m?.Data == null) return null;
        var rows = m.Data.StorageItems.Select(i => new MillRow(i, m.GrainMillInventory.GetQuantity(i), m.Inventory.GetQuantity(i), m.GrainMillInventory.GetMaxStackQuantityAvailable(i))).ToList();
        return (m.SelectedRecipe?.Recipe.DisplayName.ToString() ?? "", m.IsActive, rows);
    }

    MillItem RuleFor(Item i) => Chores.Mill.TryGetValue(i.Id, out MillItem? r) ? r : new MillItem();

    bool DoMill(Game game)
    {
        FarmingGrainMillComponent? m = Mill(game);
        if (m?.Data == null) return false;
        bool did = false;

        // It stopped on its own because it ran out (the game turns it off then): remember, so it can start again.
        bool canMake = m.SelectedRecipe != null && m.CanCreateRecipe(m.SelectedRecipe);
        if (_millWasActive && !m.IsActive && !canMake) _millStoppedEmpty = true;
        _millWasActive = m.IsActive;

        if (Chores.MillFill)
            foreach (Item i in m.Data.StorageItems)
            {
                MillItem rule = RuleFor(i);
                if (!rule.Fill) continue;
                long spare = m.Inventory.GetQuantity(i) - rule.LeaveInBank;
                int n = (int)Math.Min(spare, m.GrainMillInventory.GetMaxStackQuantityAvailable(i));
                if (n <= 0) continue;
                int before = m.GrainMillInventory.GetQuantity(i);
                m.DepositItemToGrainMill(i, n);
                int moved = m.GrainMillInventory.GetQuantity(i) - before;
                if (moved > 0) { Note($"Put {moved:N0} {i.DisplayName} in the mill"); did = true; }
            }

        if (Chores.MillRestart && _millStoppedEmpty && !m.IsActive && m.SelectedRecipe != null && m.CanCreateRecipe(m.SelectedRecipe))
        {
            if (m.ToggleGrainMillTimer(true))
            {
                _millStoppedEmpty = false;
                _millWasActive = true;
                Note($"Started the mill again ({m.SelectedRecipe.Recipe.DisplayName})");
                did = true;
            }
        }
        return did;
    }

    public sealed record MillRecipe(ActiveGrainMillRecipe Active, string Name, string Uses, bool Unlocked, string Needs);

    /// <summary>Every product the mill shows, unlocked or not.</summary>
    public List<MillRecipe> MillRecipes(Game game)
    {
        FarmingGrainMillComponent? m = Mill(game);
        if (m == null) return new();
        return m.ActiveRecipes
            .Where(kv => kv.Key.DisplayRequirements?.AreMet ?? true)
            .Select(kv =>
            {
                string uses = string.Join(", ", kv.Key.Ingredients.Items.Select(i => $"{i.Quantity:N0} {i.Item.DisplayName}"));
                string needs = string.Join("; ", kv.Key.Requirements.Conditions.Where(c => !c.IsMet).Select(c =>
                {
                    try { return Plain(System.Text.RegularExpressions.Regex.Replace(c.GetDescription(false, false), @"\[img[^\]]*\].*?\[/img\]", "")); }
                    catch { return ""; }
                }).Where(x => x.Length > 0));
                return new MillRecipe(kv.Value, kv.Key.DisplayName.ToString(), uses, kv.Key.Requirements.AreMet, needs);
            }).ToList();
    }

    /// <summary>Switches the mill's product the way the game asks you to: stop it, collect, pick, start again if it was running.</summary>
    public string SwitchMill(Game game, ActiveGrainMillRecipe recipe)
    {
        FarmingGrainMillComponent? m = Mill(game);
        if (m == null) return "No grain mill.";
        if (m.SelectedRecipe == recipe) return "";
        bool wasOn = m.IsActive;
        if (wasOn) m.ToggleGrainMillTimer(false);
        m.CollectReadyItems();
        m.SelectRecipe(recipe);
        if (m.SelectedRecipe != recipe) { if (wasOn) m.ToggleGrainMillTimer(true); return "The game would not switch the product."; }
        string name = recipe.Recipe.DisplayName.ToString();
        if (wasOn || Chores.MillRestart)
        {
            if (m.CanCreateRecipe(recipe)) m.ToggleGrainMillTimer(true);
            else _millStoppedEmpty = true;   // starts once it has the grain
        }
        _millWasActive = m.IsActive;
        Note($"Mill now makes {name}");
        Changed?.Invoke();
        return "";
    }

    public void SetMillRule(string itemId, MillItem rule)
    {
        Chores.Mill[itemId] = rule;
        Save();
    }

    // ---- files ----

    string File_ => Path.Combine(_folder, "chores", Safe(_character) + ".json");

    static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    void Load(string character)
    {
        _character = character;
        _millWasActive = _millStoppedEmpty = false;
        Log.Clear();
        try { Chores = File.Exists(File_) ? JsonSerializer.Deserialize<Chores>(File.ReadAllText(File_)) ?? new() : new(); }
        catch { Chores = new(); }
        Changed?.Invoke();
    }

    public void Save()
    {
        if (_character.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(Chores));
        }
        catch (Exception e) { _log("Could not save: " + e.Message); }
        Changed?.Invoke();
    }
}
