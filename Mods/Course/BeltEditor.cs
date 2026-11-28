using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// The Toolbelts tab: name sets of toolbelt items per skill, copy what is on now, pick an item per slot, and mark
/// consumables to refill (from the bank, or by making or gathering more) when they run out during a step.
/// </summary>
sealed class BeltEditor
{
    readonly Queue _q;
    readonly OptionButton _skill, _set;
    readonly LineEdit _name;
    readonly GridContainer _grid;
    readonly Label _note, _used;
    /// <summary>For the tour.</summary>
    public Control Top { get; private set; } = null!;
    public Control NameRow { get; private set; } = null!;
    public Control Grid => _grid;
    readonly Button _save, _delete, _wear;
    BeltSet? _work;          // the set being edited (a copy until saved)
    bool _dirty;

    public BeltEditor(Queue q, VBoxContainer box)
    {
        _q = q;
        _skill = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        _skill.ItemSelected += _ => { _work = null; FillSets(null); };
        _set = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        _set.ItemSelected += _ => Load(_set.GetItemMetadata(_set.Selected).AsString());
        box.AddChild(Top = Kit.Row(Field("skill"), _skill, Field("set"), _set,
            Tip(Kit.Btn("New set", () => New(false)), "Start an empty set for this skill"),
            Tip(Kit.Btn("New set from what's on now", () => New(true)), "Start a set with everything in this skill's toolbelt right now")));

        _name = new LineEdit { PlaceholderText = "Name this set", CustomMinimumSize = new Vector2(260, 0) };
        _name.TextChanged += t => { if (_work != null) { _work.Name = t; Dirty(); } };
        _save = Kit.Btn("Save set", Save, action: true);
        _delete = QueueWindow.Ask("Delete set", "Press again to delete", Delete);
        _used = Kit.Label("", null, 12, "Dim");
        _wear = Tip(Kit.Btn("Put it on now", Wear), "Equip this set in the skill's toolbelt now, from the bank");
        box.AddChild(NameRow = Kit.Row(Field("name"), _name, _save, _delete, _used, Kit.Spacer(), _wear));

        _grid = new GridContainer { Columns = 5 };
        _grid.AddThemeConstantOverride("h_separation", 12);
        _grid.AddThemeConstantOverride("v_separation", 4);
        box.AddChild(_grid);

        _note = Kit.Label("", null, 12, "Dim");
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.CustomMinimumSize = new Vector2(880, 0);
        box.AddChild(_note);
    }

    static Label Field(string text) => Kit.Label(text, null, 13, "Dim");
    static T Tip<T>(T c, string tip) where T : Control { c.TooltipText = tip; return c; }

    string? SkillId => _skill.Selected < 0 ? null : _skill.GetItemMetadata(_skill.Selected).AsString();

    /// <summary>Opens on one skill (Add a step's "Make a set" and "Edit sets").</summary>
    public void ShowSkill(string skill)
    {
        for (int i = 0; i < _skill.ItemCount; i++)
            if (_skill.GetItemMetadata(i).AsString() == skill && _skill.Selected != i)
            {
                _skill.Select(i);
                _work = null;
                FillSets(null);
                return;
            }
    }

    /// <summary>Called when the tab is shown.</summary>
    public void Refresh()
    {
        Game? game = KitGame.Current;
        if (game == null) { _note.Text = "Load a character to set up toolbelts."; return; }
        string? keep = SkillId;
        _skill.Clear();
        foreach (SkillChoice sk in Catalog.Skills(game).Where(s => Belts.HasToolbelt(game, s.Id)))
        {
            if (sk.Icon != null) _skill.AddIconItem(Kit.Icon(sk.Icon), sk.Name); else _skill.AddItem(sk.Name);
            _skill.SetItemMetadata(_skill.ItemCount - 1, sk.Id);
            if (sk.Id == keep) _skill.Select(_skill.ItemCount - 1);
        }
        if (_skill.Selected < 0 && _skill.ItemCount > 0) _skill.Select(0);
        FillSets(_work?.Id);
    }

    void FillSets(string? select)
    {
        Game? game = KitGame.Current;
        string? skill = SkillId;
        _set.Clear();
        if (game == null || skill == null) return;
        foreach (BeltSet b in _q.State.Belts.Where(b => b.Skill == skill))
        {
            _set.AddItem(b.Name.Length > 0 ? b.Name : "(no name)");
            _set.SetItemMetadata(_set.ItemCount - 1, b.Id);
            if (b.Id == select) _set.Select(_set.ItemCount - 1);
        }
        if (_work != null && _q.State.Belts.All(b => b.Id != _work.Id))
        {
            _set.AddItem((_work.Name.Length > 0 ? _work.Name : "New set") + " (not saved)");
            _set.SetItemMetadata(_set.ItemCount - 1, _work.Id);
            _set.Select(_set.ItemCount - 1);
        }
        if (_set.ItemCount == 0)
        {
            _work = null;
            _set.AddItem("No sets yet");
            _set.SetItemMetadata(0, "");
            _set.Disabled = true;
        }
        else
        {
            _set.Disabled = false;
            if (_set.Selected < 0) _set.Select(0);
            if (_work == null || _work.Skill != skill) Load(_set.GetItemMetadata(_set.Selected).AsString());
        }
        Build();
    }

    void Load(string id)
    {
        BeltSet? saved = _q.State.Belts.FirstOrDefault(b => b.Id == id);
        if (saved != null)
            _work = new BeltSet { Id = saved.Id, Name = saved.Name, Skill = saved.Skill, Slots = saved.Slots.Select(s => new BeltSlot { Slot = s.Slot, Item = s.Item, Refill = s.Refill }).ToList() };
        _dirty = false;
        Build();
    }

    void New(bool fromNow)
    {
        Game? game = KitGame.Current;
        string? skill = SkillId;
        if (game == null || skill == null) return;
        int n = _q.State.Belts.Count(b => b.Skill == skill) + 1;
        _work = new BeltSet { Skill = skill, Name = $"{_skill.GetItemText(_skill.Selected)} set {n}", Slots = fromNow ? Belts.Capture(game, skill) : new() };
        _dirty = true;
        FillSets(_work.Id);
    }

    void Dirty()
    {
        _dirty = true;
        _save.Text = "Save set *";
    }

    void Save()
    {
        if (_work == null) return;
        BeltSet copy = _work;
        _q.SetOption(st =>
        {
            int at = st.Belts.FindIndex(b => b.Id == copy.Id);
            var stored = new BeltSet { Id = copy.Id, Name = copy.Name.Trim(), Skill = copy.Skill, Slots = copy.Slots.Where(s => s.Item.Length > 0).Select(s => new BeltSlot { Slot = s.Slot, Item = s.Item, Refill = s.Refill }).ToList() };
            if (at >= 0) st.Belts[at] = stored; else st.Belts.Add(stored);
        });
        _dirty = false;
        FillSets(copy.Id);
    }

    void Delete()
    {
        if (_work == null) return;
        string id = _work.Id;
        _q.SetOption(st =>
        {
            st.Belts.RemoveAll(b => b.Id == id);
            foreach (Step s in st.Steps) if (s.Belt == id) s.Belt = "";
        });
        _work = null;
        FillSets(null);
    }

    void Wear()
    {
        Game? game = KitGame.Current;
        if (game == null || _work == null) return;
        string said = Belts.Apply(game, _work);
        _note.Text = said.Length > 0 ? "Put on: " + said + "." : "Everything in this set is already on.";
        Build(keepNote: true);
    }

    /// <summary>One row per toolbelt slot: the item to put there, how many you have, and the refill amount for consumables.</summary>
    void Build(bool keepNote = false)
    {
        foreach (Node n in _grid.GetChildren()) { _grid.RemoveChild(n); n.QueueFree(); }
        _save.Text = _dirty ? "Save set *" : "Save set";
        Game? game = KitGame.Current;
        string? skill = SkillId;
        bool on = _work != null && game != null && skill != null;
        _save.Disabled = _delete.Disabled = _wear.Disabled = !on;
        _name.Editable = on;
        int users = on ? _q.State.Steps.Count(st => st.Belt == _work!.Id) : 0;
        _used.Text = users == 0 ? "" : $"used by {users} step{(users == 1 ? "" : "s")}";
        _delete.TooltipText = users == 0 ? "Delete this set" : $"Delete this set; the {users} step{(users == 1 ? "" : "s")} using it will leave the toolbelt as it is";
        if (!on)
        {
            _name.Text = "";
            if (!keepNote) _note.Text = "Make a set with \"New set\", or copy what this skill has on now. Then pick it on a step under \"toolbelt\".";
            return;
        }
        BeltSet set = _work!;
        if (_name.Text != set.Name) _name.Text = set.Name;

        foreach (string h in new[] { "Slot", "Put in", "On now", "In the bank", "Refill when used up" })
            _grid.AddChild(Kit.Label(h, Kit.Parchment, 13, "Heading"));
        foreach (Belts.SlotInfo si in Belts.Slots(game!, skill!))
        {
            BeltSlot? bs = set.Slots.FirstOrDefault(s => s.Slot == si.Slot.Id);
            _grid.AddChild(Kit.Label(si.Slot.DisplayName.ToString() + (si.Unlocked ? "" : " (locked)"), si.Unlocked ? null : Kit.Dim, 13));

            var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0), Disabled = !si.Unlocked };
            pick.AddItem("leave as is");
            pick.SetItemMetadata(0, "");
            List<Item> items = Belts.ItemsFor(game!, skill!, si.Slot);
            Item? chosen = bs == null ? null : Catalog.FindItem(game!, bs.Item);
            if (chosen != null && !items.Contains(chosen)) items.Insert(0, chosen);
            foreach (Item it in items)
            {
                pick.AddIconItem(Kit.Icon(it.Icon, 18), it.DisplayName.ToString() + (si.Item == it ? " (on now)" : ""));
                pick.SetItemMetadata(pick.ItemCount - 1, it.Id);
                if (bs?.Item == it.Id) pick.Select(pick.ItemCount - 1);
            }
            if (pick.Selected < 0) pick.Select(0);
            string slotId = si.Slot.Id;
            pick.ItemSelected += i =>
            {
                string id = pick.GetItemMetadata((int)i).AsString();
                set.Slots.RemoveAll(s => s.Slot == slotId);
                if (id.Length > 0) set.Slots.Add(new BeltSlot { Slot = slotId, Item = id });
                Dirty();
                Build();
            };
            _grid.AddChild(pick);

            // What's there now, so you can see what "Put it on now" changes: green when it already matches.
            string now = si.Item == null ? "empty" : si.Item.DisplayName.ToString() + (si.Quantity > 1 ? $" ({si.Quantity:N0})" : "");
            var nowLabel = Kit.Label(chosen == null ? now : si.Item == chosen ? "same" : now, chosen == null ? Kit.Dim : si.Item == chosen ? Kit.Good : Kit.Gold, 13);
            nowLabel.TooltipText = chosen == null ? "Left as it is." : si.Item == chosen ? "Already on." : $"Putting the set on swaps this for {chosen.DisplayName}.";
            nowLabel.MouseFilter = Control.MouseFilterEnum.Pass;
            _grid.AddChild(nowLabel);

            _grid.AddChild(Kit.Label(chosen == null ? "" : $"{Queue.BankCount(game!, chosen.Id):N0}", Kit.Dim, 13));

            if (chosen != null && Belts.IsConsumable(chosen))
            {
                var refill = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 1_000_000, Value = bs!.Refill, Prefix = "up to", CustomMinimumSize = new Vector2(150, 0) });
                refill.TooltipText = "0 = do nothing. Otherwise, when this runs out during a step: take more from the bank, or make or gather this many (and what they need), then go back to the step.";
                BeltSlot target = bs;
                refill.ValueChanged += v => { target.Refill = (int)v; Dirty(); };
                _grid.AddChild(refill);
            }
            else _grid.AddChild(Kit.Label(chosen == null ? "" : "not used up", Kit.Dim, 12));
        }
        if (!keepNote)
            _note.Text = "A step that uses this set puts these on from the bank when it starts; \"leave as is\" slots aren't touched. "
                + "On now is yellow where putting the set on would change something. A consumable with a refill above 0 is refilled when it runs out: "
                + "from the bank if there is some, otherwise the queue makes or gathers that many (and what they need) first, then goes back to the step.";
    }
}
