using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Shop;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core.InventoryWalletTransactions;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Shop;

namespace Astrolabe.Course;

/// <summary>The shop as a source for the planner, and the "buy from the shop" step.</summary>
static class ShopSource
{
    public const string Entity = "*shop";
    public const string Gold = "MelvorBase:GoldPieces";

    public static ShopComponent? Shop(Game game) =>
        game.Entities.GetAllObjects().OfType<ShopEntity>().FirstOrDefault()?.Shop;

    public static long Gp(Game game)
    {
        ShopComponent? shop = Shop(game);
        Currency? gp = shop?.Wallet.Stacks.Keys.FirstOrDefault(c => c.Id == Gold);
        return shop == null || gp == null ? 0 : shop.Wallet.GetQuantity(gp);
    }

    static bool Visible(ShopPurchase p) => p.DisplayRequirements.AreMet && (p.Category.DisplayRequirements?.AreMet ?? true);
    static bool Unlocked(ShopPurchase p) => p.Requirements.AreMet && p.Category.Requirements.AreMet;

    static readonly Regex s_images = new(@"\[img[^\]]*\].*?\[/img\]", RegexOptions.Singleline);
    static readonly Regex s_tags = new(@"\[/?[^\]]*\]");
    static readonly Regex s_space = new(@"\s+");

    public static string Needs(ShopPurchase p)
    {
        var parts = new List<string>();
        foreach (ConditionGroup g in new[] { p.Category.Requirements, p.Requirements })
            foreach (Condition c in g.Conditions.Where(c => !c.IsMet))
            {
                try { parts.Add(s_space.Replace(s_tags.Replace(s_images.Replace(c.GetDescription(false, false), ""), ""), " ").Trim()); } catch { }
            }
        return string.Join("; ", parts.Where(s => s.Length > 0).Take(3));
    }

    /// <summary>A purchase that gives the item (short id): unlocked before locked, then the most per pack.
    /// Hidden purchases and one-time purchases already bought are left out.</summary>
    public static ShopPurchase? For(Game game, string localItem)
    {
        ShopComponent? shop = Shop(game);
        if (shop == null) return null;
        return shop.Config.Purchases
            .Where(p => !shop.IsPurchased(p) && p.Provides.Items.Any(i => Catalog.Local(i.Item.Id) == localItem))
            .OrderByDescending(p => Visible(p))
            .ThenByDescending(p => Unlocked(p))
            .ThenByDescending(p => p.Provides.Items.First(i => Catalog.Local(i.Item.Id) == localItem).Quantity)
            .FirstOrDefault();
    }

    /// <summary>GP per unit for items the shop sells for GP alone (Arena Entry Tickets, buckets...), by short item id.
    /// Lets the planner count such an item as an ingredient it can get.</summary>
    public static Dictionary<string, double> GpPrices(Game game)
    {
        var prices = new Dictionary<string, double>();
        ShopComponent? shop = Shop(game);
        if (shop == null) return prices;
        foreach (ShopPurchase p in shop.Config.Purchases)
        {
            if (shop.IsPurchased(p) || !Visible(p) || p.Costs.Items.Count > 0 || p.Costs.Currencies.Any(c => c.Currency.Id != Gold)) continue;
            long gp = p.Costs.Currencies.Sum(c => c.Quantity);
            foreach (ItemQuantity i in p.Provides.Items)
            {
                if (i.Quantity <= 0) continue;
                string id = Catalog.Local(i.Item.Id);
                double each = (double)gp / i.Quantity;
                if (!prices.TryGetValue(id, out double old) || each < old) prices[id] = each;
            }
        }
        return prices;
    }

    public static ShopPurchase? ById(Game game, string id) =>
        Shop(game)?.Config.Purchases.FirstOrDefault(p => p.Id == id || Catalog.Local(p.Id) == Catalog.Local(id));

    public static int PerPack(ShopPurchase p, string itemId) =>
        p.Provides.Items.FirstOrDefault(i => i.Item.Id == itemId || Catalog.Local(i.Item.Id) == Catalog.Local(itemId))?.Quantity ?? 0;

    public static ActionRef Ref(ShopPurchase p) =>
        new() { Entity = Entity, Target = p.Id, Label = $"Buy from shop: {p.DisplayName}" };

    /// <summary>Buys packs until the bank holds the step's amount. Returns "" when done, otherwise why it waits.</summary>
    public static string Buy(Game game, Step s)
    {
        ShopComponent? shop = Shop(game);
        ShopPurchase? p = ById(game, s.Action.Target);
        if (shop == null || p == null) return "that purchase is not in this game version";
        int have = Queue.BankCount(game, s.Item);
        if (have >= s.Amount) return "";
        if (!Visible(p) || !Unlocked(p)) return "needs " + Needs(p);
        int per = Math.Max(1, PerPack(p, s.Item));
        int want = shop.CapPurchaseQuantity(p, (s.Amount - have + per - 1) / per);
        if (want <= 0) return "already bought (it has a buy limit)";

        long can = want;
        var short_ = new List<string>();
        foreach (CurrencyQuantity c in p.Costs.Currencies)
        {
            long n = shop.Wallet.GetQuantity(c.Currency) / Math.Max(1, c.Quantity);
            if (n < want) short_.Add($"{c.Quantity * (want - n):N0} more {(c.Currency.Id == Gold ? "GP" : c.Currency.DisplayName.ToString())}");
            can = Math.Min(can, n);
        }
        foreach (ItemQuantity i in p.Costs.Items)
        {
            long n = shop.Inventory.GetQuantity(i.Item) / Math.Max(1, i.Quantity);
            if (n < want) short_.Add($"{(long)i.Quantity * (want - n):N0} more {i.Item.DisplayName}");
            can = Math.Min(can, n);
        }
        if (can > 0)
        {
            var r = shop.ProcessPurchase(p, (int)can);
            if (r == InventoryWalletTransactionResult.NotEnoughInventorySpace) return "no room in the bank";
            if (r != InventoryWalletTransactionResult.Success) return "the game refused the purchase";
            if (Queue.BankCount(game, s.Item) >= s.Amount) return "";
        }
        return "needs " + string.Join(", ", short_);
    }
}
