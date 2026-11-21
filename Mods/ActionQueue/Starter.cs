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

namespace Astrolabe.ActionQueue;

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
    public static List<ActionRef> BestFor(Game game, string entityId)
    {
        SkillChoice? skill = Catalog.Skills(game).FirstOrDefault(s => s.Id == entityId);
        if (skill == null) return new();
        var open = skill.Actions.Select((a, i) => (a, i)).Where(x => !x.a.LockedNow).ToList();
        PlanData? data = PlanData.Loaded;
        if (data != null)
        {
            var xp = new Dictionary<string, double>();
            foreach (var r in data.Rates)
                if (r.Skill == skill.Name || r.Skill == Catalog.Local(skill.Id)) xp[Catalog.Local(r.Id[(r.Id.IndexOf(':') + 1)..])] = r.Xp;
            return open.OrderByDescending(x => xp.TryGetValue(Catalog.Local(x.a.Ref.Target), out double v) ? v : -1)
                       .ThenByDescending(x => x.i).Select(x => x.a.Ref).ToList();
        }
        return open.OrderByDescending(x => x.i).Select(x => x.a.Ref).ToList();
    }

    static System.Reflection.FieldInfo? s_activeField;

    /// <summary>
    /// Starts an action while the others keep running (side by side). The game stops every running action when
    /// one starts; so the others are taken off its "running" list for that moment and put back afterwards.
    /// Two steps of the same skill cannot run at once: the new one replaces the old.
    /// </summary>
    public static string StartAlongside(Game game, ActionRef r)
    {
        ActionComponent? comp = Component(game, r);
        s_activeField ??= typeof(GamesByMalcs.Melvor2.Components.Actions.ActiveActionManager)
            .GetField("_activeActions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (comp == null || s_activeField?.GetValue(game.ActionManager) is not System.Collections.Generic.List<ActionComponent> list)
            return Start(game, r);
        var keep = list.Where(a => a != comp).ToList();
        foreach (ActionComponent k in keep) list.Remove(k);
        try { return Start(game, r); }
        finally { foreach (ActionComponent k in keep) if (k.IsStarted && !list.Contains(k)) list.Add(k); }
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
