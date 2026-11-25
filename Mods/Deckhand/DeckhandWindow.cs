using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Deckhand;

/// <summary>F3: the passive chores (ranching, the grain mill, farming notices) and what was done.</summary>
sealed class DeckhandWindow
{
    readonly Hand _h;
    readonly KitWindow _win;
    readonly Label _ranchState, _millState;
    readonly CheckBox _pet, _repair, _fill, _restart, _alerts;
    readonly OptionButton _product;
    List<Hand.MillRecipe> _recipes = new();
    string _productKey = "";
    readonly SpinBox _below, _reserve;
    readonly Button _repairNow, _petNow;
    readonly GridContainer _millGrid;
    readonly ItemList _log;
    bool _filling;
    string _millKey = "";

    public DeckhandWindow(Hand h)
    {
        _h = h;
        _win = new KitWindow("Deckhand", "ranching, the mill and farming, looked after", new Vector2(820, 0));
        _win.SetHelp(Help.Text);

        // ---- ranching ----
        var ranch = Card("Ranching");
        _ranchState = Kit.Label("", null, 13);
        _ranchState.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ranchState.CustomMinimumSize = new Vector2(760, 0);
        ranch.AddChild(_ranchState);
        _pet = Check("Pet animals when they want it", c => c.Pet = _pet!.ButtonPressed);
        _petNow = Kit.Btn("Pet all now", () => { if (GameAccess.CurrentGame is Game g) { var r = Hand.Ranch(g); r?.PetAllAnimals(); Refresh(); } });
        ranch.AddChild(Kit.Row(_pet, Kit.Spacer(), _petNow));
        _repair = Check("Repair enclosures when maintenance drops below", c => c.Repair = _repair!.ButtonPressed);
        _below = Kit.Spin(new SpinBox { MinValue = 1, MaxValue = 99, Suffix = "%", CustomMinimumSize = new Vector2(100, 0) });
        _below.ValueChanged += v => Set(c => c.RepairBelow = (int)v);
        _reserve = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 1e12, Prefix = "keep at least", Suffix = "GP", CustomMinimumSize = new Vector2(230, 0) });
        _reserve.ValueChanged += v => Set(c => c.GpReserve = (long)v);
        _reserve.TooltipText = "Repairs by rule never take your GP below this.";
        _repairNow = Kit.Btn("Repair all now", () => { if (GameAccess.CurrentGame is Game g) { _h.DoRanch(g, true); Refresh(); } });
        ranch.AddChild(Kit.Row(_repair, _below, _reserve, Kit.Spacer(), _repairNow));

        // ---- grain mill ----
        var mill = Card("Grain mill");
        _millState = Kit.Label("", null, 13);
        mill.AddChild(_millState);
        // The mill's product: switching stops the mill, collects, picks, and starts it again (as the game asks).
        _product = new OptionButton { CustomMinimumSize = new Vector2(320, 0) };
        _product.ItemSelected += i =>
        {
            if (_filling || GameAccess.CurrentGame is not Game g || i < 0 || i >= _recipes.Count) return;
            string why = _h.SwitchMill(g, _recipes[(int)i].Active);
            if (why.Length > 0) _millState.Text = why;
            Refresh();
        };
        _restart = Check("Start the mill again when it stopped because it ran out", c => c.MillRestart = _restart!.ButtonPressed);
        mill.AddChild(Kit.Row(Kit.Label("Make:", null, 13), _product, Kit.Spacer(), _restart));
        _fill = Check("Keep the mill stocked from the bank:", c => c.MillFill = _fill!.ButtonPressed);
        mill.AddChild(_fill);
        _millGrid = new GridContainer { Columns = 5 };
        _millGrid.AddThemeConstantOverride("h_separation", 14);
        mill.AddChild(_millGrid);

        // ---- farming ----
        var farm = Card("Farming");
        _alerts = Check("Tell me (with Beacon) when farming automation or auto-plant can't be paid for, a harvest finds the bank full, or compost runs out", c => c.FarmAlerts = _alerts!.ButtonPressed);
        farm.AddChild(_alerts);

        // ---- log ----
        var logCard = Card("What Deckhand did");
        _log = new ItemList { CustomMinimumSize = new Vector2(0, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        logCard.AddChild(_log);

        _win.Opened += Refresh;
        _h.Changed += () => { if (_win.Visible) Refresh(); };
        var timer = new Godot.Timer { WaitTime = 3, Autostart = true };
        timer.Timeout += () => { if (_win.Visible) Refresh(); };
        _win.Root.AddChild(timer);

        _win.SetTour(
            new TourStep("What Deckhand does", "Deckhand does the small upkeep jobs so you don't have to keep checking: petting and repairing in Ranching, keeping the grain mill running, and warning you about farming problems."),
            new TourStep("Ranching", "Pet animals when they want it, and repair enclosures before maintenance gets low. Repairs never take your GP below the amount you set.", () => ranch.GetParent() as Control),
            new TourStep("Grain mill", "Pick what the mill makes; switching handles the stop and collect for you. Deckhand can restart it and refill it from the bank.", () => mill.GetParent() as Control),
            new TourStep("Farming", "With Beacon installed, you get an alert when automation can't be paid for, a harvest finds the bank full, or compost runs out.", () => farm.GetParent() as Control),
            new TourStep("What it did", "Every job Deckhand did, newest first.", () => _log));
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";

    VBoxContainer Card(string title)
    {
        var card = new PanelContainer { ThemeTypeVariation = "Card" };
        var box = new VBoxContainer();
        card.AddChild(box);
        box.AddChild(Kit.Label(title, Kit.Parchment, 15, "Heading"));
        _win.Body.AddChild(card);
        return box;
    }

    CheckBox Check(string text, Action<Chores> set)
    {
        var c = new CheckBox { Text = text };
        c.Toggled += _ => Set(set);
        return c;
    }

    void Set(Action<Chores> change)
    {
        if (_filling) return;
        change(_h.Chores);
        _h.Save();
    }

    void Refresh()
    {
        Game? game = GameAccess.CurrentGame;
        _filling = true;
        try
        {
            Chores c = _h.Chores;
            _pet.SetPressedNoSignal(c.Pet);
            _repair.SetPressedNoSignal(c.Repair);
            _fill.SetPressedNoSignal(c.MillFill);
            _restart.SetPressedNoSignal(c.MillRestart);
            _alerts.SetPressedNoSignal(c.FarmAlerts);
            if (!_below.GetLineEdit().HasFocus()) _below.SetValueNoSignal(c.RepairBelow);
            if (!_reserve.GetLineEdit().HasFocus()) _reserve.SetValueNoSignal(c.GpReserve);

            if (game == null)
            {
                _ranchState.Text = _millState.Text = "Load a character.";
                return;
            }

            var r = _h.Ranching(game);
            _ranchState.Text = r == null || r.Enclosures == 0
                ? "No enclosures built yet."
                : $"{r.Enclosures} enclosure{(r.Enclosures == 1 ? "" : "s")}, lowest maintenance {r.LowestMaintenance:P0}. "
                  + (r.WantPets > 0 ? $"{r.WantPets} animal{(r.WantPets == 1 ? " wants" : "s want")} petting. " : "No animal wants petting. ")
                  + (r.CanRepair ? $"Repairing everything now costs {r.RepairCost}." : "Nothing needs repair.")
                  + (_h.Blocked.Count > 0 ? "\nCan't repair: " + string.Join("; ", _h.Blocked) + "." : "");
            _ranchState.Modulate = _h.Blocked.Count > 0 ? Kit.Gold : Colors.White;
            _petNow.Disabled = r == null || r.WantPets == 0;
            _repairNow.Disabled = r == null || !r.CanRepair;

            var m = _h.MillState(game);
            if (m == null) { _millState.Text = "No grain mill yet."; ClearGrid(); }
            else
            {
                var (recipe, active, rows) = m.Value;
                var recipes = _h.MillRecipes(game);
                // Rebuilt only when something changed, so an open list isn't closed by the refresh.
                string key = string.Join("|", recipes.Select(x => $"{x.Name}:{x.Unlocked}")) + "#" + recipe;
                if (key != _productKey)
                {
                _productKey = key;
                _recipes = recipes;
                _product.Clear();
                int sel = -1;
                for (int i = 0; i < _recipes.Count; i++)
                {
                    var rec = _recipes[i];
                    _product.AddItem(rec.Unlocked ? $"{rec.Name} (uses {rec.Uses})" : $"{rec.Name} (needs {rec.Needs})", i);
                    _product.SetItemDisabled(i, !rec.Unlocked);
                    if (rec.Name == recipe) sel = i;
                }
                _product.Select(sel);
                if (sel < 0) _product.Text = "Pick a product";
                }
                _millState.Text = recipe.Length == 0 ? "No product picked: pick one to start the mill." : $"{recipe}: {(active ? "running" : "stopped")}. What it makes goes straight to the bank.";
                FillGrid(rows);
            }

            _log.Clear();
            foreach (Done d in _h.Log) _log.AddItem($"{d.Time:HH:mm}  {d.Text}", null, false);
            if (_h.Log.Count == 0) _log.AddItem("Nothing yet this session.", null, false);
        }
        finally { _filling = false; }
    }

    void ClearGrid()
    {
        foreach (Node n in _millGrid.GetChildren()) { _millGrid.RemoveChild(n); n.QueueFree(); }
        _millKey = "";
    }

    readonly Dictionary<string, (Label Mill, Label Bank, CheckBox Fill, SpinBox Leave)> _cells = new();

    void FillGrid(List<Hand.MillRow> rows)
    {
        // Built once per set of grains; after that only the numbers change (typing in a box is never interrupted).
        string key = string.Join("|", rows.Select(r => r.Item.Id));
        if (key != _millKey)
        {
            ClearGrid();
            _cells.Clear();
            _millKey = key;
            foreach (string h in new[] { "Grain", "In the mill", "In the bank", "Fill", "Leave in the bank" })
                _millGrid.AddChild(Kit.Label(h, Kit.Parchment, 13));
            foreach (var row in rows)
            {
                string id = row.Item.Id;
                var name = Kit.Label(row.Item.DisplayName.ToString(), null, 13);
                var icon = new HBoxContainer();
                icon.AddChild(new TextureRect { Texture = Kit.Icon(row.Item.Icon, 18), StretchMode = TextureRect.StretchModeEnum.KeepCentered });
                icon.AddChild(name);
                var inMill = Kit.Label("", null, 13);
                var inBank = Kit.Label("", null, 13, "Dim");
                var fill = new CheckBox();
                var leave = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 1e9, CustomMinimumSize = new Vector2(130, 0) });
                fill.Toggled += on => { if (!_filling) { var rule = Rule(id); rule.Fill = on; _h.SetMillRule(id, rule); } };
                leave.ValueChanged += v => { if (!_filling) { var rule = Rule(id); rule.LeaveInBank = (long)v; _h.SetMillRule(id, rule); } };
                _millGrid.AddChild(icon);
                _millGrid.AddChild(inMill);
                _millGrid.AddChild(inBank);
                _millGrid.AddChild(fill);
                _millGrid.AddChild(leave);
                _cells[id] = (inMill, inBank, fill, leave);
            }
        }
        foreach (var row in rows)
        {
            if (!_cells.TryGetValue(row.Item.Id, out var cell)) continue;
            MillItem rule = Rule(row.Item.Id);
            cell.Mill.Text = row.Room > 0 ? $"{row.InMill:N0} (room for {row.Room:N0})" : $"{row.InMill:N0} (full)";
            cell.Bank.Text = row.InBank.ToString("N0");
            cell.Fill.SetPressedNoSignal(rule.Fill);
            if (!cell.Leave.GetLineEdit().HasFocus()) cell.Leave.SetValueNoSignal(rule.LeaveInBank);
        }
    }

    MillItem Rule(string id) => _h.Chores.Mill.TryGetValue(id, out MillItem? r) ? r : new MillItem();
}
