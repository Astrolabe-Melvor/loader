using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;

namespace Astrolabe.Course;

/// <summary>A named set of toolbelt items for one skill.</summary>
public sealed class BeltSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Skill { get; set; } = "";
    public List<BeltSlot> Slots { get; set; } = new();
}

/// <summary>What goes in one toolbelt slot. Refill above 0: when this consumable runs out, get up to that many.</summary>
public sealed class BeltSlot
{
    public string Slot { get; set; } = "";
    public string Item { get; set; } = "";
    public int Refill { get; set; }
}

/// <summary>
/// Every skill has its own toolbelt (the armour-shaped slots, potion and tool slots). Toolbelt sets are named
/// selections of items for one skill's toolbelt, put on when a step starts, with optional refills for consumables.
/// </summary>
static class Belts
{
    public sealed record SlotInfo(ToolbeltSlot Slot, IActiveToolbeltSlot? Active, bool Unlocked)
    {
        public Item? Item => Active?.Equipped?.Item;
        public int Quantity => Active?.Quantity ?? 0;
    }

    static IToolbeltEntity? Entity(Game game, string skillId) =>
        game.Entities.TryGetObjectById(skillId, out GameEntity? e) ? e as IToolbeltEntity : null;

    public static bool HasToolbelt(Game game, string skillId) => Entity(game, skillId) != null;

    /// <summary>The skill's toolbelt slots in the game's own order, with what is in them.</summary>
    public static List<SlotInfo> Slots(Game game, string skillId)
    {
        var list = new List<SlotInfo>();
        ToolbeltComponent? tb = Entity(game, skillId)?.Toolbelt;
        if (tb == null) return list;
        foreach (ToolbeltSlot slot in tb.Config.ToolbeltSlots)
        {
            tb.TryGetActiveSlot(slot, out IActiveToolbeltSlot? a);
            list.Add(new SlotInfo(slot, a, slot.UnlockRequirements.AreMet));
        }
        return list;
    }

    public static bool IsConsumable(Item item) =>
        item.Capabilities.TryGet<ToolbeltCapability>(out ToolbeltCapability? c) && c != null && c.ConsumesOn is { Count: > 0 };

    /// <summary>Items you own (bank or already in the slot) that fit a slot and that you can equip.</summary>
    public static List<Item> ItemsFor(Game game, string skillId, ToolbeltSlot slot)
    {
        var list = new List<Item>();
        if (Slots(game, skillId).FirstOrDefault(s => s.Slot == slot)?.Item is Item on) list.Add(on);
        var any = Catalog.AnyAction(game);
        if (any == null) return list;
        foreach (var st in any.Inventory.Stacks)
            if (!st.IsPlaceholder && st.Item.Capabilities.TryGet<ToolbeltCapability>(out ToolbeltCapability? c) && c != null
                && c.FitsInSlot == slot.FitsType && c.Requirements.AreMet)
                list.Add(st.Item);
        return list.Distinct().OrderBy(i => i.DisplayName.ToString()).ToList();
    }

    /// <summary>What is in the skill's toolbelt now, as set slots (refill off).</summary>
    public static List<BeltSlot> Capture(Game game, string skillId) =>
        Slots(game, skillId).Where(s => s.Item != null).Select(s => new BeltSlot { Slot = s.Slot.Id, Item = s.Item!.Id }).ToList();

    /// <summary>Puts the set on: each item from the bank into its slot, consumables topped up with all the bank holds.
    /// Slots the set does not list are left alone. Returns what changed.</summary>
    public static string Apply(Game game, BeltSet set)
    {
        IToolbeltEntity? owner = Entity(game, set.Skill);
        if (owner == null) return "";
        var said = new List<string>();
        var slots = Slots(game, set.Skill);
        foreach (BeltSlot bs in set.Slots)
        {
            SlotInfo? si = slots.FirstOrDefault(s => s.Slot.Id == bs.Slot);
            Item? item = Catalog.FindItem(game, bs.Item);
            if (si == null || item == null) continue;
            if (!si.Unlocked) { said.Add($"{si.Slot.DisplayName} slot is locked"); continue; }
            long bank = Queue.BankCount(game, item.Id);
            bool consumable = item.Capabilities.TryGet<ToolbeltCapability>(out ToolbeltCapability? cap) && cap != null && cap.CanEquipQuantity;
            if (si.Item == item)
            {
                if (consumable && bank > 0)
                {
                    owner.ToolbeltEquipping.EquipItem(item, (int)Math.Min(int.MaxValue, bank), si.Slot);
                    said.Add($"topped up {item.DisplayName}");
                }
                continue;
            }
            if (bank <= 0) { if (bs.Refill == 0) said.Add($"no {item.DisplayName} in the bank"); continue; }
            owner.ToolbeltEquipping.EquipItem(item, consumable ? (int)Math.Min(int.MaxValue, bank) : 1, si.Slot);
            said.Add(owner.Toolbelt.IsItemEquipped(item) ? $"put on {item.DisplayName}" : $"could not put on {item.DisplayName}");
        }
        return string.Join(", ", said);
    }

    /// <summary>A refill slot whose consumable is used up with none left in the bank: it has to be made or found.</summary>
    public static BeltSlot? OutOf(Game game, BeltSet set)
    {
        var slots = Slots(game, set.Skill);
        foreach (BeltSlot bs in set.Slots.Where(b => b.Refill > 0))
        {
            SlotInfo? si = slots.FirstOrDefault(s => s.Slot.Id == bs.Slot);
            if (si == null || !si.Unlocked) continue;
            bool empty = si.Item?.Id != bs.Item || si.Quantity <= 0;
            if (empty) return bs;
        }
        return null;
    }
}
