using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace Astrolabe.Course;

/// <summary>Swaps the game's combat loadouts (gear, combat toolbelt, food, prayers, spells) when a combat step starts.
/// Skills keep their own toolbelts, so loadouts only matter for combat.</summary>
static class Gear
{
    /// <summary>Step.Gear value: use the combat default from Options.</summary>
    public const string SkillDefault = "";
    /// <summary>Step.Gear value: leave whatever is equipped.</summary>
    public const string Keep = "*keep";

    public sealed record Choice(string Id, string Name, bool Unlocked);

    static CombatEntity? Combat(Game game) => game.Entities.GetAllObjects().OfType<CombatEntity>().FirstOrDefault();

    static LoadoutComponent? Component(Game game) => Combat(game)?.Loadouts;

    public static string? CombatId(Game game) => Combat(game)?.Id;

    public static bool IsCombat(Game game, string entityId) => entityId.Length > 0 && entityId == CombatId(game);

    /// <summary>The character's loadouts in the order the game shows them, with the names the player gave them.</summary>
    public static List<Choice> Loadouts(Game game)
    {
        LoadoutComponent? c = Component(game);
        if (c == null) return new();
        return c.LoadoutDisplayOrder.Select(l => new Choice(l.Data.Id, l.Name, l.IsUnlocked)).ToList();
    }

    public static string? CurrentId(Game game) => Component(game)?.CurrentLoadout.Data.Id;

    public static string Name(Game game, string id) =>
        Loadouts(game).FirstOrDefault(l => l.Id == id)?.Name ?? id;

    /// <summary>Which loadout a combat step wants: its own choice, else the combat default. Null: leave gear alone.</summary>
    public static string? Wanted(Game game, QueueState state, Step s)
    {
        if (s.Gear == Keep || s.IsSell || !IsCombat(game, s.Action.Entity)) return null;
        if (s.Gear.Length > 0) return s.Gear;
        return state.SkillGear.TryGetValue(s.Action.Entity, out string? id) && id.Length > 0 ? id : null;
    }

    /// <summary>Swaps to the loadout. Returns "" on success or when already wearing it, else why not.</summary>
    public static string Swap(Game game, string id)
    {
        LoadoutComponent? c = Component(game);
        if (c == null) return "no loadouts on this character";
        KeyValuePair<LoadoutSlot, ActiveLoadoutSlot> hit = c.ActiveLoadouts.FirstOrDefault(kv => kv.Key.Id == id);
        if (hit.Value == null) return "that loadout no longer exists";
        if (!hit.Value.IsUnlocked) return $"loadout {hit.Value.Name} is locked";
        if (c.CurrentLoadout == hit.Value) return "";
        c.SwapToLoadout(hit.Key);
        return "";
    }
}
