using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.Toolbelt.Conditions;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Conditions;

namespace Astrolabe.Course;

/// <summary>
/// Some actions need an item in a skill's toolbelt (Warped Cacti needs a Filtered Vial in the Woodcutting toolbelt).
/// Before a step starts, anything like that which is in the bank is put into the toolbelt, as the game's own equip
/// button does.
/// </summary>
static class Toolbelt
{
    public sealed record Need(ToolbeltItemEquippedCondition Condition, string Skill, long Have);

    /// <summary>The action's requirement groups: on the action itself and on its recipe.</summary>
    static IEnumerable<ConditionGroup> Groups(object data)
    {
        foreach (object? o in new[] { data, data.GetType().GetProperty("Recipe")?.GetValue(data) })
        {
            if (o == null) continue;
            foreach (string p in new[] { "Requirements", "UnlockRequirements" })
                if (o.GetType().GetProperty(p)?.GetValue(o) is ConditionGroup g) yield return g;
        }
    }

    /// <summary>Toolbelt items the action needs that are not in place yet.</summary>
    public static List<Need> Missing(Game game, ActionRef r)
    {
        var list = new List<Need>();
        object? data = Catalog.DataOf(game, r);
        if (data == null) return list;
        try
        {
            foreach (ConditionGroup g in Groups(data))
                foreach (Condition c in g.Conditions)
                    if (c is ToolbeltItemEquippedCondition t && !t.IsMet && list.All(n => n.Condition.Item != t.Item))
                        list.Add(new Need(t, t.Component.Entity.DisplayName.ToString(), Queue.BankCount(game, t.Item.Id)));
        }
        catch { }
        return list;
    }

    /// <summary>Puts what the action needs into the toolbelt. Returns what happened, or "" when nothing was needed.</summary>
    public static string Prepare(Game game, ActionRef r)
    {
        var said = new List<string>();
        foreach (Need n in Missing(game, r))
        {
            ToolbeltItemEquippedCondition t = n.Condition;
            string what = $"{t.Item.DisplayName} in the {n.Skill} toolbelt";
            if (n.Have <= 0) { said.Add($"needs {what}, and there is none in the bank"); continue; }
            ToolbeltComponent tb = t.Component;
            if (tb.Entity is not IToolbeltEntity owner) continue;
            ToolbeltSlot? slot = tb.GetAvailableSlotsForItem(t.Item).FirstOrDefault()?.Slot;
            if (slot == null) { said.Add($"needs {what}, but no toolbelt slot can hold it"); continue; }
            owner.ToolbeltEquipping.EquipItem(t.Item, (int)System.Math.Min(int.MaxValue, n.Have), slot);
            said.Add(t.IsMet ? $"put {what}" : $"could not put {what}");
        }
        return string.Join(", ", said);
    }
}
