using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Spyglass;

/// <summary>F10: look up any item: where it comes from, what uses it, what it unlocks, how many you have, and plan it in Course.</summary>
sealed class SpyglassWindow
{
    const string WikiSite = "https://astrolabe-melvor.github.io/";

    readonly string _folder;
    readonly Action<string> _log;
    readonly KitWindow _win;
    readonly LineEdit _search;
    readonly ItemList _list;
    readonly TextureRect _icon;
    readonly Label _title, _facts, _status;
    readonly SpinBox _amount;
    readonly Button _plan, _wiki, _back;
    readonly Tree _sources, _uses, _unlocks;
    readonly List<string> _history = new();
    string? _item;
    double _retry;

    public SpyglassWindow(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
        _win = new KitWindow("Spyglass", "where an item comes from and what it is for", new Vector2(1060, 0));
        _win.SetHelp(Help.Text);

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 10);
        _win.Body.AddChild(cols);

        // Left: search.
        var left = new PanelContainer { ThemeTypeVariation = "Card" };
        var lbox = new VBoxContainer();
        left.AddChild(lbox);
        _search = new LineEdit { PlaceholderText = "Search items", CustomMinimumSize = new Vector2(280, 0) };
        _search.TextChanged += _ => Fill();
        _search.TextSubmitted += _ => { if (_list!.ItemCount > 0) Pick(0); };
        lbox.AddChild(_search);
        _list = new ItemList { CustomMinimumSize = new Vector2(280, 560), FixedIconSize = new Vector2I(20, 20) };
        _list.ItemSelected += i => Pick((int)i);
        lbox.AddChild(_list);
        _status = Kit.Label("", null, 12, "Dim");
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(280, 0);
        lbox.AddChild(_status);
        cols.AddChild(left);

        // Right: the item.
        var right = new PanelContainer { ThemeTypeVariation = "Card", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var rbox = new VBoxContainer();
        right.AddChild(rbox);
        _icon = new TextureRect { CustomMinimumSize = new Vector2(48, 48), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
        _title = Kit.Label("Pick an item", Kit.Parchment, 20, "Heading");
        _facts = Kit.Label("", null, 13, "Dim");
        var head = new VBoxContainer();
        head.AddChild(_title);
        head.AddChild(_facts);
        _back = Kit.Btn("Back", Back);
        rbox.AddChild(Kit.Row(_icon, head, Kit.Spacer(), _back));

        _amount = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, Prefix = "get", CustomMinimumSize = new Vector2(140, 0) });
        _plan = Kit.Btn("Plan it in Course", Plan, action: true);
        _wiki = Kit.Btn("Open in the wiki", () => { if (_item != null) OS.ShellOpen($"{WikiSite}#/r/Item:{_item}"); });
        rbox.AddChild(Kit.Row(_amount, _plan, Kit.Spacer(), _wiki));

        rbox.AddChild(Kit.Label("How to get it", Kit.Parchment, 15, "Heading"));
        _sources = Table(new[] { "From", "How", "Amount", "Chance" }, new[] { 300, 220, 110, 90 }, 200);
        rbox.AddChild(_sources);
        rbox.AddChild(Kit.Label("Used for", Kit.Parchment, 15, "Heading"));
        _uses = Table(new[] { "Makes or buys", "How", "Needs" }, new[] { 300, 220, 110 }, 170);
        rbox.AddChild(_uses);
        rbox.AddChild(Kit.Label("Needed to unlock or do", Kit.Parchment, 15, "Heading"));
        _unlocks = Table(new[] { "What", "Where" }, new[] { 360, 260 }, 90);
        rbox.AddChild(_unlocks);
        var hint = Kit.Label("Double-click a line to look that item up, or to open it in the wiki when it is not an item.", null, 12, "Dim");
        rbox.AddChild(hint);
        cols.AddChild(right);

        foreach (Tree t in new[] { _sources, _uses, _unlocks }) t.ItemActivated += () => Follow(t);
        _win.Opened += () => { Fill(); Show(_item); _search.GrabFocus(); };

        _win.SetTour(
            new TourStep("Find an item", "Type part of a name. Enter picks the first match.", () => _search),
            new TourStep("Results", "Click an item to look it up.", () => _list),
            new TourStep("How to get it", "Every way to get the item: which action makes it, which monster or chest drops it, and the chance.", () => _sources),
            new TourStep("Used for", "What the item makes or buys, and what it is needed to unlock. Double-click a line to jump to that item; [b]Back[/b] returns.", () => _uses),
            new TourStep("Plan it", "Sends the item to Course's planner, which works out every step to get this many.", () => _plan),
            new TourStep("The wiki", "Opens the item's page on the Astrolabe wiki.", () => _wiki));
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";

    static Tree Table(string[] titles, int[] widths, int height)
    {
        var t = new Tree { Columns = titles.Length, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, CustomMinimumSize = new Vector2(0, height) };
        for (int c = 0; c < titles.Length; c++)
        {
            t.SetColumnTitle(c, titles[c]);
            t.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            t.SetColumnCustomMinimumWidth(c, widths[c]);
            t.SetColumnExpand(c, c == 0);
        }
        return t;
    }

    Lookup? Data() => Lookup.Get(_folder, _log);

    static Item? GameItem(Game? game, string local)
    {
        if (game == null) return null;
        try
        {
            var reg = game.Data.GetDataRegistryOutput<Item>();
            if (reg.TryGetObjectById("MelvorBase:" + local, out Item? i)) return i;
            return reg.GetAllObjects().FirstOrDefault(x => x.Id.EndsWith(":" + local));
        }
        catch { return null; }
    }

    static long Bank(Game? game, Item? item)
    {
        if (game == null || item == null) return 0;
        ActionComponent? any = game.ActionManager.ActiveActions.FirstOrDefault()
            ?? game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<ActionComponent>().FirstOrDefault();
        return any?.Inventory.GetQuantity(item) ?? 0;
    }

    public void Tick()
    {
        // While the data downloads, look again every second so the list fills in by itself.
        if (!_win.Visible || _list.ItemCount > 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _retry) return;
        _retry = now + 1;
        Fill();
    }

    void Fill()
    {
        _list.Clear();
        Lookup? d = Data();
        if (d == null) { _status.Text = $"Item data: {Lookup.State}."; return; }
        Game? game = GameAccess.CurrentGame;
        string q = _search.Text.Trim();
        var hits = d.Items.Where(kv => q.Length == 0 || kv.Value.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => q.Length > 0 && !kv.Value.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(kv => kv.Value.Name).Take(300).ToList();
        foreach (var (id, e) in hits)
        {
            Item? it = GameItem(game, id);
            int i = it?.Icon != null ? _list.AddItem(e.Name, Kit.Icon(it.Icon, 20)) : _list.AddItem(e.Name);
            _list.SetItemMetadata(i, id);
        }
        _status.Text = $"{d.Items.Count:N0} items, game {d.GameVersion}. Source: {(Lookup.State.Contains("offline") ? "offline copy" : "Astrolabe")}.";
    }

    void Pick(int index)
    {
        if (index < 0 || index >= _list.ItemCount) return;
        Go(_list.GetItemMetadata(index).AsString());
    }

    void Go(string id)
    {
        if (_item != null && _item != id) _history.Add(_item);
        Show(id);
    }

    void Back()
    {
        if (_history.Count == 0) return;
        string id = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Show(id);
    }

    static string Amount(double? min, double? max) =>
        min == null ? "" : max == null || max == min ? $"{min:N0}" : $"{min:N0}-{max:N0}";

    static string Chance(double? c)
    {
        if (c == null) return "";
        if (c >= 1) return "always";
        double pct = c.Value * 100;
        return pct >= 1 ? $"{pct:0.#}%" : pct >= 0.01 ? $"{pct:0.##}%" : $"1 in {1 / c.Value:N0}";
    }

    void Show(string? id)
    {
        _item = id;
        foreach (Tree t in new[] { _sources, _uses, _unlocks }) t.Clear();
        _back.Disabled = _history.Count == 0;
        Lookup? d = Data();
        bool on = id != null && d != null && d.Items.ContainsKey(id);
        _plan.Disabled = !on || !Messages.HasListener("course.plan");
        _plan.TooltipText = Messages.HasListener("course.plan") ? "Open Course's planner with this item" : "Install Astrolabe Course to plan items";
        _wiki.Disabled = !on;
        if (!on) { _title.Text = "Pick an item"; _facts.Text = ""; _icon.Texture = null; return; }

        Entry e = d!.Items[id!];
        Game? game = GameAccess.CurrentGame;
        Item? it = GameItem(game, id!);
        _icon.Texture = it?.Icon;
        _title.Text = e.Name;
        long have = Bank(game, it);
        string price = it?.SellsFor != null && it.SellsFor.Quantity > 0 ? $"sells for {it.SellsFor.Quantity:N0} GP" : "cannot be sold";
        _facts.Text = $"In the bank: {have:N0}, {price}.";

        Fill(_sources, e.Sources, r => new[] { r.Name, Lookup.Kind(r), Amount(r.Min, r.Max), Chance(r.Chance) });
        Fill(_uses, e.Uses, r => new[] { r.Name, Lookup.Kind(r), r.Min == null ? "" : $"{r.Min:N0}" });
        Fill(_unlocks, e.Unlocks, r => new[] { r.Name, Lookup.Kind(r) });
        if (e.Sources.Count == 0) Note(_sources, "Nothing in the game gives this item.");
        if (e.Uses.Count == 0) Note(_uses, "Nothing uses this item.");
        if (e.Unlocks.Count == 0) Note(_unlocks, "Not needed to unlock anything.");
    }

    void Fill(Tree t, List<Ref> rows, Func<Ref, string[]> cells)
    {
        TreeItem root = t.CreateItem();
        Game? game = GameAccess.CurrentGame;
        Lookup? d = Data();
        foreach (Ref r in rows)
        {
            TreeItem row = t.CreateItem(root);
            row.SetMetadata(0, $"{r.Type}:{r.Id}");
            string[] c = cells(r);
            for (int i = 0; i < c.Length; i++) row.SetText(i, c[i]);
            if (GameItem(game, r.Id)?.Icon is Texture2D icon) row.SetIcon(0, Kit.Icon(icon, 18));
            if (d != null && d.Items.ContainsKey(r.Id)) row.SetCustomColor(0, Kit.Link);
            for (int i = 1; i < c.Length; i++) row.SetCustomColor(i, Kit.Dim);
        }
    }

    static void Note(Tree t, string text)
    {
        TreeItem row = t.CreateItem(t.GetRoot() ?? t.CreateItem());
        row.SetText(0, text);
        row.SetCustomColor(0, Kit.Dim);
    }

    /// <summary>Double-click: an item (or a recipe named after its product) opens here; anything else opens in the wiki.</summary>
    void Follow(Tree t)
    {
        string key = t.GetSelected()?.GetMetadata(0).AsString() ?? "";
        int colon = key.IndexOf(':');
        if (colon < 0) return;
        string id = key[(colon + 1)..];
        Lookup? d = Data();
        if (d != null && d.Items.ContainsKey(id)) { Go(id); return; }
        OS.ShellOpen($"{WikiSite}#/r/{key}");
    }

    void Plan()
    {
        if (_item == null) return;
        Messages.Publish("course.plan", (_item, (int)_amount.Value));
    }

    /// <summary>Shows an item (sent by another mod, or by Spyglass itself).</summary>
    public void LookUp(string id)
    {
        _win.Open();
        string local = id.Contains(':') ? id[(id.LastIndexOf(':') + 1)..] : id;
        Go(local);
    }
}
