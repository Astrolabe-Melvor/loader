using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.ItemBurying;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;

namespace Astrolabe.Hold;

/// <summary>One stack Bury all or Open all would use: how many, and (for burying) what it gives.</summary>
public sealed record BulkLine(Item Item, string Name, long Qty, long Gain, string Currency);

/// <summary>
/// Bury all (bones and the like, for their currency) and Open all (chests, packs). Both use the game's own bank
/// actions, skip locked stacks, and leave what your rules keep: a Keep rule's amount, or a shop rule's stocked amount.
/// </summary>
static class Bulk
{
    static BankEntity? Bank(Game game) => game.Entities.GetAllObjects().OfType<BankEntity>().FirstOrDefault();

    static IEnumerable<(IReadonlyItemStack Stack, long Spare)> Stacks(Game game, Keeper k)
    {
        BankEntity? bank = Bank(game);
        if (bank == null) yield break;
        var seen = new HashSet<Item>();
        foreach (IReadonlyItemStack st in bank.Inventory.Stacks.ToList())
        {
            if (st.IsPlaceholder || st.IsLocked || !seen.Add(st.Item)) continue;
            long spare = ((IInventory)bank.Inventory).GetQuantity(st.Item) - k.Shop.ItemFloor(st.Item.Id);
            if (spare > 0) yield return (st, spare);
        }
    }

    /// <summary>What Bury all would bury, most reward first.</summary>
    public static List<BulkLine> Buryable(Game game, Keeper k)
    {
        var list = new List<BulkLine>();
        BankEntity? bank = Bank(game);
        if (bank == null) return list;
        foreach (var (st, spare) in Stacks(game, k))
        {
            if (!st.Item.Capabilities.TryGet<BuryCapability>(out BuryCapability? bury) || bury == null) continue;
            long each = 0;
            ItemBuryingStats? stats = null;
            try { stats = bank.ItemBurying.GetItemStats(st.Item); each = stats.RewardPerBury.Value; }
            catch { }
            finally { stats?.Destroy(); }
            list.Add(new BulkLine(st.Item, st.Item.DisplayName.ToString(), spare, each * spare, bury.CurrencyGain.Currency.DisplayName.Text));
        }
        return list.OrderByDescending(l => l.Gain).ToList();
    }

    /// <summary>What Open all would open: chests and packs whose requirements you meet.</summary>
    public static List<BulkLine> Openable(Game game, Keeper k)
    {
        var list = new List<BulkLine>();
        foreach (var (st, spare) in Stacks(game, k))
        {
            if (!st.Item.Capabilities.TryGet<OpenableCapability>(out OpenableCapability? open) || open == null) continue;
            if (open.Requirements != null && !open.Requirements.AreMet) continue;
            list.Add(new BulkLine(st.Item, st.Item.DisplayName.ToString(), spare, 0, ""));
        }
        return list.OrderBy(l => l.Name).ToList();
    }

    /// <summary>Buries every line. Returns a sentence saying what it gave.</summary>
    public static string BuryAll(Game game, List<BulkLine> lines)
    {
        BankEntity? bank = Bank(game);
        if (bank == null || lines.Count == 0) return "Nothing to bury.";
        var gained = new Dictionary<string, long>();
        long items = 0;
        foreach (BulkLine l in lines)
        {
            ItemBuryingStats? stats = null;
            try
            {
                stats = bank.ItemBurying.GetItemStats(l.Item);
                long have = ((IInventory)bank.Inventory).GetQuantity(l.Item);
                int qty = (int)Math.Min(int.MaxValue, Math.Min(l.Qty, have));
                if (qty <= 0) continue;
                long gain = stats.RewardPerBury.Value * qty;
                bank.ItemBurying.BuryItem(l.Item, stats, qty);
                items += qty;
                gained[l.Currency] = gained.GetValueOrDefault(l.Currency) + gain;
            }
            catch { }
            finally { stats?.Destroy(); }
        }
        return items == 0 ? "Nothing was buried."
            : $"Buried {items:N0} items for " + string.Join(", ", gained.Select(g => $"{g.Value:N0} {g.Key}")) + ".";
    }

    /// <summary>Opens every line. Stops a stack early when the bank has no room for what comes out.</summary>
    public static string OpenAll(Game game, List<BulkLine> lines)
    {
        BankEntity? bank = Bank(game);
        if (bank == null || lines.Count == 0) return "Nothing to open.";
        long opened = 0;
        int kinds = 0;
        bool full = false;
        var got = new Dictionary<string, long>();
        foreach (BulkLine l in lines)
        {
            try
            {
                int qty = (int)Math.Min(int.MaxValue, l.Qty);
                var res = bank.ItemOpening.OpenItem(l.Item, qty);
                if (res.Count > 0) { opened += res.Count; kinds++; }
                foreach (var d in res.Drops) got[d.Item.DisplayName.ToString()] = got.GetValueOrDefault(d.Item.DisplayName.ToString()) + d.Quantity;
                if (res.WasStopped) { full = true; break; }
            }
            catch { }
        }
        if (opened == 0) return full ? "The bank is full; nothing was opened." : "Nothing was opened.";
        string top = string.Join(", ", got.OrderByDescending(g => g.Value).Take(6).Select(g => $"{g.Value:N0} {g.Key}"));
        return $"Opened {opened:N0} from {kinds} stack{(kinds == 1 ? "" : "s")}: {top}{(got.Count > 6 ? ", ..." : "")}."
            + (full ? " Stopped: the bank is full." : "");
    }
}
