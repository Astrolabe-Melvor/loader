using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.Shop;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core.InventoryWalletTransactions;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Shop;
using MelvorModLoader;

namespace Astrolabe.Hold;

/// <summary>A shop rule. Item empty: buy this upgrade as soon as it can be bought. Once: buy it one time. Otherwise: keep this many of Item in the bank.</summary>
public sealed class BuyRule
{
    public string Item { get; set; } = "";
    public long Keep { get; set; }
    public bool Once { get; set; }
}

public sealed class Bought
{
    public DateTime Time { get; set; }
    public string Purchase { get; set; } = "";
    public string Name { get; set; } = "";
    public int Qty { get; set; }
    public string Cost { get; set; } = "";
    public long Gp { get; set; }
    public bool Auto { get; set; }
}

public enum Kind
{
    /// <summary>Has a buy limit (bank slots, stations, permits). Rule: buy when ready.</summary>
    Upgrade,
    /// <summary>No limit and gives an item (arena gear, arrows, buckets, charms). Rules: buy once, or keep N in the bank.</summary>
    Item,
    /// <summary>No limit and gives no item. Buy by hand only.</summary>
    Other,
}

public enum State { Owned, Ready, Short, Locked }

/// <summary>One shop purchase as Hold shows it.</summary>
public sealed record Offer(ShopPurchase P, string Name, string Category, string Section, Kind Kind, Item? Gives, int PerPack, State State, string Note, string Cost, int Times);

/// <summary>Reads the shop and buys by the rules, never spending below your reserves or an item's keep amount.</summary>
public sealed class Shopper
{
    public const string Gold = "MelvorBase:GoldPieces";
    readonly Keeper _k;
    readonly Dictionary<string, double> _backoff = new();

    public Shopper(Keeper k) => _k = k;

    public static ShopComponent? Shop(Game game) =>
        game.Entities.GetAllObjects().OfType<ShopEntity>().FirstOrDefault()?.Shop;

    public static Kind KindOf(ShopPurchase p) =>
        p.DefaultBuyLimit > 0 ? Kind.Upgrade : p.Provides.Items.Count > 0 ? Kind.Item : Kind.Other;

    /// <summary>The game hides these in its own shop, so Hold never shows or buys them.</summary>
    static bool Visible(ShopPurchase p) =>
        p.DisplayRequirements.AreMet && (p.Category.DisplayRequirements?.AreMet ?? true);

    static bool Unlocked(ShopPurchase p) => p.Requirements.AreMet && p.Category.Requirements.AreMet;

    static readonly Regex s_images = new(@"\[img[^\]]*\].*?\[/img\]", RegexOptions.Singleline);
    static readonly Regex s_tags = new(@"\[/?[^\]]*\]");
    static readonly Regex s_space = new(@"\s+");

    /// <summary>The game's requirement text without its BBCode and inline icons.</summary>
    static string Plain(string s) => s_space.Replace(s_tags.Replace(s_images.Replace(s, ""), ""), " ").Trim();

    /// <summary>All of a purchase's requirements, met or not ("Woodcutting Level 50"), to tell look-alikes apart.</summary>
    public static string AllNeeds(ShopPurchase p)
    {
        var parts = new List<string>();
        foreach (Condition c in p.Requirements.Conditions)
        {
            try { parts.Add(Plain(c.GetDescription(false, false))); } catch { }
        }
        return string.Join(", ", parts.Where(x => x.Length > 0).Take(2));
    }

    /// <summary>The unmet requirements, in the game's own words.</summary>
    public static string Needs(ShopPurchase p)
    {
        var parts = new List<string>();
        foreach (ConditionGroup g in new[] { p.Category.Requirements, p.Requirements })
            foreach (Condition c in g.Conditions.Where(c => !c.IsMet))
            {
                try { parts.Add(Plain(c.GetDescription(false, false))); } catch { }
            }
        return parts.Count == 0 ? "" : "Needs " + string.Join("; ", parts.Where(s => s.Length > 0).Take(3));
    }

    public static string CostText(ShopPurchase p, int qty = 1)
    {
        var parts = new List<string>();
        foreach (CurrencyQuantity c in p.Costs.Currencies)
            parts.Add($"{Num(c.Quantity * qty)} {(c.Currency.Id == Gold ? "GP" : c.Currency.DisplayName.ToString())}");
        foreach (ItemQuantity i in p.Costs.Items)
            parts.Add($"{Num((long)i.Quantity * qty)} {i.Item.DisplayName}");
        return parts.Count == 0 ? "free" : string.Join(", ", parts);
    }

    public static string Num(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e9 ? $"{v / 1e9:0.##}B" : a >= 1e6 ? $"{v / 1e6:0.##}M" : a >= 1e4 ? $"{v / 1e3:0.#}k" : $"{v:N0}";
    }

    /// <summary>The least of an item that buying may leave: its keep amount in Rules, and any pack rule's target for it.</summary>
    public long ItemFloor(string itemId)
    {
        long floor = _k.Book.Keep.TryGetValue(itemId, out long keep) ? keep : 0;
        foreach (BuyRule r in _k.Book.Buy.Values)
            if (r.Item == itemId && !r.Once) floor = Math.Max(floor, r.Keep);
        return floor;
    }

    public long Reserve(string currencyId) => _k.Book.Reserve.TryGetValue(currencyId, out long v) ? v : 0;

    /// <summary>How many can be bought now without going under a reserve or a keep amount (and within the buy limit).</summary>
    public int Affordable(ShopComponent shop, ShopPurchase p, Dictionary<string, long>? spent = null)
    {
        long most = 100_000;
        long Spent(string id) => spent?.GetValueOrDefault(id) ?? 0;
        foreach (CurrencyQuantity c in p.Costs.Currencies)
            if (c.Quantity > 0) most = Math.Min(most, Math.Max(0, shop.Wallet.GetQuantity(c.Currency) - Spent(c.Currency.Id) - Reserve(c.Currency.Id)) / c.Quantity);
        foreach (ItemQuantity i in p.Costs.Items)
            if (i.Quantity > 0) most = Math.Min(most, Math.Max(0, shop.Inventory.GetQuantity(i.Item) - Spent(i.Item.Id) - ItemFloor(i.Item.Id)) / i.Quantity);
        return shop.CapPurchaseQuantity(p, (int)most);
    }

    public List<Offer> Offers(Game game)
    {
        var list = new List<Offer>();
        ShopComponent? shop = Shop(game);
        if (shop == null) return list;
        // Several purchases share a name (a "+25 Bank Slots" per skill): name those by what unlocks them.
        var dupes = shop.Config.Purchases.Where(Visible).GroupBy(p => p.DisplayName.ToString()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (ShopPurchase p in shop.Config.Purchases)
        {
            if (!Visible(p)) continue;
            Kind kind = KindOf(p);
            string name = p.DisplayName.ToString();
            if (dupes.Contains(name) && AllNeeds(p) is { Length: > 0 } needs) name += $" ({needs})";
            ItemQuantity? gives = p.Provides.Items.FirstOrDefault();
            State state; string note;
            if (shop.IsPurchased(p)) { state = State.Owned; note = "Owned"; }
            else if (!Unlocked(p)) { state = State.Locked; note = Needs(p); }
            else if (Affordable(shop, p) > 0) { state = State.Ready; note = "Ready to buy"; }
            else { state = State.Short; note = "Can't afford" + (HasFloors(p) ? " (keeping your reserves)" : ""); }
            int times = shop.GetUpgradesPurchased(p);
            if (kind == Kind.Item && gives != null) note += $". You have {Num(shop.Inventory.GetQuantity(gives.Item))}";
            if (kind == Kind.Upgrade && p.DefaultBuyLimit > 1) note += $" ({times} of {p.DefaultBuyLimit})";
            string cat = p.Category.DisplayName.ToString(), sub = p.Subcategory.DisplayName.ToString();
            list.Add(new Offer(p, name, cat, sub.Length > 0 && sub != cat ? sub : "", kind, gives?.Item, gives?.Quantity ?? 0, state, note, CostText(p), times));
        }
        return list;
    }

    bool HasFloors(ShopPurchase p) =>
        p.Costs.Currencies.Any(c => Reserve(c.Currency.Id) > 0) || p.Costs.Items.Any(i => ItemFloor(i.Item.Id) > 0);

    /// <summary>How many a rule wants bought right now (before checking money).</summary>
    public int Wanted(ShopComponent shop, ShopPurchase p, BuyRule rule)
    {
        if (!Visible(p) || !Unlocked(p) || shop.IsPurchased(p)) return 0;
        if (rule.Item.Length == 0) return shop.CapPurchaseQuantity(p, Math.Max(1, p.DefaultBuyLimit - shop.GetUpgradesPurchased(p)));
        if (rule.Once) return shop.GetUpgradesPurchased(p) == 0 ? 1 : 0;
        ItemQuantity? gives = p.Provides.Items.FirstOrDefault(i => i.Item.Id == rule.Item);
        if (gives == null || gives.Quantity <= 0) return 0;
        long missing = rule.Keep - shop.Inventory.GetQuantity(gives.Item);
        return missing <= 0 ? 0 : (int)Math.Min(100_000, (missing + gives.Quantity - 1) / gives.Quantity);
    }

    /// <summary>The rules in buying order: your order, or cheapest first (the purchase that takes the smallest share of what you have).</summary>
    public List<(ShopPurchase P, BuyRule Rule)> Ordered(ShopComponent shop)
    {
        var byId = shop.Config.Purchases.ToDictionary(p => p.Id);
        var list = _k.RuleOrder().Where(byId.ContainsKey).Select(id => (byId[id], _k.Book.Buy[id])).ToList();
        if (_k.Book.MyOrder) return list;
        return list.OrderBy(x => Share(shop, x.Item1)).ToList();
    }

    /// <summary>The largest share of your current holdings one purchase costs (0.1 = a tenth of your GP, say).</summary>
    static double Share(ShopComponent shop, ShopPurchase p)
    {
        double most = 0;
        foreach (CurrencyQuantity c in p.Costs.Currencies)
            most = Math.Max(most, c.Quantity / Math.Max(1.0, shop.Wallet.GetQuantity(c.Currency)));
        foreach (ItemQuantity i in p.Costs.Items)
            most = Math.Max(most, i.Quantity / Math.Max(1.0, shop.Inventory.GetQuantity(i.Item)));
        return most;
    }

    /// <summary>What the rules would buy right now, in order, each one counted against what the ones before it spent.
    /// In your order, a rule that can't be afforded yet holds back later rules that cost the same currencies or items,
    /// so it gets saved up for. Waiting is what it says it is waiting for, if anything.</summary>
    public (List<(ShopPurchase P, int Qty)> Buy, string Waiting) Plan(Game game)
    {
        var buy = new List<(ShopPurchase, int)>();
        string waiting = "";
        ShopComponent? shop = Shop(game);
        if (shop == null || _k.Book.Buy.Count == 0) return (buy, waiting);
        var spent = new Dictionary<string, long>();
        var held = new HashSet<string>();
        foreach (var (p, rule) in Ordered(shop))
        {
            int want = Wanted(shop, p, rule);
            if (want <= 0) continue;
            var keys = p.Costs.Currencies.Select(c => c.Currency.Id).Concat(p.Costs.Items.Select(i => i.Item.Id)).ToList();
            if (keys.Any(held.Contains)) continue;
            int n = Math.Min(want, Affordable(shop, p, spent));
            if (n > 0)
            {
                buy.Add((p, n));
                foreach (CurrencyQuantity c in p.Costs.Currencies) spent[c.Currency.Id] = spent.GetValueOrDefault(c.Currency.Id) + c.Quantity * n;
                foreach (ItemQuantity i in p.Costs.Items) spent[i.Item.Id] = spent.GetValueOrDefault(i.Item.Id) + (long)i.Quantity * n;
            }
            else if (_k.Book.MyOrder)
            {
                foreach (string k in keys) held.Add(k);
                if (waiting.Length == 0) waiting = p.DisplayName.ToString();
            }
        }
        return (buy, waiting);
    }

    public List<(ShopPurchase P, int Qty)> Due(Game game) => Plan(game).Buy;

    /// <summary>Drops rules that are done: an upgrade now owned, or a "buy once" item already bought.
    /// Returns true when any rule went.</summary>
    public bool PruneDone(Game game)
    {
        ShopComponent? shop = Shop(game);
        if (shop == null || _k.Book.Buy.Count == 0) return false;
        var done = shop.Config.Purchases
            .Where(p => _k.Book.Buy.TryGetValue(p.Id, out BuyRule? r) && (shop.IsPurchased(p) || (r.Once && shop.GetUpgradesPurchased(p) > 0)))
            .Select(p => p.Id).ToList();
        foreach (string id in done) { _k.Book.Buy.Remove(id); _k.Book.BuyOrder.Remove(id); }
        return done.Count > 0;
    }

    public (int Bought, string Text) BuyByRules(Game game, bool auto, double now = 0)
    {
        var names = new List<string>();
        ShopComponent? shop = Shop(game);
        if (shop == null) return (0, "");
        foreach (var (p, planned) in Due(game))
        {
            if (auto && _backoff.TryGetValue(p.Id, out double until) && now < until) continue;
            int qty = Math.Min(planned, Affordable(shop, p));   // checked again: the purchases before it have been made
            if (qty <= 0) continue;
            string? err = Buy(game, p, qty, auto);
            if (err == null)
            {
                string what = qty > 1 ? $"{qty} x {p.DisplayName}" : p.DisplayName.ToString();
                names.Add(what);
                Messages.Publish("hold.bought", $"Bought {what} for {CostText(p, qty)}");
            }
            else if (auto) _backoff[p.Id] = now + 300;
        }
        if (PruneDone(game) || names.Count > 0) _k.Commit();
        return (names.Count, string.Join(", ", names));
    }

    readonly HashSet<string> _unlocked = new();
    string _seenFor = "";

    /// <summary>Tells Beacon (over the message bus) when a purchase you have a rule for goes from locked to buyable.
    /// Every purchase's state is tracked, so adding a rule to one that is already open says nothing.</summary>
    public void WatchUnlocks(Game game)
    {
        ShopComponent? shop = Shop(game);
        if (shop == null) return;
        bool first = _seenFor != _k.Character;
        if (first) { _unlocked.Clear(); _seenFor = _k.Character; }
        foreach (ShopPurchase p in shop.Config.Purchases)
        {
            bool open = !shop.IsPurchased(p) && Visible(p) && Unlocked(p);
            if (!open) { _unlocked.Remove(p.Id); continue; }
            if (_unlocked.Add(p.Id) && !first && _k.Book.Buy.ContainsKey(p.Id))
                Messages.Publish("hold.unlocked", $"{p.DisplayName} is unlocked in the shop ({CostText(p)})");
        }
    }

    /// <summary>Buys a purchase. Returns null when it worked, or why it did not.</summary>
    public string? Buy(Game game, ShopPurchase p, int qty, bool auto = false)
    {
        ShopComponent? shop = Shop(game);
        if (shop == null) return "The shop is not loaded.";
        if (!Visible(p) || !Unlocked(p)) return Needs(p);
        qty = shop.CapPurchaseQuantity(p, qty);
        if (qty <= 0) return "Already owned.";
        string cost = CostText(p, qty);
        long gp = p.Costs.Currencies.Where(c => c.Currency.Id == Gold).Sum(c => c.Quantity) * qty;
        InventoryWalletTransactionResult r = shop.ProcessPurchase(p, qty);
        if (r != InventoryWalletTransactionResult.Success)
            return r switch
            {
                InventoryWalletTransactionResult.NotEnoughInventorySpace => "No room in the bank.",
                InventoryWalletTransactionResult.CantAffordItems or InventoryWalletTransactionResult.CantAffordCurrencies => "Can't afford it.",
                InventoryWalletTransactionResult.RequirementsNotMet => Needs(p),
                _ => "The game refused the purchase.",
            };
        PruneDone(game);
        _k.Book.Bought.Insert(0, new Bought { Time = DateTime.Now, Purchase = p.Id, Name = p.DisplayName.ToString(), Qty = qty, Cost = cost, Gp = gp, Auto = auto });
        if (_k.Book.Bought.Count > 500) _k.Book.Bought.RemoveRange(500, _k.Book.Bought.Count - 500);
        if (!auto) _k.Commit();
        return null;
    }

    /// <summary>Currencies the wallet holds, for the reserve row.</summary>
    public static List<Currency> Currencies(Game game)
    {
        ShopComponent? shop = Shop(game);
        if (shop == null) return new();
        return shop.Wallet.Stacks.Keys.OrderBy(c => c.Id == Gold ? 0 : 1).ThenBy(c => c.DisplayPriority).ToList();
    }
}
