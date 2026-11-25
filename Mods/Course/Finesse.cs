using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Thieving;
using MelvorModLoader;

namespace Astrolabe.Course;

/// <summary>
/// Thieving and Agility, done well: live XP rates from the game's own success chances (so "Fastest XP" picks what
/// actually pays best for you now), the best agility path, the best gear you own for it (measured, not guessed),
/// and food: the skill's buff food (sushi for stealth, soup for agility) stays selected, and when HP gets near what
/// one failure can take, it switches to the best healing food, eats, and switches back.
/// </summary>
static class Finesse
{
    // ---- what is running ----

    public static ThievingActionComponent? Thieving(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<ThievingActionComponent>().FirstOrDefault();

    public static AgilityActionComponent? Agility(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<AgilityActionComponent>().FirstOrDefault();

    public static bool Handles(Game game, string entityId) =>
        Thieving(game)?.Entity.Id == entityId || Agility(game)?.Entity.Id == entityId;

    // ---- XP rates ----

    /// <summary>Expected XP per hour at a thieving target, from its live success chance, stages, interval and stuns.</summary>
    public static double ThieveXpHour(ActiveThievingRecipe r)
    {
        try
        {
            var s = r.Stats;
            double p = Math.Clamp(s.SuccessChance.Value, 0, 1);
            double stun = (1 - p) * (1 - Math.Clamp(s.StunAvoidanceChance.Total.Value, 0, 1)) * s.StunInterval.Value;
            double perTry = s.Interval.Value + stun;            // ms
            int stages = Math.Max(1, s.MaxStages.Value);
            double xp = s.Experience.XpToAdd.Value;
            return perTry <= 0 ? 0 : p * xp / stages / perTry * 3_600_000;
        }
        catch { return 0; }
    }

    /// <summary>Expected XP per hour on an agility path: a fail sends you back to the start, so the expected time to finish
    /// is each obstacle's time weighted by the chance of reaching it, over the chance of clearing the lot.</summary>
    public static double PathXpHour(ActiveAgilityCoursePath path)
    {
        try
        {
            double reach = 1, attempt = 0;
            foreach (ActiveAgilityObstacle o in path.Obstacles)
            {
                if (o.ShortcutCreated) continue;
                attempt += reach * o.Stats.Interval.Value;
                reach *= Math.Clamp(o.GetSuccessChance(), 0, 1);
            }
            if (reach <= 0 || attempt <= 0) return 0;
            double perFinish = attempt / reach;   // ms
            return path.Stats.Experience.XpToAdd.Value / perFinish * 3_600_000;
        }
        catch { return 0; }
    }

    public static ActiveAgilityCoursePath? BestPath(ActiveAgilityCourse c) =>
        c.ActivePaths.Values.OrderByDescending(PathXpHour).FirstOrDefault();

    /// <summary>"Fastest XP" for Thieving or Agility: open targets by live XP per hour (null when the skill isn't one of these).</summary>
    public static List<ActionRef>? BestFor(Game game, string entityId)
    {
        SkillChoice? skill = Catalog.Skills(game).FirstOrDefault(s => s.Id == entityId);
        if (skill == null) return null;
        var open = skill.Actions.Where(a => !a.LockedNow).DistinctBy(a => a.Ref.Target).ToList();   // one entry per agility course
        if (Thieving(game) is { } th && th.Entity.Id == entityId)
        {
            double Rate(ActionChoice a)
            {
                var r = th.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == a.Ref.Target).Value;
                if (r == null) return -1;
                // A target that needs a key or lockpick counts only if the bank has one to put on.
                if (Toolbelt.Missing(game, a.Ref).Any(n => n.Have <= 0)) return -1;
                return ThieveXpHour(r);
            }
            return open.Select(a => (a, x: Rate(a))).Where(t => t.x > 0).OrderByDescending(t => t.x).Select(t => t.a.Ref).ToList();
        }
        if (Agility(game) is { } ag && ag.Entity.Id == entityId)
        {
            double Rate(ActionChoice a)
            {
                var c = ag.Courses.FirstOrDefault(kv => kv.Key.Id == a.Ref.Target).Value;
                if (c == null || !c.Course.Requirements.AreMet) return -1;
                if (Toolbelt.Missing(game, a.Ref).Any(n => n.Have <= 0)) return -1;
                // Courses with an entry cost (arenas) only when you can pay it.
                if (c.Course.EntryCost != null && ag.GetRecipeIngrendientsTransaction(c.Course.EntryCost).Validate() != GamesByMalcs.Melvor2.Core.InventoryWalletTransactions.InventoryWalletTransactionResult.Success) return -1;
                return BestPath(c) is { } p ? PathXpHour(p) : -1;
            }
            ActionRef WithPath(ActionRef r)
            {
                var c = ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
                return c != null && BestPath(c) is { } p ? new ActionRef { Entity = r.Entity, Target = r.Target, Area = p.Path.Id, Label = $"{r.Label} ({p.Path.DisplayName})" } : r;
            }
            return open.Select(a => (a, x: Rate(a))).Where(t => t.x > 0).OrderByDescending(t => t.x).Select(t => WithPath(t.a.Ref)).ToList();
        }
        return null;
    }

    /// <summary>Before an agility course starts: the path the step chose (kept in the ref's Area), else the best one.</summary>
    public static void PickPath(Game game, ActionRef r)
    {
        if (Agility(game) is not { } ag) return;
        var c = ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
        if (c == null) return;
        ActiveAgilityCoursePath? p = c.ActivePaths.Values.FirstOrDefault(x => x.Path.Id == r.Area) ?? BestPath(c);
        if (p != null && c.SelectedPath != p) c.SelectPath(p);
    }

    // ---- techniques ----

    /// <summary>Target of a "train a technique" step: "*tech:" and the technique's id.</summary>
    public const string TechPrefix = "*tech:";

    public static ActionRef TechRef(string skillId, ActiveAgilityTechnique t) =>
        new() { Entity = skillId, Target = TechPrefix + t.Technique.Id, Label = $"Train {t.Technique.DisplayName} technique" };

    public static List<ActiveAgilityTechnique> Techniques(Game game) =>
        Agility(game)?.TechniquesComponent.ActiveTechniques.Values.Where(t => t.Technique.UnlockRequirements?.AreMet ?? true).ToList() ?? new();

    public static ActiveAgilityTechnique? Technique(Game game, string id) =>
        Agility(game)?.TechniquesComponent.ActiveTechniques.Values.FirstOrDefault(t => t.Technique.Id == id);

    /// <summary>Technique XP per hour on a path: the technique's share of each finished run, over the expected time to finish.</summary>
    public static double TechXpHour(ActiveAgilityCoursePath path, ActiveAgilityTechnique tech)
    {
        try
        {
            if (!path.GetTechniqueExperience().TryGetValue(tech, out double xp) || xp <= 0) return 0;
            double reach = 1, attempt = 0;
            foreach (ActiveAgilityObstacle o in path.Obstacles)
            {
                if (o.ShortcutCreated) continue;
                attempt += reach * o.Stats.Interval.Value;
                reach *= Math.Clamp(o.GetSuccessChance(), 0, 1);
            }
            return reach <= 0 || attempt <= 0 ? 0 : xp / (attempt / reach) * 3_600_000;
        }
        catch { return 0; }
    }

    /// <summary>Courses (each with its best path for the technique in Area) by technique XP per hour.</summary>
    public static List<ActionRef> BestForTechnique(Game game, string techId)
    {
        var list = new List<(ActionRef, double)>();
        if (Agility(game) is not { } ag || Technique(game, techId) is not { } tech) return new();
        SkillChoice? skill = Catalog.Skills(game).FirstOrDefault(s => s.Id == ag.Entity.Id);
        if (skill == null) return new();
        foreach (ActionChoice a in skill.Actions.Where(a => !a.LockedNow).DistinctBy(a => a.Ref.Target))
        {
            var c = ag.Courses.FirstOrDefault(kv => kv.Key.Id == a.Ref.Target).Value;
            if (c == null || !c.Course.Requirements.AreMet || Toolbelt.Missing(game, a.Ref).Any(n => n.Have <= 0)) continue;
            if (c.Course.EntryCost != null && ag.GetRecipeIngrendientsTransaction(c.Course.EntryCost).Validate() != GamesByMalcs.Melvor2.Core.InventoryWalletTransactions.InventoryWalletTransactionResult.Success) continue;
            var best = c.ActivePaths.Values.Select(p => (p, x: TechXpHour(p, tech))).OrderByDescending(t => t.x).FirstOrDefault();
            if (best.p == null || best.x <= 0) continue;
            list.Add((new ActionRef { Entity = a.Ref.Entity, Target = a.Ref.Target, Area = best.p.Path.Id, Label = $"{a.Ref.Label} ({best.p.Path.DisplayName})" }, best.x));
        }
        return list.OrderByDescending(t => t.Item2).Select(t => t.Item1).ToList();
    }

    /// <summary>A short note for the step list: "about 38k XP/h at 87% success".</summary>
    public static string RateNote(Game game, ActionRef r)
    {
        if (Thieving(game) is { } th && th.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } t)
            return $"about {Num(ThieveXpHour(t))} XP/h at {t.Stats.SuccessChance.Value:P0} success";
        if (Agility(game) is { } ag && ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } c
            && (c.ActivePaths.Values.FirstOrDefault(x => x.Path.Id == r.Area) ?? BestPath(c)) is { } p)
        {
            var techs = Techniques(game).Select(t => (t, x: TechXpHour(p, t))).Where(t => t.x > 0).Select(t => $"{t.t.Technique.DisplayName} {Num(t.x)}");
            return $"{p.Path.DisplayName}: about {Num(PathXpHour(p))} XP/h at {p.GetTotalSuccessChance():P0} per run; technique XP/h: {string.Join(", ", techs)}";
        }
        return "";
    }

    static string Num(double v) => v >= 1e6 ? $"{v / 1e6:0.##}M" : v >= 1e4 ? $"{v / 1e3:0.#}k" : $"{v:N0}";

    // ---- scoring what you wear and eat ----

    /// <summary>What gear and buff food are judged by: XP per hour first, then loot (fortune and rare chance) for thieving,
    /// or less damage for agility. Null when the action can't run (a needed key or lockpick came off, say).</summary>
    static (double Main, double Tie)? Score(Game game, ActionRef r)
    {
        if (Toolbelt.Missing(game, r).Count > 0) return null;
        if (Thieving(game) is { } th && th.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } t)
            return (ThieveXpHour(t), t.Stats.Fortune.Value + t.Stats.RareLootChance.Value * 100);
        if (Agility(game) is { } ag && ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } c && (c.SelectedPath ?? BestPath(c)) is { } p)
            return (PathXpHour(p), -p.GetMaxDamageOnFail());
        return null;
    }

    static bool Better((double Main, double Tie) a, (double Main, double Tie) b) =>
        a.Main > b.Main * 1.001 || (a.Main >= b.Main * 0.999 && a.Tie > b.Tie + 1e-9);

    /// <summary>Tries each item you own in each toolbelt slot (not consumables) and keeps whatever scores best.
    /// Returns what changed, for the log.</summary>
    public static string BestGear(Game game, ActionRef r)
    {
        string skill = r.Entity;
        if (!game.Entities.TryGetObjectById(skill, out GameEntity? e) || e is not IToolbeltEntity owner) return "";
        var changed = new List<string>();
        foreach (Belts.SlotInfo si0 in Belts.Slots(game, skill))
        {
            if (!si0.Unlocked) continue;
            ToolbeltSlot slot = si0.Slot;
            Item? start = si0.Item;
            if (start != null && Belts.IsConsumable(start)) continue;   // potions and the like stay as they are
            var candidates = Belts.ItemsFor(game, skill, slot).Where(i => !Belts.IsConsumable(i)).ToList();
            if (candidates.Count == 0 || (candidates.Count == 1 && candidates[0] == start)) continue;

            Item? best = start;
            var bestScore = Score(game, r);
            foreach (Item cand in candidates)
            {
                if (cand == start) continue;
                owner.ToolbeltEquipping.EquipItem(cand, 1, slot);
                if (!owner.Toolbelt.IsItemEquipped(cand)) continue;
                var sc = Score(game, r);
                if (sc != null && (bestScore == null || Better(sc.Value, bestScore.Value))) { best = cand; bestScore = sc; }
            }
            // Put the winner on (or the original back).
            Belts.SlotInfo? now = Belts.Slots(game, skill).FirstOrDefault(s => s.Slot == slot);
            if (best != null && now?.Item != best) owner.ToolbeltEquipping.EquipItem(best, 1, slot);
            else if (best == null && now?.Item != null) owner.ToolbeltEquipping.UnequipSlot(slot);
            if (best != start && best != null) changed.Add($"{best.DisplayName}");
        }
        return changed.Count == 0 ? "" : "put on " + string.Join(", ", changed);
    }

    // ---- food ----

    static IEnumerable<(Item Item, FoodCapability Food)> Foods(Game game)
    {
        var any = Catalog.AnyAction(game);
        if (any == null) yield break;
        foreach (var st in any.Inventory.Stacks)
            if (!st.IsPlaceholder && st.Quantity > 0 && st.Item.Capabilities.TryGet<FoodCapability>(out FoodCapability? f) && f != null)
                yield return (st.Item, f);
    }

    /// <summary>The bank's food with a bonus for this skill that scores best (the best sushi for thieving), or null.</summary>
    public static Item? BuffFood(Game game, IFoodEntity owner, ActionRef r)
    {
        FoodComponent fc = owner.Food;
        var buffs = Foods(game).Where(x => x.Food.Bonuses?.Component == fc).ToList();
        if (buffs.Count == 0) return null;
        if (buffs.Count == 1) return buffs[0].Item;
        Item? was = fc.SelectedFood?.Item;
        Item? best = null;
        int bestHeal = 0;
        (double Main, double Tie)? bestScore = null;
        foreach (var (item, food) in buffs)
        {
            fc.SelectFood(item);
            var sc = Score(game, r);
            if (sc == null) continue;
            // Better score wins; an equal score goes to the bigger heal.
            bool tie = bestScore != null && !Better(sc.Value, bestScore.Value) && !Better(bestScore.Value, sc.Value);
            if (bestScore == null || Better(sc.Value, bestScore.Value) || (tie && food.BaseHealing > bestHeal))
            {
                best = item;
                bestScore = sc;
                bestHeal = food.BaseHealing;
            }
        }
        if (was != null) fc.SelectFood(was); else fc.UnselectFood();
        return best;
    }

    /// <summary>The bank's biggest heal, preferring food without a bonus for some skill (so sushi isn't eaten as plain food).</summary>
    static Item? HealFood(Game game) =>
        Foods(game).OrderBy(x => x.Food.Bonuses != null ? 1 : 0).ThenByDescending(x => x.Food.BaseHealing).Select(x => x.Item).FirstOrDefault();

    /// <summary>Most HP one failure can take right now.</summary>
    static int MaxHit(Game game, ActionRef r)
    {
        if (Thieving(game) is { } th && th.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } t) return t.Recipe.Damage;
        if (Agility(game) is { } ag && ag.Courses.FirstOrDefault(kv => kv.Key.Id == r.Target).Value is { } c
            && (c.ActivePaths.Values.FirstOrDefault(x => x.Path.Id == r.Area) ?? BestPath(c)) is { } p)
        {
            try { return p.GetMaxDamageOnFail(); } catch { }
        }
        return 0;
    }

    /// <summary>Keeps the buff food selected; when HP is within one failure of running out (or below the floor you set),
    /// switches to healing food, eats up, and switches back. Returns what it did, or "".</summary>
    public static string Feed(Game game, ActionRef r, Item? buff, int floorPercent)
    {
        if (!game.Entities.TryGetObjectById(r.Entity, out GameEntity? e) || e is not IFoodEntity owner) return "";
        FoodComponent fc = owner.Food;
        ICharacterHealth? hp = (e as ThievingEntity)?.Health ?? (e as AgilityEntity)?.Health;
        if (hp == null) return "";
        int maxHit = MaxHit(game, r);
        // The game auto-eats whatever food is selected (the sushi) at its threshold. Heal first: at the floor you set,
        // or as soon as one failure could push HP down to that threshold.
        double autoEat = 0.25;
        try { if (fc.Stats != null) autoEat = fc.Stats.AutoEatThreshold.Value; } catch { }
        int autoEatHp = (int)Math.Ceiling(hp.Max * autoEat);
        int low = Math.Max(autoEatHp + maxHit, hp.Max * floorPercent / 100);
        string did = "";
        if (hp.Current <= low)
        {
            Item? heal = HealFood(game);
            if (heal == null) return buff != null && fc.SelectedFood?.Item == buff ? "" : "no healing food in the bank";
            fc.SelectFood(heal);
            int ate = 0, before = hp.Current;
            // Eat up to near full: stop when one more would mostly be wasted.
            while (ate < 50 && Queue.BankCount(game, heal.Id) > 0 && fc.Stats != null && hp.Max - hp.Current >= fc.Stats.Healing.Value * 0.6)
            {
                fc.ManualEat();
                ate++;
            }
            if (ate > 0) did = $"ate {ate} {heal.DisplayName} ({before} to {hp.Current} HP)";
        }
        Item? want = buff != null && Queue.BankCount(game, buff.Id) > 0 ? buff : null;
        if (want != null && fc.SelectedFood?.Item != want) fc.SelectFood(want);
        else if (want == null && did.Length > 0 && HealFood(game) is { } h && fc.SelectedFood?.Item != h) fc.SelectFood(h);
        return did;
    }
}
