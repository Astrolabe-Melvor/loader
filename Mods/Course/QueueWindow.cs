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
sealed partial class QueueWindow
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
    readonly HBoxContainer _beltRow, _goldRow, _fundRow, _rewardRow;
    readonly CheckBox _sellOut, _reward;
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
    HBoxContainer _addRow = null!;
    OptionButton _where = null!;
    Label _rewardHint = null!, _beltNone = null!;
    Button _beltEdit = null!, _beltMake = null!;
    int _editing = -1;
    bool _loading;   // filling the form from a step: keep its values instead of suggesting new ones

    // plan an item
    readonly OptionButton _planMode;
    readonly SpinBox _planAmount;
    readonly SearchPicker _planItem;
    readonly Tree _planTable;
    readonly Label _planNote;
    Label _planTotal = null!, _planKey = null!;
    OptionButton _planFrom = null!, _planWhere = null!;
    readonly Button _planAdd;
    Planner? _planner;
    List<PlanLine> _plan = new();


    // history and options
    readonly Tree _history;
    OptionButton _historyShow = null!, _historyKeep = null!;
    Button _historyClear = null!, _historySeeAll = null!, _historyFlags = null!;
    PopupMenu? _clearMenu, _flagMenu;
    readonly List<HistoryEntry> _historyRows = new();
    (int Count, DateTime Last, int Seen, int Flags) _badgeFor = (-1, default, 0, 0);
    readonly Dictionary<string, bool> _historyOpen = new();
    (int Count, DateTime Last, int Show) _historyShown;
    static readonly int[] s_historyKeep = { 500, 2000, 10000 };
    CheckBox _optGear = null!, _optGauntlet = null!, _optFinesseFood = null!;
    readonly OptionButton _gear, _weapon, _stance;
    readonly HBoxContainer _gearRow;
    readonly Label _combatNote;
    GridContainer _gearGrid = null!;
    Label _gearNote = null!;

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
        _queuePick.ItemSelected += OnQueuePicked;
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
            _tabs[i] = Kit.Btn(s_tabNames[i], () => { _tabPinned = true; ShowTab(at == _tab && _panels[at].Visible ? -1 : at); });
            _tabs[i].ThemeTypeVariation = "Tab";
            _tabs[i].ToggleMode = true;
            tabRow.AddChild(_tabs[i]);
        }
        tabRow.MoveChild(_tabs[5], 3);   // Toolbelts and Slayer sit before History
        tabRow.MoveChild(_tabs[6], 4);
        _foldTabs = WithTip(Kit.Btn("Fold", () => { _tabPinned = true; ShowTab(-1); }), "Fold this tab away to see more of the list. Click any tab to open it again (clicking the open tab folds it too).");
        tabRow.AddChild(Kit.Spacer());
        tabRow.AddChild(_foldTabs);
        _win.Body.AddChild(tabRow);

        // ---- add a step ----
        var addBox = Panel(0);
        _do = new ActionPicker(allowSell: true, allowBest: true);
        _do.Changed += OnActionPicked;
        var copyNow = Kit.Btn("Copy what I'm doing", () => _do.UseCurrent());
        copyNow.TooltipText = "Fill in the action your character is doing right now";
        HBoxContainer doRow = Kit.Row(Field("Do"), _do.SkillBox, _do.ActionPick.Button, copyNow);
        addBox.AddChild(doRow);

        _untilLabel = Field("Until");
        _goal = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        _goal.ItemSelected += _ => { RefreshTargets(); Suggest(); };
        _keep = Kit.Label("keep", null, 13, "Dim");
        _amount = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = 100, CustomMinimumSize = new Vector2(130, 0) });
        _amount.ValueChanged += _ => UpdateSummary();
        _target = new SearchPicker(300) { Placeholder = "Choose an item" };
        _target.Button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _target.Picked += _ => { Suggest(); };
        _add = Kit.Btn("Add step", AddStep, action: true);
        _add.CustomMinimumSize = new Vector2(150, 0);
        _cancelEdit = Kit.Btn("Cancel", StopEditing);
        _cancelEdit.Visible = false;
        _goalRow = Kit.Row(_untilLabel, _goal, _keep, _amount, _target.Button);
        addBox.AddChild(_goalRow);
        _gear = new OptionButton { CustomMinimumSize = new Vector2(230, 0), TooltipText = "Which combat loadout to wear for this step. Set up loadouts in the game's equipment screen." };
        _gear.ItemSelected += _ => RefreshCombat();
        _weapon = new OptionButton { CustomMinimumSize = new Vector2(260, 0), TooltipText = "The weapon decides which stances you can use. \"Best\" compares every weapon you own in the loadout's style against this monster." };
        _weapon.ItemSelected += _ => RefreshCombat();
        _stance = new OptionButton { CustomMinimumSize = new Vector2(190, 0), TooltipText = "Stab, Slash, Crush and the rest each hit a different defence bonus on the monster. Lower defence means more hits." };
        _stance.ItemSelected += _ => UpdateSummary();
        _gearRow = Kit.Row(Field("Wearing"), _gear, Kit.Label("weapon", null, 13, "Dim"), _weapon, Kit.Label("stance", null, 13, "Dim"), _stance);
        addBox.AddChild(_gearRow);
        _belt = new OptionButton { CustomMinimumSize = new Vector2(300, 0), TooltipText = "A toolbelt set to put on when this step starts. Make sets in the Toolbelts tab." };
        _belt.ItemSelected += _ => UpdateSummary();
        _sellOut = new CheckBox { Text = "Sell what it makes as it comes in (never locked bank stacks)", ButtonPressed = true };
        _sellOut.Toggled += _ => UpdateSummary();
        _goldRow = Kit.Row(Field("Gold"), _sellOut);
        addBox.AddChild(_goldRow);
        _funding = new OptionButton { CustomMinimumSize = new Vector2(460, 0),
            TooltipText = "What Most GP and Fastest XP may use. Gathering keeps what you had: it stops making when only your old stock is left and gathers more first." };
        _funding.AddItem("Use what's in the bank");
        _funding.AddItem("Only actions that use nothing from the bank");
        _funding.AddItem("Gather the ingredients itself (your bank stock is left alone)");
        _funding.ItemSelected += _ => UpdateSummary();
        _fundRow = Kit.Row(Field("Materials"), _funding);
        addBox.AddChild(_fundRow);
        _reward = new CheckBox { Text = "Use the Sacrificial Anvil" };
        _reward.Toggled += _ => UpdateSummary();
        _rewardHint = Kit.Label("", null, 12, "Dim");
        _rewardRow = Kit.Row(Field("Mode"), _reward, _rewardHint);
        addBox.AddChild(_rewardRow);
        _beltEdit = WithTip(Kit.Btn("Edit sets", () => OpenBelts()), "Open the Toolbelts tab on this skill");
        _beltNone = Kit.Label("", null, 13, "Dim");
        _beltMake = WithTip(Kit.Btn("Make a set", () => OpenBelts()), "Open the Toolbelts tab on this skill to make one");
        _beltRow = Kit.Row(Field("Toolbelt"), _belt, _beltEdit, _beltNone, _beltMake);
        addBox.AddChild(_beltRow);
        _combatNote = Wrapped("");
        addBox.AddChild(_combatNote);
        _summary = Wrapped("");
        _summary.AddThemeFontSizeOverride("font_size", 14);
        addBox.AddChild(_summary);
        _now = Kit.Label("", null, 12, "Dim");
        _where = new OptionButton { CustomMinimumSize = new Vector2(230, 0), TooltipText = "Where the new step goes. After the selected step goes at the end when no step is selected." };
        _where.AddItem("at the end of the list");
        _where.AddItem("after the selected step");
        _where.AddItem("at the top of the list");
        _addRow = Kit.Row(_now, Kit.Spacer(), _where, _cancelEdit, _add);
        addBox.AddChild(_addRow);

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
        Control planWhat = Kit.Row(Field("Plan"), _planMode, _planAmount, Kit.Label("of", null, 13, "Dim"), _planItem.Button);
        planBox.AddChild(planWhat);
        _planFrom = new OptionButton { CustomMinimumSize = new Vector2(380, 0),
            TooltipText = "Gathering them all leaves what you already have in the bank: each step gathers on top of your stock." };
        _planFrom.AddItem("Use what's in the bank first");
        _planFrom.AddItem("Gather them all (leave my bank stock alone)");
        _planFrom.ItemSelected += _ => MakePlan();
        Control planFrom = Kit.Row(Field("Ingredients"), _planFrom);
        planBox.AddChild(planFrom);
        _planTable = MakeTable(new[] { "Step", "Until the bank holds", "In the bank", "Takes about" }, new[] { 340, 210, 100, 130 }, 2, 150,
            expandCols: new[] { 0, 1 }, ratios: new[] { 3, 2, 0, 0 });
        planBox.AddChild(_planTable);
        _planTotal = Kit.Label("", null, 14);
        _planKey = Kit.Label("", null, 12, "Dim");
        _planKey.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _planKey.CustomMinimumSize = new Vector2(860, 0);
        _planNote = Wrapped("");
        planBox.AddChild(_planTotal);
        planBox.AddChild(_planKey);
        _planWhere = new OptionButton { CustomMinimumSize = new Vector2(230, 0), TooltipText = "Where the steps go. After the selected step goes at the end when no step is selected." };
        _planWhere.AddItem("at the end of the list");
        _planWhere.AddItem("after the selected step");
        _planWhere.AddItem("at the top of the list");
        Control planAddRow = Kit.Row(_planNote, Kit.Spacer(), _planWhere, _planAdd);
        planBox.AddChild(planAddRow);
        _planNote.CustomMinimumSize = new Vector2(420, 0);

        // ---- saved and shared ----
        BuildSaved(Panel(2));

        // ---- history ----
        _beltEditor = new BeltEditor(_queue, Panel(5));
        _slayer = new SlayerPanel(_queue, Panel(6));

        var historyBox = Panel(3);
        _history = new Tree
        {
            Columns = 2, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row,
            CustomMinimumSize = new Vector2(0, 220), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _history.SetColumnExpand(0, false);
        _history.SetColumnCustomMinimumWidth(0, 96);
        _history.SetColumnExpand(1, true);
        // Days fold: click the day (or its arrow). Today and yesterday start open, older days folded.
        // Click a day to fold it; click a yellow line to mark it seen.
        _history.ItemSelected += () =>
        {
            TreeItem? it = _history.GetSelected();
            if (it == null) return;
            if (it.GetMetadata(0).VariantType == Variant.Type.String)
            {
                it.Collapsed = !it.Collapsed;
                Callable.From(() => _history.DeselectAll()).CallDeferred();
            }
            else if (it.GetMetadata(0).VariantType == Variant.Type.Int) SeeLine(it);
        };
        _history.ItemCollapsed += it =>
        {
            if (it.GetMetadata(0).VariantType == Variant.Type.String) _historyOpen[it.GetMetadata(0).AsString()] = !it.Collapsed;
        };
        _historyShow = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        _historyShow.AddItem("Everything");
        _historyShow.AddItem("Only highlighted lines");
        _historyShow.ItemSelected += _ => RefreshHistory(force: true);
        Control historyTop = Kit.Row(Kit.Label("Show", null, 13, "Dim"), _historyShow, Kit.Spacer(),
            _historySeeAll = WithTip(Kit.Btn("Mark all seen", () => { _queue.MarkSeen(_queue.History); RefreshHistory(force: true); }), "Stop counting every highlighted line"),
            _historyFlags = WithTip(Kit.Btn("Highlight", HighlightMenu), "Pick what History highlights in yellow"),
            WithTip(Kit.Btn("Open all", () => FoldHistory(false)), "Open every day"),
            WithTip(Kit.Btn("Fold all", () => FoldHistory(true)), "Fold every day"),
            WithTip(Kit.Btn("Copy", CopyHistory), "Copy what's shown, to paste in a bug report or a message"));
        historyBox.AddChild(historyTop);
        historyBox.AddChild(_history);
        _historyKeep = new OptionButton { CustomMinimumSize = new Vector2(150, 0),
            TooltipText = "How much history to keep for this character. Older lines are dropped, in the tab and on disk." };
        foreach (int n in s_historyKeep) _historyKeep.AddItem($"{n:N0} lines");
        _historyKeep.ItemSelected += i => _queue.SetHistoryLimit(s_historyKeep[(int)i]);
        _historyClear = WithTip(Kit.Btn("Clear", HistoryClearMenu), "Clear older days, or everything");
        historyBox.AddChild(Kit.Row(Kit.Label("Yellow lines need a look: click one to mark it seen. Keep the last", null, 12, "Dim"), _historyKeep, Kit.Spacer(), _historyClear));

        // ---- options ----
        BuildOptions(Panel(4));

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
        _table.AllowRmbSelect = true;
        _table.ItemMouseSelected += (at, button) => { if (button == (long)MouseButton.Right) RowMenu(); };
        SetUpDragging();
        _win.Body.AddChild(_table);

        _win.Body.AddChild(Kit.Row(
            WithTip(Kit.Btn("Edit", () => WithStep(Edit)), "Change the selected step (or double-click it)"),
            WithTip(Kit.Btn("Up", () => MoveRows(-1)), "Move the ticked steps (or the selected one) up one place"),
            WithTip(Kit.Btn("Down", () => MoveRows(1)), "Move the ticked steps (or the selected one) down one place"),
            WithTip(Kit.Btn("Remove", RemoveRows), "Remove the ticked steps (or the selected one)"),
            WithTip(Kit.Btn("Duplicate", DuplicateRows), "Put a copy of the ticked steps (or the selected one) right after them"),
            WithTip(Kit.Btn("Run from here", () => WithStep(i => { _tabPinned = false; _queue.Start(i); })), "Start the queue at the selected step; steps above it are passed over (not run, not marked done)"),
            _undo = Kit.Btn("Undo", () => _queue.Undo()),
            Kit.Spacer(),
            _stepHint = Kit.Label("", null, 12, "Dim"),
            WithTip(Kit.Btn("Tick all", ToggleTicks), "Tick every step, or clear the ticks"),
            WithTip(Kit.Btn("Remove finished", _queue.RemoveDone), "Take every finished or skipped step off the list"),
            WithTip(Ask("Remove all", "Press again to remove all", _queue.Clear), "Empty the queue (Undo brings it back)")));
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
        void Opt(int i) { Tab(4); ShowOptPage(i); }
        Control addBoxCard = _panels[0];
        _win.SetTours(
            ("The basics (start here)", new[]
            {
                new TourStep("What Course does", "Course runs your actions one after another: cut 500 logs, then burn them, then fight until level 40. Build the list, press [b]Start[/b], and it keeps going while you play, idle or are away."),
                new TourStep("Tabs", "Each tab opens above the list. [b]Add a step[/b] and [b]Plan an item[/b] make steps, [b]Saved and shared[/b] keeps queues, [b]Toolbelts[/b] and [b]Slayer[/b] set up gear and tasks, [b]History[/b] shows what happened, [b]Options[/b] changes how it behaves. Every tab has its own tour under the [b]Tour[/b] button.", () => tabRow, () => Tab(0)),
                new TourStep("Fold", "Folds the open tab away so the list gets the room. Click any tab to open it again; clicking the open tab folds it too. While the queue runs, tabs fold by themselves until you open one.", () => _foldTabs, () => Tab(0)),
                new TourStep("Make a step", "Pick a skill and an action, say when it ends, then [b]Add step[/b]. The [b]Add a step[/b] tour covers every choice.", () => addBoxCard, () => Tab(0)),
                new TourStep("The list", "Steps run top to bottom. [b]State[/b] shows progress and time left. Drag rows to reorder, double-click to edit, right-click for Edit, Duplicate, Run from here, Move and Remove. Tick boxes to work on several at once.", () => _table),
                new TourStep("Edit the list", "These work on the ticked steps, or the selected one if none are ticked. [b]Undo[/b] takes back the last change, even [b]Remove all[/b] or loading a saved queue. [b]Remove all[/b] asks for a second press.", () => stepRow),
                new TourStep("Start", "Runs the queue. While it runs this button pauses it. [b]Run from here[/b] starts at the selected step.", () => _run),
                new TourStep("When it waits or ends", "What your character does while the queue waits (for food, a refill) or after the last step, so it's never idle.", () => idleRow),
                new TourStep("Your queues", "Shows the queue's name, with * when it changed since you saved it. Pick Save changes here, or pick a saved queue to load it.", () => _queuePick),
                new TourStep("That's it", "Open Course with its key from anywhere (the Mods menu shows and changes keys). [b]Help[/b] has the details, and [b]Tour[/b] has a walk through each tab.", null, () => Tab(0)),
            }),
            ("Add a step", new[]
            {
                new TourStep("Do", "Pick the skill, then the action. Besides single actions there are [b]Fastest XP[/b] and [b]Most GP[/b] (they pick the best action and switch as you level), [b]Sell from bank[/b], and for Agility each path or \"(best path)\". [b]Copy what I'm doing[/b] fills in your character's current action.", () => doRow, () => Tab(0)),
                new TourStep("Until", "When the step ends: a skill reaches a level, the bank holds some of an item, you gain some, it's done N times, time passes, or until it stops by itself. The amount starts from where you are now.", () => _goalRow, () => Tab(0)),
                new TourStep("Extra choices", "Rows appear for the action you pick: [b]Wearing[/b], weapon and stance for combat; [b]Materials[/b] for Fastest XP and Most GP (use the bank, use nothing, or gather it all); [b]Mode[/b] for the Sacrificial Anvil or Astral Offerings; [b]Toolbelt[/b] for a set from the Toolbelts tab.", () => addBoxCard, () => Tab(0)),
                new TourStep("The summary", "Says exactly what the step will do and about how fast. Yellow means something needs a look first: a level you don't have, a bank that already holds enough, a missing toolbelt item.", () => _summary, () => Tab(0)),
                new TourStep("Add it", "Pick where it goes (the end, after the selected step, or the top), then [b]Add step[/b]. Editing a step (double-click it) shows [b]Save changes[/b] and [b]Cancel[/b] here instead.", () => _addRow, () => Tab(0)),
            }),
            ("Plan an item", new[]
            {
                new TourStep("What to plan", "Get N more of an item, or have N in total. Course works out every step to get it: the cheapest way to make it with your levels, then the same for each ingredient.", () => planWhat, () => Tab(1)),
                new TourStep("Ingredients", "[b]Use what's in the bank first[/b] only gathers what's missing. [b]Gather them all[/b] leaves your stock alone and gathers on top of it.", () => planFrom, () => Tab(1)),
                new TourStep("The plan", "One line per step, ingredients first: what it does, until the bank holds how many, what you have now, and about how long. Blue lines come by themselves (Farming, Ranching, the mill); yellow ones need you, hover them for why.", () => _planTable, () => Tab(1)),
                new TourStep("Add the steps", "Puts every step in the queue at once, where you pick. Each counts \"until the bank holds\", so steps already done are skipped quickly.", () => planAddRow, () => Tab(1)),
            }),
            ("Saved and shared", new[]
            {
                new TourStep("Now", "The queue you have now: its name, and \"changed since you saved it\" when it differs from the saved copy. [b]Save[/b] writes over that copy; [b]Save as new[/b] keeps it under another name.", () => _curName.GetParent() as Control, () => Tab(2)),
                new TourStep("Your saved queues", "Every saved queue, with its step count and when it was saved. They're shared by all your characters. Click one to see its steps on the right; double-click loads it.", () => _savedTable, () => Tab(2)),
                new TourStep("What to do with it", "[b]Load[/b] replaces your queue (and stops it; Undo brings yours back). [b]Add to my queue[/b] keeps yours and puts its steps after them. [b]Copy code[/b] makes a share code. Rename and Delete (which asks twice) are here too.", () => _savedButtons, () => Tab(2)),
                new TourStep("Share codes", "Paste a code (from a friend, or the wiki's planner) and Course shows what's in it before anything changes. Then add it to your queue, replace your queue with it, or save it as a new queue. [b]Copy my queue as a code[/b] makes one to share.", () => _codeEdit.GetParent() as Control, () => Tab(2)),
            }),
            ("Toolbelts", new[]
            {
                new TourStep("Sets", "A toolbelt set is the tools, clothes and consumables a skill should have on. Pick the skill, then a set, or start one empty or from what's on now.", () => _beltEditor.Top, () => Tab(5)),
                new TourStep("Name and save", "Name it and [b]Save set[/b] (a * means unsaved changes). It shows how many steps use it. [b]Put it on now[/b] equips it straight away.", () => _beltEditor.NameRow, () => Tab(5)),
                new TourStep("Slots", "For each slot: what to put in (or leave as is), what's on now (yellow where the set would change it), how many you have, and for consumables a refill amount. When a refilled item runs out mid-step, the queue takes more from the bank or makes it first, then carries on.", () => _beltEditor.Grid, () => Tab(5)),
                new TourStep("Use it", "On Add a step, pick the set on the [b]Toolbelt[/b] row. It goes on when the step starts. Options has the switch for putting gear on.", null, () => Tab(5)),
            }),
            ("Slayer", new[]
            {
                new TourStep("Masters", "Every slayer master's current task, kills so far, and what extending or rerolling costs. Click a master to work with their task.", () => _slayer.Table, () => Tab(6)),
                new TourStep("The picked master", "Extend or reroll the task now, fight it outside the queue, have Course extend this master's tasks by rule, or switch the game's own Auto Slayer.", () => _slayer.Buttons, () => Tab(6)),
                new TourStep("Rules", "What a [b]Slayer tasks[/b] step does by itself: never spend coins below the reserve, always reroll monsters you list, reroll tasks in areas you can't enter, and (if you tick it) follow these rules even outside a Slayer tasks step.", () => _slayer.Rules, () => Tab(6)),
                new TourStep("Slayer tasks step", "On Add a step pick Combat, then Slayer tasks. It fights the highest master's task it can, moves to the next when one is done, and stops after the number of tasks you set. With the gauntlet option on (Options, Gear and combat) it wears your slayer gauntlet.", null, () => Tab(6)),
            }),
            ("History", new[]
            {
                new TourStep("What happened", "Every step started, finished or skipped, food eaten, gear changed, newest first. Each day folds: click it. Today and yesterday start open. Yellow lines need a look: click one to mark it seen. The tab shows how many are left.", () => _history, () => Tab(3)),
                new TourStep("Filter and copy", "[b]Highlight[/b] picks what turns yellow (skips, pauses, things that didn't work, running out, the queue finishing). [b]Mark all seen[/b] clears the count. Show only highlighted lines, open or fold every day, and copy what's shown for a bug report.", () => historyTop, () => Tab(3)),
                new TourStep("How much to keep", "Each character keeps the last 500, 2,000 or 10,000 lines. [b]Clear[/b] clears days before today, days older than a week, or all of it.", () => _historyClear.GetParent() as Control, () => Tab(3)),
            }),
            ("Options", new[]
            {
                new TourStep("Sections", "Options are in four sections. Pick one on the left. Settings that depend on another sit under it and dim when it's off.", () => _optNav[0].GetParent() as Control, () => Opt(0)),
                new TourStep("Queue", "Start over when it ends, take finished steps off, pause when the bank is full, and a sound and taskbar flash when it needs you.", () => _optPages[0], () => Opt(0)),
                new TourStep("Gear and combat", "Put on each step's gear when it starts. Under it: the default combat loadout and the slayer gauntlet. And stopping a combat step when its food runs out.", () => _optPages[1], () => Opt(1)),
                new TourStep("Thieving and agility", "Best XP gear, and food: keeps the bonus food (sushi, soup) on and heals with plain food before a failure could knock you out. Your food list folds by group; untick a food to keep it out. Pick what a step does when healing food runs out; Make more shows what it would make and how.", () => _optPages[2], () => Opt(2)),
                new TourStep("Display", "The small panel that shows the current step while the queue runs, and the Queue button on the game's skill and combat pages.", () => _optPages[3], () => Opt(3)),
            }));
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
        l.CustomMinimumSize = new Vector2(82, 0);
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
        _foldTabs.Visible = which >= 0;
        if (which == 1) { if (_planItem.SelectedId == null) FillPlanItems(); else MakePlan(); }
        if (which == 2) RefreshSaved();
        if (which == 3) RefreshHistory(force: true);
        if (which == 4) RefreshOptions();
        if (which == 5) _beltEditor.Refresh();
        if (which == 6) _slayer.Refresh();
        if (which == 0) RefreshTargets();   // picks up gear and option changes made on other tabs
    }

    Button _undo = null!, _foldTabs = null!;
    PopupMenu? _rowMenu;
    Label _stepHint = null!;

    void WithStep(Action<int> act)
    {
        if (_selected >= 0 && _selected < _queue.State.Steps.Count) act(_selected);
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
        _untilLabel.Text = sell ? "Sell" : "Until";
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
        _rewardRow.Visible = false;
        // Sacrificial Anvil, Astral Offerings: a choice for steps that don't count items; Most GP uses the anvil anyway.
        if (!sell && game != null && a != null && !_do.IsGold && !_do.IsTech && g is not (Goal.BankCount or Goal.MoreItems))
        {
            var mode = _do.IsBest ? RewardModes.OfSkill(game, a.Entity) : RewardModes.Unlocked(game, a);
            if (mode != null && mode.UnlockRequirements.AreMet)
            {
                _rewardRow.Visible = true;
                _reward.Text = $"Use {RewardModes.Name(mode)}{(_do.IsBest ? " where it applies" : "")}";
                _rewardHint.Text = RewardModes.What(mode);
            }
        }
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
        if (_where is not null) _where.Visible = _editing < 0;

        ActionRef? now = game == null ? null : Starter.Current(game);
        _now.Text = game == null ? "Load a character to pick actions." : "Doing now: " + (now?.Label ?? "nothing");
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
                if (RewardModes.AnvilUnlocked(game, s.Action.Entity)) text += "; smithing uses the Sacrificial Anvil (GP straight away, a little more than selling)";
                if (PlanData.Get(_queue.Folder, _queue.Log) == null) warn.Add("The planner's data is not loaded yet, so it cannot rank actions by GP.");
            }
            else if (s.IsBest && !s.IsTech && s.Funding != Funding.Bank)
            {
                text += s.Funding == Funding.None ? ". Only actions that use nothing from the bank"
                    : ". Ranks by XP per hour counting the time to gather the ingredients, gathers them first and never touches your bank stock of them";
                if (PlanData.Get(_queue.Folder, _queue.Log) == null) warn.Add("The planner's data is not loaded yet, so it cannot rank actions.");
            }
            if (s.IsBest && !s.IsTech) text += TopPicks(game, s);
            if (s.UseRewardMode) text += _do.IsBest ? ". Uses the skill's other mode where it applies (put back after)" : ". " + _reward.Text.Split(':')[0] + " (put back after)";
            else if (!s.IsGold && _do.Selected is ActionRef ra && RewardModes.For(game, ra, out var rr) is { } rm && rr?.CurrentRewardMode != null)
                text += s.Goal is Goal.BankCount or Goal.MoreItems
                    ? $". Turns {RewardModes.Name(rm)} off while it runs: it would use up what this step counts"
                    : $". Turns {RewardModes.Name(rm)} off while it runs (tick it above to keep it)";
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
            UseRewardMode = _rewardRow.Visible && _reward.ButtonPressed,
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
        int to = InsertAt();
        _selected = to;
        _queue.Insert(to, new[] { s });
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
            _reward.ButtonPressed = s.UseRewardMode;
            RefreshTargets(s.Goal == Goal.Level ? s.Skill : s.Item);
            _amount.MinValue = 0;
            _amount.MaxValue = 10_000_000;
            _amount.Value = s.Amount;
        }
        finally { _loading = false; }
        RefreshTargets();
    }

    /// <summary>Where a new step goes: the end, after the selected step, or the top (Add a step's choice, or Plan's).</summary>
    int InsertAt(OptionButton? pick = null)
    {
        int n = _queue.State.Steps.Count;
        return (pick ?? _where).Selected switch
        {
            1 when _selected >= 0 && _selected < n => _selected + 1,
            2 => 0,
            _ => n,
        };
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
        _planAdd.Text = "Add these steps to the queue";
        _planTotal.Text = _planKey.Text = "";
        _planNote.AddThemeColorOverride("font_color", Kit.Dim);
        Game? game = KitGame.Current;
        PlanData? data = PlanData.Get(_queue.Folder, _queue.Log);
        if (game == null || data == null || _planItem.SelectedId == null)
        {
            TreeItem empty = _planTable.CreateItem(_planTable.CreateItem());
            empty.SetText(0, game == null ? "Load a character to plan."
                : data == null ? "The planner's data is still loading. It downloads once a day; try again in a moment."
                : "Choose an item above to see every step that gets it, ingredients first.");
            empty.SetCustomColor(0, Kit.Dim);
            for (int c = 0; c < 4; c++) empty.SetSelectable(c, false);
            if (data != null) _planNote.Text = "";
            _planKey.Visible = false;
            return;
        }

        _planner = new Planner(data, game, selfFunded: _planFrom.Selected == 1);
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
        _planTotal.Text = _plan.Count == 0
            ? "You already have enough."
            : $"{mode}: {steps.Count} step{(steps.Count == 1 ? "" : "s")}, about {Duration(hours)} of actions" + (n > 0 && steps.Any(x => x.IsBest && x.Goal == Goal.Level) ? ", plus leveling first." : ".");
        var key = new List<string>();
        if (passive) key.Add("Blue: comes from Farming, Ranching or the mill while you play; not a queue step.");
        if (bring) key.Add("Yellow: no action makes it, so bring it yourself.");
        if (shopProblem) key.Add("Yellow shop line: can't buy yet; hover it to see why.");
        if (_planFrom.Selected == 1) key.Add("Gathers everything; your bank stock stays as it is.");
        _planKey.Text = string.Join("  ", key);
        _planKey.Visible = key.Count > 0;
        _planNote.Text = $"Using {PlanData.Source}." + (PlanData.Source.StartsWith("your") ? "" : " Run Astrolabe Tools to plan with your own rates.");
        _planAdd.Disabled = steps.Count == 0;
        if (steps.Count > 0) _planAdd.Text = $"Add {steps.Count} step{(steps.Count == 1 ? "" : "s")} to the queue";
    }

    static string Duration(double h) =>
        double.IsInfinity(h) || double.IsNaN(h) ? "?" : h < 1.0 / 60 ? "< 1 min" : h < 1 ? $"{Math.Round(h * 60)} min" : h < 48 ? $"{Math.Floor(h)} h {Math.Round(h % 1 * 60)} min" : $"{h / 24:0.#} days";

    void AddPlan()
    {
        if (_planner == null) return;
        var steps = _planner.ToSteps(_plan);
        if (steps.Count == 0) return;
        int to = InsertAt(_planWhere);
        _queue.Insert(to, steps);
        _selected = to + steps.Count - 1;
        _planNote.Text = $"Added {steps.Count} step{(steps.Count == 1 ? "" : "s")} to the queue.";
        _planNote.AddThemeColorOverride("font_color", Kit.Good);
    }

    // ---- saved and shared ----

    // ---- history and options ----

    IEnumerable<HistoryEntry> ShownHistory() =>
        Enumerable.Reverse(_queue.History).Where(e => _historyShow.Selected != 1 || _queue.Flagged(e));

    void RefreshHistory(bool force = false)
    {
        var shown = (_queue.History.Count, _queue.History.Count > 0 ? _queue.History[^1].Time : default, _historyShow.Selected);
        if (!force && shown == _historyShown && _history.GetRoot() != null) return;   // nothing new: keep the scroll
        _historyShown = shown;
        int keep = Array.IndexOf(s_historyKeep, _queue.State.HistoryLimit);
        _historyKeep.Select(keep >= 0 ? keep : 1);

        _history.Clear();
        _historyRows.Clear();
        TreeItem root = _history.CreateItem();
        var days = ShownHistory().GroupBy(e => e.Time.Date).ToList();
        foreach (var day in days)
        {
            string key = day.Key.ToString("yyyy-MM-dd");
            TreeItem d = _history.CreateItem(root);
            d.SetMetadata(0, key);
            d.SetSelectable(0, false);
            foreach (HistoryEntry e in day)
            {
                TreeItem r = _history.CreateItem(d);
                r.SetMetadata(0, _historyRows.Count);
                _historyRows.Add(e);
                r.SetText(0, e.Time.ToString("HH:mm"));
                r.SetCustomColor(0, Kit.Dim);
                r.SetText(1, e.Text);
                LineLook(r, e);
            }
            DayLook(d);
            d.Collapsed = !(_historyOpen.TryGetValue(key, out bool open) ? open : day.Key >= DateTime.Today.AddDays(-1));
        }
        if (days.Count == 0)
        {
            TreeItem r = _history.CreateItem(root);
            r.SetText(1, _historyShow.Selected == 1 ? "Nothing highlighted. Switch Show to Everything to see the rest, or change what Highlight picks." : "Nothing yet. Start the queue and what it does shows up here.");
            r.SetCustomColor(1, Kit.Dim);
        }
    }

    static readonly Color s_seenGold = new(Kit.Gold, 0.5f);

    /// <summary>A line's colour and tooltip: yellow when it needs a look, faded once seen.</summary>
    void LineLook(TreeItem r, HistoryEntry e)
    {
        bool flagged = _queue.Flagged(e), seen = flagged && _queue.Seen(e);
        r.SetCustomColor(1, flagged ? (seen ? s_seenGold : Kit.Gold) : e.Text.StartsWith("Queue") ? Kit.Link : Kit.Text);
        r.SetTooltipText(1, flagged && !seen ? e.Text + "\n(click to mark it seen)" : e.Text);
    }

    /// <summary>A day's line: how many lines, and how many still need a look.</summary>
    void DayLook(TreeItem d)
    {
        var lines = d.GetChildren().Select(c => c.GetMetadata(0).AsInt32()).Where(i => i < _historyRows.Count).Select(i => _historyRows[i]).ToList();
        int look = lines.Count(_queue.NeedsLook);
        DateTime day = lines.Count > 0 ? lines[0].Time.Date : DateTime.Today;
        string name = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, MMM d");
        d.SetText(1, $"{name}  ({lines.Count:N0} line{(lines.Count == 1 ? "" : "s")}{(look > 0 ? $", {look} need a look" : "")})");
        d.SetCustomColor(1, look > 0 ? Kit.Gold : Kit.Parchment);
    }

    void SeeLine(TreeItem it)
    {
        int i = it.GetMetadata(0).AsInt32();
        if (i < 0 || i >= _historyRows.Count || !_queue.NeedsLook(_historyRows[i])) return;
        _queue.MarkSeen(new[] { _historyRows[i] });
        LineLook(it, _historyRows[i]);
        if (it.GetParent() is TreeItem d) DayLook(d);
        UpdateHistoryBadge();
    }

    /// <summary>The History tab says how many lines need a look: "History (2)".</summary>
    void UpdateHistoryBadge()
    {
        var h = _queue.History;
        var key = (h.Count, h.Count > 0 ? h[^1].Time : default(DateTime), _queue.State.HistorySeen.Count, string.Join(",", _queue.State.HistoryFlags.OrderBy(f => f)).GetHashCode());
        if (key == _badgeFor) return;
        _badgeFor = key;
        int n = h.Count(_queue.NeedsLook);
        _tabs[3].Text = n > 0 ? $"{s_tabNames[3]} ({n})" : s_tabNames[3];
        _tabs[3].TooltipText = n > 0 ? $"{n} highlighted line{(n == 1 ? "" : "s")} you haven't looked at yet" : "";
        _historySeeAll.Disabled = n == 0;
    }

    void HighlightMenu()
    {
        if (_flagMenu == null)
        {
            _flagMenu = new PopupMenu { Theme = Kit.Theme, HideOnCheckableItemSelection = false };
            _flagMenu.IdPressed += id =>
            {
                int i = (int)id;
                if (i < 0 || i >= Queue.HistoryKinds.Length) return;
                bool on = !_flagMenu.IsItemChecked(i);
                _flagMenu.SetItemChecked(i, on);
                _queue.SetHistoryFlag(Queue.HistoryKinds[i].Id, on);
                RefreshHistory(force: true);
            };
            _historyFlags.AddChild(_flagMenu);
        }
        _flagMenu.Clear();
        for (int i = 0; i < Queue.HistoryKinds.Length; i++)
        {
            _flagMenu.AddCheckItem(Queue.HistoryKinds[i].Name, i);
            _flagMenu.SetItemChecked(i, _queue.State.HistoryFlags.Contains(Queue.HistoryKinds[i].Id));
        }
        _flagMenu.Position = (Vector2I)(_historyFlags.GetScreenPosition() + new Vector2(0, _historyFlags.Size.Y * _historyFlags.GetGlobalTransformWithCanvas().Scale.Y));
        _flagMenu.Popup();
    }

    void FoldHistory(bool fold)
    {
        TreeItem? root = _history.GetRoot();
        if (root == null) return;
        foreach (TreeItem d in root.GetChildren())
            if (d.GetMetadata(0).VariantType == Variant.Type.String) d.Collapsed = fold;
    }

    void HistoryClearMenu()
    {
        if (_clearMenu == null)
        {
            _clearMenu = new PopupMenu { Theme = Kit.Theme };
            _clearMenu.IdPressed += id =>
            {
                DateTime? before = id switch { 0 => DateTime.Today, 1 => DateTime.Today.AddDays(-6), _ => null };
                _queue.ClearHistory(before);
                RefreshHistory(force: true);
            };
            _historyClear.AddChild(_clearMenu);
        }
        _clearMenu.Clear();
        _clearMenu.AddItem("Everything before today", 0);
        _clearMenu.AddItem("Everything older than a week", 1);
        _clearMenu.AddSeparator();
        _clearMenu.AddItem("All of it", 2);
        _clearMenu.Position = (Vector2I)(_historyClear.GetScreenPosition() + new Vector2(0, _historyClear.Size.Y * _historyClear.GetGlobalTransformWithCanvas().Scale.Y));
        _clearMenu.Popup();
    }

    void CopyHistory()
    {
        var lines = ShownHistory().Select(e => $"{e.Time:yyyy-MM-dd HH:mm}  {e.Text}").ToList();
        DisplayServer.ClipboardSet(string.Join("\n", lines));
        _status.Text = $"Copied {lines.Count} history line{(lines.Count == 1 ? "" : "s")}.";
    }

    // ---- options ----
    // Four sections picked from a list on the left; only the picked one shows.

    static readonly string[] s_optNames = { "Queue", "Gear and combat", "Thieving and agility", "Display" };
    readonly Button[] _optNav = new Button[4];
    readonly VBoxContainer[] _optPages = new VBoxContainer[4];
    int _optPage;
    Control _gearKids = null!, _foodBox = null!, _makeBox = null!;

    void BuildOptions(VBoxContainer panel)
    {
        var split = new HBoxContainer();
        split.AddThemeConstantOverride("separation", 18);
        panel.AddChild(split);
        var nav = new VBoxContainer { CustomMinimumSize = new Vector2(170, 0) };
        split.AddChild(nav);
        var pages = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        split.AddChild(pages);
        for (int i = 0; i < s_optNames.Length; i++)
        {
            int at = i;
            Button b = Kit.Btn(s_optNames[i], () => ShowOptPage(at));
            b.ThemeTypeVariation = "Tab";
            b.ToggleMode = true;
            b.Alignment = HorizontalAlignment.Left;
            b.SetPressedNoSignal(i == 0);
            nav.AddChild(b);
            _optNav[i] = b;
            var page = new VBoxContainer { Visible = i == 0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            page.AddThemeConstantOverride("separation", 10);
            page.AddChild(Kit.Label(s_optNames[i], null, 15, "Heading"));
            pages.AddChild(page);
            _optPages[i] = page;
        }

        // Queue
        VBoxContainer q = _optPages[0];
        Setting(q, "Start over when the queue ends", "", s => s.Loop, (s, v) => s.Loop = v);
        Setting(q, "Take finished steps off the list", "Kept while the queue loops. Skipped steps stay so you can see them.", s => s.RemoveFinished, (s, v) => s.RemoveFinished = v);
        Setting(q, "Pause when the bank is full", "", s => s.BankGuard, (s, v) => s.BankGuard = v);
        Setting(q, "Sound and taskbar flash", "When the queue finishes, skips a step or pauses.", s => s.Alerts, (s, v) => s.Alerts = v);

        // Gear and combat: the gear switch, with what depends on it under it.
        VBoxContainer g = _optPages[1];
        _optGear = Setting(g, "Put on each step's gear when it starts", "Combat loadouts, weapons and stances, and skill toolbelt sets with refills.", s => s.AutoGear, (s, v) => s.AutoGear = v);
        _optGear.Toggled += _ => RefreshGearGrid();
        var kids = new VBoxContainer();
        kids.AddThemeConstantOverride("separation", 8);
        kids.AddChild(Kit.Label("Default combat loadout", null, 13));
        _gearNote = Hint("");
        kids.AddChild(_gearNote);
        _gearGrid = new GridContainer { Columns = 4 };
        _gearGrid.AddThemeConstantOverride("h_separation", 10);
        kids.AddChild(_gearGrid);
        _optGauntlet = Setting(kids, "Wear a slayer gauntlet on slayer tasks", "When a combat step's monster is one of your slayer tasks. Your gloves go back on after.", s => s.SlayerGauntlet, (s, v) => s.SlayerGauntlet = v);
        _gearKids = Indent(kids, 28);
        g.AddChild(_gearKids);
        Setting(g, "Stop a combat step when its food runs out", "The food selected for the fight. The queue goes on to the next step.", s => s.FoodGuard, (s, v) => s.FoodGuard = v);

        BuildFinesseOptions(_optPages[2]);

        // Display
        VBoxContainer d = _optPages[3];
        Setting(d, "Show the current step in a small panel", "While the queue runs. Drag it anywhere; \"–\" folds it.", s => s.Hud, (s, v) => s.Hud = v);
        d.AddChild(Indent(Kit.Row(Kit.Btn("Put the panel back in its first spot", () => _queue.SetOption(s => { s.HudX = s.HudY = -1; s.HudMin = false; }))), 28));
        Setting(d, "Queue button on skill and combat pages", "Top right of the page. It opens Add a step with that page's action picked.", s => s.PageButton, (s, v) => s.PageButton = v);
    }

    void BuildFinesseOptions(VBoxContainer t)
    {
        Setting(t, "Wear the best gear for XP", "The gear you own that gives the most XP for the target, then the best loot (thieving) or least damage (agility). Not for steps with their own toolbelt set.", s => s.FinesseGear, (s, v) => s.FinesseGear = v);
        _optFinesseFood = Setting(t, "Look after food", "Keeps the skill's bonus food (sushi, soup) selected, and heals before a failure could knock you out.", s => s.FinesseFood, (s, v) => s.FinesseFood = v);
        _optFinesseFood.Toggled += on => { _foodBox.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.45f); RefreshFood(); };

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _foodBox = Indent(box, 28);
        t.AddChild(_foodBox);
        _foodSummary = Hint("");
        box.AddChild(_foodSummary);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        var floor = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 90, Suffix = "%", Value = _queue.State.FinesseFloor, CustomMinimumSize = new Vector2(100, 0) });
        floor.ValueChanged += v => _queue.SetOption(s => s.FinesseFloor = (int)v);
        floor.TooltipText = "Heal at this HP percent or below, as well as whenever two failures could reach the game's auto-eat point.";
        grid.AddChild(Kit.Label("Also heal below", null, 13, "Dim"));
        grid.AddChild(Kit.Row(floor, Kit.Label("HP", null, 13, "Dim")));
        _eatOrder = new OptionButton { CustomMinimumSize = new Vector2(400, 0) };
        _eatOrder.AddItem("The cheapest per HP (gives up the least gold)");
        _eatOrder.AddItem("The biggest heal");
        _eatOrder.ItemSelected += i => { _queue.SetOption(s => s.HealBiggestFirst = i == 1); _queue.FoodChanged(); RefreshFood(); };
        grid.AddChild(Kit.Label("Eat first", null, 13, "Dim"));
        grid.AddChild(_eatOrder);
        _foodOut = new OptionButton { CustomMinimumSize = new Vector2(400, 0),
            TooltipText = "For steps in the queue. Healing food is the food under Heals with." };
        _foodOut.AddItem("Keep going (the game eats the bonus food)");
        _foodOut.AddItem("Stop the step and go on to the next one");
        _foodOut.AddItem("Make more healing food, then come back");
        _foodOut.AddItem("Do the idle action until there's healing food, then come back");
        _foodOut.ItemSelected += i => { _queue.SetOption(s => s.FoodOut = (FoodOut)(int)i); RefreshMake(); };
        grid.AddChild(Kit.Label("When a step runs out", null, 13, "Dim"));
        grid.AddChild(_foodOut);
        box.AddChild(grid);

        // Make more: what, how many, where the ingredients come from, and what that means next time.
        var make = new GridContainer { Columns = 2 };
        make.AddThemeConstantOverride("h_separation", 12);
        make.AddThemeConstantOverride("v_separation", 6);
        _makeAmount = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 100_000, Value = _queue.State.FoodMakeAmount, CustomMinimumSize = new Vector2(110, 0) });
        _makeAmount.ValueChanged += v => { _queue.SetOption(s => s.FoodMakeAmount = (int)v); RefreshMakePreviewLater(); };
        _makeFood = new OptionButton { CustomMinimumSize = new Vector2(330, 0) };
        _makeFood.ItemSelected += i => { _queue.SetOption(s => s.FoodMake = _makeFood.GetItemMetadata((int)i).AsString()); RefreshMakePreview(); };
        make.AddChild(Kit.Label("Make", null, 13, "Dim"));
        make.AddChild(Kit.Row(_makeAmount, _makeFood));
        _makeFrom = new OptionButton { CustomMinimumSize = new Vector2(330, 0) };
        _makeFrom.AddItem("Use what's in the bank first");
        _makeFrom.AddItem("Gather them all (leave my bank stock alone)");
        _makeFrom.ItemSelected += i => { _queue.SetOption(s => s.FoodSelfFund = i == 1); RefreshMakePreview(); };
        make.AddChild(Kit.Label("Ingredients", null, 13, "Dim"));
        make.AddChild(_makeFrom);
        var makeBox = new VBoxContainer();
        makeBox.AddThemeConstantOverride("separation", 6);
        makeBox.AddChild(make);
        _makePreview = Hint("");
        makeBox.AddChild(_makePreview);
        _makeBox = Indent(makeBox, 28);
        box.AddChild(_makeBox);

        // The food list: groups fold (click the group), and unticked food folds away under Kept out.
        box.AddChild(Kit.Label("Your food", null, 13));
        box.AddChild(Hint("Untick a food to keep it out; it moves to Kept out at the bottom. Click a group to fold it."));
        _foodTable = MakeTable(new[] { "Use", "Food", "Heals", "In bank", "GP per HP", "Notes" }, new[] { 70, 180, 60, 70, 80, 170 }, 1, 150,
            expandCols: new[] { 1, 5 }, ratios: new[] { 0, 2, 0, 0, 0, 3 });
        _foodTable.ItemEdited += () =>
        {
            TreeItem? it = _foodTable.GetEdited();
            if (it == null || it.GetMetadata(0).VariantType == Variant.Type.Nil) return;
            string[] m = it.GetMetadata(0).AsString().Split('|');
            if (m.Length != 2) return;
            string id = m[0];
            bool on = it.IsChecked(0), other = m[1] == nameof(Finesse.FoodKind.Other);
            _queue.SetOption(s =>
            {
                if (other) { if (on) s.FoodAllow.Add(id); else s.FoodAllow.Remove(id); }
                else if (on) s.FoodSkip.Remove(id); else s.FoodSkip.Add(id);
            });
            _queue.FoodChanged();
            _foodFocus = id;
            Callable.From(() => RefreshFood()).CallDeferred();   // not while the table is mid-edit
        };
        _foodTable.ItemSelected += () =>
        {
            TreeItem? it = _foodTable.GetSelected();
            if (it == null || !it.GetMetadata(0).AsString().StartsWith('#')) return;
            it.Collapsed = !it.Collapsed;
            Callable.From(() => _foodTable.DeselectAll()).CallDeferred();
        };
        _foodTable.ItemCollapsed += it =>
        {
            string key = it.GetMetadata(0).AsString();
            if (!key.StartsWith('#')) return;
            _foodFolded[key] = it.Collapsed;
            SizeFoodTable();
        };
        box.AddChild(_foodTable);
    }

    void ShowOptPage(int at)
    {
        _optPage = at;
        for (int i = 0; i < _optPages.Length; i++)
        {
            _optPages[i].Visible = i == at;
            _optNav[i].SetPressedNoSignal(i == at);
        }
        if (at == 2) RefreshFood();
    }

    /// <summary>A checkbox as wide as its text, with a short dim line under it.</summary>
    CheckBox Setting(VBoxContainer to, string text, string hint, Func<QueueState, bool> get, Action<QueueState, bool> set)
    {
        CheckBox c = Option(text, get, set);
        c.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        if (hint.Length == 0) { to.AddChild(c); return c; }
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        box.AddChild(c);
        box.AddChild(Indent(Hint(hint), 28));
        to.AddChild(box);
        return c;
    }

    static Label Hint(string text)
    {
        var l = Kit.Label(text, null, 12, "Dim");
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(560, 0);
        return l;
    }

    static Control Indent(Control c, int px)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", px);
        m.AddChild(c);
        return m;
    }

    void RefreshOptions()
    {
        foreach (var (box, get) in _options) box.SetPressedNoSignal(get(_queue.State));
        ShowOptPage(_optPage);
        RefreshGearGrid();
        if (_optPage != 2) RefreshFood();
    }

    OptionButton _foodOut = null!, _makeFood = null!, _eatOrder = null!, _makeFrom = null!;
    SpinBox _makeAmount = null!;
    Label _foodSummary = null!, _makePreview = null!;
    Tree _foodTable = null!;
    string _foodFocus = "";
    bool _previewQueued;
    /// <summary>Which food groups are folded; Saved for combat and Kept out start folded.</summary>
    readonly Dictionary<string, bool> _foodFolded = new() { ["#heal"] = false, ["#here"] = false, ["#combat"] = true, ["#out"] = true };

    /// <summary>The thieving and agility section: summary line, choices, the make-more panel and the food list.</summary>
    void RefreshFood()
    {
        QueueState st = _queue.State;
        Finesse.FoodRules rules = st.FoodRules;
        _foodBox.Modulate = st.FinesseFood ? Colors.White : new Color(1, 1, 1, 0.45f);
        _eatOrder.Select(st.HealBiggestFirst ? 1 : 0);
        _foodOut.Select((int)st.FoodOut);
        _makeAmount.SetValueNoSignal(st.FoodMakeAmount);
        _makeFrom.Select(st.FoodSelfFund ? 1 : 0);
        Game? game = KitGame.Current;
        _makeFood.Clear();
        _makeFood.AddItem("The cheapest per HP I can make");
        _makeFood.SetItemMetadata(0, "");
        if (game == null) { _makeFood.Select(0); _foodSummary.Text = "Load a character to choose food."; _foodTable.Clear(); RefreshMake(); return; }

        List<Finesse.FoodInfo> all = Finesse.AllFoods(game);
        foreach (var f in all.Where(f => f.Kind == Finesse.FoodKind.Plain).OrderBy(f => f.PerHp))
        {
            _makeFood.AddItem($"{f.Item.DisplayName} (heals {f.Heal}, {f.PerHp:0.00} GP per HP)");
            _makeFood.SetItemMetadata(_makeFood.ItemCount - 1, f.Item.Id);
        }
        int at = 0;
        for (int i = 0; i < _makeFood.ItemCount; i++) if (_makeFood.GetItemMetadata(i).AsString() == st.FoodMake) at = i;
        _makeFood.Select(at);
        RefreshMake();

        // What's in the bank, plus food you changed from its default and have none of now (so it can be changed back).
        var rows = Finesse.BankFoods(game).ToList();
        foreach (var f in all)
            if ((st.FoodSkip.Contains(f.Item.Id) || st.FoodAllow.Contains(f.Item.Id)) && rows.All(r => r.Food.Item.Id != f.Item.Id))
                rows.Add((f, 0));
        int BankOf(Finesse.FoodInfo f) => rows.First(r => r.Food.Item.Id == f.Item.Id).Count;
        var heals = rules.Order(rows.Where(r => r.Count > 0).Select(r => r.Food).Where(rules.Heals)).ToList();
        var here = rows.Where(r => r.Count > 0 && r.Food.Kind == Finesse.FoodKind.Here && !st.FoodSkip.Contains(r.Food.Item.Id)).Select(r => r.Food).ToList();
        int saved = rows.Count(r => r.Count > 0 && r.Food.Kind == Finesse.FoodKind.Other && !st.FoodAllow.Contains(r.Food.Item.Id));

        // One line that says what it will do.
        var parts = new List<string>();
        parts.Add(heals.Count == 0 ? "No food it may heal with in the bank."
            : "Heals with " + string.Join(", then ", heals.Take(3).Select(f => f.Item.DisplayName.ToString())) + (heals.Count > 3 ? $", and {heals.Count - 3} more." : "."));
        if (here.Count > 0) parts.Add("Keeps " + string.Join(", ", here.Select(f => $"{f.Item.DisplayName} on for {f.Skill.ToLowerInvariant()}")) + ".");
        if (saved > 0) parts.Add($"{saved} food{(saved == 1 ? "" : "s")} saved for combat.");
        _foodSummary.Text = st.FinesseFood ? string.Join(" ", parts) : "Off: Course leaves thieving and agility food to you.";
        _foodSummary.AddThemeColorOverride("font_color", heals.Count == 0 && st.FinesseFood ? Kit.Gold : Kit.Dim);

        // Groups: what it heals with (plain food and any combat food you ticked), bonus food kept on here,
        // combat food saved for combat, and the food you unticked.
        Game gm = game;
        Tree table = _foodTable;
        table.Clear();
        TreeItem root = table.CreateItem();
        TreeItem? focus = null;
        TreeItem Group(string key, string title, int count)
        {
            TreeItem g = table.CreateItem(root);
            g.SetMetadata(0, key);
            g.SetText(1, $"{title} ({count})");
            g.SetCustomColor(1, Kit.Gold);
            for (int c = 0; c < 6; c++) g.SetSelectable(c, c == 1);
            g.Collapsed = _foodFolded[key];
            return g;
        }
        void Row(TreeItem g, Finesse.FoodInfo f, bool on, string note, bool best)
        {
            TreeItem r = table.CreateItem(g);
            r.SetMetadata(0, f.Item.Id + "|" + f.Kind);
            r.SetCellMode(0, TreeItem.TreeCellMode.Check);
            r.SetEditable(0, true);
            r.SetChecked(0, on);
            r.SetIcon(1, Kit.Icon(Catalog.ItemIcon(gm, f.Item.Id)));
            r.SetText(1, f.Item.DisplayName.ToString());
            r.SetText(2, f.Heal.ToString());
            r.SetText(3, BankOf(f).ToString("N0"));
            r.SetText(4, f.PerHp == double.MaxValue ? "" : f.PerHp.ToString("0.00"));
            for (int c = 2; c <= 4; c++) r.SetTextAlignment(c, HorizontalAlignment.Right);
            r.SetText(5, note);
            r.SetTooltipText(5, note);
            Color text = on ? Kit.Text : Kit.Dim;
            for (int c = 1; c <= 5; c++) r.SetCustomColor(c, text);
            if (best) r.SetCustomColor(4, Kit.Good);
            if (f.Item.Id == _foodFocus) focus = g.Collapsed ? g : r;
        }

        Finesse.FoodInfo? first = heals.FirstOrDefault();
        var healRows = rules.Order(rows.Select(r => r.Food).Where(rules.Heals)).ToList();
        var hereRows = rows.Select(r => r.Food).Where(f => f.Kind == Finesse.FoodKind.Here && !st.FoodSkip.Contains(f.Item.Id))
            .OrderBy(f => f.Skill).ThenByDescending(f => f.Heal).ToList();
        var combatRows = rows.Select(r => r.Food).Where(f => f.Kind == Finesse.FoodKind.Other && !st.FoodAllow.Contains(f.Item.Id)).OrderBy(f => f.PerHp).ToList();
        var outRows = rows.Select(r => r.Food).Where(f => f.Kind != Finesse.FoodKind.Other && st.FoodSkip.Contains(f.Item.Id))
            .OrderBy(f => f.Kind).ThenBy(f => f.PerHp).ToList();

        TreeItem hg = Group("#heal", st.HealBiggestFirst ? "Heals with, biggest heal first" : "Heals with, cheapest per HP first", healRows.Count);
        foreach (var f in healRows)
            Row(hg, f, true, (f == first ? "Eaten first. " : "") + (f.Kind == Finesse.FoodKind.Other ? "Combat food: " + f.Bonus : ""), f == first);
        if (hereRows.Count > 0)
        {
            TreeItem g = Group("#here", "Bonus food here, kept on for its bonus and never eaten to heal", hereRows.Count);
            foreach (var f in hereRows) Row(g, f, true, f.Bonus, false);
        }
        if (combatRows.Count > 0)
        {
            TreeItem g = Group("#combat", "Saved for combat, tick to heal with it too", combatRows.Count);
            foreach (var f in combatRows) Row(g, f, false, f.Bonus, false);
        }
        if (outRows.Count > 0)
        {
            TreeItem g = Group("#out", "Kept out, tick to use again", outRows.Count);
            foreach (var f in outRows) Row(g, f, false, f.Kind == Finesse.FoodKind.Here ? f.Bonus : "", false);
        }
        SizeFoodTable();
        if (focus != null) { TreeItem to = focus; Callable.From(() => table.ScrollToItem(to)).CallDeferred(); }
        _foodFocus = "";
    }

    /// <summary>The food list as tall as its open rows, up to a point (it scrolls past that).</summary>
    void SizeFoodTable()
    {
        TreeItem? root = _foodTable.GetRoot();
        int rows = 0;
        if (root != null)
            foreach (TreeItem g in root.GetChildren()) rows += 1 + (g.Collapsed ? 0 : g.GetChildCount());
        _foodTable.CustomMinimumSize = new Vector2(0, Math.Clamp(34 + rows * 28, 120, 400));
    }

    void RefreshMake()
    {
        _makeBox.Visible = _queue.State.FoodOut == FoodOut.MakeMore;
        RefreshMakePreview();
    }

    /// <summary>Typing an amount changes it on every key: plan once, after the last one.</summary>
    void RefreshMakePreviewLater()
    {
        if (_previewQueued) return;
        _previewQueued = true;
        Callable.From(() => { _previewQueued = false; RefreshMakePreview(); }).CallDeferred();
    }

    /// <summary>What making more would do if a step ran out now, planned the same way the queue will plan it.</summary>
    void RefreshMakePreview()
    {
        if (!_makeBox.Visible) return;
        Game? game = KitGame.Current;
        if (game == null) { _makePreview.Text = ""; return; }
        Queue.FoodPlan? p = _queue.PlanFood(game, out string why);
        if (p == null)
        {
            _makePreview.Text = "Nothing it can make right now" + (why.Length > 0 ? $" ({why})" : "") + ", so the step would stop and the queue go on.";
            _makePreview.AddThemeColorOverride("font_color", Kit.Gold);
            return;
        }
        var what = new List<string>();
        foreach (PlanLine l in p.Lines)
            what.Add(l.Gold ? "earn GP for the shop"
                : l.Shop.Length > 0 ? $"buy {l.ItemName}"
                : l.Drop && l.Action != null ? $"{l.Action.Label} for {l.ItemName}"
                : l.Rate == null ? $"wait for {l.ItemName}"
                : $"{l.Rate.Skill}: {l.Rate.Name}");
        string from = _queue.State.FoodSelfFund ? "Gathers every ingredient; your bank stock stays as it is." : "Ingredients in the bank are used first.";
        _makePreview.Text = $"If a step ran out now: {p.Amount:N0} {p.Food.DisplayName}, by {string.Join(", then ", what)}. "
            + $"About {Duration(p.Hours)}, then back to the step. {from} Up to 3 times in 30 minutes; after that the step stops.";
        _makePreview.AddThemeColorOverride("font_color", Kit.Dim);
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
        _combatNote.TooltipText = "";
        if (a?.Target == Slayer.Target)
        {
            _combatNote.Text = Slayer.Summary(game) + " The step fights the highest master's task it can, moves on when one is done, and follows the Slayer tab's reroll and extend rules. Weapon and stance are picked per monster.";
            return;
        }
        var enemies = a == null ? new List<GamesByMalcs.Melvor2.Entities.Combat.Enemy>() : CombatSetup.Enemies(game, a);
        if (enemies.Count == 0) { _combatNote.Text = ""; return; }
        _combatNote.TooltipText = CombatSetup.DefenceText(game, enemies);
        _combatNote.MouseFilter = Control.MouseFilterEnum.Pass;
        string text = CombatSetup.WeakestText(game, enemies);
        string task = Slayer.TaskNote(game, enemies);
        if (task.Length > 0) text = task + " " + text;
        var lw = CombatSetup.LoadoutWeapon(game, LoadoutInEffect(game), out string own);
        if (lw != null)
        {
            var style = lw.Type.Stances[0].CombatStyle;
            var best = CombatSetup.BestWeapon(game, enemies, style);
            if (best?.Weapon != null)
                text += $"\nBest {style.DisplayName} with your current gear: {best.Weapon.Item.DisplayName}"
                    + (best.Offhand != null ? $" with {best.Offhand.DisplayName}" : "")
                    + $", {best.Stance.DisplayName} stance, about {best.Dps:0.#} damage a second.";
            else if (style.MaxHitFormula is not GamesByMalcs.Melvor2.Entities.Combat.StrengthBonusMaxHitFormula)
                text += "\nMagic damage comes from the spell, so weapons are not ranked for it.";
        }
        _combatNote.Text = text;
    }

    void RefreshGearGrid()
    {
        foreach (Node n in _gearGrid.GetChildren()) { _gearGrid.RemoveChild(n); n.QueueFree(); }
        bool on = _queue.State.AutoGear;
        _gearKids.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.45f);
        _optGauntlet.Disabled = !on;
        Game? game = KitGame.Current;
        if (game == null) return;
        List<Gear.Choice> loadouts = Gear.Loadouts(game);
        string? combatId = Gear.CombatId(game);
        _gearNote.Text = loadouts.Count == 0
            ? "This character has no loadouts yet."
            : "Combat steps wear this unless the step picks its own loadout (Melee for one monster, Magic for the next).";
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

    void DuplicateRows()
    {
        var rows = Rows();
        if (rows.Count == 0) return;
        bool ticked = _ticked.Count > 0;
        AfterMove(_queue.Duplicate(rows), ticked);
    }

    /// <summary>Right-click a step: what the buttons below do, for that step (or every ticked one).</summary>
    void RowMenu()
    {
        if (_selected < 0 || _selected >= _queue.State.Steps.Count) return;
        if (_ticked.Count > 0 && !_ticked.Contains(_selected)) _ticked.Clear();
        int n = Rows().Count;
        string them = n > 1 ? $" ({n} ticked)" : "";
        if (_rowMenu == null)
        {
            _rowMenu = new PopupMenu { Theme = Kit.Theme };
            _rowMenu.IdPressed += id =>
            {
                switch (id)
                {
                    case 0: WithStep(Edit); break;
                    case 1: DuplicateRows(); break;
                    case 2: WithStep(i => { _tabPinned = false; _queue.Start(i); }); break;
                    case 3: AfterMove(_queue.MoveManyTo(Rows(), 0), _ticked.Count > 0); break;
                    case 4: { var r = Rows(); AfterMove(_queue.MoveManyTo(r, _queue.State.Steps.Count - r.Count), _ticked.Count > 0); break; }
                    case 5: RemoveRows(); break;
                }
            };
            _table.AddChild(_rowMenu);
        }
        _rowMenu.Clear();
        _rowMenu.AddItem("Edit", 0);
        _rowMenu.SetItemDisabled(0, n > 1);
        _rowMenu.AddItem("Duplicate" + them, 1);
        _rowMenu.AddItem("Run from here", 2);
        _rowMenu.SetItemDisabled(_rowMenu.GetItemIndex(2), n > 1);
        _rowMenu.AddSeparator();
        _rowMenu.AddItem("Move to the top" + them, 3);
        _rowMenu.AddItem("Move to the bottom" + them, 4);
        _rowMenu.AddSeparator();
        _rowMenu.AddItem("Remove" + them, 5);
        _rowMenu.Position = (Vector2I)(_table.GetScreenTransform() * _table.GetLocalMousePosition());
        _rowMenu.Popup();
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

    ActionRef? _idleShownFor;

    static bool SameAction(ActionRef? a, ActionRef? b) =>
        a == null || b == null ? a == b : a.Entity == b.Entity && a.Target == b.Target && a.Area == b.Area;

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
        // Loading a queue or Undo can change the idle action: show it.
        if (KitGame.Current is Game ig && !SameAction(_idleShownFor, st.Idle))
        {
            _idleShownFor = st.Idle;
            if (!SameAction(_idle.Selected, st.Idle)) { _idle.Fill(ig); _idle.Show(st.Idle); }
        }
        if (st.Running && !_tabPinned && _panels.Any(p => p.Visible)) ShowTab(-1);
        if (_tab == 3 && _panels[3].Visible) RefreshHistory();
        UpdateHistoryBadge();
        if (_tab == 2 && _panels[2].Visible) RefreshCurrent();

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

    string _beltSkill = "";

    void OpenBelts()
    {
        _tabPinned = true;
        ShowTab(5);
        if (_beltSkill.Length > 0) _beltEditor.ShowSkill(_beltSkill);
    }

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
        bool none = _belt.ItemCount == 1;
        _belt.Visible = _beltEdit.Visible = !none;
        _beltNone.Visible = _beltMake.Visible = none;
        _beltSkill = skill;
        _beltNone.Text = $"No {Catalog.Skills(KitGame.Current!).FirstOrDefault(k => k.Id == skill)?.Name ?? "toolbelt"} sets yet, so the toolbelt stays as it is.";
        _belt.TooltipText = "A toolbelt set to put on when this step starts. Make sets in the Toolbelts tab.";
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
            : Starter.BestScored(game, s.Action.Entity, s.Funding, s.UseRewardMode).Take(5).Select(x => (x.Ref, Value: x.Xp, Unit: "XP")).ToList();
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
        _idleNote.Text = st.Idle == null ? "None set: when the queue waits or ends, your character keeps doing whatever it was doing." : "";
        _idleNote.Visible = st.Idle == null;
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
