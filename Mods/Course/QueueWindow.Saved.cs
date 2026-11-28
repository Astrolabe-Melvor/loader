using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// The Saved and shared tab: the current queue (saved or changed), your saved queues with what's in each, and share
/// codes with a look at what a pasted code holds before it goes anywhere.
/// </summary>
sealed partial class QueueWindow
{
    Label _curName = null!, _curSteps = null!, _curChanged = null!;
    Button _curSave = null!;
    HBoxContainer _nameRow = null!;
    Label _nameAsk = null!;
    LineEdit _nameEdit = null!;
    Button _nameOk = null!;
    Action<string>? _nameDone;
    string _nameKeep = "";        // the name the box started with (renaming to the same name does nothing)
    Tree _savedTable = null!;
    Label _savedTitle = null!, _savedSteps = null!, _savedMeta = null!;
    Control _savedButtons = null!;
    Button _savedDelete = null!;
    string? _savedPick;
    LineEdit _codeEdit = null!;
    Label _codePreview = null!, _shareNote = null!;
    Control _codeButtons = null!;
    List<Step> _codeSteps = new();

    void BuildSaved(VBoxContainer box)
    {
        // The queue you have now.
        _curName = Kit.Label("", null, 15);
        _curSteps = Kit.Label("", null, 13, "Dim");
        _curChanged = Kit.Label("", Kit.Gold, 13);
        _curSave = Kit.Btn("Save", SaveCurrent, action: true);
        box.AddChild(Kit.Row(Field("Now"), _curName, _curSteps, _curChanged, Kit.Spacer(), _curSave,
            WithTip(Kit.Btn("Save as new", () => AskName("Save the current queue as", "", "Save", n => { _queue.SaveAs(n); _savedPick = n; Note($"Saved as \"{n}\"."); })),
                "Keep it under a new name; the old saved queue stays as it was")));

        // One line for naming things: save as, rename, save a code.
        _nameAsk = Kit.Label("", null, 13, "Dim");
        _nameEdit = new LineEdit { CustomMinimumSize = new Vector2(320, 0), PlaceholderText = "Name" };
        _nameEdit.TextChanged += _ => NameCheck();
        _nameEdit.TextSubmitted += _ => NameOk();
        _nameOk = Kit.Btn("Save", NameOk, action: true);
        _nameRow = Kit.Row(_nameAsk, _nameEdit, _nameOk, Kit.Btn("Cancel", () => _nameRow.Visible = false));
        _nameRow.Visible = false;
        box.AddChild(Indent(_nameRow, 46));

        // Saved queues on the left, the picked one on the right.
        _savedTable = MakeTable(new[] { "Your saved queues", "Steps", "Saved" }, new[] { 200, 56, 90 }, 0, 190,
            expandCols: new[] { 0 }, ratios: new[] { 1, 0, 0 });
        _savedTable.CustomMinimumSize = new Vector2(380, 190);
        _savedTable.ItemSelected += () => { _savedPick = _savedTable.GetSelected()?.GetMetadata(0).AsString(); ShowSaved(); };
        _savedTable.ItemActivated += () => { if (_savedPick != null) LoadPicked(); };

        var detail = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        detail.AddThemeConstantOverride("separation", 6);
        _savedTitle = Kit.Label("", null, 15, "Heading");
        _savedSteps = Kit.Label("", null, 13);
        _savedSteps.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _savedSteps.CustomMinimumSize = new Vector2(420, 0);
        _savedMeta = Kit.Label("", null, 12, "Dim");
        _savedMeta.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _savedMeta.CustomMinimumSize = new Vector2(420, 0);
        _savedDelete = Ask("Delete", "Press again to delete", () => Picked(n => { _queue.DeleteSaved(n); _savedPick = null; Note($"Deleted \"{n}\"."); }));
        var buttons = new VBoxContainer();
        buttons.AddThemeConstantOverride("separation", 4);
        buttons.AddChild(Kit.Row(
            WithTip(Kit.Btn("Load", LoadPicked, action: true), "Replace the current queue with this one (Undo brings yours back)"),
            WithTip(Kit.Btn("Add to my queue", () => Picked(n => { _queue.AddSaved(n); Note($"Added the steps of \"{n}\" to the end of your queue. Undo takes them off."); })), "Put its steps after the ones in your queue now. Your queue keeps its name."),
            WithTip(Kit.Btn("Copy code", () => Picked(CopySavedCode)), "Copy a share code for this saved queue"),
            WithTip(Kit.Btn("Rename", () => Picked(n => AskName("Rename to", n, "Rename", to => { _queue.RenameSaved(n, to); _savedPick = to; Note($"Renamed to \"{to}\"."); }))), "Give it a new name"),
            _savedDelete));
        buttons.AddChild(Kit.Label("Load replaces the queue you have now and stops it. Undo brings yours back.", null, 12, "Dim"));
        _savedButtons = buttons;
        detail.AddChild(_savedTitle);
        detail.AddChild(_savedSteps);
        detail.AddChild(_savedMeta);
        detail.AddChild(buttons);
        var split = Kit.Row(_savedTable, detail);
        split.AddThemeConstantOverride("separation", 14);
        box.AddChild(split);

        // Share codes: paste one to see what's in it, then pick what to do with it.
        box.AddChild(Kit.Label("Share codes", null, 15, "Heading"));
        _codeEdit = new LineEdit { PlaceholderText = "Paste a code here (they start with CRS1:)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _codeEdit.TextChanged += _ => ReadCode();
        box.AddChild(Kit.Row(_codeEdit,
            WithTip(Kit.Btn("Paste", () => { _codeEdit.Text = DisplayServer.ClipboardGet().Trim(); ReadCode(); }), "Paste the code you copied"),
            WithTip(Kit.Btn("Copy my queue as a code", CopyCode), "Copy a code for the queue you have now, to share or keep")));
        _codePreview = Hint("");
        _codePreview.CustomMinimumSize = new Vector2(860, 0);
        box.AddChild(_codePreview);
        _codeButtons = Kit.Row(
            WithTip(Kit.Btn("Add to my queue", () => UseCode(add: true)), "Put the code's steps after the ones in your queue now"),
            WithTip(Kit.Btn("Replace my queue", () => UseCode(add: false)), "Use only the code's steps (Undo brings yours back)"),
            WithTip(Kit.Btn("Save as new", () => AskName("Save the code as", "", "Save", n => { _queue.SaveStepsAs(n, _codeSteps); _savedPick = n; Note($"Saved the code as \"{n}\"."); })),
                "Keep it with your saved queues without changing the queue you have now"));
        _codeButtons.Visible = false;
        box.AddChild(_codeButtons);
        box.AddChild(Kit.Label("Codes also come from the planner on the Astrolabe wiki (Send to Course). Saved queues are shared by all your characters.", null, 12, "Dim"));
        _shareNote = Kit.Label("", Kit.Good, 13);
        box.AddChild(_shareNote);
    }

    /// <summary>A button that asks first: the first press says <paramref name="ask"/>, a second within 4 seconds does it.</summary>
    internal static Button Ask(string text, string ask, Action act)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        int armed = 0;
        b.Pressed += () =>
        {
            if (armed == 0 || b.Text != ask)
            {
                int me = ++armed;
                string was = b.Text;
                b.Text = ask;
                b.GetTree().CreateTimer(4).Timeout += () => { if (armed == me && GodotObject.IsInstanceValid(b)) { b.Text = was; armed = 0; } };
                return;
            }
            armed = 0;
            b.Text = text;
            act();
        };
        return b;
    }

    void Note(string text)
    {
        _shareNote.Text = text;
        _shareNote.AddThemeColorOverride("font_color", Kit.Good);
        RefreshSaved();
    }

    void Picked(Action<string> act) { if (_savedPick != null) act(_savedPick); }

    void LoadPicked() => Picked(n => { _queue.LoadSaved(n); Note($"Loaded \"{n}\". Undo brings your last queue back."); });

    void SaveCurrent()
    {
        if (_queue.State.Name.Length == 0) { AskName("Save the current queue as", "", "Save", n => { _queue.SaveAs(n); _savedPick = n; Note($"Saved as \"{n}\"."); }); return; }
        string n = _queue.State.Name;
        _queue.SaveAs(n);
        _savedPick = n;
        Note($"Saved \"{n}\".");
    }

    void AskName(string ask, string start, string ok, Action<string> done)
    {
        _nameAsk.Text = ask;
        _nameEdit.Text = start;
        _nameKeep = start;
        _nameOkText = ok;
        _nameDone = done;
        _nameRow.Visible = true;
        _nameEdit.GrabFocus();
        _nameEdit.SelectAll();
        NameCheck();
    }

    bool _nameArmed;
    string _nameOkText = "Save";

    /// <summary>Saving over a saved queue with the same name takes a second press.</summary>
    void NameCheck()
    {
        _nameArmed = false;
        string n = _nameEdit.Text.Trim();
        bool taken = n.Length > 0 && n != _nameKeep && _queue.SavedExists(n);
        _nameOk.Disabled = n.Length == 0 || (n == _nameKeep && _nameKeep.Length > 0);
        _nameOk.TooltipText = taken ? $"There's already a saved queue called \"{n}\"" : "";
        _nameOk.Text = _nameOkText;
    }

    void NameOk()
    {
        string n = _nameEdit.Text.Trim();
        if (n.Length == 0 || _nameDone == null) return;
        if (n != _nameKeep && _queue.SavedExists(n))
        {
            if (_nameAsk.Text.StartsWith("Rename")) { _nameOk.TooltipText = $"There's already a saved queue called \"{n}\": pick another name"; Warn($"There's already a saved queue called \"{n}\"."); return; }
            if (!_nameArmed) { _nameArmed = true; _nameOk.Text = $"Replace \"{n}\""; return; }
        }
        _nameRow.Visible = false;
        _nameDone(n);
    }

    void Warn(string text)
    {
        _shareNote.Text = text;
        _shareNote.AddThemeColorOverride("font_color", Kit.Gold);
    }

    /// <summary>The "Now" line: cheap, so it follows every change while the tab is open.</summary>
    void RefreshCurrent()
    {
        QueueState st = _queue.State;
        bool changed = _queue.ChangedSinceSaved;
        _curName.Text = st.Name.Length > 0 ? st.Name : "Not saved yet";
        _curSteps.Text = $"{st.Steps.Count} step{(st.Steps.Count == 1 ? "" : "s")}" + (st.Loop ? ", loops" : "");
        _curChanged.Text = st.Name.Length > 0 && changed ? "changed since you saved it" : "";
        _curSave.Text = st.Name.Length > 0 ? "Save" : "Save as";
        _curSave.Disabled = st.Steps.Count == 0 || (st.Name.Length > 0 && !changed);
        _curSave.TooltipText = st.Name.Length > 0 ? $"Save over \"{st.Name}\"" : "Give the queue a name and keep it";
    }

    void RefreshSaved()
    {
        RefreshCurrent();
        QueueState st = _queue.State;
        var names = _queue.SavedNames().ToList();
        if (_savedPick != null && !names.Contains(_savedPick)) _savedPick = null;
        _savedPick ??= names.Contains(st.Name) ? st.Name : names.FirstOrDefault();
        _savedTable.Clear();
        TreeItem root = _savedTable.CreateItem();
        foreach (string n in names)
        {
            QueueState? q = _queue.ReadSaved(n);
            TreeItem r = _savedTable.CreateItem(root);
            r.SetMetadata(0, n);
            r.SetText(0, n == st.Name ? $"{n}  (loaded)" : n);
            if (n == st.Name) r.SetCustomColor(0, Kit.Good);
            r.SetText(1, q == null ? "?" : q.Steps.Count.ToString());
            r.SetTextAlignment(1, HorizontalAlignment.Right);
            DateTime t = _queue.SavedTime(n);
            r.SetText(2, t == default ? "" : t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("MMM d"));
            r.SetCustomColor(2, Kit.Dim);
            if (n == _savedPick) r.Select(0);
        }
        if (names.Count == 0)
        {
            TreeItem r = _savedTable.CreateItem(root);
            r.SetText(0, "None yet");
            r.SetCustomColor(0, Kit.Dim);
            for (int c = 0; c < 3; c++) r.SetSelectable(c, false);
        }
        ShowSaved();
    }

    void ShowSaved()
    {
        QueueState? q = _savedPick == null ? null : _queue.ReadSaved(_savedPick);
        _savedButtons.Visible = q != null;
        if (q == null)
        {
            _savedTitle.Text = "Nothing saved yet";
            _savedSteps.Text = "Save the queue you have now to keep it, or paste a share code below and save that.";
            _savedMeta.Text = "";
            return;
        }
        _savedTitle.Text = _savedPick!;
        var lines = q.Steps.Take(8).Select((s, i) => $"{i + 1}. {Queue.Label(s)} {s.GoalText()}").ToList();
        if (q.Steps.Count > 8) lines.Add($"and {q.Steps.Count - 8} more");
        _savedSteps.Text = q.Steps.Count == 0 ? "No steps." : string.Join("\n", lines);
        var meta = new List<string>();
        if (q.Loop) meta.Add("Starts over when it ends.");
        if (q.Idle != null) meta.Add($"When it waits or ends: {q.Idle.Label}.");
        _savedMeta.Text = string.Join(" ", meta);
    }

    void CopySavedCode(string name)
    {
        QueueState? q = _queue.ReadSaved(name);
        if (q == null || q.Steps.Count == 0) { Warn($"\"{name}\" has no steps to share."); return; }
        DisplayServer.ClipboardSet(ShareCode.Export(q));
        Note($"Copied a code for \"{name}\" ({q.Steps.Count} steps). Anyone can paste it here.");
    }

    void CopyCode()
    {
        if (_queue.State.Steps.Count == 0) { Warn("The queue is empty: nothing to share."); return; }
        DisplayServer.ClipboardSet(ShareCode.Export(_queue.State));
        Note($"Copied a code for your queue ({_queue.State.Steps.Count} steps). Anyone can paste it here.");
    }

    /// <summary>Reads the pasted code and says what's in it; nothing changes until you pick what to do with it.</summary>
    void ReadCode()
    {
        _codeSteps = new();
        string code = _codeEdit.Text.Trim();
        Game? game = KitGame.Current;
        if (code.Length == 0 || game == null) { _codePreview.Text = ""; _codeButtons.Visible = false; return; }
        var (steps, problems) = ShareCode.Import(game, code);
        _codeSteps = steps;
        _codeButtons.Visible = steps.Count > 0;
        string what = steps.Count == 0 ? "Nothing in this code can be added."
            : $"{steps.Count} step{(steps.Count == 1 ? "" : "s")}: " + string.Join(", ", steps.Take(6).Select(s => $"{Queue.Label(s)} {s.GoalText()}"))
              + (steps.Count > 6 ? $", and {steps.Count - 6} more." : ".");
        string bad = problems.Count == 0 ? "" : " Left out: " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? "; and more." : ".");
        _codePreview.Text = what + bad;
        _codePreview.AddThemeColorOverride("font_color", steps.Count == 0 || problems.Count > 0 ? Kit.Gold : Kit.Text);
    }

    void UseCode(bool add)
    {
        if (_codeSteps.Count == 0) return;
        int n = _codeSteps.Count;
        if (add) _queue.AddRange(_codeSteps.Select(s => s.Copy()));
        else _queue.ReplaceSteps(_codeSteps);
        Note(add ? $"Added {n} step{(n == 1 ? "" : "s")} to the end of your queue. Undo takes them off." : $"Your queue is now the code's {n} step{(n == 1 ? "" : "s")}. Undo brings yours back.");
        _codeEdit.Text = "";
        ReadCode();
    }

    void RefreshQueuePick()
    {
        QueueState st = _queue.State;
        bool changed = _queue.ChangedSinceSaved;
        _queuePick.Clear();
        _queuePick.AddItem(st.Name.Length > 0 ? $"Queue: {st.Name}{(changed ? " *" : "")}" : "Queue: not saved");
        _queuePick.SetItemMetadata(0, "");
        if (st.Name.Length > 0 && changed)
        {
            _queuePick.AddItem($"Save changes to \"{st.Name}\"");
            _queuePick.SetItemMetadata(_queuePick.ItemCount - 1, "*save");
        }
        var names = _queue.SavedNames().ToList();
        if (names.Count > 0) _queuePick.AddSeparator("Load a saved queue");
        foreach (string n in names)
        {
            _queuePick.AddItem(n);
            _queuePick.SetItemMetadata(_queuePick.ItemCount - 1, n);
        }
        _queuePick.Select(0);
        _queuePick.TooltipText = st.Name.Length == 0 ? "This queue isn't saved. Save it in Saved and shared, or load a saved one here."
            : changed ? $"Changed since you saved \"{st.Name}\". Pick Save changes, or load a saved queue."
            : $"Saved as \"{st.Name}\". Pick another to load it (Undo brings this one back).";
    }

    void OnQueuePicked(long i)
    {
        string what = _queuePick.GetItemMetadata((int)i).AsString();
        if (what == "*save") _queue.SaveAs(_queue.State.Name);
        else if (what.Length > 0) _queue.LoadSaved(what);
        RefreshQueuePick();
    }
}
