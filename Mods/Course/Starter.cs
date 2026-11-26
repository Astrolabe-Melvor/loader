using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.ItemSales;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;

namespace Astrolabe.Course;

/// <summary>An action the player can be doing: a skill's action component plus what it is working on.</summary>
public sealed class ActionRef
{
    public string Entity { get; set; } = "";   // skill entity id, e.g. "melvor2:Woodcutting"
    public string Target { get; set; } = "";   // what is being done, e.g. a tree, recipe or enemy id
    public string Area { get; set; } = "";     // combat only: the area or dungeon id
    public string Label { get; set; } = "";    // "Woodcutting: Mahogany Tree"

    public override string ToString() => Label;
}

/// <summary>Reads what the player is doing and starts actions, skill by skill.</summary>
static class Starter
{
    /// <summary>Entity id of the pseudo action "sell from the bank".</summary>
    public const string SellEntity = "*sell";

    /// <summary>Target of the pseudo action "train this skill the fastest way" (picks the action as you level).</summary>
    public const string BestTarget = "*best";
    /// <summary>Target of a "most GP" step; with <see cref="AnyEntity"/> it compares every skill.</summary>
    public const string GoldTarget = "*gold";
    public const string AnyEntity = "*any";

    /// <summary>Sells an item down to <paramref name="keep"/>. Locked stacks are never sold.</summary>
    public static string Sell(Game game, string itemId, int keep)
    {
        ItemSaleComponent? sale = game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<ItemSaleComponent>().FirstOrDefault();
        if (sale == null) return "the bank could not be found";
        Item? item = Catalog.FindItem(game, itemId);
        if (item == null) return "item not found";
        IReadonlyItemStack? stack = sale.Inventory.Stacks.FirstOrDefault(st => st.Item == item && !st.IsPlaceholder);
        if (stack == null) return "";
        if (stack.IsLocked) return "the bank stack is locked";
        int sell = ((IInventory)sale.Inventory).GetQuantity(item) - Math.Max(0, keep);
        if (sell > 0) sale.SellItem(item, sale.GetItemStats(item), sell);
        return "";
    }

    /// <summary>The action running right now, or null.</summary>
    public static ActionRef? Current(Game game)
    {
        ActionComponent? comp = game.ActionManager.ActiveActions.FirstOrDefault();
        IDescribedObject? what = comp?.CurrentDescription;
        if (comp == null || what == null) return null;

        var r = new ActionRef
        {
            Entity = comp.Entity.Id,
            Target = what.Id,
            Label = $"{comp.Entity.DisplayName}: {what.DisplayName.Text}",
        };
        if (comp is CombatActionComponent)
        {
            CombatAreasComponent? areas = Areas(comp.Entity);
            BaseCombatArea? area = areas?.CurrentArea?.Area;
            if (area != null)
            {
                r.Area = area.Id;
                if (area is Dungeon) r.Label = $"{comp.Entity.DisplayName}: {area.DisplayName}";
                else r.Label = $"{comp.Entity.DisplayName}: {what.DisplayName.Text} ({area.DisplayName})";
            }
        }
        return r;
    }

    public static ActionComponent? Component(Game game, ActionRef r)
    {
        if (!game.Entities.TryGetObjectById(r.Entity, out GameEntity? entity) || entity == null) return null;
        return entity.Components.Values.OfType<ActionComponent>().FirstOrDefault();
    }

    /// <summary>True when the player is doing exactly this action.</summary>
    public static bool IsRunning(Game game, ActionRef r)
    {
        ActionComponent? comp = Component(game, r);
        if (comp == null || !comp.IsStarted) return false;
        // Agility: the right course on a different path (Simple instead of Advanced) is not this step.
        if (comp is AgilityActionComponent ag && r.Area.Length > 0 && ag.CurrentPath is { } cp && cp.Course.Course.Id == r.Target)
            return cp.Path.Id == r.Area;
        string? now = comp.CurrentDescription?.Id;
        return now == r.Target || (r.Area.Length > 0 && now == r.Area);
    }

    /// <summary>Starts the action. Returns an empty string on success, or why it could not start.</summary>
    public static string Start(Game game, ActionRef r)
    {
        if (IsRunning(game, r)) return "";
        ActionComponent? comp = Component(game, r);
        if (comp == null) return "that skill is not in this game version";

        try
        {
            switch (comp)
            {
                case WoodcuttingActionComponent wc:
                    {
                        var tree = wc.Trees.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (tree == null) return "tree not found";
                        wc.SelectTree(tree);
                        break;
                    }
                case MiningActionComponent mi:
                    {
                        var rock = mi.Rocks.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (rock == null) return "rock not found";
                        mi.SelectRock(rock);
                        break;
                    }
                case FishingActionComponent fi:
                    {
                        var fish = fi.Recipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (fish == null) return "fishing spot not found";
                        fi.SelectRecipe(fish);
                        break;
                    }
                case ThievingActionComponent th:
                    {
                        var target = th.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (target == null) return "thieving target not found";
                        th.TryStartActiveRecipe(target);
                        break;
                    }
                case HuntingActionComponent hu:
                    {
                        var animal = hu.Animals.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (animal == null) return "animal not found";
                        hu.SelectAnimal(animal);
                        hu.StartTracking();
                        break;
                    }
                case AgilityActionComponent ag:
                    {
                        var course = ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (course == null) return "course not found";
                        Finesse.PickPath(game, r);
                        ag.SelectCourse(course);
                        break;
                    }
                case AstrologyActionComponent ast:
                    {
                        var con = ast.ActiveConstellations.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                        if (con == null) return "constellation not found";
                        ast.StudyConstellation(con);
                        break;
                    }
                case CombatActionComponent:
                    {
                        string why = StartCombat(comp.Entity, r);
                        if (why.Length > 0) return why;
                        break;
                    }
                default:
                    {
                        string why = StartCrafting(comp, r);
                        if (why.Length > 0) return why;
                        break;
                    }
            }
        }
        catch (Exception e) { return "the game refused: " + e.Message; }

        return IsRunning(game, r) ? "" : "it would not start (level, unlock or materials missing?)";
    }

    /// <summary>
    /// The actions of a skill you can do now, best XP per hour first (from the wiki's rates when loaded, otherwise
    /// the game's own order, where later actions are the higher-level ones).
    /// </summary>
    /// <summary>
    /// The actions that make the most GP per hour now: what they make at its sell price, minus what they use. Only
    /// actions you can do and whose inputs are in the bank. <paramref name="entityId"/> "*any": every skill.
    /// </summary>
    public static List<ActionRef> GoldFor(Game game, string entityId, Funding funding = Funding.Bank) =>
        GoldScored(game, entityId, funding).Select(x => x.Ref).ToList();

    /// <summary>Most GP's ranking with each action's GP per hour (of your own time when self-funded).</summary>
    public static List<(ActionRef Ref, double Gp)> GoldScored(Game game, string entityId, Funding funding = Funding.Bank)
    {
        PlanData? data = PlanData.Loaded;
        if (data == null) return new();
        Planner? self = funding == Funding.Self ? SelfPlanner(game, data) : null;
        var skills = Catalog.Skills(game).Where(s => entityId == AnyEntity ? !Gear.IsCombat(game, s.Id) : s.Id == entityId).ToList();
        var full = Catalog.Items(game).GroupBy(i => Catalog.Local(i.Id)).ToDictionary(g => g.Key, g => g.First().Id);
        double Price(string local) =>
            full.TryGetValue(local, out string? id) && Catalog.FindItem(game, id) is { } it && it.SellsFor != null ? it.SellsFor.Quantity : 0;
        var scored = new List<(ActionRef Ref, double Gp)>();
        foreach (SkillChoice skill in skills)
        {
            var rates = data.Rates.Where(r => r.Skill == skill.Name || r.Skill == Catalog.Local(skill.Id))
                .GroupBy(r => Catalog.Local(r.Id[(r.Id.IndexOf(':') + 1)..])).ToDictionary(g => g.Key, g => g.First());
            foreach (ActionChoice a in skill.Actions.Where(a => funding == Funding.Bank ? Toolbelt.Usable(game, a) : !a.LockedNow).DistinctBy(a => a.Ref.Target))
            {
                if (!rates.TryGetValue(Catalog.Local(a.Ref.Target), out var r)) continue;
                if (funding != Funding.Bank && Toolbelt.UsesConsumable(game, a.Ref)) continue;
                double sales = r.Out.Sum(o => o.PerHour * Price(o.Item));
                double gp;
                switch (funding)
                {
                    case Funding.None:
                        if (r.In.Count > 0) continue;
                        gp = sales;
                        break;
                    case Funding.Self:
                        // GP per hour of your own time: sales over the hour of making plus the hours of gathering.
                        double ih = self!.InputHours(r);
                        if (double.IsInfinity(ih)) continue;
                        gp = sales / (1 + ih);
                        break;
                    default:
                        if (r.In.Any(i => !full.TryGetValue(i.Item, out string? fid) || Queue.BankCount(game, fid) <= 0)) continue;
                        gp = sales - r.In.Sum(i => i.PerHour * Price(i.Item));
                        break;
                }
                if (gp > 0) scored.Add((a.Ref, gp));
            }
        }
        return scored.OrderByDescending(x => x.Gp).ToList();
    }

    public static List<ActionRef> BestFor(Game game, string entityId, bool gold) => gold ? GoldFor(game, entityId) : BestFor(game, entityId);

    /// <summary>For a step: Most GP, train a technique (Agility), or Fastest XP.</summary>
    public static List<ActionRef> BestFor(Game game, string entityId, string target, Funding funding = Funding.Bank) =>
        target == GoldTarget ? GoldFor(game, entityId, funding)
        : target.StartsWith(Finesse.TechPrefix) ? Finesse.BestForTechnique(game, target[Finesse.TechPrefix.Length..])
        : BestFor(game, entityId, funding);

    public static List<ActionRef> BestFor(Game game, string entityId, Funding funding = Funding.Bank) =>
        BestScored(game, entityId, funding).Select(x => x.Ref).ToList();

    /// <summary>Fastest XP's ranking with each action's XP per hour (NaN where the plan has no rate: Thieving, Agility).</summary>
    public static List<(ActionRef Ref, double Xp)> BestScored(Game game, string entityId, Funding funding = Funding.Bank)
    {
        // Thieving and Agility: rank by the game's live success chances instead of the plan's base rates (they use
        // nothing from the bank, so every funding choice allows them).
        if (Finesse.BestFor(game, entityId) is { } live) return live.Select(r => (r, double.NaN)).ToList();
        SkillChoice? skill = Catalog.Skills(game).FirstOrDefault(s => s.Id == entityId);
        if (skill == null) return new();
        var open = skill.Actions.Select((a, i) => (a, i)).Where(x => funding == Funding.Bank ? Toolbelt.Usable(game, x.a) : !x.a.LockedNow).ToList();
        PlanData? data = PlanData.Loaded;
        if (data != null)
        {
            Planner? self = funding == Funding.Self ? SelfPlanner(game, data) : null;
            var xp = new Dictionary<string, double>();
            foreach (var r in data.Rates)
            {
                if (r.Skill != skill.Name && r.Skill != Catalog.Local(skill.Id)) continue;
                double v = r.Xp;
                if (funding == Funding.None && r.In.Count > 0) v = double.NaN;
                if (self != null)
                {
                    // XP per hour of your own time, gathering the ingredients counted in.
                    double ih = self.InputHours(r);
                    v = double.IsInfinity(ih) ? double.NaN : r.Xp / (1 + ih);
                }
                xp[Catalog.Local(r.Id[(r.Id.IndexOf(':') + 1)..])] = v;
            }
            if (funding != Funding.Bank)
                // Only actions the plan knows and that fit the funding choice (no used-up toolbelt items either).
                open = open.Where(x => xp.TryGetValue(Catalog.Local(x.a.Ref.Target), out double v) && !double.IsNaN(v)
                                       && !Toolbelt.UsesConsumable(game, x.a.Ref)).ToList();
            double Xp((ActionChoice a, int i) x) => xp.TryGetValue(Catalog.Local(x.a.Ref.Target), out double v) ? v : double.NaN;
            return open.OrderByDescending(x => double.IsNaN(Xp(x)) ? -1 : Xp(x))
                       .ThenByDescending(x => x.i).Select(x => (x.a.Ref, Xp(x))).ToList();
        }
        return funding == Funding.Bank ? open.OrderByDescending(x => x.i).Select(x => (x.a.Ref, double.NaN)).ToList() : new();
    }

    // A self-funded planner is costly to build (it prices every item); one per half minute is plenty.
    static Planner? s_selfPlanner;
    static DateTime s_selfPlannerAt;
    static object? s_selfPlannerFor;

    public static Planner SelfPlanner(Game game, PlanData data)
    {
        if (s_selfPlanner == null || s_selfPlannerFor != game || DateTime.UtcNow - s_selfPlannerAt > TimeSpan.FromSeconds(30))
        {
            s_selfPlanner = new Planner(data, game, selfFunded: true);
            s_selfPlannerAt = DateTime.UtcNow;
            s_selfPlannerFor = game;
        }
        return s_selfPlanner;
    }

    /// <summary>The planner's rate for an action (null when the plan doesn't know it).</summary>
    public static PlanData.Rate? RateFor(Game game, PlanData data, ActionRef a)
    {
        SkillChoice? skill = Catalog.Skills(game).FirstOrDefault(s => s.Id == a.Entity);
        if (skill == null) return null;
        string target = Catalog.Local(a.Target);
        return data.Rates.FirstOrDefault(r => (r.Skill == skill.Name || r.Skill == Catalog.Local(skill.Id))
                                              && Catalog.Local(r.Id[(r.Id.IndexOf(':') + 1)..]) == target);
    }


    static CombatAreasComponent? Areas(GameEntity entity) =>
        entity.Components.Values.OfType<CombatAreasComponent>().FirstOrDefault();

    static string StartCombat(GameEntity entity, ActionRef r)
    {
        CombatAreasComponent? areas = Areas(entity);
        if (areas == null) return "combat areas not found";
        BaseCombatArea? area = areas.ActiveAreas.Keys.FirstOrDefault(a => a.Id == r.Area);
        switch (area)
        {
            case Dungeon d:
                areas.SelectDungeon(d);
                return "";
            case CombatArea ca:
                Enemy? enemy = ca.Enemies.FirstOrDefault(e => e.Id == r.Target);
                if (enemy == null) return "monster not found in that area";
                areas.SelectEnemy(ca, enemy);
                return "";
            default:
                return "combat area not found";
        }
    }

    // Every crafting skill (Smithing, Cooking, Firemaking, Crafting, Fletching, Herblore...) shares one generic base:
    // ActiveRecipes[recipe] -> SelectRecipe(active) -> StartCreating(). Generic, so reached by reflection.
    static string StartCrafting(ActionComponent comp, ActionRef r)
    {
        Type t = comp.GetType();
        PropertyInfo? recipesProp = t.GetProperty("ActiveRecipes", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? select = t.GetMethod("SelectRecipe", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? start = t.GetMethod("StartCreating", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (recipesProp?.GetValue(comp) is not IDictionary recipes || select == null || start == null)
            return "this skill is not supported yet";

        object? active = null;
        foreach (DictionaryEntry kv in recipes)
            if (kv.Key is INamespacedObject key && key.Id == r.Target) { active = kv.Value; break; }
        if (active == null) return "recipe not found";

        select.Invoke(comp, new[] { active });
        start.Invoke(comp, null);
        return "";
    }
}
