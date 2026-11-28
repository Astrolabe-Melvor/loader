using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;

namespace Astrolabe.Course;

/// <summary>
/// A skill's other way of making a recipe: the Sacrificial Anvil in Smithing (what you make is destroyed for its sale
/// value, with a 50/50 roll for 1.1x XP or 1.1x GP) and Astral Offerings in Beastcrafting (weapons and armour; about
/// 1% per recipe step of a Gift of Ceedyr instead of the item). The game keeps the choice per recipe.
/// </summary>
static class RewardModes
{
    /// <summary>The mode this recipe can use, with the recipe itself; null when the action has none.</summary>
    public static AlternativeRewardMode? For(Game game, ActionRef r, out ActiveArtisanRecipe? recipe)
    {
        recipe = null;
        if (Starter.Component(game, r) is not ArtisanActionComponent art) return null;
        recipe = art.ActiveRecipes.FirstOrDefault(kv => kv.Key.Id == r.Target).Value;
        if (recipe == null) return null;
        // Quest items can't be sacrificed (the game's data rules them out).
        if (recipe.Recipe.Category.Id.EndsWith(":Quests", StringComparison.Ordinal)) return null;
        foreach (AlternativeRewardMode m in art.Config.RewardModes)
            if (m.ShowAlternativeMode(recipe)) return m;
        return null;
    }

    /// <summary>The mode for this action when you have unlocked it, else null.</summary>
    public static AlternativeRewardMode? Unlocked(Game game, ActionRef r)
    {
        try { return For(game, r, out _) is { } m && m.UnlockRequirements.AreMet ? m : null; }
        catch { return null; }
    }

    /// <summary>The first mode a skill offers (for the Add a step form before a recipe is picked).</summary>
    public static AlternativeRewardMode? OfSkill(Game game, string entityId)
    {
        try
        {
            return Starter.Component(game, new ActionRef { Entity = entityId }) is ArtisanActionComponent art
                ? art.Config.RewardModes.FirstOrDefault() : null;
        }
        catch { return null; }
    }

    public static bool IsAnvil(AlternativeRewardMode m) => m is SacrificialAnvilRewardMode;

    /// <summary>You have unlocked the Sacrificial Anvil in a skill this Most GP step can pick (any skill, or that one).</summary>
    public static bool AnvilUnlocked(Game game, string entityId)
    {
        try
        {
            foreach (SkillChoice sk in Catalog.Skills(game))
                if ((entityId == Starter.AnyEntity || sk.Id == entityId) && OfSkill(game, sk.Id) is SacrificialAnvilRewardMode m && m.UnlockRequirements.AreMet)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>Average XP and GP compared with making the item normally (and selling it, for the GP).</summary>
    public static (double Xp, double Gp) Gain(AlternativeRewardMode m)
    {
        if (m is not SacrificialAnvilRewardMode a || a.TotalWeight <= 0) return (1, 1);
        double total = a.TotalWeight;
        return (1 + a.BonusXpWeight / total * (a.BonusXpMultiplier - 1), 1 + a.BonusSaleValueWeight / total * (a.BonusSaleValueMultiplier - 1));
    }

    public static string Name(AlternativeRewardMode m) => m.DisplayName.ToString();

    /// <summary>One line on what the mode does, for the form.</summary>
    public static string What(AlternativeRewardMode m)
    {
        if (m is SacrificialAnvilRewardMode)
        {
            var (xp, gp) = Gain(m);
            return $"what it makes is destroyed for its sale value: about {(xp - 1) * 100:0}% more XP and {(gp - 1) * 100:0}% more GP than making and selling it";
        }
        return "about 1% per recipe step: a Gift of Ceedyr instead of the item (XP as normal)";
    }

    static string Key(ActionRef r) => r.Entity + "|" + r.Target;

    /// <summary>
    /// Puts the recipe's mode on or off. The first change to a recipe keeps what it was in
    /// <see cref="QueueState.RewardRestore"/>, so <see cref="Restore"/> can put it back. Returns a note, or "".
    /// </summary>
    public static string Set(Game game, QueueState state, ActionRef r, bool on)
    {
        AlternativeRewardMode? mode = For(game, r, out ActiveArtisanRecipe? recipe);
        if (mode == null || recipe == null) return "";
        AlternativeRewardMode? now = recipe.CurrentRewardMode;
        AlternativeRewardMode? want = on && mode.UnlockRequirements.AreMet ? mode : null;
        if (now == want) return "";
        string key = Key(r);
        if (!state.RewardRestore.ContainsKey(key)) state.RewardRestore[key] = now?.Id ?? "";
        // The game only toggles: picking the mode that is on turns it off.
        if (now != null) recipe.ToggleRewardMode(now);
        if (want != null) recipe.ToggleRewardMode(want);
        if (recipe.CurrentRewardMode != want) return "";
        return want != null ? $"using {Name(want)}" : $"{Name(mode)} off for this step";
    }

    /// <summary>Puts back the mode of every recipe a step changed that no running step still wants changed.</summary>
    public static void Restore(Game game, QueueState state, IEnumerable<ActionRef> inUse)
    {
        if (state.RewardRestore.Count == 0) return;
        var busy = inUse.Select(Key).ToHashSet();
        foreach (string key in state.RewardRestore.Keys.ToList())
        {
            if (busy.Contains(key)) continue;
            string was = state.RewardRestore[key];
            state.RewardRestore.Remove(key);
            string[] p = key.Split('|');
            if (p.Length != 2) continue;
            try
            {
                var r = new ActionRef { Entity = p[0], Target = p[1] };
                if (For(game, r, out ActiveArtisanRecipe? recipe) is not { } mode || recipe == null) continue;
                bool on = was.Length > 0 && mode.Id == was;
                if ((recipe.CurrentRewardMode != null) != on) recipe.ToggleRewardMode(recipe.CurrentRewardMode ?? mode);
            }
            catch { }
        }
    }
}
