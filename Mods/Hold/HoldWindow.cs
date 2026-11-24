using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;
using GamesByMalcs.Melvor2.Data.Currencies;

namespace Astrolabe.Hold;

/// <summary>F9: what the bank is worth, a rule per item (keep some, or always sell), shop rules, and a history of both.</summary>
sealed class HoldWindow
{
    readonly Keeper _k;
    readonly KitWindow _win;
    readonly Button[] _tabButtons = new Button[3];
    readonly Control[] _panels = new Control[3];
    int _tab;

    readonly Label _summary, _due, _ruleFor, _soldTotal;
    readonly HFlowContainer _tabs;
    readonly LineEdit _search;
    readonly OptionButton _show, _rule;
    readonly SpinBox _keep;
    readonly Button _apply, _sellNow, _sellRules;
    readonly CheckBox _auto;
    readonly Tree _table, _sold;
    int _tabFilter = -1;
    string? _selected;
    List<Row> _rows = new();

    // shop
    readonly Label _shopSummary, _offerFor, _buyDue, _buyMsg;
    readonly LineEdit _shopSearch;
    readonly OptionButton _shopShow, _shopCat, _buyRule;
    readonly SpinBox _buyKeep, _buyQty;
    readonly Button _saveBuy, _buyNow, _buyRules, _ruleAll, _up, _down;
    readonly OptionButton _order;
    readonly CheckBox _autoBuy;
    readonly Tree _shop;
    readonly HFlowContainer _reserves;
    string? _offer;
    List<Offer> _offers = new();

    public HoldWindow(Keeper k)
    {
        _k = k;
        _win = new KitWindow("Hold", "what your bank is worth, what to keep, and what to buy", new Vector2(980, 0));
        _win.SetHelp(Help.Text);

        var tabRow = new HBoxContainer();
        string[] names = { "Bank", "Shop", "History" };
        for (int i = 0; i < names.Length; i++)
        {
            int at = i;
            _tabButtons[i] = Kit.Btn(names[i], () => Show(at));
            _tabButtons[i].ThemeTypeVariation = "Tab";
            _tabButtons[i].ToggleMode = true;
            tabRow.AddChild(_tabButtons[i]);
        }
        _win.Body.AddChild(tabRow);

        // ---- bank ----
        var bank = Panel(0);
        _summary = Kit.Label("", null, 14);
        bank.AddChild(_summary);
        _tabs = new HFlowContainer();
        _tabs.AddThemeConstantOverride("h_separation", 6);
        bank.AddChild(_tabs);

        _search = new LineEdit { PlaceholderText = "Search the bank", CustomMinimumSize = new Vector2(260, 0) };
        _search.TextChanged += _ => Fill();
        _show = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        foreach (string s in new[] { "Everything, most valuable first", "Only items with a rule", "Only items without a rule", "Worth nothing to sell" }) _show.AddItem(s);
        _show.ItemSelected += _ => Fill();
        bank.AddChild(Kit.Row(_search, _show));

        _table = new Tree { Columns = 6, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, CustomMinimumSize = new Vector2(0, 300), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        string[] cols = { "Item", "In the bank", "Sells for", "Worth", "Tab", "Rule" };
        int[] widths = { 280, 110, 100, 120, 60, 200 };
        for (int c = 0; c < cols.Length; c++)
        {
            _table.SetColumnTitle(c, cols[c]);
            _table.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _table.SetColumnCustomMinimumWidth(c, widths[c]);
            _table.SetColumnExpand(c, c == 0);
        }
        _table.ItemSelected += () => Select(_table.GetSelected()?.GetMetadata(0).AsString());
        bank.AddChild(_table);

        // Rule for the selected item.
        _ruleFor = Kit.Label("Pick an item to give it a rule or sell it.", null, 13, "Dim");
        _ruleFor.CustomMinimumSize = new Vector2(250, 0);
        _rule = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        _rule.AddItem("No rule");
        _rule.AddItem("Keep some, sell the rest");
        _rule.AddItem("Junk: always sell all");
        _rule.ItemSelected += _ => { _keep!.Visible = _rule.Selected == 1; };
        _keep = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 100_000_000, Value = 100, Prefix = "keep", CustomMinimumSize = new Vector2(150, 0), Visible = false });
        _apply = Kit.Btn("Save rule", SaveRule, action: true);
        _sellNow = Kit.Btn("Sell all now", () => SellSelected());
        bank.AddChild(Kit.Row(_ruleFor, _rule, _keep, _apply, _sellNow));

        _due = Kit.Label("", null, 13, "Dim");
        _due.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _sellRules = Kit.Btn("Sell by my rules now", () => { if (GameAccess.CurrentGame is Game g) _k.SellByRules(g, auto: false); }, action: true);
        _auto = new CheckBox { Text = "Sell by my rules automatically" };
        _auto.TooltipText = "Checked every 10 seconds while you play. Locked bank stacks are never sold.";
        _auto.Toggled += on => _k.SetAuto(on);
        bank.AddChild(Kit.Row(_due, _auto, _sellRules));

        // ---- shop ----
        var shop = Panel(1);
        _shopSummary = Kit.Label("", null, 14);
        shop.AddChild(_shopSummary);
        _shopSearch = new LineEdit { PlaceholderText = "Search the shop", CustomMinimumSize = new Vector2(240, 0) };
        _shopSearch.TextChanged += _ => FillShop();
        _shopShow = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        foreach (string s in new[] { "Ready to buy now", "Everything you can see", "Never bought", "Upgrades (buy limit)", "Items (no buy limit)", "Only purchases with a rule", "Owned or bought before" }) _shopShow.AddItem(s);
        _shopShow.Select(1);
        _shopShow.ItemSelected += _ => FillShop();
        _shopCat = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        _shopCat.ItemSelected += _ => FillShop();
        shop.AddChild(Kit.Row(_shopSearch, _shopShow, _shopCat));

        _shop = new Tree { Columns = 5, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, CustomMinimumSize = new Vector2(0, 280), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        string[] shcols = { "Purchase", "Category", "Costs", "Status", "Rule" };
        int[] shw = { 240, 120, 200, 260, 160 };
        for (int c = 0; c < shcols.Length; c++)
        {
            _shop.SetColumnTitle(c, shcols[c]);
            _shop.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _shop.SetColumnCustomMinimumWidth(c, shw[c]);
            _shop.SetColumnExpand(c, c is 0 or 1 or 3);
        }
        _shop.ItemSelected += () => SelectOffer(_shop.GetSelected()?.GetMetadata(0).AsString());
        shop.AddChild(_shop);

        // Rule and buy for the selected purchase. The rule list only offers what fits the purchase.
        _offerFor = Kit.Label("Pick a purchase to give it a rule or buy it.", null, 13, "Dim");
        _offerFor.CustomMinimumSize = new Vector2(240, 0);
        _buyRule = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        _buyRule.ItemSelected += _ => _buyKeep!.Visible = _buyRule.GetSelectedId() == 3;
        _buyKeep = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 100_000_000, Value = 100, Prefix = "keep", CustomMinimumSize = new Vector2(170, 0), Visible = false });
        _saveBuy = Kit.Btn("Save rule", SaveBuyRule, action: true);
        _up = Kit.Btn("Earlier", () => Move(-1));
        _up.TooltipText = "Buy this one before the rule above it.";
        _down = Kit.Btn("Later", () => Move(1));
        _down.TooltipText = "Buy this one after the rule below it.";
        _buyQty = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 100_000, Value = 1, Prefix = "x", CustomMinimumSize = new Vector2(110, 0) });
        _buyQty.ValueChanged += _ => UpdateBuyButton();
        _buyNow = Kit.Btn("Buy", BuySelected);
        shop.AddChild(Kit.Row(_offerFor, _buyRule, _buyKeep, _saveBuy, _up, _down, Kit.Spacer(), _buyQty, _buyNow));
        _buyMsg = Kit.Label("", null, 13, "Dim");
        shop.AddChild(_buyMsg);

        _order = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        _order.AddItem("Cheapest first");
        _order.AddItem("In my order, saving up for each");
        _order.TooltipText = "Cheapest first: when money is short, the rules that take the smallest share of what you have go first.\n"
            + "In my order: rules go in the order shown by #. One you can't afford yet makes later rules that cost the same currency or item wait, so it gets saved up for. Use Earlier and Later to change the order.";
        _order.ItemSelected += i => _k.SetMyOrder(i == 1);
        _reserves = new HFlowContainer();
        _reserves.AddThemeConstantOverride("h_separation", 8);
        shop.AddChild(Kit.Row(Kit.Label("Buy order:", null, 13), _order));
        shop.AddChild(_reserves);

        _buyDue = Kit.Label("", null, 13, "Dim");
        _buyDue.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _buyDue.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ruleAll = Kit.Btn("Rule: buy every upgrade listed", RuleAllUpgrades);
        _ruleAll.TooltipText = "Gives every upgrade in the table above (not owned yet) the rule 'Buy as soon as I can'.";
        _autoBuy = new CheckBox { Text = "Buy by my shop rules automatically" };
        _autoBuy.TooltipText = "Checked every 10 seconds while you play. Never spends below your reserves or an item's keep amount.";
        _autoBuy.Toggled += on => _k.SetAutoBuy(on);
        _buyRules = Kit.Btn("Buy by my rules now", () => { if (GameAccess.CurrentGame is Game g) Report(_k.Shop.BuyByRules(g, auto: false)); }, action: true);
        shop.AddChild(Kit.Row(_buyDue, _ruleAll, _autoBuy, _buyRules));

        // ---- history ----
        var sold = Panel(2);
        _soldTotal = Kit.Label("", null, 14);
        sold.AddChild(_soldTotal);
        _sold = new Tree { Columns = 5, ColumnTitlesVisible = true, HideRoot = true, CustomMinimumSize = new Vector2(0, 420), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        string[] scol = { "Time", "What", "Amount", "GP", "Paid with" };
        int[] sw = { 120, 300, 90, 110, 220 };
        for (int c = 0; c < scol.Length; c++)
        {
            _sold.SetColumnTitle(c, scol[c]);
            _sold.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _sold.SetColumnCustomMinimumWidth(c, sw[c]);
            _sold.SetColumnExpand(c, c == 1);
        }
        sold.AddChild(_sold);

        _win.Opened += () => Show(_tab);
        _k.Changed += () => { if (_win.Visible) Refresh(); };
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";

    VBoxContainer Panel(int index)
    {
        var card = new PanelContainer { ThemeTypeVariation = "Card", Visible = index == 0, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var box = new VBoxContainer();
        card.AddChild(box);
        _panels[index] = card;
        _win.Body.AddChild(card);
        return box;
    }

    void Show(int which)
    {
        _tab = which;
        for (int i = 0; i < _panels.Length; i++)
        {
            _panels[i].Visible = i == which;
            _tabButtons[i].SetPressedNoSignal(i == which);
        }
        Refresh();
    }

    static string Num(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e9 ? $"{v / 1e9:0.##}B" : a >= 1e6 ? $"{v / 1e6:0.##}M" : a >= 1e4 ? $"{v / 1e3:0.#}k" : $"{v:N0}";
    }

    string RuleText(string id) =>
        !_k.Book.Keep.TryGetValue(id, out long keep) ? "" : keep == 0 ? "junk: sell all" : $"keep {keep:N0}, sell the rest";

    void Refresh()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) { _summary.Text = "Load a character to see the bank."; return; }
        _auto.SetPressedNoSignal(_k.Book.Auto);
        _autoBuy.SetPressedNoSignal(_k.Book.AutoBuy);
        _order.Select(_k.Book.MyOrder ? 1 : 0);
        if (_tab == 1) { RefreshShop(game); return; }
        if (_tab == 2) { FillSold(); return; }

        _rows = _k.Rows(game);
        var (used, max) = _k.Slots(game);
        _summary.Text = $"{used} of {max} slots used. The bank sells for {Num(_rows.Sum(r => r.Worth))} GP in all.";

        // One button per bank tab with what it is worth; click to show just that tab.
        foreach (Node n in _tabs.GetChildren()) { _tabs.RemoveChild(n); n.QueueFree(); }
        var all = Kit.Btn("All tabs", () => { _tabFilter = -1; Refresh(); });
        all.ThemeTypeVariation = "Tab";
        all.ToggleMode = true;
        all.SetPressedNoSignal(_tabFilter < 0);
        _tabs.AddChild(all);
        foreach (var g in _rows.GroupBy(r => r.Tab).OrderBy(g => g.Key))
        {
            int tab = g.Key;
            var b = Kit.Btn($"{Num(g.Sum(r => r.Worth))} GP", () => { _tabFilter = tab; Refresh(); });
            b.Icon = Kit.Icon(g.First().Item.Icon, 18);
            b.ThemeTypeVariation = "Tab";
            b.ToggleMode = true;
            b.TooltipText = $"Tab {tab + 1}: {g.Count()} stacks";
            b.SetPressedNoSignal(_tabFilter == tab);
            _tabs.AddChild(b);
        }

        var due = _k.Due(game);
        _due.Text = _k.Book.Keep.Count == 0
            ? "No rules yet. Pick an item below the table to give it one."
            : due.Count == 0 ? $"{_k.Book.Keep.Count} rules. Nothing to sell right now."
            : $"{_k.Book.Keep.Count} rules. Would sell {due.Count} stacks now for about {Num(due.Sum(d => d.Sell * d.Row.Price))} GP.";
        _sellRules.Disabled = due.Count == 0;
        Fill();
    }

    void Fill()
    {
        Game? game = GameAccess.CurrentGame;
        _table.Clear();
        TreeItem root = _table.CreateItem();
        string q = _search.Text.Trim();
        IEnumerable<Row> rows = _rows.Where(r => _tabFilter < 0 || r.Tab == _tabFilter);
        if (q.Length > 0) rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        rows = _show.Selected switch
        {
            1 => rows.Where(r => _k.Book.Keep.ContainsKey(r.Item.Id)),
            2 => rows.Where(r => !_k.Book.Keep.ContainsKey(r.Item.Id)),
            3 => rows.Where(r => r.Price == 0),
            _ => rows,
        };
        foreach (Row r in rows.OrderByDescending(r => r.Worth).ThenBy(r => r.Name))
        {
            TreeItem t = _table.CreateItem(root);
            t.SetMetadata(0, r.Item.Id);
            t.SetText(0, r.Name);
            t.SetIcon(0, Kit.Icon(r.Item.Icon, 18));
            t.SetText(1, r.Qty.ToString("N0"));
            t.SetText(2, r.Price > 0 ? Num(r.Price) : "nothing");
            t.SetText(3, r.Worth > 0 ? Num(r.Worth) : "");
            t.SetText(4, (r.Tab + 1).ToString());
            t.SetText(5, r.Locked ? "locked, never sold" : RuleText(r.Item.Id));
            t.SetCustomColor(4, Kit.Dim);
            if (r.Locked) t.SetCustomColor(5, Kit.Dim);
            else if (_k.Book.Keep.TryGetValue(r.Item.Id, out long keep)) t.SetCustomColor(5, keep == 0 ? Kit.Gold : Kit.Link);
            if (r.Item.Id == _selected) t.Select(0);
        }
        Select(_selected);
    }

    void Select(string? id)
    {
        _selected = id;
        Row? r = _rows.FirstOrDefault(x => x.Item.Id == id);
        bool on = r != null;
        _rule.Disabled = _apply.Disabled = !on;
        _sellNow.Disabled = !on || r!.Locked || r.Qty == 0;
        if (r == null) { _ruleFor.Text = "Pick an item to give it a rule or sell it."; _keep.Visible = false; return; }
        _ruleFor.Text = $"{r.Name} ({r.Qty:N0}, {Num(r.Worth)} GP)";
        if (!_k.Book.Keep.TryGetValue(r.Item.Id, out long keep)) { _rule.Select(0); _keep.Visible = false; }
        else if (keep == 0) { _rule.Select(2); _keep.Visible = false; }
        else { _rule.Select(1); _keep.Value = keep; _keep.Visible = true; }
        _sellNow.Text = r.Locked ? "Locked" : $"Sell all for {Num(r.Worth)} GP";
    }

    void SaveRule()
    {
        if (_selected == null) return;
        _k.SetRule(_selected, _rule.Selected switch { 1 => (long)_keep.Value, 2 => 0, _ => null });
    }

    void SellSelected()
    {
        if (_selected == null || GameAccess.CurrentGame is not Game game) return;
        Row? r = _rows.FirstOrDefault(x => x.Item.Id == _selected);
        if (r != null) _k.Sell(game, r.Item, r.Qty);
    }

    void FillSold()
    {
        _sold.Clear();
        TreeItem root = _sold.CreateItem();
        var today = _k.Book.Sold.Where(s => s.Time.Date == DateTime.Today).ToList();
        var spent = _k.Book.Bought.Where(b => b.Time.Date == DateTime.Today).ToList();
        _soldTotal.Text = $"Today: sold {Num(today.Sum(s => s.Gp))} GP in {today.Count} sales, spent {Num(spent.Sum(b => b.Gp))} GP on {spent.Count} purchases. "
            + $"All kept: sold {Num(_k.Book.Sold.Sum(s => s.Gp))} GP, spent {Num(_k.Book.Bought.Sum(b => b.Gp))} GP.";
        var lines = _k.Book.Sold.Select(s => (s.Time, What: "Sold " + s.Name + (s.Auto ? " (by rule)" : ""), Qty: s.Qty, Gp: s.Gp, Paid: ""))
            .Concat(_k.Book.Bought.Select(b => (b.Time, What: "Bought " + b.Name + (b.Auto ? " (by rule)" : ""), Qty: (long)b.Qty, Gp: -b.Gp, Paid: b.Cost)))
            .OrderByDescending(l => l.Time);
        foreach (var l in lines)
        {
            TreeItem t = _sold.CreateItem(root);
            t.SetText(0, l.Time.ToString("MMM d HH:mm"));
            t.SetText(1, l.What);
            t.SetText(2, l.Qty.ToString("N0"));
            t.SetText(3, l.Gp > 0 ? "+" + Num(l.Gp) : l.Gp < 0 ? "-" + Num(-l.Gp) : "");
            t.SetCustomColor(3, l.Gp >= 0 ? Kit.Gold : Kit.Bad);
            t.SetText(4, l.Paid);
            t.SetCustomColor(4, Kit.Dim);
        }
        if (_k.Book.Sold.Count == 0 && _k.Book.Bought.Count == 0)
        {
            TreeItem t = _sold.CreateItem(root);
            t.SetText(1, "Nothing sold or bought through Hold yet.");
            t.SetCustomColor(1, Kit.Dim);
        }
    }

    // ---- shop ----

    static string RuleText(BuyRule r, Offer? o) =>
        r.Item.Length == 0 ? "buy as soon as I can" : r.Once ? "buy once" : $"keep {r.Keep:N0}" + (o?.Gives != null ? $" {o.Gives.DisplayName}" : "");

    void RefreshShop(Game game)
    {
        _offers = _k.Shop.Offers(game);
        if (_offers.Count == 0) { _shopSummary.Text = "The shop is not loaded yet."; return; }
        int ready = _offers.Count(o => o.State == State.Ready);
        _shopSummary.Text = $"{ready} purchases you can buy now, {_offers.Count(o => o.State == State.Locked)} still locked. {_k.Book.Buy.Count} shop rules.";

        // Category list, keeping the current pick.
        string cat = _shopCat.ItemCount > 0 ? _shopCat.GetItemText(_shopCat.Selected) : "All categories";
        _shopCat.Clear();
        _shopCat.AddItem("All categories");
        foreach (string c in _offers.Select(o => o.Category).Distinct()) _shopCat.AddItem(c);
        for (int i = 0; i < _shopCat.ItemCount; i++) if (_shopCat.GetItemText(i) == cat) _shopCat.Select(i);

        // Reserves: one box per currency you hold.
        foreach (Node n in _reserves.GetChildren()) { _reserves.RemoveChild(n); n.QueueFree(); }
        _reserves.AddChild(Kit.Label("Never spend below:", null, 13));
        foreach (Currency c in Shopper.Currencies(game))
        {
            string id = c.Id;
            var box = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 1e15, Value = _k.Shop.Reserve(id), CustomMinimumSize = new Vector2(150, 0) });
            box.Suffix = id == Shopper.Gold ? "GP" : c.DisplayNameShort.ToString();
            box.TooltipText = $"Shop rules never take your {c.DisplayName} below this. Item costs never go below the item's keep amount in Rules.";
            box.ValueChanged += v => _k.SetReserve(id, (long)v);
            _reserves.AddChild(box);
        }

        var (due, waiting) = _k.Shop.Plan(game);
        string wait = waiting.Length > 0 ? $" Saving up for {waiting}." : "";
        _buyDue.Text = _k.Book.Buy.Count == 0
            ? "No shop rules yet. Pick a purchase to give it one."
            : due.Count == 0 ? "Nothing to buy by your rules right now." + wait
            : "Would buy now: " + string.Join(", ", due.Take(4).Select(d => d.Qty > 1 ? $"{d.Qty} x {d.P.DisplayName}" : d.P.DisplayName.ToString())) + (due.Count > 4 ? $" and {due.Count - 4} more" : "") + "." + wait;
        _buyRules.Disabled = due.Count == 0;
        FillShop();
    }

    IEnumerable<Offer> ShownOffers()
    {
        string q = _shopSearch.Text.Trim();
        IEnumerable<Offer> rows = _offers;
        if (_shopCat.Selected > 0) { string c = _shopCat.GetItemText(_shopCat.Selected); rows = rows.Where(o => o.Category == c); }
        if (q.Length > 0) rows = rows.Where(o => o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Section.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (o.Gives?.DisplayName.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        return _shopShow.Selected switch
        {
            0 => rows.Where(o => o.State == State.Ready),
            2 => rows.Where(o => o.Times == 0),
            3 => rows.Where(o => o.Kind == Kind.Upgrade),
            4 => rows.Where(o => o.Kind == Kind.Item),
            5 => rows.Where(o => _k.Book.Buy.ContainsKey(o.P.Id)),
            6 => rows.Where(o => o.Times > 0),
            _ => rows,
        };
    }

    void FillShop()
    {
        _shop.Clear();
        TreeItem root = _shop.CreateItem();
        var shown = ShownOffers().ToList();
        var order = _k.RuleOrder();
        if (_shopShow.Selected == 5 && _k.Book.MyOrder) shown = shown.OrderBy(o => order.IndexOf(o.P.Id)).ToList();
        foreach (Offer o in shown)
        {
            TreeItem t = _shop.CreateItem(root);
            t.SetMetadata(0, o.P.Id);
            t.SetText(0, o.Name);
            // Some purchases have no icon of their own; the item they give always does.
            t.SetIcon(0, Kit.Icon(o.Gives?.Icon ?? o.P.Icon, 18));
            t.SetText(1, o.Section.Length > 0 ? $"{o.Category}: {o.Section}" : o.Category);
            t.SetTooltipText(1, t.GetText(1));
            t.SetCustomColor(1, Kit.Dim);
            t.SetText(2, o.State == State.Owned ? "" : o.Cost);
            t.SetText(3, o.Note);
            t.SetTooltipText(3, o.Note);
            t.SetCustomColor(3, o.State switch { State.Ready => Kit.Good, State.Short => Kit.Gold, _ => Kit.Dim });
            if (_k.Book.Buy.TryGetValue(o.P.Id, out BuyRule? r))
            {
                t.SetText(4, (_k.Book.MyOrder ? $"#{order.IndexOf(o.P.Id) + 1} " : "") + RuleText(r, o));
                t.SetCustomColor(4, Kit.Link);
            }
            if (o.P.Id == _offer) t.Select(0);
        }
        if (shown.Count == 0)
        {
            TreeItem t = _shop.CreateItem(root);
            t.SetText(0, _shopShow.Selected == 0 ? "Nothing you can buy right now." : "Nothing matches.");
            t.SetCustomColor(0, Kit.Dim);
        }
        _ruleAll.Disabled = !shown.Any(o => o.Kind == Kind.Upgrade && o.State != State.Owned && !_k.Book.Buy.ContainsKey(o.P.Id));
        // Keep the pick only while it is in the table, so the buttons never act on a row you cannot see.
        SelectOffer(shown.Any(o => o.P.Id == _offer) ? _offer : null);
    }

    void SelectOffer(string? id)
    {
        _offer = id;
        Offer? o = _offers.FirstOrDefault(x => x.P.Id == id);
        _buyRule.Clear();
        _buyRule.AddItem("No rule", 0);
        if (o == null)
        {
            _offerFor.Text = "Pick a purchase to give it a rule or buy it.";
            _buyRule.Disabled = _saveBuy.Disabled = _buyNow.Disabled = true;
            _up.Visible = _down.Visible = false;
            _buyKeep.Visible = false;
            _buyQty.Visible = false;
            return;
        }
        _offerFor.Text = o.Name + (o.Kind == Kind.Item && o.Gives != null && o.PerPack > 1 ? $" ({o.PerPack:N0} {o.Gives.DisplayName} each)" : "");

        // Only rules that fit: upgrades get "buy as soon as I can", items get "buy once" (not bought yet) or "keep N", others none.
        bool owned = o.State == State.Owned;
        _k.Book.Buy.TryGetValue(o.P.Id, out BuyRule? r);
        if (o.Kind == Kind.Upgrade && (!owned || r != null)) _buyRule.AddItem("Buy as soon as I can", 1);
        if (o.Kind == Kind.Item && o.Gives != null && (o.Times == 0 || r?.Once == true)) _buyRule.AddItem("Buy once as soon as I can", 2);
        if (o.Kind == Kind.Item && o.Gives != null) _buyRule.AddItem($"Keep {o.Gives.DisplayName} stocked", 3);
        _buyRule.Disabled = _buyRule.ItemCount == 1;
        _buyRule.TooltipText = owned ? "Already owned." : o.Kind == Kind.Other ? "This purchase gives no item, so it can only be bought by hand." : "";
        int sel = r == null ? 0 : r.Item.Length == 0 ? 1 : r.Once ? 2 : 3;
        int idx = _buyRule.GetItemIndex(sel);
        _buyRule.Select(idx < 0 ? 0 : idx);
        if (r != null && r.Item.Length > 0 && !r.Once) _buyKeep.Value = r.Keep;
        else if (o.Kind == Kind.Item) _buyKeep.Value = Math.Max(1, o.PerPack);
        _buyKeep.Suffix = o.Gives?.DisplayName.ToString() ?? "";
        _buyKeep.Visible = _buyRule.GetSelectedId() == 3;
        _saveBuy.Disabled = _buyRule.Disabled;
        var ord = _k.RuleOrder();
        int pos = ord.IndexOf(o.P.Id);
        _up.Visible = _down.Visible = _k.Book.MyOrder && pos >= 0;
        _up.Disabled = pos <= 0;
        _down.Disabled = pos < 0 || pos >= ord.Count - 1;

        _buyQty.Visible = o.Kind != Kind.Upgrade || o.P.DefaultBuyLimit > 1;
        if (!_buyQty.Visible) _buyQty.Value = 1;
        _buyMsg.Text = o.State == State.Locked ? o.Note : "";
        UpdateBuyButton();
    }

    void UpdateBuyButton()
    {
        Offer? o = _offers.FirstOrDefault(x => x.P.Id == _offer);
        if (o == null || GameAccess.CurrentGame is not Game game || Shopper.Shop(game) is not { } shop) { _buyNow.Disabled = true; return; }
        int qty = (int)_buyQty.Value;
        int can = o.State is State.Owned or State.Locked ? 0 : _k.Shop.Affordable(shop, o.P);
        _buyNow.Disabled = can < qty;
        _buyNow.Text = o.State == State.Owned ? "Owned" : o.State == State.Locked ? "Locked" : $"Buy for {Shopper.CostText(o.P, qty)}";
        _buyNow.TooltipText = can < qty && o.State is State.Ready or State.Short
            ? (can == 0 ? "Can't afford it without going under your reserves or keep amounts." : $"You can afford {can} without going under your reserves.")
            : "";
    }

    void SaveBuyRule()
    {
        Offer? o = _offers.FirstOrDefault(x => x.P.Id == _offer);
        if (o == null) return;
        _k.SetBuyRule(o.P.Id, _buyRule.GetSelectedId() switch
        {
            1 => new BuyRule(),
            2 when o.Gives != null => new BuyRule { Item = o.Gives.Id, Once = true },
            3 when o.Gives != null => new BuyRule { Item = o.Gives.Id, Keep = (long)_buyKeep.Value },
            _ => null,
        });
    }

    void RuleAllUpgrades()
    {
        int n = 0;
        foreach (Offer o in ShownOffers().Where(o => o.Kind == Kind.Upgrade && o.State != State.Owned && !_k.Book.Buy.ContainsKey(o.P.Id)).ToList())
        {
            _k.Book.Buy[o.P.Id] = new BuyRule();
            if (!_k.Book.BuyOrder.Contains(o.P.Id)) _k.Book.BuyOrder.Add(o.P.Id);
            n++;
        }
        _k.Commit();
        _buyMsg.Text = $"Added 'buy as soon as I can' to {n} upgrades.";
    }

    void Move(int by)
    {
        if (_offer != null) _k.MoveRule(_offer, by);
    }

    void BuySelected()
    {
        Offer? o = _offers.FirstOrDefault(x => x.P.Id == _offer);
        if (o == null || GameAccess.CurrentGame is not Game game) return;
        string? err = _k.Shop.Buy(game, o.P, (int)_buyQty.Value);
        _buyMsg.Text = err ?? $"Bought {o.Name}.";
    }

    void Report((int Bought, string Text) r) =>
        _buyMsg.Text = r.Bought == 0 ? "Nothing was bought." : "Bought " + r.Text + ".";
}
