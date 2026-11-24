using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Currencies;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// The Slayer tab: every master's task and progress, extend and reroll by hand, and the rules a "Slayer tasks" step
/// (or, if asked, Course all the time) follows: extend a master's tasks, reroll monsters you skip, keep a coin reserve.
/// </summary>
sealed class SlayerPanel
{
    readonly Queue _q;
    readonly Label _coins, _note, _sel;
    readonly SpinBox _reserve;
    readonly Tree _table;
    readonly Button _extend, _reroll, _fight;
    readonly CheckBox _autoExtend, _gameAuto, _rerollLocked, _always;
    readonly SearchPicker _skipPick;
    readonly HFlowContainer _skipList;
    string? _master;
    List<SlayerTask> _tasks = new();
    bool _filling;

    public SlayerPanel(Queue q, VBoxContainer box)
    {
        _q = q;
        _coins = Kit.Label("", null, 14);
        _reserve = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 1e12, Prefix = "keep at least", Suffix = "coins", CustomMinimumSize = new Vector2(260, 0) });
        _reserve.ValueChanged += v => { if (!_filling) _q.SetOption(s => s.SlayerReserve = (long)v); };
        _reserve.TooltipText = "Rerolls and extensions by your rules never take slayer coins below this. Buttons below ignore it.";
        box.AddChild(Kit.Row(_coins, Kit.Spacer(), Kit.Label("Rules spend coins but", null, 13, "Dim"), _reserve));

        _table = new Tree
        {
            Columns = 6, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row,
            CustomMinimumSize = new Vector2(0, 220), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        string[] cols = { "Master", "Task", "Kills", "Extend", "Reroll", "Rules" };
        int[] widths = { 140, 280, 110, 150, 150, 220 };
        for (int c = 0; c < cols.Length; c++)
        {
            _table.SetColumnTitle(c, cols[c]);
            _table.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _table.SetColumnCustomMinimumWidth(c, widths[c]);
            _table.SetColumnExpand(c, c is 1 or 5);
        }
        _table.ItemSelected += () => { _master = _table.GetSelected()?.GetMetadata(0).AsString(); Select(); };
        box.AddChild(_table);

        _sel = Kit.Label("Pick a master.", null, 13, "Dim");
        _sel.CustomMinimumSize = new Vector2(200, 0);
        _extend = Kit.Btn("Extend", () => Do(t => Slayer.Extend(GameAccess.CurrentGame!, t), "extended"));
        _reroll = Kit.Btn("Reroll", () => Do(t => Slayer.Reroll(GameAccess.CurrentGame!, t), "rerolled"));
        _fight = Kit.Btn("Fight it now", Fight);
        _fight.TooltipText = "Start fighting this task's monster (outside the queue).";
        _autoExtend = new CheckBox { Text = "Extend its tasks by rule" };
        _autoExtend.TooltipText = "Extend each new task from this master as soon as it is given (keeping the coin reserve).";
        _autoExtend.Toggled += on => { if (!_filling && _master != null) _q.SetOption(s => { if (on) s.SlayerExtend.Add(_master); else s.SlayerExtend.Remove(_master); }); Fill(); };
        _gameAuto = new CheckBox { Text = "Game's Auto Slayer" };
        _gameAuto.TooltipText = "The game's own setting: when you finish this master's task while fighting it, the game moves on to the next one in the same place.";
        _gameAuto.Toggled += on => { if (!_filling && Current() is SlayerTask t) { t.Master.AutoSlayerEnabled = on; Fill(); } };
        box.AddChild(Kit.Row(_sel, _extend, _reroll, _fight, Kit.Spacer(), _autoExtend, _gameAuto));

        // Monsters to reroll whenever they come up.
        _skipPick = new SearchPicker(280) { Placeholder = "Choose a monster", AutoSelect = false };
        var add = Kit.Btn("Always reroll it", AddSkip);
        add.TooltipText = "Whenever a master gives this monster as a task, reroll it (keeping the coin reserve). Slayer tasks steps never fight it.";
        box.AddChild(Kit.Row(Kit.Label("Reroll these monsters:", null, 13), _skipPick.Button, add));
        _skipList = new HFlowContainer();
        _skipList.AddThemeConstantOverride("h_separation", 6);
        box.AddChild(_skipList);

        _rerollLocked = new CheckBox { Text = "Reroll tasks in areas I can't enter yet" };
        _rerollLocked.Toggled += on => { if (!_filling) _q.SetOption(s => s.SlayerRerollLocked = on); };
        _always = new CheckBox { Text = "Use these rules all the time, not only during a Slayer tasks step" };
        _always.Toggled += on => { if (!_filling) _q.SetOption(s => s.SlayerAlways = on); };
        box.AddChild(Kit.Row(_rerollLocked, _always));

        _note = Kit.Label("A [i]Slayer tasks[/i] step (Add a step, then Combat, then Slayer tasks) fights the highest master's task you can fight, moves to the next task when one is done, and follows these rules. It does its own switching, so the game's Auto Slayer can stay on or off.", null, 12, "Dim");
        _note.Text = _note.Text.Replace("[i]", "").Replace("[/i]", "");
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.CustomMinimumSize = new Vector2(860, 0);
        box.AddChild(_note);

        // Kills tick up while you fight: keep the table current while the tab is open.
        var timer = new Godot.Timer { WaitTime = 2, Autostart = true };
        timer.Timeout += () => { if (box.IsVisibleInTree()) Fill(); };
        box.AddChild(timer);
    }

    SlayerTask? Current() => _tasks.FirstOrDefault(t => t.MasterId == _master);

    public void Refresh()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) return;
        var monsters = Catalog.Skills(game).Where(s => Gear.IsCombat(game, s.Id)).SelectMany(s => s.Actions)
            .Where(a => a.Ref.Area != a.Ref.Target)
            .GroupBy(a => a.Ref.Target).Select(g => g.First())
            .Select(a => new PickItem(a.Ref.Target, a.Name[..(a.Name.LastIndexOf(" (") is int i && i > 0 ? i : a.Name.Length)], a.Icon))
            .OrderBy(p => p.Text).ToList();
        _skipPick.SetItems(monsters);
        Fill();
    }

    void Fill()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) return;
        _filling = true;
        try
        {
            var st = _q.State;
            _tasks = Slayer.Tasks(game);
            Currency? coin = _tasks.FirstOrDefault()?.Reroll.Currency;
            _coins.Text = coin == null ? "No slayer masters unlocked yet." : $"{Slayer.Have(game, coin):N0} {coin.DisplayName}";
            if (!_reserve.GetLineEdit().HasFocus()) _reserve.SetValueNoSignal(st.SlayerReserve);
            _rerollLocked.SetPressedNoSignal(st.SlayerRerollLocked);
            _always.SetPressedNoSignal(st.SlayerAlways);

            _table.Clear();
            TreeItem root = _table.CreateItem();
            foreach (SlayerTask t in _tasks)
            {
                TreeItem r = _table.CreateItem(root);
                r.SetMetadata(0, t.MasterId);
                r.SetText(0, t.MasterName);
                r.SetIcon(0, Kit.Icon(t.Master.Data.Icon, 18));
                r.SetText(1, t.Enemy == null ? "no task" : $"{t.EnemyName} ({t.AreaName}){(t.Locked ? ", area locked" : "")}");
                if (t.Locked || (t.Enemy != null && st.SlayerSkip.Contains(t.Enemy.Id))) r.SetCustomColor(1, Kit.Gold);
                r.SetText(2, t.Enemy == null ? "" : $"{t.Done:N0} / {t.Length:N0}");
                r.SetText(3, t.Extended ? "extended" : Slayer.CostText(t.Extend));
                r.SetCustomColor(3, t.Extended ? Kit.Dim : Kit.Text);
                r.SetText(4, Slayer.CostText(t.Reroll));
                var rules = new List<string>();
                if (st.SlayerExtend.Contains(t.MasterId)) rules.Add("extend by rule");
                if (t.GameAuto) rules.Add("game's Auto Slayer on");
                r.SetText(5, string.Join(", ", rules));
                r.SetCustomColor(5, Kit.Link);
                if (t.MasterId == _master) r.Select(0);
            }
            if (_tasks.Count == 0)
            {
                TreeItem r = _table.CreateItem(root);
                r.SetText(0, "No slayer masters unlocked yet.");
                r.SetCustomColor(0, Kit.Dim);
            }

            foreach (Node n in _skipList.GetChildren()) { _skipList.RemoveChild(n); n.QueueFree(); }
            var names = Catalog.Skills(game).Where(s => Gear.IsCombat(game, s.Id)).SelectMany(s => s.Actions)
                .GroupBy(a => a.Ref.Target).ToDictionary(g => g.Key, g => g.First());
            foreach (string id in st.SlayerSkip.OrderBy(x => x))
            {
                string name = names.TryGetValue(id, out var a) ? a.Name[..(a.Name.LastIndexOf(" (") is int i && i > 0 ? i : a.Name.Length)] : Catalog.Local(id);
                string skip = id;
                var b = Kit.Btn(name + "  ×", () => _q.SetOption(s => s.SlayerSkip.Remove(skip)));
                b.TooltipText = "Stop rerolling this monster";
                if (names.TryGetValue(id, out var a2)) b.Icon = Kit.Icon(a2.Icon, 18);
                _skipList.AddChild(b);
            }
            if (st.SlayerSkip.Count == 0) _skipList.AddChild(Kit.Label("none", null, 12, "Dim"));
            Select();
        }
        finally { _filling = false; }
    }

    void Select()
    {
        bool was = _filling;
        _filling = true;
        SlayerTask? t = Current();
        bool on = t != null;
        _extend.Disabled = !on || t!.Enemy == null || t.Extended;
        _reroll.Disabled = !on || t!.Enemy == null;
        _fight.Disabled = !on || t!.Fight == null || t.Locked;
        _autoExtend.Disabled = _gameAuto.Disabled = !on;
        _extend.Text = on && !t!.Extended ? $"Extend ({Slayer.CostText(t.Extend)})" : "Extend";
        _reroll.Text = on ? $"Reroll ({Slayer.CostText(t!.Reroll)})" : "Reroll";
        _autoExtend.SetPressedNoSignal(on && _q.State.SlayerExtend.Contains(t!.MasterId));
        _gameAuto.SetPressedNoSignal(on && t!.GameAuto);
        _sel.Text = on ? t!.MasterName : "Pick a master.";
        _filling = was;
    }

    void Do(Func<SlayerTask, string> act, string done)
    {
        if (Current() is not SlayerTask t) return;
        string why = act(t);
        _q.Note(why.Length == 0 ? $"Slayer: {done} {t.EnemyName} from {t.MasterName}" : $"Slayer: {t.MasterName}: {why}");
        Fill();
    }

    void Fight()
    {
        if (Current() is not SlayerTask t || t.Fight == null || GameAccess.CurrentGame is not Game game) return;
        string why = Starter.Start(game, t.Fight);
        if (why.Length > 0) _q.Note($"Slayer: can't fight {t.EnemyName}: {why}");
    }

    void AddSkip()
    {
        string? id = _skipPick.SelectedId;
        if (id == null) return;
        _q.SetOption(s => s.SlayerSkip.Add(id));
        Fill();
    }
}
