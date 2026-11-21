using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.ActionQueue;

/// <summary>
/// The queue window. Tabs on top (add a step, plan an item, saved and shared queues, history, options), the steps
/// table in the middle (drag to reorder, double-click to edit), then the selected-step buttons, the idle action
/// and the status line.
/// </summary>
sealed class QueueWindow
{
    static readonly string[] s_goalNames =
    {
        "it stops by itself", "I own this many of an item", "I get this many more of an item",
        "a skill reaches level", "minutes pass", "it has been done this many times",
    };
    static readonly string[] s_tabNames = { "Add a step", "Plan an item", "Saved and shared", "History", "Options" };
    static readonly Color s_currentRow = new(0.18f, 0.43f, 0.18f, 0.35f);

    readonly Queue _queue;
    readonly KitWindow _win;
    readonly OptionButton _queuePick;
    readonly Button _run, _lanes;
    readonly Button[] _tabs = new Button[5];
    readonly Control[] _panels = new Control[5];
    int _tab;
    bool _tabPinned;   // the player opened a tab while the queue runs (otherwise tabs fold away to give the table room)

    // add a step
    readonly ActionPicker _do;
    readonly OptionButton _goal;
    readonly SpinBox _amount, _addLane;
    readonly SearchPicker _target;
    readonly Button _add, _cancelEdit;
    readonly Label _now, _keep, _untilLabel;
    int _editing = -1;

    // plan an item
    readonly OptionButton _planMode;
    readonly SpinBox _planAmount;
    readonly SearchPicker _planItem;
    readonly Tree _planTable;
    readonly Label _planNote;
    readonly Button _planAdd;
    Planner? _planner;
    List<PlanLine> _plan = new();

    // saved and shared
    readonly LineEdit _saveName;
    readonly ItemList _savedList;
    readonly Label _shareNote;

    // history and options
    readonly ItemList _history;
    readonly CheckBox _optAlerts, _optFood, _optBank, _optHud, _optLoop;

    // table and below
    readonly Tree _table;
    readonly Button _laneUp, _laneDown;
    readonly ActionPicker _idle;
    readonly Label _idleNote, _status;
    int _selected = -1;

    public QueueWindow(Queue queue)
    {
        _queue = queue;
        _win = new KitWindow("Action Queue", "do this, then that, then that", new Vector2(940, 0));

        // ---- header ----
        _lanes = Kit.Btn("Side by side: off", () => _queue.SetLanes(_queue.State.Lanes % 4 + 1));
        _lanes.TooltipText = "Run up to 4 lanes at once. Each lane runs its own steps in order.\nTwo steps of the same skill cannot run at the same moment.";
        _queuePick = new OptionButton { CustomMinimumSize = new Vector2(190, 0), TooltipText = "Load a saved queue" };
        _queuePick.ItemSelected += i => { if (i > 0) _queue.LoadSaved(_queuePick.GetItemText((int)i)); };
        _run = Kit.Btn("Start", () =>
        {
            if (_queue.State.Running) _queue.Pause();
            else { _tabPinned = false; _queue.Start(); }
        }, action: true);
        _run.CustomMinimumSize = new Vector2(100, 0);
        _win.HeaderExtras.AddChild(_lanes);
        _win.HeaderExtras.AddChild(_queuePick);
        _win.HeaderExtras.AddChild(_run);

        // ---- tabs ----
        var tabRow = new HBoxContainer();
        for (int i = 0; i < _tabs.Length; i++)
        {
            int at = i;
            _tabs[i] = Kit.Btn(s_tabNames[i], () => { _tabPinned = true; ShowTab(at == _tab && _panels[at].Visible && _queue.State.Running ? -1 : at); });
            _tabs[i].ThemeTypeVariation = "Tab";
            _tabs[i].ToggleMode = true;
            tabRow.AddChild(_tabs[i]);
        }
        _win.Body.AddChild(tabRow);

        // ---- add a step ----
        var addBox = Panel(0);
        _do = new ActionPicker(allowSell: true, allowBest: true);
        _do.Changed += OnActionPicked;
        addBox.AddChild(Kit.Row(Field("Do"), _do.SkillBox, _do.ActionPick.Button, Kit.Btn("Use what I am doing now", () => _do.UseCurrent())));

        _untilLabel = Field("until");
        _goal = new OptionButton { CustomMinimumSize = new Vector2(250, 0) };
        foreach (string g in s_goalNames) _goal.AddItem(g);
        _goal.Select(1);
        _goal.ItemSelected += _ => RefreshTargets();
        _keep = Kit.Label("keep", null, 13, "Dim");
        _amount = new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, CustomMinimumSize = new Vector2(110, 0) };
        _target = new SearchPicker(200) { Placeholder = "Choose an item" };
        _addLane = new SpinBox { MinValue = 1, MaxValue = 4, Value = 1, Prefix = "lane", CustomMinimumSize = new Vector2(96, 0) };
        _add = Kit.Btn("Add step", AddStep, action: true);
        _cancelEdit = Kit.Btn("Cancel", StopEditing);
        _cancelEdit.Visible = false;
        addBox.AddChild(Kit.Row(_untilLabel, _goal, _keep, _amount, _target.Button, _addLane, _add, _cancelEdit));
        _now = Kit.Label("", null, 12, "Dim");
        addBox.AddChild(_now);

        // ---- plan an item ----
        var planBox = Panel(1);
        _planMode = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        _planMode.AddItem("Make");
        _planMode.AddItem("Own in total");
        _planMode.ItemSelected += _ => MakePlan();
        _planAmount = new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, CustomMinimumSize = new Vector2(110, 0) };
        _planAmount.ValueChanged += _ => MakePlan();
        _planItem = new SearchPicker(260) { Placeholder = "Choose an item to plan", AutoSelect = false };
        _planItem.Picked += _ => MakePlan();
        _planAdd = Kit.Btn("Add these steps to the queue", AddPlan, action: true);
        planBox.AddChild(Kit.Row(_planMode, _planAmount, Kit.Label("of", null, 13, "Dim"), _planItem.Button, _planAdd));
        _planTable = MakeTable(new[] { "Step", "Until the bank holds", "In the bank", "Takes about" }, new[] { 340, 230, 100, 100 }, 2, 150);
        planBox.AddChild(_planTable);
        _planNote = Wrapped("");
        planBox.AddChild(_planNote);

        // ---- saved and shared ----
        var savedBox = Panel(2);
        _saveName = new LineEdit { PlaceholderText = "Name for this queue", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        savedBox.AddChild(Kit.Row(Field("Save as"), _saveName, Kit.Btn("Save the current queue", () => { _queue.SaveAs(_saveName.Text); RefreshSaved(); }, action: true)));
        _savedList = new ItemList { CustomMinimumSize = new Vector2(0, 80), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _savedList.ItemActivated += i => _queue.LoadSaved(_savedList.GetItemText((int)i));
        savedBox.AddChild(Kit.Row(_savedList, Column(
            Kit.Btn("Load", () => WithSaved(_queue.LoadSaved)),
            Kit.Btn("Delete", () => WithSaved(n => { _queue.DeleteSaved(n); RefreshSaved(); })))));
        savedBox.AddChild(Kit.Row(Field("Share"), Kit.Btn("Copy share code", CopyCode), Kit.Btn("Add steps from a pasted code", PasteCode),
            Kit.Label("Codes also come from the Astrolabe wiki planner.", null, 12, "Dim")));
        _shareNote = Wrapped("");
        savedBox.AddChild(_shareNote);

        // ---- history ----
        var historyBox = Panel(3);
        _history = new ItemList { CustomMinimumSize = new Vector2(0, 150), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        historyBox.AddChild(_history);
        historyBox.AddChild(Kit.Row(Kit.Label("What the queue did, newest first. Kept between game sessions.", null, 12, "Dim"), Kit.Spacer(),
            Kit.Btn("Clear history", _queue.ClearHistory)));

        // ---- options ----
        var optBox = Panel(4);
        _optLoop = Option("Start over when the queue ends", s => s.Loop, (s, v) => s.Loop = v);
        _optAlerts = Option("Play a sound and flash the taskbar when the queue finishes, skips a step or pauses", s => s.Alerts, (s, v) => s.Alerts = v);
        _optFood = Option("Stop a combat step when the selected food runs out", s => s.FoodGuard, (s, v) => s.FoodGuard = v);
        _optBank = Option("Pause the queue when the bank is full", s => s.BankGuard, (s, v) => s.BankGuard = v);
        _optHud = Option("Show the current step in the top-right corner while the queue runs", s => s.Hud, (s, v) => s.Hud = v);
        foreach (CheckBox c in new[] { _optLoop, _optAlerts, _optFood, _optBank, _optHud }) optBox.AddChild(c);

        // ---- the steps ----
        _table = MakeTable(new[] { "#", "Lane", "Do", "Until", "State" }, new[] { 34, 50, 290, 290, 230 }, 2, 230, expandCols: new[] { 2, 3 });
        _table.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _table.ItemSelected += () => _selected = _table.GetSelected()?.GetMetadata(0).AsInt32() ?? -1;
        _table.ItemActivated += () => { int i = _table.GetSelected()?.GetMetadata(0).AsInt32() ?? -1; if (i >= 0) Edit(i); };
        SetUpDragging();
        _win.Body.AddChild(_table);

        _laneDown = Kit.Btn("Lane -", () => WithStep(i => _queue.SetLane(i, _queue.State.Steps[i].Lane - 1)));
        _laneUp = Kit.Btn("Lane +", () => WithStep(i => _queue.SetLane(i, _queue.State.Steps[i].Lane + 1)));
        _win.Body.AddChild(Kit.Row(
            Kit.Label("Selected step:", null, 13, "Dim"),
            Kit.Btn("Edit", () => WithStep(Edit)),
            Kit.Btn("Up", () => WithStep(i => { _selected = Math.Max(0, i - 1); _queue.Move(i, -1); })),
            Kit.Btn("Down", () => WithStep(i => { _selected = Math.Min(_queue.State.Steps.Count - 1, i + 1); _queue.Move(i, 1); })),
            Kit.Btn("Remove", () => WithStep(i => { _selected = -1; _queue.Remove(i); })),
            Kit.Btn("Run from here", () => WithStep(i => { _tabPinned = false; _queue.Start(i); })),
            _laneDown, _laneUp, Kit.Spacer(),
            Kit.Label("drag rows to reorder, double-click to edit", null, 12, "Dim"),
            Kit.Btn("Remove all", _queue.Clear)));

        _idle = new ActionPicker(allowNone: true);
        _idle.Changed += () => _queue.SetIdle(_idle.Selected);
        _win.Body.AddChild(Kit.Row(Kit.Label("When the queue waits or ends, do", null, 13, "Dim"), _idle.SkillBox, _idle.ActionPick.Button,
            Kit.Btn("Use what I am doing now", () => _idle.UseCurrent())));
        _idleNote = Kit.Label("", null, 12, "Dim");
        _win.Body.AddChild(_idleNote);
        _status = Kit.Label("", Kit.Good, 14);
        _status.ClipText = true;
        _win.Body.AddChild(_status);

        _win.Opened += OnOpened;
        _queue.Changed += () => { if (_win.Visible) Rebuild(); };
    }

    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";
    public void Toggle() => _win.Toggle();
    public void Open() => _win.Open();

    // ---- building blocks ----

    VBoxContainer Panel(int index)
    {
        var card = new PanelContainer { ThemeTypeVariation = "Card", Visible = index == 0 };
        var box = new VBoxContainer();
        card.AddChild(box);
        _panels[index] = card;
        _win.Body.AddChild(card);
        return box;
    }

    static Label Field(string text)
    {
        var l = Kit.Label(text, null, 13, "Dim");
        l.CustomMinimumSize = new Vector2(46, 0);
        return l;
    }

    static Label Wrapped(string text)
    {
        var l = Kit.Label(text, null, 12, "Dim");
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(860, 0);
        return l;
    }

    static VBoxContainer Column(params Control[] items)
    {
        var v = new VBoxContainer();
        foreach (Control c in items) v.AddChild(c);
        return v;
    }

    CheckBox Option(string text, Func<QueueState, bool> get, Action<QueueState, bool> set)
    {
        var c = new CheckBox { Text = text };
        c.Toggled += on => _queue.SetOption(s => set(s, on));
        c.SetMeta("opt", text);
        _options.Add((c, get));
        return c;
    }
    readonly List<(CheckBox Box, Func<QueueState, bool> Get)> _options = new();

    static Tree MakeTable(string[] titles, int[] widths, int iconCol, int minHeight, int[]? expandCols = null)
    {
        var t = new Tree
        {
            Columns = titles.Length,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            CustomMinimumSize = new Vector2(0, minHeight),
        };
        for (int c = 0; c < titles.Length; c++)
        {
            t.SetColumnTitle(c, titles[c]);
            t.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            t.SetColumnCustomMinimumWidth(c, widths[c]);
            t.SetColumnExpand(c, expandCols?.Contains(c) ?? c < 2);
        }
        return t;
    }

    void ShowTab(int which)
    {
        _tab = which < 0 ? _tab : which;
        for (int i = 0; i < _panels.Length; i++)
        {
            _panels[i].Visible = i == which;
            _tabs[i].SetPressedNoSignal(i == which);
        }
        if (which == 1) { if (_planItem.SelectedId == null) FillPlanItems(); else MakePlan(); }
        if (which == 2) RefreshSaved();
        if (which == 3) RefreshHistory();
        if (which == 4) RefreshOptions();
    }

    void WithStep(Action<int> act)
    {
        if (_selected >= 0 && _selected < _queue.State.Steps.Count) act(_selected);
    }

    void WithSaved(Action<string> act)
    {
        int[] sel = _savedList.GetSelectedItems();
        if (sel.Length > 0) act(_savedList.GetItemText(sel[0]));
    }

    void OnOpened()
    {
        Game? game = GameAccess.CurrentGame;
        if (game != null)
        {
            _do.Fill(game);
            _idle.Fill(game);
            _idle.Show(_queue.State.Idle);
            PlanData.Get(_queue.Folder, _queue.Log);   // start loading the planner data early
        }
        ShowTab(_queue.State.Running && !_tabPinned ? -1 : _tab);
        RefreshTargets();
        Rebuild();
    }

    // ---- add or edit a step ----

    Goal SelectedGoal => (Goal)_goal.Selected;

    void OnActionPicked()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) { RefreshTargets(); return; }
        if (_do.IsBest)
        {
            // Training a skill: the natural goal is a level of that skill.
            _goal.Select((int)Goal.Level);
            RefreshTargets();
            return;
        }
        // Suggest the item the action most likely makes: one named like the action ("Copper Bar" -> Copper Bar).
        ActionChoice? a = _do.SelectedChoice;
        string? suggest = null;
        if (a != null && SelectedGoal is Goal.BankCount or Goal.MoreItems)
        {
            var items = Catalog.Items(game);
            suggest = items.FirstOrDefault(i => i.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).Id;
            if (suggest == null && a.Name.EndsWith(" Tree"))
                suggest = items.FirstOrDefault(i => i.Name.Equals(a.Name[..^5] + " Log", StringComparison.OrdinalIgnoreCase)).Id;
        }
        RefreshTargets(suggest);
    }

    void RefreshTargets(string? select = null)
    {
        bool sell = _do.IsSell;
        Goal g = sell ? Goal.BankCount : SelectedGoal;
        Game? game = GameAccess.CurrentGame;
        _goal.Visible = !sell;
        _untilLabel.Text = sell ? "item" : "until";
        _keep.Visible = sell;
        _amount.Visible = sell || g != Goal.UntilStops;
        _amount.MinValue = sell ? 0 : 1;
        _amount.MaxValue = g == Goal.Level ? 120 : g == Goal.Minutes ? 100_000 : 10_000_000;
        _target.Visible = g is Goal.BankCount or Goal.MoreItems or Goal.Level;
        _addLane.Visible = _queue.State.Lanes > 1;
        _addLane.MaxValue = Math.Max(1, _queue.State.Lanes);

        if (game != null && g == Goal.Level)
        {
            string? cur = _target.SelectedId;
            var skills = game.LeveledEntities.OfType<GameEntity>().OrderBy(e => e.DisplayName.ToString())
                .Select(e => new PickItem(e.Id, e.DisplayName.ToString(), e.Icon, Note: $"level {Queue.Level(game, e.Id)}")).ToList();
            ActionRef? sel = _do.Selected;
            _target.Placeholder = "Choose a skill";
            _target.SetItems(skills, select ?? (sel != null && skills.Any(s => s.Id == sel.Entity) ? sel.Entity : cur));
        }
        else if (game != null && g is Goal.BankCount or Goal.MoreItems)
        {
            string? cur = _target.SelectedId;
            _target.Placeholder = "Choose an item";
            _target.SetItems(ItemPicks(game), select ?? cur);
        }

        bool ready = sell || _do.Selected != null;
        _add.Disabled = !ready;
        _add.Text = _editing >= 0 ? "Save changes" : "Add step";
        _cancelEdit.Visible = _editing >= 0;
        ActionRef? now = game == null ? null : Starter.Current(game);
        _now.Text = game == null ? "Load a character to pick actions." : "Right now: " + (now?.Label ?? "nothing");
    }

    List<PickItem>? _itemPicks;
    Game? _itemPicksFor;

    List<PickItem> ItemPicks(Game game)
    {
        if (_itemPicks != null && _itemPicksFor == game) return _itemPicks;
        _itemPicksFor = game;
        return _itemPicks = Catalog.Items(game).Select(i => new PickItem(i.Id, i.Name, Catalog.ItemIcon(game, i.Id))).ToList();
    }

    Step? BuildStep()
    {
        int lane = (int)_addLane.Value;
        if (_do.IsSell)
        {
            PickItem? item = _target.Selected;
            if (item == null) return null;
            return new Step
            {
                Action = new ActionRef { Entity = Starter.SellEntity, Label = $"Sell from bank: {item.Text}" },
                Goal = Goal.BankCount, Amount = (int)_amount.Value, Item = item.Id, ItemName = item.Text, Lane = lane,
            };
        }
        ActionRef? a = _do.Selected;
        if (a == null) return null;
        var s = new Step { Action = a, Goal = SelectedGoal, Amount = (int)_amount.Value, Lane = lane };
        if (s.Goal is Goal.Level or Goal.BankCount or Goal.MoreItems)
        {
            PickItem? t = _target.Selected;
            if (t == null) return null;
            if (s.Goal == Goal.Level) { s.Skill = t.Id; s.SkillName = t.Text; }
            else { s.Item = t.Id; s.ItemName = t.Text; }
        }
        return s;
    }

    void AddStep()
    {
        Step? s = BuildStep();
        if (s == null) return;
        if (_editing >= 0 && _editing < _queue.State.Steps.Count)
        {
            _selected = _editing;
            int at = _editing;
            StopEditing();
            _queue.Replace(at, s);
            return;
        }
        _selected = _queue.State.Steps.Count;
        _queue.Add(s);
    }

    void Edit(int i)
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null || i < 0 || i >= _queue.State.Steps.Count) return;
        Step s = _queue.State.Steps[i];
        _editing = i;
        _tabPinned = true;
        ShowTab(0);
        _do.Fill(game);
        _do.Show(s.Action);
        if (!s.IsSell) _goal.Select((int)s.Goal);
        _amount.Value = s.Amount;
        _addLane.Value = s.Lane;
        RefreshTargets(s.Goal == Goal.Level ? s.Skill : s.Item);
    }

    void StopEditing()
    {
        _editing = -1;
        RefreshTargets();
    }

    // ---- plan an item ----

    void FillPlanItems()
    {
        Game? game = GameAccess.CurrentGame;
        PlanData? data = PlanData.Get(_queue.Folder, _queue.Log);
        if (game == null) return;
        if (data == null) { _planNote.Text = $"Planner data: {PlanData.State}. It comes from {PlanData.Url} and is kept for offline use."; return; }
        // Only items some action makes can be planned.
        var makeable = new HashSet<string>(data.Rates.SelectMany(r => r.Out.Select(o => o.Item)));
        var all = Catalog.Items(game);
        var picks = data.Items.Where(kv => makeable.Contains(kv.Key)).OrderBy(kv => kv.Value)
            .Select(kv =>
            {
                string? full = all.FirstOrDefault(i => Catalog.Local(i.Id) == kv.Key).Id;
                return new PickItem(kv.Key, kv.Value, full == null ? null : Catalog.ItemIcon(game, full));
            }).ToList();
        _planItem.SetItems(picks);
        MakePlan();
    }

    void MakePlan()
    {
        _planTable.Clear();
        _plan = new();
        _planAdd.Disabled = true;
        Game? game = GameAccess.CurrentGame;
        PlanData? data = PlanData.Get(_queue.Folder, _queue.Log);
        if (game == null || data == null || _planItem.SelectedId == null) return;

        _planner = new Planner(data, game);
        bool more = _planMode.Selected == 0;
        int amount = (int)_planAmount.Value;
        _plan = _planner.Plan(_planItem.SelectedId, amount, more);
        _planner.ResolveIds(_plan);
        List<Step> steps = _planner.ToSteps(_plan);

        TreeItem root = _planTable.CreateItem();
        double hours = 0;
        int n = 0;
        bool bring = false;
        foreach (Step train in steps.Where(s => s.IsBest))
        {
            TreeItem row = _planTable.CreateItem(root);
            row.SetText(0, $"{++n}. {train.Action.Label} to level {train.Amount}");
            row.SetIcon(0, Kit.Icon(Catalog.ActionIcon(game, train.Action)));
            row.SetText(1, "(levels up first)");
            row.SetCustomColor(0, Kit.Link);
        }
        foreach (PlanLine l in _plan)
        {
            TreeItem row = _planTable.CreateItem(root);
            if (l.Rate == null) bring = true;
            string what = l.Rate == null ? $"Bring {l.ItemName} yourself" : $"{++n}. {l.Rate.Skill}: {l.Rate.Name}";
            row.SetText(0, l.Rate != null && l.LevelNeeded > 0 ? $"{what}  (after training to {l.LevelNeeded})" : what);
            if (l.Action != null) row.SetIcon(0, Kit.Icon(Catalog.ActionIcon(game, l.Action)));
            row.SetText(1, $"{l.Target:N0} {l.ItemName}");
            row.SetText(2, l.Have.ToString("N0"));
            row.SetText(3, Duration(l.Hours));
            if (l.Rate == null || (l.Action == null && l.LevelNeeded == 0)) for (int c = 0; c < 4; c++) row.SetCustomColor(c, Kit.Gold);
            hours += l.Hours;
        }
        string mode = more ? $"To make {amount:N0} more" : $"To own {amount:N0}";
        _planNote.Text = _plan.Count == 0
            ? "You already have enough."
            : $"{mode}: {steps.Count} step{(steps.Count == 1 ? "" : "s")}, about {Duration(hours)} of actions at base game speed (gear and upgrades make it faster; leveling time not included)."
              + (bring ? " Yellow lines are items no action makes; bring them yourself." : "");
        _planAdd.Disabled = steps.Count == 0;
    }

    static string Duration(double h) =>
        double.IsInfinity(h) || double.IsNaN(h) ? "?" : h < 1.0 / 60 ? "< 1 min" : h < 1 ? $"{Math.Round(h * 60)} min" : h < 48 ? $"{Math.Floor(h)} h {Math.Round(h % 1 * 60)} min" : $"{h / 24:0.#} days";

    void AddPlan()
    {
        if (_planner == null) return;
        var steps = _planner.ToSteps(_plan);
        if (steps.Count == 0) return;
        _queue.AddRange(steps);
        ShowTab(0);
    }

    // ---- saved and shared ----

    void RefreshSaved()
    {
        _savedList.Clear();
        foreach (string n in _queue.SavedNames()) _savedList.AddItem(n);
        if (_saveName.Text.Length == 0) _saveName.Text = _queue.State.Name;
    }

    void RefreshQueuePick()
    {
        _queuePick.Clear();
        _queuePick.AddItem(_queue.State.Name.Length > 0 ? $"Queue: {_queue.State.Name}" : "Queue: not saved");
        foreach (string n in _queue.SavedNames()) _queuePick.AddItem(n);
        _queuePick.Select(0);
    }

    void CopyCode()
    {
        if (_queue.State.Steps.Count == 0) { _shareNote.Text = "The queue is empty."; return; }
        DisplayServer.ClipboardSet(ShareCode.Export(_queue.State));
        _shareNote.Text = $"Copied a code for {_queue.State.Steps.Count} steps. Paste it anywhere; others add it with \"Add steps from a pasted code\".";
    }

    void PasteCode()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) return;
        var (steps, lanes, problems) = ShareCode.Import(game, DisplayServer.ClipboardGet());
        if (steps.Count > 0)
        {
            if (lanes > _queue.State.Lanes) _queue.SetLanes(lanes);
            _queue.AddRange(steps);
        }
        _shareNote.Text = (steps.Count > 0 ? $"Added {steps.Count} step{(steps.Count == 1 ? "" : "s")}." : "Nothing added.")
            + (problems.Count > 0 ? " " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? "..." : "") : "");
    }

    // ---- history and options ----

    void RefreshHistory()
    {
        _history.Clear();
        foreach (HistoryEntry e in Enumerable.Reverse(_queue.History))
        {
            int i = _history.AddItem($"{e.Time:MMM d HH:mm}   {e.Text}");
            if (e.Text.StartsWith("Skipped") || e.Text.StartsWith("Paused")) _history.SetItemCustomFgColor(i, Kit.Gold);
            else if (e.Text.StartsWith("Queue")) _history.SetItemCustomFgColor(i, Kit.Link);
        }
    }

    void RefreshOptions()
    {
        foreach (var (box, get) in _options) box.SetPressedNoSignal(get(_queue.State));
    }

    // ---- the table ----

    void SetUpDragging()
    {
        _table.SetDragForwarding(
            Callable.From((Vector2 at) =>
            {
                TreeItem? item = _table.GetItemAtPosition(at);
                if (item == null || item.GetMetadata(0).VariantType == Variant.Type.Nil) return default(Variant);
                _table.SetDragPreview(Kit.Label(item.GetText(2), Kit.Text));
                _table.DropModeFlags = (int)Tree.DropModeFlagsEnum.Inbetween;
                return Variant.From(item.GetMetadata(0).AsInt32());
            }),
            Callable.From((Vector2 at, Variant data) => data.VariantType == Variant.Type.Int),
            Callable.From((Vector2 at, Variant data) =>
            {
                _table.DropModeFlags = 0;
                int from = data.AsInt32();
                TreeItem? item = _table.GetItemAtPosition(at);
                int to = _queue.State.Steps.Count - 1;
                if (item != null && item.GetMetadata(0).VariantType != Variant.Type.Nil)
                {
                    to = item.GetMetadata(0).AsInt32() + (_table.GetDropSectionAtPosition(at) > 0 ? 1 : 0);
                    if (to > from) to--;
                }
                _selected = to;
                _queue.MoveTo(from, to);
            }));
    }

    void Rebuild()
    {
        var st = _queue.State;
        _run.Text = st.Running ? "Pause" : "Start";
        _lanes.Text = st.Lanes == 1 ? "Side by side: off" : $"Side by side: {st.Lanes} at once";
        _laneUp.Visible = _laneDown.Visible = st.Lanes > 1;
        _addLane.Visible = st.Lanes > 1;
        _addLane.MaxValue = st.Lanes;
        RefreshQueuePick();
        if (st.Running && !_tabPinned && _panels.Any(p => p.Visible)) ShowTab(-1);
        if (_tab == 3 && _panels[3].Visible) RefreshHistory();

        _table.Clear();
        TreeItem root = _table.CreateItem();
        Game? game = GameAccess.CurrentGame;
        var current = new HashSet<Step>(st.Running ? _queue.CurrentSteps() : Enumerable.Empty<Step>());
        for (int i = 0; i < st.Steps.Count; i++)
        {
            Step s = st.Steps[i];
            TreeItem row = _table.CreateItem(root);
            row.SetMetadata(0, i);
            row.SetText(0, (i + 1).ToString());
            row.SetText(1, st.Lanes > 1 ? _queue.LaneOf(s).ToString() : "");
            row.SetText(2, Queue.Label(s));
            if (game != null)
            {
                Texture2D? icon = s.IsSell && s.Item.Length > 0 ? Catalog.ItemIcon(game, s.Item) : Catalog.ActionIcon(game, s.Effective);
                row.SetIcon(2, Kit.Icon(icon));
            }
            row.SetText(3, s.GoalText());
            row.SetText(4, StateText(game, s, current.Contains(s)));
            row.SetCustomColor(0, Kit.Dim);
            row.SetCustomColor(1, Kit.Dim);
            row.SetCustomColor(4, StateColor(s));
            if (s.Done) { row.SetCustomColor(2, Kit.Dim); row.SetCustomColor(3, Kit.Dim); }
            if (current.Contains(s)) for (int col = 0; col < 5; col++) row.SetCustomBgColor(col, s_currentRow);
            if (i == _selected) row.Select(0);
        }
        if (st.Steps.Count == 0)
        {
            TreeItem hint = _table.CreateItem(root);
            hint.SetText(2, game == null ? "Load a character to pick actions." : "No steps yet. Add a step or plan an item above.");
            hint.SetCustomColor(2, Kit.Dim);
            for (int col = 0; col < 5; col++) hint.SetSelectable(col, false);
        }
        UpdateStatus();
    }

    string StateText(Game? game, Step s, bool current)
    {
        string progress = _queue.Progress(game, s);
        string state = s.State.Length > 0 ? s.State : s.Done ? "done" : current ? "starting" : "";
        return state.Length == 0 ? progress : progress.Length == 0 ? state : $"{state}  {progress}";
    }

    static Color StateColor(Step s) =>
        s.State.StartsWith("running") ? Kit.Good : s.State.StartsWith("waiting") ? Kit.Gold : s.State == "skipped" ? Kit.Bad : s.Done ? Kit.Dim : Kit.Text;

    void UpdateStatus()
    {
        var st = _queue.State;
        int done = st.Steps.Count(x => x.Done);
        _status.Text = st.Running ? $"{done}/{st.Steps.Count} done. {_queue.Status}"
            : st.Steps.Count == 0 ? "Add steps, then Start." : _queue.Status;
        _idleNote.Text = st.Idle == null ? "Idle action: none (the game keeps doing whatever it was doing)" : $"Idle action: {st.Idle.Label}";
    }

    double _nextTick;

    /// <summary>Called every frame; refreshes the live columns a few times a second while open.</summary>
    public void Tick()
    {
        if (!_win.Visible) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextTick) return;
        _nextTick = now + 0.3;

        Game? game = GameAccess.CurrentGame;
        if (game != null && _do.IsEmpty) { OnOpened(); return; }   // a character was loaded while the window was open
        if (_tab == 1 && _panels[1].Visible && _planItem.SelectedId == null && PlanData.Loaded != null) FillPlanItems();

        TreeItem? row = _table.GetRoot()?.GetFirstChild();
        var current = new HashSet<Step>(_queue.State.Running ? _queue.CurrentSteps() : Enumerable.Empty<Step>());
        while (row != null)
        {
            Variant meta = row.GetMetadata(0);
            int i = meta.VariantType == Variant.Type.Nil ? -1 : meta.AsInt32();
            if (i >= 0 && i < _queue.State.Steps.Count)
            {
                Step s = _queue.State.Steps[i];
                row.SetText(2, Queue.Label(s));
                row.SetText(4, StateText(game, s, current.Contains(s)));
                row.SetCustomColor(4, StateColor(s));
            }
            row = row.GetNext();
        }
        UpdateStatus();
    }
}

/// <summary>Skill dropdown (with icons) and a searchable action list, filled from the running game.</summary>
sealed class ActionPicker
{
    const string SellId = "*sell", NoneId = "*none";
    readonly bool _allowNone, _allowSell, _allowBest;
    List<SkillChoice> _skills = new();
    readonly List<string> _skillIds = new();

    public OptionButton SkillBox { get; } = new() { CustomMinimumSize = new Vector2(170, 0) };
    public SearchPicker ActionPick { get; } = new(240) { Placeholder = "Choose an action" };
    public event Action? Changed;

    public ActionPicker(bool allowNone = false, bool allowSell = false, bool allowBest = false)
    {
        _allowNone = allowNone;
        _allowSell = allowSell;
        _allowBest = allowBest;
        SkillBox.ItemSelected += _ => { FillActions(); Changed?.Invoke(); };
        ActionPick.Picked += _ => Changed?.Invoke();
    }

    string? SkillId => SkillBox.Selected >= 0 && SkillBox.Selected < _skillIds.Count ? _skillIds[SkillBox.Selected] : null;
    SkillChoice? Skill => _skills.FirstOrDefault(s => s.Id == SkillId);

    public bool IsSell => SkillId == SellId;
    public bool IsBest => ActionPick.SelectedId == Starter.BestTarget;
    public bool IsEmpty => _skills.Count == 0;

    public ActionChoice? SelectedChoice => Skill?.Actions.FirstOrDefault(a => Key(a.Ref) == ActionPick.SelectedId);

    public ActionRef? Selected
    {
        get
        {
            SkillChoice? s = Skill;
            if (s == null) return null;
            if (IsBest) return new ActionRef { Entity = s.Id, Target = Starter.BestTarget, Label = $"Train {s.Name}" };
            return SelectedChoice?.Ref;
        }
    }

    static string Key(ActionRef r) => r.Area.Length > 0 && r.Area != r.Target ? r.Target + "@" + r.Area : r.Target;

    public void Fill(Game game)
    {
        if (_skills.Count > 0) return;
        _skills = Catalog.Skills(game);
        SkillBox.Clear();
        _skillIds.Clear();
        if (_allowNone) { SkillBox.AddItem("Nothing"); _skillIds.Add(NoneId); }
        if (_allowSell) { SkillBox.AddItem("Sell from bank"); _skillIds.Add(SellId); }
        foreach (var s in _skills)
        {
            Texture2D? icon = Kit.Icon(s.Icon, 18);
            if (icon != null) SkillBox.AddIconItem(icon, s.Name); else SkillBox.AddItem(s.Name);
            _skillIds.Add(s.Id);
        }
        int first = _allowSell ? 1 : 0;
        if (SkillBox.ItemCount > first) SkillBox.Select(first);
        FillActions();
    }

    void FillActions()
    {
        SkillChoice? s = Skill;
        ActionPick.Visible = !IsSell && SkillId != NoneId;
        if (s == null) { ActionPick.SetItems(Enumerable.Empty<PickItem>()); return; }
        var picks = new List<PickItem>();
        if (_allowBest) picks.Add(new PickItem(Starter.BestTarget, "Fastest XP (switches as you level)", s.Icon, Note: "train"));
        picks.AddRange(s.Actions.Select(a => new PickItem(Key(a.Ref), a.Name, a.Icon, a.LockedNow, a.LockedNow ? "locked" : "")));
        // Default to the best action you can do now.
        string? best = s.Actions.LastOrDefault(a => !a.LockedNow) is ActionChoice open ? Key(open.Ref) : null;
        ActionPick.SetItems(picks, best);
    }

    /// <summary>Selects an action in the pickers, or "Nothing".</summary>
    public void Show(ActionRef? r)
    {
        if (r == null) { if (_allowNone && SkillBox.ItemCount > 0) { SkillBox.Select(0); FillActions(); } return; }
        int si = _skillIds.IndexOf(r.Entity == Starter.SellEntity ? SellId : r.Entity);
        if (si < 0) return;
        SkillBox.Select(si);
        FillActions();
        ActionPick.Select(r.Target == Starter.BestTarget ? Starter.BestTarget : Key(r));
    }

    public void UseCurrent()
    {
        Game? game = GameAccess.CurrentGame;
        if (game == null) return;
        Fill(game);
        Show(Starter.Current(game));
        Changed?.Invoke();
    }
}
