using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace Astrolabe.Course;

/// <summary>One action the player could pick: a tree, recipe, fishing spot, monster...</summary>
public sealed record ActionChoice(ActionRef Ref, string Name, bool Locked, Godot.Texture2D? Icon = null, object? Key = null)
{
    /// <summary>Locked right now (levels change while playing; <see cref="Locked"/> is from when the list was made).</summary>
    public bool LockedNow => Key == null ? Locked : Catalog.IsLocked(Key);
}

/// <summary>A skill with every action it offers, in the game's own order.</summary>
public sealed record SkillChoice(string Id, string Name, List<ActionChoice> Actions, Godot.Texture2D? Icon = null);

/// <summary>Lists every skill and action, and every item, from the running game.</summary>
static class Catalog
{
    // The dictionary on each action component that maps "what you can do" to its live state. Starter knows how to
    // start an entry from each of these.
    static readonly string[] s_actionTables =
        { "Trees", "Rocks", "Recipes", "ActiveRecipes", "Animals", "Courses", "ActiveConstellations" };

    static List<SkillChoice>? s_skills;
    static List<(string Id, string Name)>? s_items;
    static Game? s_for;

    public static List<SkillChoice> Skills(Game game)
    {
        if (s_for != game) { s_for = game; s_skills = null; s_items = null; }
        return s_skills ??= BuildSkills(game);
    }

    static List<SkillChoice> BuildSkills(Game game)
    {
        var result = new List<SkillChoice>();
        foreach (GameEntity entity in game.Entities.GetAllObjects())
        {
            ActionComponent? comp = entity.Components.Values.OfType<ActionComponent>().FirstOrDefault();
            if (comp == null) continue;
            string skill = entity.DisplayName.ToString();
            var actions = new List<ActionChoice>();

            if (comp is CombatActionComponent)
                AddCombat(entity, skill, actions);
            else
                foreach (string table in s_actionTables)
                {
                    if (comp.GetType().GetProperty(table, BindingFlags.Public | BindingFlags.Instance)?.GetValue(comp) is not IEnumerable entries) continue;
                    foreach (object? entry in entries)
                    {
                        object? key = entry?.GetType().GetProperty("Key")?.GetValue(entry);
                        if (key is not IDescribedObject d) continue;
                        string name = d.DisplayName.Text;
                        actions.Add(new ActionChoice(new ActionRef { Entity = entity.Id, Target = d.Id, Label = $"{skill}: {name}" }, name, IsLocked(key), d.Icon, key));
                    }
                    if (actions.Count > 0) break;
                }

            if (actions.Count > 0) result.Add(new SkillChoice(entity.Id, skill, actions, entity.Icon));
        }
        return result.OrderBy(s => s.Name).ToList();
    }

    static void AddCombat(GameEntity entity, string skill, List<ActionChoice> actions)
    {
        CombatAreasComponent? areas = entity.Components.Values.OfType<CombatAreasComponent>().FirstOrDefault();
        if (areas == null) return;
        foreach (BaseCombatArea area in areas.ActiveAreas.Keys)
        {
            bool locked = !(area.UnlockRequirements?.AreMet ?? true);
            string areaName = area.DisplayName.ToString();
            if (area is Dungeon)
                actions.Add(new ActionChoice(new ActionRef { Entity = entity.Id, Target = area.Id, Area = area.Id, Label = $"{skill}: {areaName}" }, $"{areaName} (dungeon)", locked, area.Icon, area));
            else if (area is CombatArea ca)
                foreach (Enemy e in ca.Enemies)
                {
                    string name = $"{e.DisplayName.Text} ({areaName})";
                    actions.Add(new ActionChoice(new ActionRef { Entity = entity.Id, Target = e.Id, Area = area.Id, Label = $"{skill}: {name}" }, name, locked, ((IDescribedObject)e).Icon, area));
                }
        }
    }

    // Recipes and areas carry a ConditionGroup "Requirements" (or "UnlockRequirements"); AreMet says if it is open now.
    public static bool IsLocked(object key)
    {
        if (QuestOnly(key)) return true;
        try
        {
            foreach (string p in new[] { "Requirements", "UnlockRequirements" })
            {
                object? req = key.GetType().GetProperty(p)?.GetValue(key);
                if (req?.GetType().GetProperty("AreMet")?.GetValue(req) is bool met) return !met;
            }
        }
        catch { }
        return false;
    }

    /// <summary>True when the game hides the action for now: its display requirements are not met, which for quest
    /// recipes (Abyssal Bar, Ship Keel Repair) means their quest step is not active.</summary>
    public static bool QuestOnly(object key)
    {
        try
        {
            foreach (object? o in new[] { key, key.GetType().GetProperty("Recipe")?.GetValue(key) })
                if (o?.GetType().GetProperty("DisplayRequirements")?.GetValue(o) is GamesByMalcs.Melvor2.Conditions.ConditionGroup g
                    && g.Conditions.Count > 0 && !g.AreMet)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>Every item in the game, by name.</summary>
    public static List<(string Id, string Name)> Items(Game game)
    {
        Skills(game);
        if (s_items != null) return s_items;
        var list = new List<(string, string)>();
        try
        {
            foreach (Item i in game.Data.GetDataRegistryOutput<Item>().GetAllObjects())
                list.Add((i.Id, i.DisplayName.ToString()));
        }
        catch
        {
            // Fall back to what is in the bank.
            ActionComponent? any = AnyAction(game);
            if (any != null) foreach (var s in any.Inventory.Stacks) list.Add((s.Item.Id, s.Item.DisplayName.ToString()));
        }
        return s_items = list.DistinctBy(x => x.Item1).OrderBy(x => x.Item2).ToList();
    }

    static readonly Dictionary<string, Godot.Texture2D?> s_itemIcons = new();

    public static Godot.Texture2D? ItemIcon(Game game, string id)
    {
        if (s_itemIcons.TryGetValue(id, out var t)) return t;
        return s_itemIcons[id] = FindItem(game, id)?.Icon;
    }

    /// <summary>The icon of an action, for the table.</summary>
    public static Godot.Texture2D? ActionIcon(Game game, ActionRef r)
    {
        if (r.Entity == Starter.SellEntity) return null;
        SkillChoice? s = Skills(game).FirstOrDefault(x => x.Id == r.Entity);
        if (s == null) return null;
        if (r.Target == Starter.BestTarget) return s.Icon;
        return s.Actions.FirstOrDefault(a => a.Ref.Target == r.Target && a.Ref.Area == r.Area)?.Icon ?? s.Icon;
    }

    // ---- what an action gives and needs ----

    static readonly Dictionary<string, List<Item>> s_outputs = new();
    static readonly Dictionary<string, bool> s_needsInput = new();
    static readonly HashSet<string> s_skipProps = new()
    {
        "Ingredients", "EntryCost", "Cost", "Costs", "Inputs", "Input", "PreferredBait", "Bait",
        "Requirements", "SecondaryRequirements", "DisplayRequirements", "UnlockRequirements",
        "Component", "Stats", "Health", "ActiveEffects", "Entities", "Category", "Subcategory", "GoalCategories",
        "Events", "MinorNodes", "MajorNodes", "StarNodes", "PerMinorNodeBonuses", "Icon", "Media", "InvertedMedia",
        "DisplayName", "Description", "Sprite", "Namespace", "Manager",
    };
    static readonly HashSet<string> s_combatSkills = new() { "Attack", "Strength", "Defence", "Hitpoints", "Ranged", "Magic", "Prayer", "Slayer" };

    public static object? DataOf(Game game, ActionRef r)
    {
        SkillChoice? s = Skills(game).FirstOrDefault(x => x.Id == r.Entity);
        ActionChoice? a = s?.Actions.FirstOrDefault(x => x.Ref.Target == r.Target && x.Ref.Area == r.Area);
        if (a?.Key is CombatArea ca && r.Target != ca.Id) return ca.Enemies.FirstOrDefault(e => e.Id == r.Target);
        return a?.Key;
    }

    /// <summary>Every item an action can give: products, random and rare drops, monster and dungeon loot. Main product first.</summary>
    public static List<Item> Outputs(Game game, ActionRef r)
    {
        string key = r.Entity + "|" + r.Target + "|" + r.Area;
        if (s_outputs.TryGetValue(key, out var hit)) return hit;
        var found = new List<Item>();
        object? data = DataOf(game, r);
        if (data != null)
            try { Walk(data, found, new HashSet<object>(ReferenceEqualityComparer.Instance), 0); }
            catch { }
        return s_outputs[key] = found.Distinct().ToList();
    }

    /// <summary>True when the action uses up items (recipes, agility entry costs) and so can stop by itself.</summary>
    public static bool NeedsInput(Game game, ActionRef r)
    {
        string key = r.Entity + "|" + r.Target + "|" + r.Area;
        if (s_needsInput.TryGetValue(key, out bool hit)) return hit;
        bool needs = false;
        object? data = DataOf(game, r);
        if (data != null)
            foreach (string p in new[] { "Ingredients", "EntryCost" })
                if (data.GetType().GetProperty(p)?.GetValue(data) is object ing
                    && ing.GetType().GetProperty("Items")?.GetValue(ing) is IEnumerable items && items.Cast<object>().Any())
                    needs = true;
        return s_needsInput[key] = needs;
    }

    static void Walk(object o, List<Item> found, HashSet<object> seen, int depth)
    {
        if (depth > 6 || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !seen.Add(o)) return;
        if (o is Item item) { found.Add(item); return; }
        if (o is Enemy enemy) { if (enemy.Drops != null) Walk(enemy.Drops, found, seen, depth + 1); return; }
        if (o is IDictionary) return;
        if (o is IEnumerable list)
        {
            // Lists of other game objects (passives, skills) are not loot; items and monsters are.
            foreach (object? x in list)
                if (x != null && (x is Item || x is Enemy || x is not GamesByMalcs.Melvor2.Data.NamespacedObject)) Walk(x, found, seen, depth + 1);
            return;
        }
        Type t = o.GetType();
        if (t.Namespace?.StartsWith("GamesByMalcs") != true) return;
        // Other game objects (skills, categories) lead away from this action; only its own recipe is followed.
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || s_skipProps.Contains(p.Name)) continue;
            Type pt = p.PropertyType;
            if (pt.IsPrimitive || pt.IsEnum || pt == typeof(string)) continue;
            bool ownRecipe = p.Name.Contains("Recipe");
            if (!ownRecipe && typeof(GamesByMalcs.Melvor2.Data.NamespacedObject).IsAssignableFrom(pt)
                && !typeof(Item).IsAssignableFrom(pt) && !typeof(Enemy).IsAssignableFrom(pt)) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v != null) Walk(v, found, seen, depth + 1);
        }
    }

    /// <summary>Skills a step of this action can level: its own, or for combat the combat skills.</summary>
    public static List<GameEntity> LevelSkills(Game game, string entityId)
    {
        var all = game.LeveledEntities.OfType<GameEntity>().ToList();
        GameEntity? own = all.FirstOrDefault(e => e.Id == entityId);
        bool combat = game.Entities.TryGetObjectById(entityId, out GameEntity? ent) && ent is CombatEntity;
        if (!combat) return own != null ? new() { own } : new();
        var list = all.Where(e => s_combatSkills.Contains(e.GetType().Namespace?.Split('.').Last() ?? "")).OrderBy(e => e.DisplayName.ToString()).ToList();
        if (own != null) list.Insert(0, own);
        return list;
    }

    static Dictionary<string, string>? s_passive;
    static Game? s_passiveFor;

    /// <summary>"Farming", "Ranching" or "the mill" when a passive skill gives the item (they run alongside the queue
    /// and are not queue steps), else null.</summary>
    public static string? PassiveSource(Game game, string itemId)
    {
        if (s_passive == null || s_passiveFor != game)
        {
            s_passiveFor = game;
            s_passive = new();
            void From<T>(string source, Func<T, IEnumerable<Item?>> items) where T : GamesByMalcs.Melvor2.Data.NamespacedObject
            {
                try
                {
                    foreach (T r in game.Data.GetDataRegistryOutput<T>().GetAllObjects())
                        foreach (Item? i in items(r)) if (i != null) s_passive!.TryAdd(Local(i.Id), source);
                }
                catch { }
            }
            From<GamesByMalcs.Melvor2.Entities.Farming.FarmingRecipe>("Farming", r => new[] { r.Product?.Item });
            From<GamesByMalcs.Melvor2.Entities.Farming.GrainMillRecipe>("the mill", r => new[] { r.Product?.Item });
            From<GamesByMalcs.Melvor2.Entities.Ranching.RanchingAnimal>("Ranching", r => r.Products.Select(p => (Item?)p.Item));
        }
        return s_passive.TryGetValue(Local(itemId), out string? src) ? src : null;   // short or full id
    }

    static Dictionary<string, ActionChoice?>? s_monsterFor;
    static Game? s_monsterGame;

    /// <summary>A monster that drops the item (short id), one you can fight now first, single monsters before
    /// dungeons. Null when no monster drops it.</summary>
    public static ActionChoice? MonsterFor(Game game, string localItem)
    {
        if (s_monsterFor == null || s_monsterGame != game)
        {
            s_monsterGame = game;
            s_monsterFor = new();
            SkillChoice? combat = Skills(game).FirstOrDefault(s => Gear.IsCombat(game, s.Id));
            if (combat != null)
                foreach (ActionChoice a in combat.Actions
                             .OrderBy(a => a.LockedNow)
                             .ThenBy(a => a.Ref.Area == a.Ref.Target && a.Ref.Area.Length > 0))
                    foreach (Item i in Outputs(game, a.Ref))
                        s_monsterFor.TryAdd(Local(i.Id), a);
        }
        return s_monsterFor.TryGetValue(localItem, out ActionChoice? hit) ? hit : null;
    }

    public static string Local(string id) => id.Contains(':') ? id[(id.LastIndexOf(':') + 1)..] : id;

    public static Item? FindItem(Game game, string id)
    {
        try { if (game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(id, out Item? item)) return item; }
        catch { }
        return null;
    }

    public static ActionComponent? AnyAction(Game game) =>
        game.ActionManager.ActiveActions.FirstOrDefault()
        ?? game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<ActionComponent>().FirstOrDefault();
}
