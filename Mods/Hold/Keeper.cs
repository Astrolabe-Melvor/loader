using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.ItemSales;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using MelvorModLoader;

namespace Astrolabe.Hold;

public sealed class Sale
{
    public DateTime Time { get; set; }
    public string Item { get; set; } = "";
    public string Name { get; set; } = "";
    public long Qty { get; set; }
    public long Gp { get; set; }
    public bool Auto { get; set; }
}

/// <summary>Per character: the rule for each item (how many to keep; 0 = junk, sell all) and what was sold.</summary>
public sealed class Book
{
    public Dictionary<string, long> Keep { get; set; } = new();
    public bool Auto { get; set; }
    public List<Sale> Sold { get; set; } = new();
    /// <summary>Shop rules by purchase id.</summary>
    public Dictionary<string, BuyRule> Buy { get; set; } = new();
    /// <summary>Never spend a currency below this, by currency id.</summary>
    public Dictionary<string, long> Reserve { get; set; } = new();
    public bool AutoBuy { get; set; }
    /// <summary>True: buy in BuyOrder, saving up for each rule. False: cheapest first.</summary>
    public bool MyOrder { get; set; }
    public List<string> BuyOrder { get; set; } = new();
    public List<Bought> Bought { get; set; } = new();
}

/// <summary>One bank stack as Hold shows it.</summary>
public sealed record Row(Item Item, string Name, long Qty, long Price, int Tab, bool Locked)
{
    public long Worth => Price * Qty;
}

/// <summary>Reads the bank and sells by the rules. Locked stacks are never sold.</summary>
public sealed class Keeper
{
    readonly string _folder;
    readonly Action<string> _log;
    string _character = "";
    double _nextAuto;

    public Book Book { get; private set; } = new();
    public Shopper Shop { get; }
    public event Action? Changed;
    public string Character => _character;

    public Keeper(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
        Shop = new Shopper(this);
    }

    static BankEntity? Bank(Game game) => game.Entities.GetAllObjects().OfType<BankEntity>().FirstOrDefault();

    public void Tick(Game? game, double now)
    {
        if (game == null) return;
        if (game.CharacterName != _character) Load(game.CharacterName);
        if (GameAccess.IsComputingOffline || now < _nextAuto) return;
        _nextAuto = now + 10;
        try { Shop.WatchUnlocks(game); if (Shop.PruneDone(game)) Commit(); } catch { }
        if (Book.Auto)
        {
            var (count, gp) = SellByRules(game, auto: true);
            if (count > 0) _log($"Sold {count} stacks for {gp:N0} GP by your rules");
        }
        if (Book.AutoBuy)
        {
            try
            {
                var (n, text) = Shop.BuyByRules(game, auto: true, now);
                if (n > 0) _log("Bought by your shop rules: " + text);
            }
            catch (Exception e) { _log("Shop rules failed: " + e.Message); }
        }
    }

    /// <summary>Every stack in the bank with its sell price (with your sale bonuses) and tab.</summary>
    public List<Row> Rows(Game game)
    {
        var rows = new List<Row>();
        BankEntity? bank = Bank(game);
        if (bank == null) return rows;
        BankInventoryComponent inv = bank.Inventory;
        ItemSaleComponent sale = bank.ItemSales;
        foreach (IReadonlyItemStack st in inv.Stacks)
        {
            if (st.IsPlaceholder || rows.Any(r => r.Item == st.Item)) continue;
            long price = 0;
            try { price = sale.GetItemStats(st.Item).PricePerItem.Value; } catch { }
            int tab = 0;
            try { tab = inv.GetTabIndexForItem(st.Item); } catch { }
            rows.Add(new Row(st.Item, st.Item.DisplayName.ToString(), ((IInventory)inv).GetQuantity(st.Item), price, tab, st.IsLocked));
        }
        return rows;
    }

    public (int Used, int Max) Slots(Game game)
    {
        BankEntity? bank = Bank(game);
        return bank == null ? (0, 0) : (bank.Inventory.StackCount, bank.Inventory.MaxStacks);
    }

    /// <summary>What the rules would sell right now: (row, how many).</summary>
    /// <summary>A pack rule's target counts as a keep amount, so Hold never sells what it just bought.</summary>
    public List<(Row Row, long Sell)> Due(Game game) =>
        Rows(game).Where(r => !r.Locked && Book.Keep.ContainsKey(r.Item.Id) && r.Qty > Shop.ItemFloor(r.Item.Id))
            .Select(r => (r, r.Qty - Shop.ItemFloor(r.Item.Id))).ToList();

    public (int Stacks, long Gp) SellByRules(Game game, bool auto)
    {
        int n = 0;
        long gp = 0;
        foreach (var (row, sell) in Due(game))
        {
            long got = Sell(game, row.Item, sell, auto);
            if (got >= 0) { n++; gp += got; }
        }
        if (n > 0) { Save(); Changed?.Invoke(); }
        return (n, gp);
    }

    /// <summary>Sells some of an item. Returns the GP it brought, or -1 when nothing was sold.</summary>
    public long Sell(Game game, Item item, long qty, bool auto = false)
    {
        BankEntity? bank = Bank(game);
        if (bank == null || qty <= 0) return -1;
        IReadonlyItemStack? st = bank.Inventory.Stacks.FirstOrDefault(s => s.Item == item && !s.IsPlaceholder);
        if (st == null || st.IsLocked) return -1;
        ItemSaleComponentStats stats = bank.ItemSales.GetItemStats(item);
        int amount = (int)Math.Min(int.MaxValue, Math.Min(qty, ((IInventory)bank.Inventory).GetQuantity(item)));
        if (amount <= 0) return -1;
        long price = stats.PricePerItem.Value;
        bank.ItemSales.SellItem(item, stats, amount);
        long gp = price * amount;
        Book.Sold.Insert(0, new Sale { Time = DateTime.Now, Item = item.Id, Name = item.DisplayName.ToString(), Qty = amount, Gp = gp, Auto = auto });
        if (Book.Sold.Count > 500) Book.Sold.RemoveRange(500, Book.Sold.Count - 500);
        if (!auto) { Save(); Changed?.Invoke(); }
        return gp;
    }

    public void SetRule(string itemId, long? keep)
    {
        if (keep == null) Book.Keep.Remove(itemId);
        else Book.Keep[itemId] = Math.Max(0, keep.Value);
        Save();
        Changed?.Invoke();
    }

    public void SetBuyRule(string purchaseId, BuyRule? rule)
    {
        if (rule == null) { Book.Buy.Remove(purchaseId); Book.BuyOrder.Remove(purchaseId); }
        else
        {
            Book.Buy[purchaseId] = rule;
            if (!Book.BuyOrder.Contains(purchaseId)) Book.BuyOrder.Add(purchaseId);
        }
        Commit();
    }

    /// <summary>Rule ids in your order; rules saved before the order existed go last.</summary>
    public List<string> RuleOrder() =>
        Book.BuyOrder.Where(Book.Buy.ContainsKey).Concat(Book.Buy.Keys.Where(k => !Book.BuyOrder.Contains(k))).ToList();

    /// <summary>Moves a rule earlier (-1) or later (+1) in your order.</summary>
    public void MoveRule(string purchaseId, int by)
    {
        var order = RuleOrder();
        int at = order.IndexOf(purchaseId);
        int to = at + by;
        if (at < 0 || to < 0 || to >= order.Count) return;
        (order[at], order[to]) = (order[to], order[at]);
        Book.BuyOrder = order;
        Commit();
    }

    public void SetMyOrder(bool on)
    {
        Book.MyOrder = on;
        Commit();
    }

    public void SetReserve(string currencyId, long amount)
    {
        if (amount <= 0) Book.Reserve.Remove(currencyId);
        else Book.Reserve[currencyId] = amount;
        Save();
    }

    public void SetAutoBuy(bool on)
    {
        Book.AutoBuy = on;
        Commit();
    }

    /// <summary>Save and tell the window.</summary>
    public void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    public void SetAuto(bool on)
    {
        Book.Auto = on;
        Save();
        Changed?.Invoke();
    }

    string File_ => Path.Combine(_folder, "rules", Safe(_character) + ".json");

    static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    void Load(string character)
    {
        _character = character;
        try { Book = File.Exists(File_) ? JsonSerializer.Deserialize<Book>(File.ReadAllText(File_)) ?? new() : new(); }
        catch { Book = new(); }
        Changed?.Invoke();
    }

    void Save()
    {
        if (_character.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(Book));
        }
        catch (Exception e) { _log("Could not save rules: " + e.Message); }
    }
}
