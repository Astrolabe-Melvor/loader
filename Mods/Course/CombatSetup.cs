// Item.Capabilities.TryGet has no nullability attributes; every out value below is checked by its bool.
#pragma warning disable CS8600
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace Astrolabe.Course;

/// <summary>
/// Per combat step: which weapon to hold and which stance to fight in. Damage type is the same for every weapon
/// ("Normal"); what differs per monster is its defence bonus against each stance (Stab, Slash, Crush, Accurate...),
/// and a weapon decides which stances you can use. "Best" scores every weapon you own in the loadout's style,
/// with each of its stances, using the game's formulas (hit chance, max hit, protection, attack speed).
/// </summary>
static class CombatSetup
{
    public const string Best = "*best";

    public sealed record Weapon(Item Item, EquipmentCapability Eq, WeaponCapability W, bool Usable, string Why)
    {
        public CombatStyle Style => W.Type.Stances[0].CombatStyle;
        public string Stances => string.Join(", ", W.Type.Stances.Select(s => s.DisplayName.ToString()));
    }

    public sealed record Pick(Weapon? Weapon, AttackStance Stance, double Dps, Item? Offhand = null);

    // ---- the offhand ----

    static EquipmentSlot? OffhandSlot(EquipmentComponent eq) =>
        eq.Config.EquipmentSlots.FirstOrDefault(s => s.FitsType.Id.EndsWith(":Offhand"));

    /// <summary>A two-handed weapon takes the offhand slot too.</summary>
    static bool TwoHanded(EquipmentComponent eq, EquipmentCapability weapon) =>
        OffhandSlot(eq) is EquipmentSlot off && weapon.OccupiesSlots?.Contains(off) == true;

    static Item? CurrentOffhand(EquipmentComponent eq) =>
        OffhandSlot(eq) is EquipmentSlot off && eq.TryGetActiveSlot(off, out IActiveEquipmentSlot? a) && !a.IsEmpty && !a.IsOccupied ? a.Equipped.Item : null;

    /// <summary>Offhands you can wear: the one on now and any in the bank.</summary>
    static List<Item> Offhands(Game game, EquipmentComponent eq)
    {
        var list = new List<Item>();
        if (OffhandSlot(eq) is not EquipmentSlot off) return list;
        if (CurrentOffhand(eq) is Item on) list.Add(on);
        ActionComponent? any = Catalog.AnyAction(game);
        if (any != null)
            foreach (var st in any.Inventory.Stacks)
                if (!st.IsPlaceholder && st.Item.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability e) && e.Type == off.FitsType && e.Requirements.AreMet)
                    list.Add(st.Item);
        return list.Distinct().ToList();
    }

    static CombatEntity? Combat(Game game) => game.Entities.GetAllObjects().OfType<CombatEntity>().FirstOrDefault();

    // ---- monsters ----

    /// <summary>The monsters a combat step fights: one, or every monster of a dungeon.</summary>
    public static List<Enemy> Enemies(Game game, ActionRef r)
    {
        var list = new List<Enemy>();
        CombatAreasComponent? areas = Combat(game)?.Components.Values.OfType<CombatAreasComponent>().FirstOrDefault();
        if (areas == null) return list;
        BaseCombatArea? area = areas.ActiveAreas.Keys.FirstOrDefault(a => a.Id == r.Area);
        if (area is CombatArea ca) { if (ca.Enemies.FirstOrDefault(e => e.Id == r.Target) is Enemy e) list.Add(e); }
        else if (area is Dungeon d) CollectEnemies(d.Enemies, list, 0);
        return list.Distinct().ToList();
    }

    static void CollectEnemies(object o, List<Enemy> into, int depth)
    {
        if (depth > 4) return;
        if (o is Enemy e) { into.Add(e); return; }
        if (o is IEnumerable list && o is not string) { foreach (object? x in list) if (x != null) CollectEnemies(x, into, depth + 1); return; }
        foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && (typeof(Enemy).IsAssignableFrom(p.PropertyType) || p.Name == "Enemies"))
                try { if (p.GetValue(o) is object v) CollectEnemies(v, into, depth + 1); } catch { }
    }

    /// <summary>Every attack stance in the game, in its own order (Stab, Slash, Crush, Block, Accurate...).</summary>
    public static List<AttackStance> AllStances(Game game)
    {
        try { return game.Data.GetDataRegistryOutput<AttackStance>().GetAllObjects().ToList(); }
        catch { return new(); }
    }

    /// <summary>How hard a monster is to hit with each stance: its defence bonus, lower is easier.</summary>
    public static string DefenceText(Game game, List<Enemy> enemies)
    {
        if (enemies.Count == 0) return "";
        var parts = new List<string>();
        foreach (var group in AllStances(game).GroupBy(s => s.CombatStyle))
        {
            var vals = group.Select(s => (s.DisplayName.ToString(), enemies.Average(e => e.Stats.GetCombatStatTotal(s.EvasionStat)))).ToList();
            double low = vals.Min(v => v.Item2);
            parts.Add(group.Key.DisplayName.ToString() + ": " + string.Join(", ", vals.Select(v => $"{v.Item1} {v.Item2:0}{(v.Item2 == low && vals.Count > 1 ? " (weakest)" : "")}")));
        }
        string who = enemies.Count == 1 ? enemies[0].DisplayName.ToString() : $"{enemies.Count} dungeon monsters (average)";
        return $"{who}: defence by stance, lower is easier to hit\n" + string.Join("\n", parts);
    }

    /// <summary>One short line: each style's weakest stance against these monsters ("Crush 92 (Melee)").</summary>
    public static string WeakestText(Game game, List<Enemy> enemies)
    {
        if (enemies.Count == 0) return "";
        var parts = new List<string>();
        foreach (var group in AllStances(game).GroupBy(s => s.CombatStyle))
        {
            var low = group.Select(s => (Name: s.DisplayName.ToString(), Def: enemies.Average(e => e.Stats.GetCombatStatTotal(s.EvasionStat)))).OrderBy(v => v.Def).First();
            parts.Add($"{low.Name} {low.Def:0} ({group.Key.DisplayName})");
        }
        string who = enemies.Count == 1 ? enemies[0].DisplayName.ToString() : $"{enemies.Count} dungeon monsters, averaged";
        return $"{who}. Weakest defence: {string.Join(", ", parts)}. Lower is easier to hit; hover for every stance.";
    }

    // ---- weapons ----

    /// <summary>Weapons you hold or have in the bank. Unusable ones (level, ammo) say why.</summary>
    public static List<Weapon> Owned(Game game)
    {
        var list = new List<Weapon>();
        CombatEntity? ce = Combat(game);
        if (ce == null) return list;
        EquipmentComponent eq = ce.Equipment;
        void Consider(Item item)
        {
            if (!item.Capabilities.TryGet<WeaponCapability>(out WeaponCapability w) || !item.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability e)) return;
            string why = !e.Requirements.AreMet ? "level too low"
                : w.RequiredAmmoType != null && !eq.IsAmmoEquipped(w.RequiredAmmoType) ? "needs ammo equipped"
                : "";
            list.Add(new Weapon(item, e, w, why.Length == 0, why));
        }
        if (eq.CurrentWeapon != eq.DefaultWeapon) Consider(eq.CurrentWeapon.Item);
        ActionComponent? any = Catalog.AnyAction(game);
        if (any != null) foreach (var st in any.Inventory.Stacks) if (!st.IsPlaceholder) Consider(st.Item);
        return list.DistinctBy(w => w.Item.Id).OrderBy(w => w.Style.DisplayName.ToString()).ThenBy(w => w.Item.DisplayName.ToString()).ToList();
    }

    /// <summary>The weapon a loadout holds (the current gear for the loadout being worn). Null: unarmed or unknown.</summary>
    public static WeaponCapability? LoadoutWeapon(Game game, string? loadoutId, out string name)
    {
        name = "unarmed";
        CombatEntity? ce = Combat(game);
        if (ce == null) return null;
        EquipmentComponent eq = ce.Equipment;
        EquipmentItem? item;
        if (string.IsNullOrEmpty(loadoutId) || loadoutId == Gear.CurrentId(game))
            item = eq.CurrentWeapon == eq.DefaultWeapon ? null : new EquipmentItem(eq.CurrentWeapon.Item, eq.CurrentWeapon.Equipment);
        else
        {
            LoadoutSlot? slot = ce.Loadouts.ActiveLoadouts.Keys.FirstOrDefault(k => k.Id == loadoutId);
            item = slot == null ? null : eq.GetComparedItem(eq.DefaultWeapon.Equipment.Type, slot);
        }
        if (item == null || !item.Item.Capabilities.TryGet<WeaponCapability>(out WeaponCapability w)) return eq.DefaultWeapon.Weapon;
        name = item.Item.DisplayName.ToString();
        return w;
    }

    // ---- scoring ----

    /// <summary>Average damage per second against the monsters with this weapon and stance, from the game's formulas.
    /// Modifiers that scale every weapon alike (prayers, potions) are left out; they do not change the ranking.
    /// Returns -1 for magic, where the spell decides the damage.</summary>
    public static double Dps(Game game, List<Enemy> enemies, Weapon? weapon, AttackStance st, Item? offhand = null)
    {
        CombatEntity? ce = Combat(game);
        if (ce == null || enemies.Count == 0) return -1;
        EquipmentComponent eq = ce.Equipment;
        CombatStyle style = st.CombatStyle;
        if (style.MaxHitFormula is not StrengthBonusMaxHitFormula f) return -1;

        // The gear you would wear: what is on now, minus what the weapon pushes out, plus the weapon.
        var removed = new HashSet<Item>();
        if (weapon != null && !eq.IsItemEquipped(weapon.Item))
        {
            if (eq.CurrentWeapon != eq.DefaultWeapon) removed.Add(eq.CurrentWeapon.Item);
            AvailableEquipmentSlot? at = eq.GetAvailableSlotsForItem(weapon.Item).FirstOrDefault();
            if (at != null) foreach (IActiveEquipmentSlot r in at.Replaces) if (r.Equipped != null) removed.Add(r.Equipped.Item);
        }
        var gear = new List<EquipmentCapability>();
        foreach (Item i in eq.EquippedItems)
            if (!removed.Contains(i) && i.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability c)) gear.Add(c);
        if (weapon != null && !eq.IsItemEquipped(weapon.Item)) gear.Add(weapon.Eq);
        if (!gear.Any(g => g.Type == eq.DefaultWeapon.Equipment.Type)) gear.Add(eq.DefaultWeapon.Equipment);
        // With a one-handed weapon, the offhand being tried replaces whatever offhand is on.
        EquipmentCapability held = weapon?.Eq ?? eq.CurrentWeapon.Equipment;
        if (offhand != null && !TwoHanded(eq, held) && offhand.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability oc))
        {
            if (CurrentOffhand(eq) is Item on && !removed.Contains(on) && on.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability onc)) gear.Remove(onc);
            if (!gear.Contains(oc)) gear.Add(oc);
        }

        double Stat(GamesByMalcs.Melvor2.Components.CombatStats.CombatStat s) => gear.Sum(g => g.Stats.GetCombatStatTotal(s));
        static int Lvl(CombatLevel l) => l.Component?.Level ?? 1;

        double eff = style.AccuracyRatingLevels.Sum(r => r.Ratio * Lvl(r.Level));
        double acc = Math.Floor((eff + 9) * (Stat(st.AccuracyStat) + 64));
        double L = f.Levels.Sum(r => r.Ratio * Lvl(r.Level));
        double maxHit = Math.Max(1, Math.Floor(22 + L + (L + 17) * Stat(f.StrengthBonusStat) / 64));
        double power = Math.Max(1, gear.Sum(g => g.Stats.PowerRatings.Sum(p => p.Value)));
        double interval = Stat(style.AttackIntervalStat);
        interval = Math.Max(250, interval <= 0 ? 3000 : interval);

        double total = 0;
        foreach (Enemy e in enemies)
        {
            double eEff = style.EvasionLevels.Sum(r => r.Ratio * (e.Levels.TryGetValue(r.Level, out int v) ? v : 1));
            double ev = Math.Max(1, Math.Floor((eEff + 9) * (e.Stats.GetCombatStatTotal(st.EvasionStat) + 64)));
            double hit = acc >= ev ? 1 - ev / (2 * acc) : acc / (2 * ev);
            double prot = e.Stats.ProtectionRatings.Sum(p => p.Value);
            double res = Math.Clamp(prot / (power + prot), 0, 0.7);
            total += hit * (1 + maxHit) / 2 * (1 - res) / (interval / 1000.0);
        }
        return total / enemies.Count;
    }

    /// <summary>The best stance for one weapon (null: the weapon held now), with the best offhand you own for it.</summary>
    public static Pick? BestStance(Game game, List<Enemy> enemies, Weapon? weapon)
    {
        EquipmentComponent? eq = Combat(game)?.Equipment;
        if (eq == null) return null;
        WeaponType type = weapon?.W.Type ?? eq.CurrentWeapon.Weapon.Type;
        bool twoHanded = TwoHanded(eq, weapon?.Eq ?? eq.CurrentWeapon.Equipment);
        var offhands = twoHanded ? new List<Item?> { null } : Offhands(game, eq).Cast<Item?>().Append(null).ToList();
        return type.Stances
            .SelectMany(s => offhands.Select(o => new Pick(weapon, s, Dps(game, enemies, weapon, s, o), o)))
            .Where(p => p.Dps >= 0).MaxBy(p => p.Dps);
    }

    /// <summary>The best usable weapon and stance in one combat style (the style of the weapon held after the loadout swap).</summary>
    public static Pick? BestWeapon(Game game, List<Enemy> enemies, CombatStyle style)
    {
        var picks = Owned(game).Where(w => w.Usable && w.Style == style).Select(w => BestStance(game, enemies, w)).Where(p => p != null).Select(p => p!);
        return picks.MaxBy(p => p.Dps);
    }

    // ---- applying at step start ----

    /// <summary>Puts on the step's weapon and stance after its loadout. Returns what changed (for History), or why not.</summary>
    public static string Apply(Game game, QueueState state, Step s)
    {
        CombatEntity? ce = Combat(game);
        if (ce == null) return "";
        if (s.Weapon.Length == 0 && s.Stance.Length == 0)
        {
            // Nothing to change but the slayer gauntlet: give back any swapped weapon or offhand, then check the task.
            var parts = new[] { RestoreWeapon(game, state), RestoreOffhand(game, state), Slayer(game, state, Enemies(game, s.Effective)) };
            return string.Join(", ", parts.Where(x => x.Length > 0));
        }
        EquipmentComponent eq = ce.Equipment;
        List<Enemy> enemies = Enemies(game, s.Effective);
        var said = new List<string>();

        Pick? best = null;
        if (s.Weapon == Best)
        {
            CombatStyle style = eq.CurrentWeapon.Weapon.Type.Stances[0].CombatStyle;
            best = BestWeapon(game, enemies, style);
            if (best?.Weapon == null) said.Add($"no usable {style.DisplayName} weapon to pick from");
            else
            {
                said.Add(Equip(game, state, best.Weapon));
                said.Add(FillOffhand(game, state, best.Offhand, replace: true));
            }
        }
        else if (s.Weapon.Length > 0)
        {
            Weapon? w = Owned(game).FirstOrDefault(x => x.Item.Id == s.Weapon);
            if (w == null) said.Add("that weapon is not in the bank");
            else if (!w.Usable) said.Add($"{w.Item.DisplayName}: {w.Why}");
            else
            {
                said.Add(Equip(game, state, w));
                // An empty offhand next to a one-handed weapon (a two-hander before took it): put the best one in.
                if (CurrentOffhand(eq) == null) said.Add(FillOffhand(game, state, BestStance(game, enemies, null)?.Offhand, replace: false));
            }
        }
        else said.Add(Restore(game, state));

        said.Add(Slayer(game, state, enemies));

        WeaponType type = eq.CurrentWeapon.Weapon.Type;
        AttackStance? stance = null;
        if (s.Stance == Best || (s.Weapon == Best && s.Stance.Length == 0)) stance = (best?.Weapon?.Item == eq.CurrentWeapon.Item ? best : BestStance(game, enemies, null))?.Stance;
        else if (s.Stance.Length > 0)
        {
            stance = type.Stances.FirstOrDefault(x => x.Id == s.Stance);
            if (stance == null) said.Add($"{eq.CurrentWeapon.Item.DisplayName} has no {Name(game, s.Stance)} stance");
        }
        if (stance != null && type.Stances.Contains(stance))
        {
            ce.Player.SetSelectedStance(type, stance);
            said.Add($"{stance.DisplayName} stance");
        }
        return string.Join(", ", said.Where(x => x.Length > 0));
    }

    static string Name(Game game, string stanceId) => AllStances(game).FirstOrDefault(x => x.Id == stanceId)?.DisplayName.ToString() ?? stanceId;

    static string Equip(Game game, QueueState state, Weapon w)
    {
        CombatEntity ce = Combat(game)!;
        EquipmentComponent eq = ce.Equipment;
        if (eq.IsItemEquipped(w.Item)) return $"holding {w.Item.DisplayName}";
        string loadout = Gear.CurrentId(game) ?? "";
        // Remember the loadout's own weapon and offhand: the game saves whatever is worn back into the loadout when it
        // swaps away, and a two-handed weapon pushes the offhand into the bank.
        if (!state.WeaponRestore.ContainsKey(loadout))
            state.WeaponRestore[loadout] = eq.CurrentWeapon == eq.DefaultWeapon ? "" : eq.CurrentWeapon.Item.Id;
        if (!state.OffhandRestore.ContainsKey(loadout))
            state.OffhandRestore[loadout] = CurrentOffhand(eq)?.Id ?? "";
        AvailableEquipmentSlot? at = eq.GetAvailableSlotsForItem(w.Item).FirstOrDefault();
        if (at == null) return $"no slot for {w.Item.DisplayName}";
        ce.ItemEquipping.EquipItem(w.Item, 1, at.Slot.Slot);
        return eq.IsItemEquipped(w.Item) ? $"holding {w.Item.DisplayName}" : $"could not equip {w.Item.DisplayName} (bank full?)";
    }

    /// <summary>Puts an offhand on next to a one-handed weapon. <paramref name="replace"/>: swap out one already on.</summary>
    static string FillOffhand(Game game, QueueState state, Item? offhand, bool replace)
    {
        CombatEntity ce = Combat(game)!;
        EquipmentComponent eq = ce.Equipment;
        if (offhand == null || eq.IsItemEquipped(offhand) || TwoHanded(eq, eq.CurrentWeapon.Equipment)) return "";
        if (!replace && CurrentOffhand(eq) != null) return "";
        if (OffhandSlot(eq) is not EquipmentSlot off) return "";
        string loadout = Gear.CurrentId(game) ?? "";
        if (!state.OffhandRestore.ContainsKey(loadout)) state.OffhandRestore[loadout] = CurrentOffhand(eq)?.Id ?? "";
        ce.ItemEquipping.EquipItem(offhand, 1, off);
        return eq.IsItemEquipped(offhand) ? $"offhand {offhand.DisplayName}" : $"could not equip {offhand.DisplayName}";
    }

    /// <summary>Gives the worn loadout its own weapon, offhand and gloves back if a step swapped them.</summary>
    public static string Restore(Game game, QueueState state)
    {
        string weapon = RestoreWeapon(game, state);
        string offhand = RestoreOffhand(game, state);
        string slots = RestoreSlots(game, state);
        return string.Join(", ", new[] { weapon, offhand, slots }.Where(x => x.Length > 0));
    }

    // ---- slayer gauntlet ----

    static GamesByMalcs.Melvor2.Components.SlayerTasks.SlayerTasksComponent? Tasks(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values)
            .OfType<GamesByMalcs.Melvor2.Components.SlayerTasks.SlayerTasksComponent>().FirstOrDefault();

    /// <summary>True when any of the step's monsters is a current slayer task (any master).</summary>
    public static bool IsTask(Game game, List<Enemy> enemies)
    {
        var t = Tasks(game);
        return t != null && enemies.Any(t.HasTaskForEnemy);
    }

    /// <summary>The best slayer gauntlet you own and can wear (newer ones come later in the game's list).</summary>
    static Item? Gauntlet(Game game)
    {
        Item? best = null;
        var eq = Combat(game)?.Equipment;
        if (eq == null) return null;
        var bank = Catalog.AnyAction(game)?.Inventory.Stacks.Where(st => !st.IsPlaceholder).Select(st => st.Item) ?? Enumerable.Empty<Item>();
        foreach (Item i in eq.EquippedItems.Concat(bank).Distinct())
            if (i.Id.Contains("SlayerGauntlet") && i.Capabilities.TryGet<EquipmentCapability>(out EquipmentCapability e) && e.Requirements.AreMet)
                best = i;
        return best;
    }

    /// <summary>Fighting a slayer task: the gauntlet goes in its slot (over the loadout's gloves), remembering those.</summary>
    static string Slayer(Game game, QueueState state, List<Enemy> enemies)
    {
        if (!state.SlayerGauntlet || !IsTask(game, enemies)) return RestoreSlots(game, state);
        Item? g = Gauntlet(game);
        CombatEntity ce = Combat(game)!;
        EquipmentComponent eq = ce.Equipment;
        if (g == null || eq.IsItemEquipped(g)) return "";
        AvailableEquipmentSlot? at = eq.GetAvailableSlotsForItem(g).FirstOrDefault();
        if (at == null) return "";
        string key = $"{Gear.CurrentId(game) ?? ""}|{at.Slot.Slot.Id}";
        if (!state.SlotRestore.ContainsKey(key)) state.SlotRestore[key] = at.Slot.Equipped?.Item.Id ?? "";
        ce.ItemEquipping.EquipItem(g, 1, at.Slot.Slot);
        return eq.IsItemEquipped(g) ? $"slayer task: wearing {g.DisplayName}" : "";
    }

    /// <summary>Puts back what a step took out of a slot (for the loadout being worn).</summary>
    static string RestoreSlots(Game game, QueueState state)
    {
        CombatEntity? ce = Combat(game);
        if (ce == null) return "";
        string prefix = (Gear.CurrentId(game) ?? "") + "|";
        var said = new List<string>();
        foreach (string key in state.SlotRestore.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            string id = state.SlotRestore[key];
            state.SlotRestore.Remove(key);
            string slotId = key[prefix.Length..];
            EquipmentSlot? slot = ce.Equipment.Config.EquipmentSlots.FirstOrDefault(s => s.Id == slotId);
            if (slot == null) continue;
            if (id.Length == 0)
            {
                if (ce.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? a) && !a.IsEmpty && !a.IsOccupied)
                {
                    ce.ItemEquipping.UnequipSlot(slot);
                    said.Add($"{slot.DisplayName} back to empty");
                }
                continue;
            }
            Item? own = Catalog.FindItem(game, id);
            if (own == null || ce.Equipment.IsItemEquipped(own) || Queue.BankCount(game, id) <= 0) continue;
            ce.ItemEquipping.EquipItem(own, 1, slot);
            said.Add($"back to {own.DisplayName}");
        }
        return string.Join(", ", said);
    }

    static string RestoreOffhand(Game game, QueueState state)
    {
        CombatEntity? ce = Combat(game);
        string loadout = Gear.CurrentId(game) ?? "";
        if (ce == null || !state.OffhandRestore.TryGetValue(loadout, out string? id)) return "";
        state.OffhandRestore.Remove(loadout);
        EquipmentComponent eq = ce.Equipment;
        if (OffhandSlot(eq) is not EquipmentSlot off || TwoHanded(eq, eq.CurrentWeapon.Equipment)) return "";
        Item? on = CurrentOffhand(eq);
        if (id.Length == 0)
        {
            if (on == null) return "";
            ce.ItemEquipping.UnequipSlot(off);
            return "offhand back in the bank";
        }
        Item? own = Catalog.FindItem(game, id);
        if (own == null || on == own) return "";
        if (Queue.BankCount(game, id) <= 0) return $"{own.DisplayName} is no longer in the bank";
        ce.ItemEquipping.EquipItem(own, 1, off);
        return $"offhand back to {own.DisplayName}";
    }

    static string RestoreWeapon(Game game, QueueState state)
    {
        CombatEntity? ce = Combat(game);
        string loadout = Gear.CurrentId(game) ?? "";
        if (ce == null || !state.WeaponRestore.TryGetValue(loadout, out string? id)) return "";
        state.WeaponRestore.Remove(loadout);
        EquipmentComponent eq = ce.Equipment;
        if (id.Length == 0)
        {
            if (eq.CurrentWeapon == eq.DefaultWeapon) return "";
            Item held = eq.CurrentWeapon.Item;
            foreach (IActiveEquipmentSlot slot in eq.GetSlotsForType(eq.DefaultWeapon.Equipment.Type))
                if (slot.Equipped?.Item == held) { ce.ItemEquipping.UnequipSlot(slot.Slot); break; }
            return "put the weapon back in the bank";
        }
        Item? own = Catalog.FindItem(game, id);
        if (own == null || eq.IsItemEquipped(own)) return "";
        AvailableEquipmentSlot? at = eq.GetAvailableSlotsForItem(own).FirstOrDefault();
        if (at == null || Queue.BankCount(game, id) <= 0) return $"{own.DisplayName} is no longer in the bank";
        ce.ItemEquipping.EquipItem(own, 1, at.Slot.Slot);
        return $"back to {own.DisplayName}";
    }
}
