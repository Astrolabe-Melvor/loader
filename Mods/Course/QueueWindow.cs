using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// The queue window. Tabs on top (add a step, plan an item, saved and shared queues, history, options), the steps
/// table in the middle (drag to reorder, double-click to edit), then the selected-step buttons, the idle action
/// and the status line.
/// </summary>
sealed class QueueWindow
{
    static readonly string[] s_tabNames = { "Add a step", "Plan an item", "Saved and shared", "History", "Options", "Toolbelts", "Slayer" };
    static readonly Color s_currentRow = new(0.18f, 0.43f, 0.18f, 0.35f);

    readonly Queue _queue;
    readonly KitWindow _win;
    readonly OptionButton _queuePick;
    readonly Button _run;
    readonly Button[] _tabs = new Button[7];
    readonly Control[] _panels = new Control[7];
    SlayerPanel _slayer = null!;
    readonly BeltEditor _beltEditor;
    readonly OptionButton _belt;
    readonly HBoxContainer _beltRow, _goldRow, _fundRow;
    readonly CheckBox _sellOut;
    readonly OptionButton _funding;
    int _tab;
    bool _tabPinned;   // the player opened a tab while the queue runs (otherwise tabs fold away to give the table room)

    // add a step
    readonly ActionPicker _do;
    readonly OptionButton _goal;
    readonly SpinBox _amount;
    readonly SearchPicker _target;
    readonly Button _add, _cancelEdit;
    readonly Label _now, _keep, _untilLabel, _summary;
    readonly HBoxContainer _goalRow;
    int _editing = -1;
    bool _loading;   // filling the form from a step: keep its values instead of suggesting new ones

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
    readonly CheckBox _optAlerts, _optFood, _optBank, _optHud, _optLoop, _optGear;
    readonly OptionButton _gear, _weapon, _stance;
    readonly HBoxContainer _gearRow;
    readonly Label _combatNote;
    readonly GridContainer _gearGrid;
    readonly Label _gearNote;

    // table and below
    readonly Tree _table;
    readonly ActionPicker _idle;
    readonly Label _idleNote, _status;
    int _selected = -1;
    /// <summary>Ticked rows: Up, Down, Remove and dragging work on all of them at once.</summary>
    HashSet<int> _ticked = new();

    public QueueWindow(Queue queue)
    {
        _queue = queue;
        _win = new KitWindow("Course", "queue your actions: this, then that, then that", new Vector2(940, 0));
        _win.SetHelp(Help.Text);

        // ---- header ----
        _queuePick = new OptionButton { CustomMinimumSize = new Vector2(190, 0), TooltipText = "Load a saved queue" };
        _queuePick.ItemSelected += i => { if (i > 0) _queue.LoadSaved(_queuePick.GetItemText((int)i)); };
        _run = Kit.Btn("Start", () =>
        {
            if (_queue.State.Running) _queue.Pause();
            else { _tabPinned = false; _queue.Start(); }
        }, action: true);
        _run.CustomMinimumSize = new Vector2(100, 0);
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
        tabRow.MoveChild(_tabs[5], 3);   // Toolbelts and Slayer sit before History
        tabRow.MoveChild(_tabs[6], 4);
        _win.Body.AddChild(tabRow);

        // ---- add a step ----
        var addBox = Panel(0);
        _do = new ActionPicker(allowSell: true, allowBest: true);
        _do.Changed += OnActionPicked;
        var copyNow = Kit.Btn("Copy what I'm doing", () => _do.UseCurrent());
        copyNow.TooltipText = "Fill in the action your character is doing right now";
        HBoxContainer doRow = Kit.Row(Field("Do"), _do.SkillBox, _do.ActionPick.Button, copyNow);
        addBox.AddChild(doRow);

        _untilLabel = Field("until");
        _goal = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        _goal.ItemSelected += _ => { RefreshTargets(); Suggest(); };
        _keep = Kit.Label("keep", null, 13, "Dim");
        _amount = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, CustomMinimumSize = new Vector2(130, 0) });
        _amount.ValueChanged += _ => UpdateSummary();
        _target = new SearchPicker(230) { Placeholder = "Choose an item" };
        _target.Picked += _ => { Suggest(); };
        _add = Kit.Btn("Add step", AddStep, action: true);
        _cancelEdit = Kit.Btn("Cancel", StopEditing);
        _cancelEdit.Visible = false;
        _goalRow = Kit.Row(_untilLabel, _goal, _keep, _amount, _target.Button, _add, _cancelEdit);
        addBox.AddChild(_goalRow);
        _gear = new OptionButton { CustomMinimumSize = new Vector2(230, 0), TooltipText = "Which combat loadout to wear for this step. Set up loadouts in the game's equipment screen." };
        _gear.ItemSelected += _ => RefreshCombat();
        _weapon = new OptionButton { CustomMinimumSize = new Vector2(260, 0), TooltipText = "The weapon decides which stances you can use. \"Best\" compares every weapon you own in the loadout's style against this monster." };
        _weapon.ItemSelected += _ => RefreshCombat();
        _stance = new OptionButton { CustomMinimumSize = new Vector2(190, 0), TooltipText = "Stab, Slash, Crush and the rest each hit a different defence bonus on the monster. Lower defence means more hits." };
        _stance.ItemSelected += _ => UpdateSummary();
        _gearRow = Kit.Row(Field("wearing"), _gear, Field("weapon"), _weapon, Field("stance"), _stance);
        addBox.AddChild(_gearRow);
        _belt = new OptionButton { CustomMinimumSize = new Vector2(300, 0), TooltipText = "A toolbelt set to put on when this step starts. Make sets in the Toolbelts tab." };
        _belt.ItemSelected += _ => UpdateSummary();
        _sellOut = new CheckBox { Text = "Sell what it makes as it comes in (never locked bank stacks)", ButtonPressed = true };
        _sellOut.Toggled += _ => UpdateSummary();
        _goldRow = Kit.Row(Field("gold"), _sellOut);
        addBox.AddChild(_goldRow);
        _funding = new OptionButton { CustomMinimumSize = new Vector2(460, 0),
            TooltipText = "What Most GP and Fastest XP may use. Gathering keeps what you had: it stops making when only your old stock is left and gathers more first." };
        _funding.AddItem("Use what's in the bank");
        _funding.AddItem("Only actions that use nothing from the bank");
        _funding.AddItem("Gather the ingredients itself (your bank stock is left alone)");
        _funding.ItemSelected += _ => UpdateSummary();
        _fundRow = Kit.Row(Field("materials"), _funding);
        addBox.AddChild(_fundRow);
        _beltRow = Kit.Row(Field("toolbelt"), _belt, WithTip(Kit.Btn("Edit sets", () => { _tabPinned = true; ShowTab(5); }), "Open the Toolbelts tab"));
        addBox.AddChild(_beltRow);
        _combatNote = Wrapped("");
        addBox.AddChild(_combatNote);
        _summary = Wrapped("");
        _summary.AddThemeFontSizeOverride("font_size", 14);
        addBox.AddChild(_summary);
        _now = Kit.Label("", null, 12, "Dim");
        addBox.AddChild(_now);

        // ---- plan an item ----
        var planBox = Panel(1);
        _planMode = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        _planMode.AddItem("Get this many more");
        _planMode.AddItem("Have this many in total");
        _planMode.ItemSelected += _ => MakePlan();
        _planAmount = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, CustomMinimumSize = new Vector2(110, 0) });
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
        _beltEditor = new BeltEditor(_queue, Panel(5));
        _slayer = new SlayerPanel(_queue, Panel(6));

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
        optBox.AddChild(Option("Show a Queue button on the game's skill and combat pages (top right): it opens Add a step with that page's action picked", s => s.PageButton, (s, v) => s.PageButton = v));
        optBox.AddChild(Option("Take steps off the list once they reach their goal (kept while the queue loops; skipped steps stay so you can see them)", s => s.RemoveFinished, (s, v) => s.RemoveFinished = v));
        _optHud = Option("Show the current step in a small panel while the queue runs (drag it anywhere, \"–\" folds it)", s => s.Hud, (s, v) => s.Hud = v);
        _optGear = Option("Put on each step's gear when it starts: combat loadouts, weapons and stances, and skill toolbelt sets with refills", s => s.AutoGear, (s, v) => s.AutoGear = v);
        _optGear.Toggled += _ => RefreshGearGrid();
        optBox.AddChild(Option("Wear a slayer gauntlet when a combat step's monster is one of your slayer tasks (puts your gloves back after)", s => s.SlayerGauntlet, (s, v) => s.SlayerGauntlet = v));
        optBox.AddChild(Option("Thieving and Agility steps wear the gear you own that gives the most XP there (then the best loot), unless the step has a toolbelt set", s => s.FinesseGear, (s, v) => s.FinesseGear = v));
        optBox.AddChild(Option("Thieving and Agility food: keep the skill's best bonus food (sushi, soup) selected, and switch to healing food before a failure could knock you out", s => s.FinesseFood, (s, v) => s.FinesseFood = v));
        var floor = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 90, Suffix = "%", Value = _queue.State.FinesseFloor, CustomMinimumSize = new Vector2(100, 0) });
        floor.ValueChanged += v => _queue.SetOption(s => s.FinesseFloor = (int)v);
        floor.TooltipText = "Heal at this HP percent or below, as well as whenever one failure could take what's left.";
        optBox.AddChild(Kit.Row(Kit.Label("Also heal below", null, 13, "Dim"), floor, Kit.Label("HP", null, 13, "Dim")));
        optBox.AddChild(Kit.Row(Kit.Btn("Put the panel back in its first spot", () => _queue.SetOption(s => { s.HudX = s.HudY = -1; s.HudMin = false; }))));
        foreach (CheckBox c in new[] { _optLoop, _optAlerts, _optFood, _optBank, _optHud, _optGear }) optBox.AddChild(c);
        optBox.AddChild(Kit.Label("Combat loadout", null, 15, "Heading"));
        _gearNote = Wrapped("");
        optBox.AddChild(_gearNote);
        _gearGrid = new GridContainer { Columns = 8 };
        _gearGrid.AddThemeConstantOverride("h_separation", 10);
        optBox.AddChild(_gearGrid);

        // ---- the steps ----
        // State grows with the window too (it holds the time left), so widening the window shows all of it.
        _table = MakeTable(new[] { "#", "Do", "Until", "State" }, new[] { 62, 280, 260, 340 }, 1, 230, expandCols: new[] { 1, 2, 3 }, ratios: new[] { 1, 4, 4, 3 });
        _table.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _table.ItemSelected += () => _selected = _table.GetSelected()?.GetMetadata(0).AsInt32() ?? -1;
        _table.ItemEdited += () =>
        {
            TreeItem? it = _table.GetEdited();
            if (it == null || it.GetMetadata(0).VariantType == Variant.Type.Nil) return;
            int i = it.GetMetadata(0).AsInt32();
            if (it.IsChecked(0)) _ticked.Add(i); else _ticked.Remove(i);
            UpdateTickHint();
        };
        _table.ItemActivated += () => { int i = _table.GetSelected()?.GetMetadata(0).AsInt32() ?? -1; if (i >= 0) Edit(i); };
        SetUpDragging();
        _win.Body.AddChild(_table);

        _win.Body.AddChild(Kit.Row(
            WithTip(Kit.Btn("Edit", () => WithStep(Edit)), "Change the selected step (or double-click it)"),
            WithTip(Kit.Btn("Up", () => MoveRows(-1)), "Move the ticked steps (or the selected one) up one place"),
            WithTip(Kit.Btn("Down", () => MoveRows(1)), "Move the ticked steps (or the selected one) down one place"),
            WithTip(Kit.Btn("Remove", RemoveRows), "Remove the ticked steps (or the selected one)"),
            WithTip(Kit.Btn("Run from here", () => WithStep(i => { _tabPinned = false; _queue.Start(i); })), "Start the queue at the selected step; steps above it are passed over (not run, not marked done)"),
            _undo = Kit.Btn("Undo", () => _queue.Undo()),
            Kit.Spacer(),
            _stepHint = Kit.Label("", null, 12, "Dim"),
            WithTip(Kit.Btn("Tick all", ToggleTicks), "Tick every step, or clear the ticks"),
            WithTip(Kit.Btn("Remove finished", _queue.RemoveDone), "Take every finished or skipped step off the list"),
            WithTip(Kit.Btn("Remove all", _queue.Clear), "Empty the queue (Undo brings it back)")));
        var stepRow = (Control)_win.Body.GetChild(_win.Body.GetChildCount() - 1);

        _idle = new ActionPicker(allowNone: true);
        _idle.Changed += () => _queue.SetIdle(_idle.Selected);
        _win.Body.AddChild(Kit.Row(Kit.Label("When the queue waits or ends, do", null, 13, "Dim"), _idle.SkillBox, _idle.ActionPick.Button,
            WithTip(Kit.Btn("Copy what I'm doing", () => _idle.UseCurrent()), "Set the idle action to what your character is doing right now")));
        var idleRow = (Control)_win.Body.GetChild(_win.Body.GetChildCount() - 1);
        _idleNote = Kit.Label("", null, 12, "Dim");
        _win.Body.AddChild(_idleNote);
        _status = Kit.Label("", Kit.Good, 14);
        _status.ClipText = true;
        _win.Body.AddChild(_status);

        _win.Opened += OnOpened;
        _queue.Changed += () => { if (_win.Visible) Rebuild(); };

        void Tab(int i) { _tabPinned = true; ShowTab(i); }
        _win.SetTour(
            new TourStep("What Course does", "Course runs your actions one after another: cut 500 logs, then burn them, then fight until level 40. Build the list, press [b]Start[/b], and it keeps going while you play or idle."),
            new TourStep("Tabs", "[b]Add a step[/b] is where most steps come from. [b]Plan an item[/b] works out every step to make something. [b]Saved and shared[/b] keeps queues and share codes from the wiki planner. [b]Options[/b] changes how the queue behaves.", () => tabRow, () => Tab(0)),
            new TourStep("Pick the action", "Choose a skill, then the action. [b]Copy what I'm doing[/b] fills in what your character is doing now. Agility lists each path of a course on its own, or \"(best path)\" to let Course pick. The game's skill pages also get a [b]Queue[/b] button (top right) that opens this with that page's action picked.", () => doRow, () => Tab(0)),
            new TourStep("Say when it ends", "Pick the goal: a level, an amount, until the bank holds some, and so on. [b]Add step[/b] puts it at the bottom of the list. A line under this shows how long it should take.", () => _goalRow, () => Tab(0)),
            new TourStep("The list", "Steps run top to bottom. Tick the boxes to work on several at once, drag rows to reorder them, and double-click a step to change it. [b]State[/b] shows progress.", () => _table),
            new TourStep("Edit the list", "These work on the ticked steps, or the selected one if none are ticked. [b]Undo[/b] takes back the last change, even [b]Remove all[/b].", () => stepRow),
            new TourStep("Start", "Runs the queue from the top. While it runs this button pauses it. [b]Run from here[/b] starts at the selected step.", () => _run),
            new TourStep("When it's done", "What your character does when the queue waits or ends, so it is never idle.", () => idleRow),
            new TourStep("Options", "Loop the queue, take finished steps off, sounds when it needs you, putting on each step's gear, and more. [b]Help[/b] in the title bar has the details.", () => _panels[4], () => Tab(4)),
            new TourStep("That's it", "Open Course with its key from anywhere (the Mods menu shows and changes keys). The Tour button runs this again.", null, () => Tab(0)));
    }

    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";
    public void Toggle() => _win.Toggle();
    public void Open() => _win.Open();

    /// <summary>Opens the Add a step tab with this action picked (the Queue button on the game's skill pages).</summary>
    public void AddFrom(ActionRef r)
    {
        _win.Open();
        if (_editing >= 0) StopEditing();
        _tabPinned = true;
        ShowTab(0);
        if (KitGame.Current is not Game game) return;
        _do.Fill(game);
        _do.Show(r);
        OnActionPicked();
    }

    /// <summary>Opens the planner on an item (sent by another mod, such as Spyglass). Item id: local or full.</summary>
    public void PlanFor(string item, int amount)
    {
        _win.Open();
        _tabPinned = true;
        ShowTab(1);
        if (_planItem.SelectedId == null || _planItem.SelectedId != Catalog.Local(item)) FillPlanItems();
        _planItem.Select(Catalog.Local(item));
        _planMode.Select(0);
        _planAmount.Value = Math.Max(1, amount);
        MakePlan();
    }

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

    static Tree MakeTable(string[] titles, int[] widths, int iconCol, int minHeight, int[]? expandCols = null, int[]? ratios = null)
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
            if (ratios != null) t.SetColumnExpandRatio(c, ratios[c]);
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
        if (which == 5) _beltEditor.Refresh();
        if (which == 6) _slayer.Refresh();
        if (which == 0) RefreshTargets();   // picks up gear and option changes made on other tabs
    }

    Button _undo = null!;
    Label _stepHint = null!;

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
        Game? game = KitGame.Current;
        if (game != null)
        {
            _do.Fill(game);
            _idle.Fill(game);
            _idle.Show(_queue.State.Idle);
            PlanData.Get(_queue.Folder, _queue.Log);   // start loading the planner data early
        }
        ShowTab(_queue.State.Running && !_tabPinned ? -1 : _tab);
        if (game != null && _editing < 0 && _goal.ItemCount == 0) OnActionPicked(); else RefreshTargets();
        Rebuild();
    }

    // ---- add or edit a step ----
    //
    // Every choice narrows the next: the action decides which goals make sense, the goal decides what the
    // picker lists (only that action's products and drops, or only the skills it trains), and the amount
    // starts from where you are now. The line under the form says what the step will do before it is added.

    Goal SelectedGoal => _do.IsSell ? Goal.BankCount : _goal.Selected < 0 ? Goal.Minutes : (Goal)_goal.GetItemId(_goal.Selected);

    void SelectGoal(Goal g)
    {
        int i = _goal.GetItemIndex((int)g);
        if (i >= 0) _goal.Select(i);
    }

    static bool IsDungeon(Game game, ActionRef a) =>
        a.Area.Length > 0 && a.Area == a.Target && Catalog.Skills(game).Any(s => s.Id == a.Entity);

    /// <summary>The goals that make sense for the picked action, most useful first.</summary>
    List<(Goal Goal, string Text)> GoalsFor(Game game, ActionRef? a)
    {
        var list = new List<(Goal, string)>();
        if (a == null) return list;
        if (_do.IsSlayer)
        {
            list.Add((Goal.Times, "this many tasks are done"));
            list.Add((Goal.Level, "a skill reaches"));
            list.Add((Goal.Minutes, "time passes"));
            return list;
        }
        if (_do.IsTech)
        {
            list.Add((Goal.Technique, "the technique reaches level"));
            list.Add((Goal.Level, "a skill reaches"));
            list.Add((Goal.Minutes, "time passes"));
            return list;
        }
        if (_do.IsBest)
        {
            if (_do.IsGold) list.Add((Goal.Gold, "I have this much GP"));
            if (!_do.IsAny) list.Add((Goal.Level, "a skill reaches"));
            list.Add((Goal.Minutes, "time passes"));
            return list;
        }
        if (Catalog.Outputs(game, a).Count > 0)
        {
            list.Add((Goal.BankCount, "the bank holds"));
            list.Add((Goal.MoreItems, "I gain"));
        }
        list.Add((Goal.Level, "a skill reaches"));
        list.Add((Goal.Times, Gear.IsCombat(game, a.Entity) ? "it is done (kills or runs)" : "it is done"));
        list.Add((Goal.Minutes, "time passes"));
        if (IsDungeon(game, a)) list.Add((Goal.UntilStops, "the dungeon run ends"));
        else if (Catalog.NeedsInput(game, a)) list.Add((Goal.UntilStops, "materials run out"));
        return list;
    }

    void FillGoals(Game game, Goal? want = null)
    {
        Goal? keep = want ?? (_goal.ItemCount > 0 ? SelectedGoal : null);
        var goals = GoalsFor(game, _do.Selected);
        _goal.Clear();
        foreach (var (g, text) in goals) _goal.AddItem(text, (int)g);
        if (_goal.ItemCount == 0) return;
        if (keep is Goal k && _goal.GetItemIndex((int)k) >= 0) SelectGoal(k);
        else _goal.Select(0);
    }

    void OnActionPicked()
    {
        Game? game = KitGame.Current;
        if (game != null && !_do.IsSell) FillGoals(game, _do.IsTech ? Goal.Technique : _do.IsBest ? Goal.Level : null);
        RefreshTargets();
        Suggest();
    }

    /// <summary>Fills the target picker and shows only the fields the goal uses, in an order that reads as a sentence.</summary>
    void RefreshTargets(string? select = null)
    {
        bool sell = _do.IsSell;
        Goal g = SelectedGoal;
        Game? game = KitGame.Current;
        ActionRef? a = _do.Selected;

        _goal.Visible = !sell && _goal.ItemCount > 0;
        _untilLabel.Text = sell ? "sell" : "until";
        _keep.Visible = sell;
        _amount.Visible = sell || g != Goal.UntilStops;
        _target.Visible = sell || g is Goal.BankCount or Goal.MoreItems or Goal.Level;
        _amount.Prefix = g is Goal.Level or Goal.Technique && !sell ? "level" : "";
        _amount.Suffix = sell ? "" : g switch { Goal.Minutes => "min", Goal.Times => _do.IsSlayer ? "tasks" : "times", Goal.Gold => "GP", _ => "" };
        // "sell [item] keep [n]", "a skill reaches [skill] [level n]", "the bank holds [n] [item]"
        bool targetFirst = sell || g == Goal.Level;
        _goalRow.MoveChild(_target.Button, targetFirst ? 2 : 4);

        if (game != null && sell)
        {
            string? cur = select ?? _target.SelectedId;
            _target.Placeholder = "Choose an item in the bank";
            _target.SetItems(BankPicks(game), cur);
        }
        else if (game != null && a != null && g == Goal.Level)
        {
            string? cur = select ?? _target.SelectedId;
            var skills = Catalog.LevelSkills(game, a.Entity)
                .Select(e => new PickItem(e.Id, e.DisplayName.ToString(), e.Icon, Note: $"level {Queue.Level(game, e.Id)}")).ToList();
            _target.Placeholder = "Choose a skill";
            _target.SetItems(skills, skills.Any(x => x.Id == cur) ? cur : skills.FirstOrDefault()?.Id);
            _target.Disabled = skills.Count <= 1;
        }
        else if (game != null && a != null && g is Goal.BankCount or Goal.MoreItems)
        {
            string? cur = select ?? _target.SelectedId;
            var picks = Catalog.Outputs(game, a)
                .Select(i => new PickItem(i.Id, i.DisplayName.ToString(), Catalog.ItemIcon(game, i.Id), Note: $"have {Queue.BankCount(game, i.Id):N0}")).ToList();
            // A step saved before this list existed may point at something else; keep it so editing does not lose it.
            // A new step starts from what the action makes instead of carrying over the last pick.
            if (_editing >= 0 && cur != null && picks.All(x => x.Id != cur) && Catalog.FindItem(game, cur) is { } old)
                picks.Add(new PickItem(old.Id, old.DisplayName.ToString(), old.Icon, true, "not made by this action"));
            _target.Placeholder = "Choose what it makes";
            _target.SetItems(picks, picks.Any(x => x.Id == cur) ? cur : picks.FirstOrDefault()?.Id);
        }
        if (!(g == Goal.Level && !sell)) _target.Disabled = false;

        bool combat = !sell && game != null && a != null && Gear.IsCombat(game, a.Entity);
        _gearRow.Visible = _combatNote.Visible = combat;
        bool belted = !sell && !combat && game != null && a != null && !_do.IsBest && Belts.HasToolbelt(game, a.Entity);
        _beltRow.Visible = belted;
        _goldRow.Visible = !sell && _do.IsGold;
        _fundRow.Visible = !sell && _do.IsBest && !_do.IsTech;
        if (belted) FillBelt(a!.Entity, _belt.ItemCount > 0 ? SelectedBelt : "");
        if (combat)
        {
            FillGear(game!, _gear.ItemCount > 0 ? SelectedGear : Gear.SkillDefault);
            FillWeapons(game!, _weapon.ItemCount > 0 ? SelectedWeapon : "");
            FillStances(game!, _stance.ItemCount > 0 ? SelectedStance : "");
            UpdateCombatNote(game!);
        }

        _add.Text = _editing >= 0 ? "Save changes" : "Add step";
        _cancelEdit.Visible = _editing >= 0;
        ActionRef? now = game == null ? null : Starter.Current(game);
        _now.Text = game == null ? "Load a character to pick actions." : "Your character is doing: " + (now?.Label ?? "nothing");
        UpdateSummary();
    }

    /// <summary>Starts the amount from where you are now. Skipped while a step is being loaded for editing.</summary>
    void Suggest()
    {
        Game? game = KitGame.Current;
        if (_loading || game == null) { UpdateSummary(); return; }
        Goal g = SelectedGoal;
        string? id = _target.SelectedId;
        if (_do.IsSell) { SetAmount(0, 10_000_000, 0); return; }
        switch (g)
        {
            case Goal.Level when id != null:
                int cur = Queue.Level(game, id), cap = Queue.LevelCap(game, id);
                SetAmount(Math.Min(cur + 1, cap), cap, Math.Min(cur + 1, cap));
                break;
            case Goal.BankCount when id != null:
                int have = Queue.BankCount(game, id);
                SetAmount(1, 10_000_000, have + 100);
                break;
            case Goal.MoreItems: SetAmount(1, 10_000_000, 100); break;
            case Goal.Times when _do.IsSlayer: SetAmount(1, 10_000, 5); break;
            case Goal.Times: SetAmount(1, 10_000_000, 100); break;
            case Goal.Minutes: SetAmount(1, 100_000, 60); break;
            case Goal.Gold: SetAmount(1, 2_000_000_000, (int)Math.Min(2_000_000_000, ShopSource.Gp(game) + 100_000)); break;
            case Goal.Technique when _do.SelectedTech is { } tech:
                int tl = tech.Experience.Level, tc = tech.Experience.LevelCap;
                SetAmount(Math.Min(tl + 1, tc), tc, Math.Min(tl + 1, tc));
                break;
            default: UpdateSummary(); break;
        }
    }

    void SetAmount(double min, double max, double value)
    {
        _amount.MinValue = min;
        _amount.MaxValue = max;
        _amount.Value = Math.Clamp(value, min, max);
        UpdateSummary();
    }

    List<PickItem> BankPicks(Game game)
    {
        var list = new List<PickItem>();
        ActionComponent? any = Catalog.AnyAction(game);
        if (any == null) return list;
        foreach (var st in any.Inventory.Stacks)
        {
            if (st.IsPlaceholder) continue;
            int n = any.Inventory.GetQuantity(st.Item);
            list.Add(new PickItem(st.Item.Id, st.Item.DisplayName.ToString(), st.Item.Icon, st.IsLocked, st.IsLocked ? "locked, never sold" : $"have {n:N0}"));
        }
        return list.DistinctBy(x => x.Id).OrderBy(x => x.Text).ToList();
    }

    /// <summary>Says in plain words what the step will do, and anything that will stop it working.</summary>
    void UpdateSummary()
    {
        if (_summary is null || _gearRow is null) return;   // still building the window
        Game? game = KitGame.Current;
        Step? s = game == null ? null : BuildStep();
        _add.Disabled = s == null;
        if (game == null) { _summary.Text = ""; return; }
        if (s == null)
        {
            _summary.Text = _do.IsSell ? "Choose an item to sell." : _do.Selected == null ? "Choose what to do." : "Choose " + (SelectedGoal == Goal.Level ? "a skill." : "an item.");
            _summary.AddThemeColorOverride("font_color", Kit.Dim);
            return;
        }
        var warn = new List<string>();
        string text = $"{Queue.Label(s)}, {s.GoalText()}";
        if (!s.IsSell && !s.IsBest && Finesse.RateNote(game, s.Action) is { Length: > 0 } rate) text += $" ({rate})";
        if (s.IsSell)
        {
            int have = Queue.BankCount(game, s.Item);
            text = $"Sells {Math.Max(0, have - s.Amount):N0} {s.ItemName} now and keeps {Math.Min(have, s.Amount):N0}.";
            if (_target.Selected?.Dim == true) warn.Add("That bank stack is locked, so nothing will be sold.");
        }
        else
        {
            var belt = _do.Selected is ActionRef sel ? Toolbelt.Missing(game, sel) : new List<Toolbelt.Need>();
            foreach (var n in belt)
            {
                string what = $"{n.Condition.Item.DisplayName} in the {n.Skill} toolbelt";
                if (n.Have > 0) text += $". Puts {what} first (you have {n.Have:N0})";
                else warn.Add($"Needs {what}, and there is none in the bank: get one first.");
            }
            // Locked only by a toolbelt item the step will put in place is not really locked.
            bool lockedByBeltOnly = belt.Count > 0 && belt.All(n => n.Have > 0) && _do.SelectedChoice?.Key is object key && Toolbelt.OnlyBelt(key);
            if (s.IsGold)
            {
                text += ". Picks what makes the most GP per hour " + s.Funding switch
                {
                    Funding.None => "from actions that use nothing from the bank",
                    Funding.Self => "counting the time to gather the ingredients, gathers them first and never touches your bank stock of them",
                    _ => "from what you can do with what is in the bank",
                } + ", and switches as you level" + (s.SellOutput ? "; sells what it makes" : "; keeps what it makes (sell it with Hold or by hand)");
                if (PlanData.Get(_queue.Folder, _queue.Log) == null) warn.Add("The planner's data is not loaded yet, so it cannot rank actions by GP.");
            }
            else if (s.IsBest && !s.IsTech && s.Funding != Funding.Bank)
            {
                text += s.Funding == Funding.None ? ". Only actions that use nothing from the bank"
                    : ". Ranks by XP per hour counting the time to gather the ingredients, gathers them first and never touches your bank stock of them";
                if (PlanData.Get(_queue.Folder, _queue.Log) == null) warn.Add("The planner's data is not loaded yet, so it cannot rank actions.");
            }
            if (s.IsBest && !s.IsTech) text += TopPicks(game, s);
            if (_do.SelectedChoice?.LockedNow == true && !lockedByBeltOnly)
                warn.Add("You cannot do this yet. The step will wait for it; put a step that trains the skill before it.");
            if (s.Goal == Goal.Level && Queue.Level(game, s.Skill) >= s.Amount)
                warn.Add($"{s.SkillName} is already level {Queue.Level(game, s.Skill)}, so this step would be skipped.");
            if (s.Goal == Goal.BankCount && Queue.BankCount(game, s.Item) >= s.Amount)
                warn.Add($"The bank already holds {Queue.BankCount(game, s.Item):N0} {s.ItemName}, so this step would be skipped.");
            if (s.Goal == Goal.BankCount) text += $" (have {Queue.BankCount(game, s.Item):N0})";
            if (_beltRow.Visible && _queue.SetOf(s) is BeltSet bset)
            {
                text += $". Puts on toolbelt set {bset.Name}";
                var refills = bset.Slots.Where(b => b.Refill > 0).Select(b => $"{Catalog.FindItem(game, b.Item)?.DisplayName} up to {b.Refill:N0}").ToList();
                if (refills.Count > 0) text += $" (refills {string.Join(", ", refills)} when they run out)";
                if (!_queue.State.AutoGear) warn.Add("Gear and toolbelt swapping is off in Options, so the set will not be put on.");
            }
            if (_gearRow.Visible)
            {
                string? g = Gear.Wanted(game, _queue.State, s);
                if (!_queue.State.AutoGear) text += ". Loadout swapping is off in Options, so gear stays as it is";
                else if (g == null) text += ". Gear stays as it is";
                else
                {
                    Gear.Choice? c = Gear.Loadouts(game).FirstOrDefault(l => l.Id == g);
                    text += $". Wears {c?.Name ?? g}";
                    if (c is { Unlocked: false }) warn.Add($"Loadout {c.Name} is locked, so gear will not change.");
                }
                if (_queue.State.AutoGear)
                {
                    if (s.Weapon == CombatSetup.Best) text += ", with the best weapon and stance you own for it";
                    else if (s.Weapon.Length > 0) text += $", holding {_weapon.GetItemText(_weapon.Selected)}";
                    if (s.Stance.Length > 0 && s.Weapon != CombatSetup.Best) text += $", {_stance.GetItemText(_stance.Selected)} stance";
                }
            }
            text += ".";
        }
        _summary.Text = warn.Count == 0 ? text : text + "  " + string.Join("  ", warn);
        _summary.AddThemeColorOverride("font_color", warn.Count == 0 ? Kit.Good : Kit.Gold);
    }

    Step? BuildStep()
    {
        if (_do.IsSell)
        {
            PickItem? item = _target.Selected;
            if (item == null) return null;
            return new Step
            {
                Action = new ActionRef { Entity = Starter.SellEntity, Label = $"Sell from bank: {item.Text}" },
                Goal = Goal.BankCount, Amount = (int)_amount.Value, Item = item.Id, ItemName = item.Text,
            };
        }
        ActionRef? a = _do.Selected;
        if (a == null || _goal.ItemCount == 0) return null;
        bool fight = _gearRow.Visible;
        var s = new Step
        {
            Action = a, Goal = SelectedGoal, Amount = (int)_amount.Value,
            Gear = fight ? SelectedGear : Gear.SkillDefault, Weapon = fight ? SelectedWeapon : "", Stance = fight ? SelectedStance : "",
            Belt = _beltRow.Visible ? SelectedBelt : "",
            SellOutput = !_goldRow.Visible || _sellOut.ButtonPressed,
            Funding = _fundRow.Visible ? (Funding)Math.Max(0, _funding.Selected) : Funding.Bank,
        };
        if (s.Goal is Goal.Level or Goal.BankCount or Goal.MoreItems)
        {
            PickItem? t = _target.Selected;
            if (t == null) return null;
            if (s.Goal == Goal.Level) { s.Skill = t.Id; s.SkillName = t.Text; }
            else { s.Item = t.Id; s.ItemName = t.Text; }
        }
        if (s.Goal == Goal.Technique)
        {
            if (_do.SelectedTech is not { } tech) return null;
            s.Item = tech.Technique.Id;
            s.ItemName = tech.Technique.DisplayName.ToString();
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
        UpdateSummary();
    }

    void Edit(int i)
    {
        Game? game = KitGame.Current;
        if (game == null || i < 0 || i >= _queue.State.Steps.Count) return;
        Step s = _queue.State.Steps[i];
        if (s.IsShop) { _stepHint.Text = "Shop steps can't be edited: remove it and plan the item again."; return; }
        _stepHint.Text = "drag rows to reorder, double-click to edit";
        _editing = i;
        _tabPinned = true;
        ShowTab(0);
        _loading = true;
        try
        {
            _do.Fill(game);
            _do.Show(s.Action);
            if (!s.IsSell) FillGoals(game, s.Goal);
            FillGear(game, s.Gear);
            FillWeapons(game, s.Weapon);
            FillStances(game, s.Stance);
            if (!s.IsSell) FillBelt(s.Action.Entity, s.Belt);
            _sellOut.ButtonPressed = s.SellOutput;
            _funding.Select((int)s.Funding);
            RefreshTargets(s.Goal == Goal.Level ? s.Skill : s.Item);
            _amount.MinValue = 0;
            _amount.MaxValue = 10_000_000;
            _amount.Value = s.Amount;
        }
        finally { _loading = false; }
        RefreshTargets();
    }

    void StopEditing()
    {
        _editing = -1;
        RefreshTargets();
    }

    // ---- plan an item ----

    void FillPlanItems()
    {
        Game? game = KitGame.Current;
        PlanData? data = PlanData.Get(_queue.Folder, _queue.Log);
        if (game == null) return;
        if (data == null) { _planNote.Text = $"Planner data: {PlanData.State}. It comes from {PlanData.Url} and is kept for offline use."; return; }
        // Every item: made by a skill, dropped by a monster, from Farming or Ranching, or (dimmed) none of those.
        var makeable = new HashSet<string>(data.Rates.SelectMany(r => r.Out.Select(o => o.Item)));
        var all = Catalog.Items(game);
        var picks = data.Items.OrderBy(kv => kv.Value)
            .Select(kv =>
            {
                string? full = all.FirstOrDefault(i => Catalog.Local(i.Id) == kv.Key).Id;
                string? note = makeable.Contains(kv.Key) ? null
                    : Catalog.MonsterFor(game, kv.Key) != null ? "monster drop"
                    : full != null && Catalog.PassiveSource(game, full) is string p ? p.Replace("the ", "")
                    : "quest, shop or chest only";
                return new PickItem(kv.Key, kv.Value, full == null ? null : Catalog.ItemIcon(game, full), note == "quest, shop or chest only", note ?? "");
            }).ToList();
        _planItem.SetItems(picks);
        MakePlan();
    }

    void MakePlan()
    {
        _planTable.Clear();
        _plan = new();
        _planAdd.Disabled = true;
        Game? game = KitGame.Current;
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
        bool bring = false, passive = false, shopProblem = false;
        foreach (Step train in steps.Where(s => s.IsBest && s.Goal == Goal.Level))
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
            if (l.Gold || l.Shop.Length > 0)
            {
                // Earn GP for the shop, then buy: both are queue steps.
                row.SetText(0, l.Gold ? $"{++n}. Most GP: any skill (for the shop)" : $"{++n}. {l.Action!.Label} ({l.Cost})");
                row.SetIcon(0, Kit.Icon(l.Gold ? null : Catalog.ItemIcon(game, Planner.FullIdOf(l))));
                row.SetText(1, l.Gold ? $"{l.Target:N0} GP" : $"{l.Target:N0} {l.ItemName}");
                row.SetText(2, l.Have.ToString("N0"));
                row.SetText(3, l.Gold ? (l.Have >= l.Target ? "ready" : "until you have it") : "instant");
                if (l.Problem.Length > 0) { for (int c = 0; c < 4; c++) row.SetCustomColor(c, Kit.Gold); row.SetTooltipText(0, l.Problem); shopProblem = true; }
                continue;
            }
            string? from = l.Rate == null && !l.Drop ? Catalog.PassiveSource(game, l.Item) : null;
            if (l.Rate == null && from == null && !l.Drop) bring = true;
            if (from != null) passive = true;
            string what = from != null ? $"From {from} (passive): {l.ItemName}"
                : l.Drop && l.Action != null ? $"{++n}. {l.Action.Label} (drops it)"
                : l.Rate == null ? $"Bring {l.ItemName} yourself" : $"{++n}. {l.Rate.Skill}: {l.Rate.Name}";
            row.SetText(0, l.Rate != null && l.LevelNeeded > 0 ? $"{what}  (after training to {l.LevelNeeded})" : what);
            if (l.Action != null) row.SetIcon(0, Kit.Icon(Catalog.ActionIcon(game, l.Action)));
            else if (l.Rate == null) row.SetIcon(0, Kit.Icon(Catalog.ItemIcon(game, l.Item)));
            row.SetText(1, $"{l.Target:N0} {l.ItemName}");
            row.SetText(2, l.Have.ToString("N0"));
            row.SetText(3, from != null ? (l.Have >= l.Target ? "ready" : "while you play") : l.Drop ? "depends on luck" : Duration(l.Hours));
            if (from != null) for (int c = 0; c < 4; c++) row.SetCustomColor(c, Kit.Link);
            else if (l.Drop) { if (l.Problem.Length > 0) for (int c = 0; c < 4; c++) row.SetCustomColor(c, Kit.Gold); }
            else if (l.Rate == null || (l.Action == null && l.LevelNeeded == 0)) for (int c = 0; c < 4; c++) row.SetCustomColor(c, Kit.Gold);
            hours += l.Hours;
        }
        string mode = more ? $"To get {amount:N0} more" : $"To have {amount:N0}";
        _planNote.Text = _plan.Count == 0
            ? "You already have enough."
            : $"{mode}: {steps.Count} step{(steps.Count == 1 ? "" : "s")}, about {Duration(hours)} of actions (leveling time not included)."
              + (passive ? " Blue lines come from Farming, Ranching or the mill, which run alongside the queue; they are not queue steps." : "")
              + (bring ? " Yellow lines are items no action makes; bring them yourself." : "")
              + (shopProblem ? " A yellow shop line can't buy yet: hover it to see why (a lock, or a currency the queue can't earn for you)." : "")
              + $" Using {PlanData.Source}." + (PlanData.Source.StartsWith("your") ? "" : " Run Astrolabe Tools to plan with your own rates.");
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
        Game? game = KitGame.Current;
        if (game == null) return;
        var (steps, problems) = ShareCode.Import(game, DisplayServer.ClipboardGet());
        if (steps.Count > 0) _queue.AddRange(steps);
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
        RefreshGearGrid();
    }

    // ---- gear ----

    string SelectedGear => _gear.Selected < 0 ? Gear.SkillDefault : _gear.GetItemMetadata(_gear.Selected).AsString();

    void FillGear(Game game, string select)
    {
        _gear.Clear();
        void Add(string text, string id) { _gear.AddItem(text); _gear.SetItemMetadata(_gear.ItemCount - 1, id); }
        List<Gear.Choice> loadouts = Gear.Loadouts(game);
        string? def = Gear.CombatId(game) is string cid && _queue.State.SkillGear.TryGetValue(cid, out string? d) ? d : null;
        string defName = def == null ? "leave as is" : loadouts.FirstOrDefault(l => l.Id == def)?.Name ?? "leave as is";
        Add($"combat default ({defName})", Gear.SkillDefault);
        Add("leave as is", Gear.Keep);
        foreach (Gear.Choice l in loadouts)
        {
            Add(l.Unlocked ? l.Name : l.Name + " (locked)", l.Id);
            _gear.SetItemDisabled(_gear.ItemCount - 1, !l.Unlocked);
        }
        int at = 0;
        for (int i = 0; i < _gear.ItemCount; i++) if (_gear.GetItemMetadata(i).AsString() == select && !_gear.IsItemDisabled(i)) at = i;
        _gear.Select(at);
        _gear.Disabled = !_queue.State.AutoGear;
        _gear.TooltipText = _queue.State.AutoGear
            ? "Which combat loadout to wear for this step. Set up and name loadouts in the game's equipment screen."
            : "Loadout swapping is off. Turn it on under Options.";
    }

    string SelectedWeapon => _weapon.Selected < 0 ? "" : _weapon.GetItemMetadata(_weapon.Selected).AsString();
    string SelectedStance => _stance.Selected < 0 ? "" : _stance.GetItemMetadata(_stance.Selected).AsString();

    /// <summary>The loadout this step would wear, as a loadout id ("" = whatever is on now).</summary>
    string LoadoutInEffect(Game game)
    {
        string g = SelectedGear;
        if (!_queue.State.AutoGear || g == Gear.Keep) return "";
        if (g == Gear.SkillDefault)
            return Gear.CombatId(game) is string cid && _queue.State.SkillGear.TryGetValue(cid, out string? d) ? d : "";
        return g;
    }

    void FillWeapons(Game game, string select)
    {
        _weapon.Clear();
        void Add(string text, string id, bool off = false)
        {
            _weapon.AddItem(text);
            _weapon.SetItemMetadata(_weapon.ItemCount - 1, id);
            _weapon.SetItemDisabled(_weapon.ItemCount - 1, off);
        }
        CombatSetup.LoadoutWeapon(game, LoadoutInEffect(game), out string own);
        Add($"loadout's own ({own})", "");
        Add("best I own against it", CombatSetup.Best);
        foreach (CombatSetup.Weapon w in CombatSetup.Owned(game))
            Add($"{w.Item.DisplayName} ({(w.Usable ? w.Stances : w.Why)})", w.Item.Id, !w.Usable);
        int at = 0;
        for (int i = 0; i < _weapon.ItemCount; i++) if (_weapon.GetItemMetadata(i).AsString() == select && !_weapon.IsItemDisabled(i)) at = i;
        _weapon.Select(at);
        _weapon.Disabled = !_queue.State.AutoGear;
    }

    void FillStances(Game game, string select)
    {
        _stance.Clear();
        void Add(string text, string id) { _stance.AddItem(text); _stance.SetItemMetadata(_stance.ItemCount - 1, id); }
        string w = SelectedWeapon;
        if (w == CombatSetup.Best)
        {
            Add("picked with the weapon", "");
            _stance.Select(0);
            _stance.Disabled = true;
            return;
        }
        Add("leave as is", "");
        Add("best for this weapon", CombatSetup.Best);
        var type = w.Length > 0
            ? CombatSetup.Owned(game).FirstOrDefault(x => x.Item.Id == w)?.W.Type
            : CombatSetup.LoadoutWeapon(game, LoadoutInEffect(game), out _)?.Type;
        if (type != null) foreach (var st in type.Stances) Add(st.DisplayName.ToString(), st.Id);
        int at = 0;
        for (int i = 0; i < _stance.ItemCount; i++) if (_stance.GetItemMetadata(i).AsString() == select) at = i;
        _stance.Select(at);
        _stance.Disabled = !_queue.State.AutoGear;
    }

    /// <summary>After the loadout or weapon changes: the other lists depend on it.</summary>
    void RefreshCombat()
    {
        Game? game = KitGame.Current;
        if (game == null || !_gearRow.Visible) { UpdateSummary(); return; }
        FillWeapons(game, SelectedWeapon);
        FillStances(game, SelectedStance);
        UpdateCombatNote(game);
        UpdateSummary();
    }

    /// <summary>The monster's defence per stance and the best weapon and stance you own for it.</summary>
    void UpdateCombatNote(Game game)
    {
        ActionRef? a = _do.Selected;
        if (a?.Target == Slayer.Target)
        {
            _combatNote.Text = Slayer.Summary(game) + " The step fights the highest master's task it can, moves on when one is done, and follows the Slayer tab's reroll and extend rules. Weapon and stance are picked per monster.";
            return;
        }
        var enemies = a == null ? new List<GamesByMalcs.Melvor2.Entities.Combat.Enemy>() : CombatSetup.Enemies(game, a);
        if (enemies.Count == 0) { _combatNote.Text = ""; return; }
        string text = CombatSetup.DefenceText(game, enemies);
        string task = Slayer.TaskNote(game, enemies);
        if (task.Length > 0) text = task + " " + text;
        var lw = CombatSetup.LoadoutWeapon(game, LoadoutInEffect(game), out string own);
        if (lw != null)
        {
            var style = lw.Type.Stances[0].CombatStyle;
            var best = CombatSetup.BestWeapon(game, enemies, style);
            if (best?.Weapon != null)
                text += $" Best {style.DisplayName} with your current gear: {best.Weapon.Item.DisplayName}"
                    + (best.Offhand != null ? $" with {best.Offhand.DisplayName}" : "")
                    + $", {best.Stance.DisplayName} stance, about {best.Dps:0.#} damage a second.";
            else if (style.MaxHitFormula is not GamesByMalcs.Melvor2.Entities.Combat.StrengthBonusMaxHitFormula)
                text += " Magic damage comes from the spell, so weapons are not ranked for it.";
        }
        _combatNote.Text = text;
    }

    void RefreshGearGrid()
    {
        foreach (Node n in _gearGrid.GetChildren()) { _gearGrid.RemoveChild(n); n.QueueFree(); }
        Game? game = KitGame.Current;
        if (game == null) return;
        List<Gear.Choice> loadouts = Gear.Loadouts(game);
        string? combatId = Gear.CombatId(game);
        _gearNote.Text = loadouts.Count == 0
            ? "This character has no loadouts yet."
            : "Combat steps wear this unless the step picks its own loadout (Melee for one monster, Magic for the next). Skills keep their own toolbelts, so loadouts only matter for combat.";
        foreach (SkillChoice sk in Catalog.Skills(game).Where(k => k.Id == combatId))
        {
            var name = Kit.Label(sk.Name, null, 13);
            var pick = new OptionButton { CustomMinimumSize = new Vector2(190, 0) };
            pick.AddItem("leave as is");
            pick.SetItemMetadata(0, "");
            foreach (Gear.Choice l in loadouts)
            {
                pick.AddItem(l.Unlocked ? l.Name : l.Name + " (locked)");
                pick.SetItemMetadata(pick.ItemCount - 1, l.Id);
                pick.SetItemDisabled(pick.ItemCount - 1, !l.Unlocked);
            }
            pick.Disabled = !_queue.State.AutoGear;
            string cur = _queue.State.SkillGear.TryGetValue(sk.Id, out string? id) ? id : "";
            for (int i = 0; i < pick.ItemCount; i++) if (pick.GetItemMetadata(i).AsString() == cur) pick.Select(i);
            string skill = sk.Id;
            pick.ItemSelected += i =>
            {
                string chosen = pick.GetItemMetadata((int)i).AsString();
                _queue.SetOption(st => { if (chosen.Length == 0) st.SkillGear.Remove(skill); else st.SkillGear[skill] = chosen; });
            };
            if (sk.Icon != null) _gearGrid.AddChild(new TextureRect { Texture = Kit.Icon(sk.Icon), StretchMode = TextureRect.StretchModeEnum.KeepCentered });
            else _gearGrid.AddChild(new Control());
            _gearGrid.AddChild(name);
            _gearGrid.AddChild(pick);
            _gearGrid.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
        }
    }

    // ---- the table ----

    // Ticked rows if any, else the selected one.
    List<int> Rows() => _ticked.Count > 0 ? _ticked.Where(i => i < _queue.State.Steps.Count).OrderBy(i => i).ToList()
        : _selected >= 0 && _selected < _queue.State.Steps.Count ? new List<int> { _selected } : new List<int>();

    void MoveRows(int by)
    {
        var rows = Rows();
        if (rows.Count == 0) return;
        bool ticked = _ticked.Count > 0;
        var moved = _queue.MoveMany(rows, by);
        AfterMove(moved, ticked);
    }

    void AfterMove(List<int> moved, bool ticked)
    {
        if (moved.Count == 0) return;
        _selected = moved[0];
        _ticked = ticked ? moved.ToHashSet() : new();
        Rebuild();
    }

    void RemoveRows()
    {
        var rows = Rows();
        if (rows.Count == 0) return;
        _selected = -1;
        _ticked.Clear();
        _queue.RemoveMany(rows);
    }

    void ToggleTicks()
    {
        int n = _queue.State.Steps.Count;
        _ticked = _ticked.Count == n ? new() : Enumerable.Range(0, n).ToHashSet();
        Rebuild();
    }

    void UpdateTickHint() =>
        _stepHint.Text = _ticked.Count > 0 ? $"{_ticked.Count} ticked: Up, Down, Remove and dragging move them together" : "tick steps to move or remove several; drag to reorder; double-click to edit";

    void SetUpDragging()
    {
        _table.SetDragForwarding(
            Callable.From((Vector2 at) =>
            {
                TreeItem? item = _table.GetItemAtPosition(at);
                if (item == null || item.GetMetadata(0).VariantType == Variant.Type.Nil) return default(Variant);
                int i = item.GetMetadata(0).AsInt32();
                // A ticked row drags every ticked row with it.
                int[] rows = _ticked.Contains(i) ? _ticked.OrderBy(x => x).ToArray() : new[] { i };
                _table.SetDragPreview(Kit.Label(rows.Length > 1 ? $"{rows.Length} steps" : item.GetText(1), Kit.Text));
                _table.DropModeFlags = (int)Tree.DropModeFlagsEnum.Inbetween;
                return Variant.From(rows);
            }),
            Callable.From((Vector2 at, Variant data) => data.VariantType == Variant.Type.PackedInt32Array),
            Callable.From((Vector2 at, Variant data) =>
            {
                _table.DropModeFlags = 0;
                int[] rows = data.AsInt32Array();
                if (rows.Length == 0) return;
                TreeItem? item = _table.GetItemAtPosition(at);
                // Where the drop lands in the list, then without the moved rows above it.
                int to = _queue.State.Steps.Count;
                if (item != null && item.GetMetadata(0).VariantType != Variant.Type.Nil)
                    to = item.GetMetadata(0).AsInt32() + (_table.GetDropSectionAtPosition(at) > 0 ? 1 : 0);
                to -= rows.Count(r => r < to);
                bool ticked = rows.Length > 1 || _ticked.Contains(rows[0]);
                AfterMove(_queue.MoveManyTo(rows, to), ticked);
            }));
    }

    void Rebuild()
    {
        var st = _queue.State;
        _run.Text = st.Running ? "Pause" : "Start";
        _undo.Disabled = _queue.UndoWhat.Length == 0;
        _undo.TooltipText = _queue.UndoWhat.Length == 0 ? "Nothing to undo" : "Undo: " + _queue.UndoWhat;
        RefreshQueuePick();
        if (st.Running && !_tabPinned && _panels.Any(p => p.Visible)) ShowTab(-1);
        if (_tab == 3 && _panels[3].Visible) RefreshHistory();

        // Rows are updated in place (Kit.Rows), so the list keeps its scroll position while the queue runs.
        TreeItem[] rows = Kit.Rows(_table, Math.Max(1, st.Steps.Count));
        Game? game = KitGame.Current;
        var current = new HashSet<Step>(st.Running ? _queue.CurrentSteps() : Enumerable.Empty<Step>());
        for (int i = 0; i < st.Steps.Count; i++)
        {
            Step s = st.Steps[i];
            TreeItem row = rows[i];
            row.SetMetadata(0, i);
            row.SetCellMode(0, TreeItem.TreeCellMode.Check);
            row.SetEditable(0, true);
            row.SetChecked(0, _ticked.Contains(i));
            row.SetText(0, (i + 1).ToString());
            if (game != null)
            {
                Texture2D? icon = (s.IsSell || s.IsShop) && s.Item.Length > 0 ? Catalog.ItemIcon(game, s.Item) : Catalog.ActionIcon(game, s.Effective);
                row.SetIcon(1, Kit.Icon(icon));
            }
            string extra = DoExtra(game, s);
            row.SetText(1, Queue.Label(s) + extra);
            if (extra.Length > 0) row.SetTooltipText(1, "Set up when this step starts:" + extra.TrimStart(','));
            row.SetText(2, s.GoalText());
            string state = StateText(game, s, current.Contains(s));
            row.SetText(3, state);
            row.SetTooltipText(3, state);
            row.SetCustomColor(0, Kit.Dim);
            row.SetCustomColor(3, StateColor(s));
            if (s.Done) { row.SetCustomColor(1, Kit.Dim); row.SetCustomColor(2, Kit.Dim); }
            if (current.Contains(s)) for (int col = 0; col < 4; col++) row.SetCustomBgColor(col, s_currentRow);
            if (i == _selected) row.Select(0); else row.Deselect(0);
        }
        _ticked.RemoveWhere(i => i >= st.Steps.Count);
        UpdateTickHint();
        if (st.Steps.Count == 0)
        {
            TreeItem hint = rows[0];
            hint.SetCellMode(0, TreeItem.TreeCellMode.String);
            hint.SetEditable(0, false);
            hint.SetMetadata(0, default(Variant));
            for (int col = 0; col < 4; col++) hint.SetText(col, "");
            hint.SetText(1, game == null ? "Load a character to pick actions." : "No steps yet. Add a step or plan an item above.");
            hint.SetCustomColor(1, Kit.Dim);
            for (int col = 0; col < 4; col++) hint.SetSelectable(col, false);
        }
        UpdateStatus();
    }

    /// <summary>For combat steps: the loadout, weapon and stance the step sets up.</summary>
    string DoExtra(Game? game, Step s)
    {
        var st = _queue.State;
        if (game == null || !st.AutoGear) return "";
        if (_queue.SetOf(s) is BeltSet bs) return $", toolbelt {bs.Name}";
        if (!Gear.IsCombat(game, s.Action.Entity)) return "";
        string? g = Gear.Wanted(game, st, s);
        return (g != null ? $", wearing {Gear.Name(game, g)}" : "") + CombatText(game, s);
    }

    static Button WithTip(Button b, string tip) { b.TooltipText = tip; return b; }

    string SelectedBelt => _belt.Selected < 0 ? "" : _belt.GetItemMetadata(_belt.Selected).AsString();

    void FillBelt(string skill, string select)
    {
        _belt.Clear();
        _belt.AddItem("leave the toolbelt as it is");
        _belt.SetItemMetadata(0, "");
        foreach (BeltSet b in _queue.State.Belts.Where(b => b.Skill == skill))
        {
            _belt.AddItem(b.Name + (b.Slots.Any(x => x.Refill > 0) ? " (refills)" : ""));
            _belt.SetItemMetadata(_belt.ItemCount - 1, b.Id);
            if (b.Id == select) _belt.Select(_belt.ItemCount - 1);
        }
        if (_belt.Selected < 0) _belt.Select(0);
        _belt.Disabled = _belt.ItemCount == 1;
        _belt.TooltipText = _belt.ItemCount == 1
            ? "No toolbelt sets for this skill yet. Make one in the Toolbelts tab."
            : "A toolbelt set to put on when this step starts. Make sets in the Toolbelts tab.";
    }

    static string CombatText(Game game, Step s)
    {
        string w = s.Weapon == CombatSetup.Best ? ", best weapon" : s.Weapon.Length > 0 ? ", " + (Catalog.FindItem(game, s.Weapon)?.DisplayName.ToString() ?? "a weapon") : "";
        string st = s.Stance == CombatSetup.Best ? ", best stance"
            : s.Stance.Length > 0 ? ", " + (CombatSetup.AllStances(game).FirstOrDefault(x => x.Id == s.Stance)?.DisplayName.ToString() ?? s.Stance) : "";
        return s.Weapon == CombatSetup.Best ? w + " and stance" : w + st;
    }

    /// <summary>What Most GP or Fastest XP would pick now, best first, with its rate: so you can see why.</summary>
    static string TopPicks(Game game, Step s)
    {
        if (PlanData.Loaded == null) return "";
        var picks = s.IsGold
            ? Starter.GoldScored(game, s.Action.Entity, s.Funding).Take(5).Select(x => (x.Ref, Value: x.Gp, Unit: "GP")).ToList()
            : Starter.BestScored(game, s.Action.Entity, s.Funding).Take(5).Select(x => (x.Ref, Value: x.Xp, Unit: "XP")).ToList();
        if (picks.Count == 0) return ". Nothing fits right now";
        return ". Top picks now: " + string.Join(", ", picks.Select((x, i) =>
            $"{i + 1}. {x.Ref.Label}" + (double.IsNaN(x.Value) ? "" : $" ({x.Value:N0} {x.Unit}/h)")));
    }

    string StateText(Game? game, Step s, bool current)
    {
        string progress = _queue.Progress(game, s);
        string state = s.PassedOver ? (s.PassedAfterStart ? "stopped early (queue started below)" : "not run (queue started below)") : s.State.Length > 0 ? s.State : s.Done ? "done" : current ? "starting" : "";
        return state.Length == 0 ? progress : progress.Length == 0 ? state : $"{state}  {progress}";
    }

    static Color StateColor(Step s) =>
        s.State.StartsWith("running") ? Kit.Good : s.State.StartsWith("waiting") ? Kit.Gold : s.State == "skipped" ? Kit.Bad : s.Done ? Kit.Dim : Kit.Text;

    void UpdateStatus()
    {
        var st = _queue.State;
        int done = st.Steps.Count(x => x.Done && !x.PassedOver);
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

        Game? game = KitGame.Current;
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
                row.SetText(1, Queue.Label(s) + DoExtra(game, s));
                string state = StateText(game, s, current.Contains(s));
                row.SetText(3, state);
                row.SetTooltipText(3, state);
                row.SetCustomColor(3, StateColor(s));
            }
            row = row.GetNext();
        }
        UpdateStatus();
    }
}

/// <summary>Skill dropdown (with icons) and a searchable action list, filled from the running game.</summary>
sealed class ActionPicker
{
    const string SellId = "*sell", NoneId = "*none", AnyId = Starter.AnyEntity;
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
    public bool IsBest => IsAny || IsTech || ActionPick.SelectedId == Starter.BestTarget || ActionPick.SelectedId == Starter.GoldTarget;
    public bool IsTech => ActionPick.SelectedId?.StartsWith(Finesse.TechPrefix) == true;
    public ActiveAgilityTechnique? SelectedTech =>
        IsTech && KitGame.Current is { } g ? Finesse.Technique(g, ActionPick.SelectedId![Finesse.TechPrefix.Length..]) : null;
    public bool IsGold => IsAny || ActionPick.SelectedId == Starter.GoldTarget;
    public bool IsAny => SkillId == AnyId;
    public bool IsSlayer => ActionPick.SelectedId == Slayer.Target;
    public bool IsEmpty => _skills.Count == 0;

    public ActionChoice? SelectedChoice => Skill?.Actions.FirstOrDefault(a => Key(a.Ref) == ActionPick.SelectedId);

    public ActionRef? Selected
    {
        get
        {
            if (IsAny) return new ActionRef { Entity = Starter.AnyEntity, Target = Starter.GoldTarget, Label = "Most GP: any skill" };
            SkillChoice? s = Skill;
            if (s == null) return null;
            if (IsGold) return new ActionRef { Entity = s.Id, Target = Starter.GoldTarget, Label = $"Most GP: {s.Name}" };
            if (SelectedTech is { } tech) return Finesse.TechRef(s.Id, tech);
            if (IsBest) return new ActionRef { Entity = s.Id, Target = Starter.BestTarget, Label = $"Train {s.Name}" };
            if (IsSlayer) return new ActionRef { Entity = s.Id, Target = Slayer.Target, Label = "Slayer tasks" };
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
        if (_allowBest) { SkillBox.AddItem("Any skill (most GP)"); _skillIds.Add(AnyId); }
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
        ActionPick.Visible = !IsSell && SkillId != NoneId && !IsAny;
        if (s == null) { ActionPick.SetItems(Enumerable.Empty<PickItem>()); return; }
        var picks = new List<PickItem>();
        if (_allowBest) picks.Add(new PickItem(Starter.BestTarget, "Fastest XP (switches as you level)", s.Icon, Note: "train"));
        if (_allowBest && !Gear.IsCombat(KitGame.Current!, s.Id)) picks.Add(new PickItem(Starter.GoldTarget, "Most GP (switches as you level)", s.Icon, Note: "gold"));
        if (_allowBest && Gear.IsCombat(KitGame.Current!, s.Id)) picks.Add(new PickItem(Slayer.Target, "Slayer tasks (follows your tasks)", s.Icon, Note: "slayer"));
        if (_allowBest && Finesse.Agility(KitGame.Current!)?.Entity.Id == s.Id)
            foreach (var t in Finesse.Techniques(KitGame.Current!))
                picks.Add(new PickItem(Finesse.TechPrefix + t.Technique.Id, $"Train {t.Technique.DisplayName} technique (level {t.Experience.Level}/{t.Experience.LevelCap})", s.Icon, Note: "technique"));
        picks.AddRange(s.Actions.Select(a => new PickItem(Key(a.Ref), a.Name, a.Icon, a.LockedNow, a.LockedNow ? (a.Key != null && Catalog.QuestOnly(a.Key) ? "needs its quest" : "locked") : "")));
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
        ActionPick.Select(r.Target is Starter.BestTarget or Starter.GoldTarget or Slayer.Target || r.Target.StartsWith(Finesse.TechPrefix) ? r.Target : Key(r));
    }

    public void UseCurrent()
    {
        Game? game = KitGame.Current;
        if (game == null) return;
        Fill(game);
        Show(Starter.Current(game));
        Changed?.Invoke();
    }
}
