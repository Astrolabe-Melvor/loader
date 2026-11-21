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

namespace Astrolabe.ActionQueue;

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
