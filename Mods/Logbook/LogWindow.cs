using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Logbook;

/// <summary>F7: this session's XP, items and currencies, notable drops, past sessions, options.</summary>
sealed class LogWindow
{
    static readonly string[] s_tabs = { "This session", "Notable drops", "Past sessions", "Options" };

    readonly Tracker _t;
    readonly KitWindow _win;
    readonly Button[] _tabButtons = new Button[4];
    readonly Control[] _panels = new Control[4];
    int _tab;

    readonly Label _head, _money, _away;
    readonly Tree _skills, _items, _drops;
    readonly CheckBox _rareOnly, _panelOn;
    readonly ItemList _pastList;
    readonly Label _pastText;
    List<Session> _past = new();
    double _next;

    public LogWindow(Tracker t)
    {
        _t = t;
        _win = new KitWindow("Logbook", "what you gained, and how fast", new Vector2(940, 0));
        _win.SetHelp(Help.Text);
        _win.HeaderExtras.AddChild(Kit.Btn("Start a new session", () => _t.Restart()));

        var tabRow = new HBoxContainer();
        for (int i = 0; i < s_tabs.Length; i++)
        {
            int at = i;
            _tabButtons[i] = Kit.Btn(s_tabs[i], () => Show(at));
            _tabButtons[i].ThemeTypeVariation = "Tab";
            _tabButtons[i].ToggleMode = true;
            tabRow.AddChild(_tabButtons[i]);
        }
        _win.Body.AddChild(tabRow);

        // ---- this session ----
        var now = Panel(0);
        _head = Kit.Label("", null, 14);
        now.AddChild(_head);
        _money = Wrapped("");
        now.AddChild(_money);
        _skills = Table(new[] { "Skill", "XP gained", "XP per hour", "Level", "Next level in", "Actions" }, new[] { 200, 110, 110, 90, 120, 100 }, 230);
        now.AddChild(_skills);
        _items = Table(new[] { "Item", "Gained", "Per hour", "Worth per hour", "Used" }, new[] { 280, 100, 100, 130, 100 }, 200);
        now.AddChild(_items);
        _away = Wrapped("");
        now.AddChild(_away);

        // ---- notable drops ----
        var drops = Panel(1);
        _rareOnly = new CheckBox { Text = "Only rare drops" };
        _rareOnly.Toggled += on => _t.SetOption(o => o.RareOnly = on);
        drops.AddChild(Kit.Row(_rareOnly, Kit.Spacer(),
            Kit.Label("Rare: came in at most once per 40 actions of what you were doing. First: not seen before this session.", null, 12, "Dim")));
        _drops = Table(new[] { "Time", "Item", "Amount", "Why it is here" }, new[] { 120, 300, 90, 330 }, 430);
        drops.AddChild(_drops);

        // ---- past sessions ----
        var past = Panel(2);
        _pastList = new ItemList { CustomMinimumSize = new Vector2(330, 430) };
        _pastList.ItemSelected += i => ShowPast((int)i);
        _pastText = Wrapped("Pick a session.");
        _pastText.CustomMinimumSize = new Vector2(520, 0);
        _pastText.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        past.AddChild(Kit.Row(_pastList, _pastText));

        // ---- options ----
        var opts = Panel(3);
        _panelOn = new CheckBox { Text = "Show a small panel with your XP and GP per hour (drag it anywhere, \"–\" folds it)" };
        _panelOn.Toggled += on => _t.SetOption(o => o.Panel = on);
        opts.AddChild(_panelOn);
        opts.AddChild(Kit.Row(Kit.Btn("Put the panel back in its first spot", () => _t.SetOption(o => { o.PanelX = o.PanelY = -1; o.PanelMin = false; }))));
        opts.AddChild(Wrapped("A session starts when you load a character and ends when you close the game or press \"Start a new session\". "
            + "The last 60 are kept under Past sessions. Time the game spends catching up on time away is not counted in the per-hour rates; "
            + "it shows on its own as \"Last time away\"."));

        _win.Opened += () => { Show(_tab); Refresh(); };
        _t.Changed += () => { if (_win.Visible && _tab != 2) Refresh(); };
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";

    VBoxContainer Panel(int index)
    {
        var card = new PanelContainer { ThemeTypeVariation = "Card", Visible = index == 0 };
        var box = new VBoxContainer();
        card.AddChild(box);
        _panels[index] = card;
        _win.Body.AddChild(card);
        return box;
    }

    static Label Wrapped(string text)
    {
        var l = Kit.Label(text, null, 13, "Dim");
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(880, 0);
        return l;
    }

    static Tree Table(string[] titles, int[] widths, int height)
    {
        var t = new Tree
        {
            Columns = titles.Length, ColumnTitlesVisible = true, HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row, CustomMinimumSize = new Vector2(0, height),
        };
        for (int c = 0; c < titles.Length; c++)
        {
            t.SetColumnTitle(c, titles[c]);
            t.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            t.SetColumnCustomMinimumWidth(c, widths[c]);
            t.SetColumnExpand(c, c == 0);
        }
        return t;
    }

    void Show(int which)
    {
        _tab = which;
        for (int i = 0; i < _panels.Length; i++)
        {
            _panels[i].Visible = i == which;
            _tabButtons[i].SetPressedNoSignal(i == which);
        }
        if (which == 2) RefreshPast();
        else Refresh();
    }

    public void Tick()
    {
        if (!_win.Visible) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _next) return;
        _next = now + 2;
        if (_tab != 2) Refresh();
    }

    // ---- formatting ----

    public static string Num(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e9 ? $"{v / 1e9:0.##}B" : a >= 1e6 ? $"{v / 1e6:0.##}M" : a >= 1e4 ? $"{v / 1e3:0.#}k" : $"{v:N0}";
    }

    public static string Span(double hours)
    {
        if (double.IsInfinity(hours) || double.IsNaN(hours)) return "";
        var t = TimeSpan.FromHours(hours);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : "< 1 min";
    }

    static Texture2D? ItemIcon(Game? game, string id)
    {
        if (game == null) return null;
        try { if (game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(id, out Item? i)) return Kit.Icon(i?.Icon, 18); } catch { }
        return null;
    }

    static Texture2D? SkillIcon(Game? game, string id) =>
        game != null && game.Entities.TryGetObjectById(id, out GameEntity? e) && e != null ? Kit.Icon(e.Icon, 18) : null;

    // ---- this session ----

    void Refresh()
    {
        Session s = _t.Session;
        Game? game = GameAccess.CurrentGame;
        _rareOnly.SetPressedNoSignal(_t.Options.RareOnly);
        _panelOn.SetPressedNoSignal(_t.Options.Panel);
        if (s.Character.Length == 0) { _head.Text = "Load a character to start a session."; return; }

        _head.Text = $"{s.Character}: {Span(s.Hours)} of play since {s.StartedUtc.ToLocalTime():MMM d HH:mm}";
        _money.Text = MoneyText(s, s.Hours);

        if (_tab == 0) FillSkills(s, game);
        if (_tab == 0) FillItems(s, game);
        if (_tab == 1) FillDrops(s, game);
        _away.Text = s.LastAway == null ? "" : "Last time away: " + AwayText(s, s.LastAway);
    }

    static string MoneyText(Session s, double hours)
    {
        var parts = s.CurrencyGained.Keys.Union(s.CurrencySpent.Keys).Select(id =>
        {
            long g = s.CurrencyGained.TryGetValue(id, out long a) ? a : 0, sp = s.CurrencySpent.TryGetValue(id, out long b) ? b : 0;
            return $"{s.NameOf(id)}: +{Num(g)} ({Num(g / hours)} per hour)" + (sp > 0 ? $", spent {Num(sp)}" : "");
        }).ToList();
        long worth = s.GainedValue;
        if (worth > 0) parts.Add($"Items gained are worth {Num(worth)} GP at sell price ({Num(worth / hours)} per hour)");
        return parts.Count == 0 ? "Nothing gained yet." : string.Join(".  ", parts) + ".";
    }

    void FillSkills(Session s, Game? game)
    {
        _skills.Clear();
        TreeItem root = _skills.CreateItem();
        foreach (var (id, line) in s.Skills.Where(kv => kv.Value.Xp > 0 || kv.Value.Actions > 0).OrderByDescending(kv => kv.Value.Xp))
        {
            TreeItem r = _skills.CreateItem(root);
            r.SetText(0, line.Name.Length > 0 ? line.Name : s.NameOf(id));
            r.SetIcon(0, SkillIcon(game, id));
            r.SetText(1, line.Xp > 0 ? Num(line.Xp) : "");
            double rate = _t.RecentXpPerHour(id);
            r.SetText(2, line.Xp > 0 ? Num(rate) : "");
            r.SetTooltipText(2, line.Xp > 0 ? $"Last 10 minutes. Whole session: {Num(line.Xp / s.Hours)} per hour." : "");
            r.SetText(3, line.Level == 0 ? "" : line.Level > line.StartLevel ? $"{line.StartLevel} → {line.Level}" : line.Level.ToString());
            if (line.Level > line.StartLevel) r.SetCustomColor(3, Kit.Good);
            double? h = _t.HoursToLevel(game, id);
            r.SetText(4, h == null ? "" : Span(h.Value));
            r.SetText(5, line.Actions > 0 ? Num(line.Actions) : "");
        }
        if (root.GetChildCount() == 0)
        {
            TreeItem r = _skills.CreateItem(root);
            r.SetText(0, "Nothing yet. Rates fill in as you train.");
            r.SetCustomColor(0, Kit.Dim);
        }
    }

    void FillItems(Session s, Game? game)
    {
        _items.Clear();
        TreeItem root = _items.CreateItem();
        foreach (string id in s.Gained.Keys.Union(s.Used.Keys).OrderByDescending(id => s.Gained.TryGetValue(id, out long g) ? g : 0).Take(200))
        {
            long g = s.Gained.TryGetValue(id, out long a) ? a : 0, u = s.Used.TryGetValue(id, out long b) ? b : 0;
            TreeItem r = _items.CreateItem(root);
            r.SetText(0, s.NameOf(id));
            r.SetIcon(0, ItemIcon(game, id));
            r.SetText(1, g > 0 ? Num(g) : "");
            r.SetText(2, g > 0 ? Num(g / s.Hours) : "");
            long worth = s.ValueOf(id, g);
            r.SetText(3, worth > 0 ? Num(worth / s.Hours) + " GP" : "");
            r.SetText(4, u > 0 ? Num(u) : "");
            if (u > 0) r.SetCustomColor(4, Kit.Dim);
        }
    }

    void FillDrops(Session s, Game? game)
    {
        _drops.Clear();
        TreeItem root = _drops.CreateItem();
        foreach (DropEvent d in Enumerable.Reverse(s.Drops).Where(d => d.Rare || !_t.Options.RareOnly))
        {
            TreeItem r = _drops.CreateItem(root);
            r.SetText(0, d.Time.ToString("MMM d HH:mm"));
            r.SetText(1, s.NameOf(d.Item));
            r.SetIcon(1, ItemIcon(game, d.Item));
            r.SetText(2, Num(d.Qty));
            r.SetText(3, d.Why);
            r.SetCustomColor(3, d.Rare ? Kit.Gold : Kit.Dim);
            if (d.Rare) r.SetCustomColor(1, Kit.Gold);
        }
        if (root.GetChildCount() == 0)
        {
            TreeItem r = _drops.CreateItem(root);
            r.SetText(1, "Nothing yet.");
            r.SetCustomColor(1, Kit.Dim);
        }
    }

    static string AwayText(Session s, Away a)
    {
        var parts = new List<string>();
        parts.AddRange(a.Xp.OrderByDescending(kv => kv.Value).Select(kv => $"+{Num(kv.Value)} {s.NameOf(kv.Key)} XP"));
        parts.AddRange(a.Currency.Where(kv => kv.Value > 0).Select(kv => $"+{Num(kv.Value)} {s.NameOf(kv.Key)}"));
        parts.AddRange(a.Items.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"+{Num(kv.Value)} {s.NameOf(kv.Key)}"));
        int more = a.Items.Count(kv => kv.Value > 0) - 12;
        return $"(caught up {a.EndedUtc.ToLocalTime():MMM d HH:mm}) " + string.Join(", ", parts) + (more > 0 ? $", and {more} more items" : "") + ".";
    }

    // ---- past sessions ----

    void RefreshPast()
    {
        _past = _t.Past();
        _pastList.Clear();
        foreach (Session p in _past)
        {
            double xp = p.Skills.Values.Sum(x => x.Xp);
            _pastList.AddItem($"{p.StartedUtc.ToLocalTime():MMM d HH:mm}, {Span(p.Hours)}, +{Num(xp)} XP");
        }
        _pastText.Text = _past.Count == 0 ? "No past sessions yet. One is kept each time you close the game or start a new session." : "Pick a session.";
    }

    void ShowPast(int i)
    {
        if (i < 0 || i >= _past.Count) return;
        Session p = _past[i];
        var lines = new List<string>
        {
            $"{p.StartedUtc.ToLocalTime():dddd MMM d, HH:mm} to {p.LastUtc.ToLocalTime():HH:mm}: {Span(p.Hours)} of play.",
            "",
        };
        foreach (var (id, l) in p.Skills.Where(kv => kv.Value.Xp > 0).OrderByDescending(kv => kv.Value.Xp))
            lines.Add($"{l.Name}: +{Num(l.Xp)} XP ({Num(l.Xp / p.Hours)} per hour)" + (l.Level > l.StartLevel ? $", level {l.StartLevel} → {l.Level}" : ""));
        lines.Add("");
        lines.Add(MoneyText(p, p.Hours));
        var top = p.Gained.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{p.NameOf(kv.Key)} +{Num(kv.Value)}").ToList();
        if (top.Count > 0) { lines.Add(""); lines.Add("Most gained: " + string.Join(", ", top) + "."); }
        var rare = p.Drops.Where(d => d.Rare).Select(d => p.NameOf(d.Item)).Distinct().ToList();
        if (rare.Count > 0) lines.Add("Rare drops: " + string.Join(", ", rare) + ".");
        if (p.LastAway != null) { lines.Add(""); lines.Add("Time away caught up during this session: " + AwayText(p, p.LastAway)); }
        _pastText.Text = string.Join("\n", lines);
        _pastText.AddThemeColorOverride("font_color", Kit.Text);
    }
}
