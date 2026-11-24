using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GamesByMalcs.Melvor2;

namespace Astrolabe.Course;

/// <summary>
/// Queues as text to paste anywhere: "CRS1:" + base64 (older "AQ1:" codes still load) of a small JSON. Ids are written without their namespace
/// ("Woodcutting:MahoganyTree"), so the Astrolabe wiki can write the same codes from its planner.
/// </summary>
/// <remarks>
/// Format: { "loop":false, "steps":[ { "a":"Skill:Action", "area":"Area", "g":"BankCount", "n":100,
/// "i":"Item", "s":"Skill", "l":1 } ] }. "a" is "Skill:*best" to train a skill, "*sell" to sell "i", "*shop:Purchase" to buy until the bank holds "n" of "i".
/// </remarks>
static class ShareCode
{
    const string Prefix = "CRS1:";
    const string OldPrefix = "AQ1:";

    public static string Export(QueueState q)
    {
        var steps = new JsonArray();
        foreach (Step s in q.Steps)
        {
            var o = new JsonObject
            {
                ["a"] = s.IsSell ? Starter.SellEntity : s.IsShop ? $"{ShopSource.Entity}:{Catalog.Local(s.Action.Target)}" : $"{Catalog.Local(s.Action.Entity)}:{(s.IsBest ? s.Action.Target : Catalog.Local(s.Action.Target))}",
                ["g"] = s.Goal.ToString(),
                ["n"] = s.Amount,
            };
            if (s.Action.Area.Length > 0) o["area"] = Catalog.Local(s.Action.Area);
            if (s.Item.Length > 0) o["i"] = Catalog.Local(s.Item);
            if (s.Skill.Length > 0) o["s"] = Catalog.Local(s.Skill);
            steps.Add(o);
        }
        var root = new JsonObject { ["loop"] = q.Loop, ["steps"] = steps };
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    /// <summary>Reads a code. Steps the game does not know are reported, not added.</summary>
    public static (List<Step> Steps, List<string> Problems) Import(Game game, string code)
    {
        var steps = new List<Step>();
        var problems = new List<string>();
        code = code.Trim();
        int skip = code.StartsWith(Prefix) ? Prefix.Length : code.StartsWith(OldPrefix) ? OldPrefix.Length : -1;
        if (skip < 0) { problems.Add("That is not a Course code (they start with CRS1:)."); return (steps, problems); }

        JsonNode? root;
        try { root = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(code[skip..]))); }
        catch { problems.Add("The code is damaged or incomplete."); return (steps, problems); }

        var skills = Catalog.Skills(game);
        var items = Catalog.Items(game);
        string? ItemId(string local) => items.FirstOrDefault(i => Catalog.Local(i.Id) == local).Id;
        string ItemName(string id) => items.FirstOrDefault(i => i.Id == id).Name ?? id;
        SkillChoice? Skill(string local) => skills.FirstOrDefault(s => Catalog.Local(s.Id) == local || s.Name == local);

        foreach (JsonNode? n in root?["steps"]?.AsArray() ?? new JsonArray())
        {
            if (n == null) continue;
            string a = n["a"]?.GetValue<string>() ?? "";
            var step = new Step
            {
                Goal = Enum.TryParse(n["g"]?.GetValue<string>(), out Goal g) ? g : Goal.UntilStops,
                Amount = n["n"]?.GetValue<int>() ?? 1,
            };

            string itemLocal = n["i"]?.GetValue<string>() ?? "";
            if (itemLocal.Length > 0)
            {
                string? id = ItemId(itemLocal);
                if (id == null) { problems.Add($"Unknown item {itemLocal}"); continue; }
                step.Item = id;
                step.ItemName = ItemName(id);
            }

            if (a == Starter.SellEntity)
            {
                step.Action = new ActionRef { Entity = Starter.SellEntity, Label = $"Sell from bank: {step.ItemName}" };
                steps.Add(step);
                continue;
            }

            if (a.StartsWith(ShopSource.Entity + ":"))
            {
                var p = ShopSource.ById(game, a[(ShopSource.Entity.Length + 1)..]);
                if (p == null) { problems.Add($"Unknown shop purchase in {a}"); continue; }
                step.Action = ShopSource.Ref(p);
                steps.Add(step);
                continue;
            }

            int colon = a.IndexOf(':');
            if (a == $"{Starter.AnyEntity}:{Starter.GoldTarget}")
            {
                step.Action = new ActionRef { Entity = Starter.AnyEntity, Target = Starter.GoldTarget, Label = "Most GP: any skill" };
                steps.Add(step);
                continue;
            }
            SkillChoice? skill = colon > 0 ? Skill(a[..colon]) : null;
            string target = colon > 0 ? a[(colon + 1)..] : "";
            if (skill == null) { problems.Add($"Unknown skill in {a}"); continue; }

            if (target == Starter.BestTarget)
                step.Action = new ActionRef { Entity = skill.Id, Target = Starter.BestTarget, Label = $"Train {skill.Name}" };
            else if (target == Starter.GoldTarget)
                step.Action = new ActionRef { Entity = skill.Id, Target = Starter.GoldTarget, Label = $"Most GP: {skill.Name}" };
            else
            {
                string area = n["area"]?.GetValue<string>() ?? "";
                ActionChoice? act = skill.Actions.FirstOrDefault(x => Catalog.Local(x.Ref.Target) == target && (area.Length == 0 || Catalog.Local(x.Ref.Area) == area));
                if (act == null) { problems.Add($"Unknown action {a}"); continue; }
                step.Action = act.Ref;
            }

            string skillLocal = n["s"]?.GetValue<string>() ?? "";
            if (step.Goal == Goal.Level)
            {
                SkillChoice? lv = skillLocal.Length > 0 ? Skill(skillLocal) : skill;
                var leveled = game.LeveledEntities.OfType<GamesByMalcs.Melvor2.Entities.GameEntity>()
                    .FirstOrDefault(e => Catalog.Local(e.Id) == (skillLocal.Length > 0 ? skillLocal : Catalog.Local(skill.Id)));
                step.Skill = leveled?.Id ?? lv?.Id ?? skill.Id;
                step.SkillName = leveled?.DisplayName.ToString() ?? lv?.Name ?? skill.Name;
            }
            steps.Add(step);
        }
        return (steps, problems);
    }
}
