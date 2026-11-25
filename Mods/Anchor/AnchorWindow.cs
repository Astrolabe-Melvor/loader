using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Anchor;

/// <summary>F4: each character's backups, back up now with a note, restore (from the title screen), copy as an export string.</summary>
sealed class AnchorWindow
{
    readonly Vault _v;
    readonly KitWindow _win;
    readonly OptionButton _who;
    readonly Label _summary, _msg, _selLabel;
    readonly LineEdit _note;
    readonly Button _backup, _restore, _copy, _delete, _saveNote;
    readonly LineEdit _editNote;
    readonly Tree _table;
    List<Character> _chars = new();
    Backup? _sel;
    double _confirmUntil;

    public AnchorWindow(Vault v)
    {
        _v = v;
        _win = new KitWindow("Anchor", "save backups", new Vector2(900, 0));
        _win.SetHelp(Help.Text);

        _who = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        _who.ItemSelected += _ => { _sel = null; Fill(); };
        _summary = Kit.Label("", null, 13, "Dim");
        _summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _win.Body.AddChild(Kit.Row(Kit.Label("Character:", null, 13), _who, _summary));

        _note = new LineEdit { PlaceholderText = "Note (optional), e.g. before spending marks", CustomMinimumSize = new Vector2(360, 0) };
        _note.TextSubmitted += _ => BackupNow();
        _backup = Kit.Btn("Back up now", BackupNow, action: true);
        _win.Body.AddChild(Kit.Row(_note, _backup));

        _table = new Tree
        {
            Columns = 6, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row,
            CustomMinimumSize = new Vector2(0, 320), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        string[] cols = { "When", "Why", "Total level", "GP", "Note", "Size" };
        int[] widths = { 170, 130, 100, 100, 260, 80 };
        for (int c = 0; c < cols.Length; c++)
        {
            _table.SetColumnTitle(c, cols[c]);
            _table.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _table.SetColumnCustomMinimumWidth(c, widths[c]);
            _table.SetColumnExpand(c, c == 4);
        }
        _table.ItemSelected += () => Select(_table.GetSelected()?.GetMetadata(0).AsString());
        _win.Body.AddChild(_table);

        _selLabel = Kit.Label("Pick a backup.", null, 13, "Dim");
        _selLabel.CustomMinimumSize = new Vector2(220, 0);
        _editNote = new LineEdit { PlaceholderText = "note", CustomMinimumSize = new Vector2(220, 0) };
        _saveNote = Kit.Btn("Save note", SaveNote);
        _restore = Kit.Btn("Restore this backup", Restore, action: true);
        _copy = Kit.Btn("Copy as export string", Copy);
        _copy.TooltipText = "The game's own Import (title screen) accepts this text.";
        _delete = Kit.Btn("Delete", Delete);
        _win.Body.AddChild(Kit.Row(_selLabel, _editNote, _saveNote, Kit.Spacer(), _copy, _delete, _restore));

        _msg = Kit.Label("", null, 13);
        _msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _msg.CustomMinimumSize = new Vector2(600, 0);
        _win.Body.AddChild(_msg);

        // Options.
        var o = v.Options;
        var every = Kit.Spin(new SpinBox { MinValue = 5, MaxValue = 720, Value = o.EveryMinutes, Suffix = "min", CustomMinimumSize = new Vector2(120, 0) });
        every.ValueChanged += x => { o.EveryMinutes = (int)x; v.SaveOptions(); Fill(); };
        var keep = Kit.Spin(new SpinBox { MinValue = 3, MaxValue = 500, Value = o.KeepAuto, CustomMinimumSize = new Vector2(100, 0) });
        keep.ValueChanged += x => { o.KeepAuto = (int)x; v.SaveOptions(); };
        var days = Kit.Spin(new SpinBox { MinValue = 0, MaxValue = 365, Value = o.KeepDays, Suffix = "days", CustomMinimumSize = new Vector2(120, 0) });
        days.ValueChanged += x => { o.KeepDays = (int)x; v.SaveOptions(); };
        _win.Body.AddChild(Kit.Row(
            Kit.Label("Back up the open character every", null, 13, "Dim"), every,
            Kit.Label("keep the newest", null, 13, "Dim"), keep, Kit.Label("automatic backups, plus one a day for", null, 13, "Dim"), days,
            Kit.Spacer(), Kit.Btn("Open backups folder", () => { System.IO.Directory.CreateDirectory(v.Root); OS.ShellOpen(v.Root); })));

        _win.Opened += Refresh;
        _v.Changed += () => { if (_win.Visible) Refresh(); };

        _win.SetTour(
            new TourStep("What Anchor does", "Anchor keeps copies of your save, so a mistake (a bad sale, a wrong unlock) can be undone. It backs up on a timer and when the game starts."),
            new TourStep("Which character", "Backups are kept per character. The one you're playing is picked.", () => _who),
            new TourStep("Back up now", "Makes a backup right away. Add a note so you know which is which later.", () => _backup),
            new TourStep("Your backups", "Newest first, with total level and GP so you can tell them apart. Pick one to work on it.", () => _table),
            new TourStep("Restore", "Puts that backup back. Anchor saves the current state first, so a restore can be undone too. [b]Copy as export string[/b] gives text the game's own Import accepts.", () => _restore),
            new TourStep("How often", "How often to back up, and how many to keep. One a day is kept for longer as well.", () => (Control?)_win.Body.GetChild(_win.Body.GetChildCount() - 1)));
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";

    Character? Who => _who.Selected >= 0 && _who.Selected < _chars.Count ? _chars[_who.Selected] : null;

    static string Num(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e9 ? $"{v / 1e9:0.##}B" : a >= 1e6 ? $"{v / 1e6:0.##}M" : a >= 1e4 ? $"{v / 1e3:0.#}k" : $"{v:N0}";
    }

    static string Ago(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} days ago";
    }

    static string Why(Backup b) => b.Kind switch
    {
        "timer" => "timer",
        "launch" => "game start",
        "restore" => "before a restore",
        _ => "by you",
    };

    void Refresh()
    {
        // Default to the open character, else keep the pick.
        Guid? keepId = Who?.Id;
        if (KitGame.Current != null && Vault.ActiveId() is Guid active && keepId == null) keepId = active;
        _chars = _v.Characters();
        _who.Clear();
        foreach (Character c in _chars) _who.AddItem(c.InGame ? c.Name : $"{c.Name} (not in the game now)");
        int at = _chars.FindIndex(c => c.Id == keepId);
        if (_chars.Count > 0) _who.Select(at >= 0 ? at : 0);
        Fill();
    }

    void Fill()
    {
        _table.Clear();
        TreeItem root = _table.CreateItem();
        Character? who = Who;
        bool inGame = KitGame.Current != null;
        if (who == null)
        {
            _summary.Text = "No characters yet. Backups start once the game has read its saves.";
            _backup.Disabled = true;
            Select(null);
            return;
        }
        var list = _v.Backups(who.Id);
        _backup.Disabled = !who.InGame;
        _backup.TooltipText = who.InGame ? "" : "This character is not in the game's save list; restore a backup to bring it back.";
        _summary.Text = (list.Count == 0 ? "No backups yet." : $"{list.Count} backups, newest {Ago(list[0].Time)}.")
            + $" Automatic: when the game starts, and every {_v.Options.EveryMinutes} min while this character is open."
            + (inGame ? " Restoring works from the title screen." : "");
        foreach (Backup b in list)
        {
            TreeItem t = _table.CreateItem(root);
            t.SetMetadata(0, b.File);
            t.SetText(0, b.Time.ToString("MMM d, HH:mm"));
            t.SetTooltipText(0, Ago(b.Time));
            t.SetText(1, Why(b));
            t.SetCustomColor(1, b.Auto ? Kit.Dim : Kit.Link);
            t.SetText(2, b.TotalLevel.ToString("N0"));
            t.SetText(3, Num(b.Gp));
            t.SetCustomColor(3, Kit.Gold);
            t.SetText(4, b.Note);
            t.SetTooltipText(4, b.Note);
            t.SetText(5, $"{b.Bytes / 1024.0:0} KB");
            t.SetCustomColor(5, Kit.Dim);
            if (b == _sel) t.Select(0);
        }
        if (list.Count == 0)
        {
            TreeItem t = _table.CreateItem(root);
            t.SetText(0, "No backups for this character yet.");
            t.SetCustomColor(0, Kit.Dim);
        }
        Select(_sel?.File);
    }

    void Select(string? file)
    {
        Character? who = Who;
        _sel = who == null || file == null ? null : _v.Backups(who.Id).FirstOrDefault(b => b.File == file);
        bool on = _sel != null;
        _confirmUntil = 0;
        _copy.Disabled = _delete.Disabled = _saveNote.Disabled = !on;
        _editNote.Editable = on;
        _editNote.Text = _sel?.Note ?? "";
        bool inGame = KitGame.Current != null;
        _restore.Disabled = !on || inGame;
        _restore.Text = "Restore this backup";
        _restore.TooltipText = inGame ? "Return to the title screen first: the open character would save over a restore within seconds." : "";
        _selLabel.Text = _sel == null ? "Pick a backup." : $"{_sel.Time:MMM d, HH:mm}, level {_sel.TotalLevel:N0}";
    }

    void BackupNow()
    {
        Character? who = Who;
        if (who == null) return;
        string? why = _v.TakeBackup(who.Id, "manual", _note.Text, out Backup? made);
        if (why != null) { _msg.Text = "Not backed up: " + why; _msg.Modulate = Kit.Bad; return; }
        _note.Text = "";
        _sel = made;
        _msg.Text = $"Backed up {who.Name}.";
        _msg.Modulate = Kit.Good;
        Fill();
    }

    async void Restore()
    {
        Character? who = Who;
        Backup? b = _sel;
        if (who == null || b == null) return;
        // Two clicks: the first arms it for 5 seconds.
        double now = Time.GetTicksMsec() / 1000.0;
        if (now > _confirmUntil)
        {
            _confirmUntil = now + 5;
            _restore.Text = "Click again to restore";
            _msg.Text = $"This replaces {who.Name}'s save with the one from {b.Time:MMM d, HH:mm}. The current save is backed up first, so you can undo it.";
            _msg.Modulate = Kit.Gold;
            return;
        }
        _confirmUntil = 0;
        _restore.Disabled = true;
        _msg.Text = "Restoring...";
        _msg.Modulate = Kit.Text;
        string err = await _v.Restore(who.Id, b);
        _msg.Text = err.Length == 0 ? $"Restored {who.Name} to {b.Time:MMM d, HH:mm}. Load the character to play from there." : err;
        _msg.Modulate = err.Length == 0 ? Kit.Good : Kit.Bad;
        Refresh();
    }

    void Copy()
    {
        Character? who = Who;
        if (who == null || _sel == null) return;
        string? text = _v.Text(who.Id, _sel);
        if (text == null) { _msg.Text = "The backup file is missing."; _msg.Modulate = Kit.Bad; return; }
        DisplayServer.ClipboardSet(text);
        _msg.Text = "Copied. The game's Import on the title screen accepts it (as a new character).";
        _msg.Modulate = Kit.Good;
    }

    void Delete()
    {
        Character? who = Who;
        if (who == null || _sel == null) return;
        _v.Delete(who.Id, _sel);
        _sel = null;
        _msg.Text = "Deleted.";
        _msg.Modulate = Kit.Text;
    }

    void SaveNote()
    {
        Character? who = Who;
        if (who == null || _sel == null) return;
        _v.SetNote(who.Id, _sel, _editNote.Text);
    }
}
