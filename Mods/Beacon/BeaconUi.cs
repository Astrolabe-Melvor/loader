using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Beacon;

/// <summary>Short notes at the top of the screen (each fades after a few seconds; click to dismiss), sounds and taskbar flashes.</summary>
sealed class Toasts
{
    readonly VBoxContainer _box;
    public Sounds Sounds { get; }

    public Toasts(string modFolder)
    {
        var layer = new CanvasLayer { Layer = 56, Name = "BeaconToasts" };
        _box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _box.AddThemeConstantOverride("separation", 4);
        layer.AddChild(_box);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(layer);
        Sounds = new Sounds(modFolder, layer);
    }

    public static string DefaultChime(Kind k, Sounds sounds)
    {
        if (k == Kind.LevelUp) return Sounds.SkillOwn;
        // Different game sounds for different alerts, so they can be told apart by ear.
        var game = sounds.All().Where(c => c.Id.StartsWith("res://")).ToList();
        var plain = game.Where(c => !c.Id.Contains("SkillLevelUps")).ToList();
        var pool = plain.Count >= 4 ? plain : game;
        return pool.Count == 0 ? Sounds.None : pool[((int)k - 1 + pool.Count) % pool.Count].Id;
    }

    public void Show(Alert a, Rule r, Options o, Game? game)
    {
        if (r.Show)
        {
            var card = new PanelContainer { Theme = Kit.Theme, ThemeTypeVariation = "Card", MouseFilter = Control.MouseFilterEnum.Stop };
            card.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            card.TooltipText = "Click to dismiss";
            Color c = a.Kind is Kind.LevelUp or Kind.Pet or Kind.NewItem ? Kit.Good : Kit.Gold;
            var label = Kit.Label(a.Text, c, 15);
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            card.AddChild(label);
            card.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) card.QueueFree(); };
            _box.AddChild(card);
            // Remove right away: QueueFree only takes effect next frame, so counting children after it would never drop.
            while (_box.GetChildCount() > 4)
            {
                Node old = _box.GetChild(0);
                _box.RemoveChild(old);
                old.QueueFree();
            }
            card.GetTree().CreateTimer(8).Timeout += () => { if (GodotObject.IsInstanceValid(card)) card.QueueFree(); };
            Callable.From(Place).CallDeferred();
        }
        try
        {
            string chime = r.Chime ?? DefaultChime(a.Kind, Sounds);
            AudioStream? skill = SkillSound(game, a.Skill);
            // A real level up with the skill's own sound: while the game is focused and not muted it plays that itself.
            int master = AudioServer.GetBusIndex("Master");
            bool gameHeard = DisplayServer.WindowIsFocused() && master >= 0 && !AudioServer.IsBusMute(master);
            if (!(chime == Sounds.SkillOwn && !a.Test && gameHeard)) Sounds.Play(chime, skill, o.Volume * r.Volume, o.EvenWhenMuted);
            if (r.Flash && !DisplayServer.WindowIsFocused()) DisplayServer.WindowRequestAttention();
        }
        catch { }
    }

    static AudioStream? SkillSound(Game? game, string id)
    {
        if (game == null) return null;
        var skills = game.LeveledEntities;
        var hit = id.Length > 0 ? skills.FirstOrDefault(e => e is GamesByMalcs.Melvor2.Entities.GameEntity g && g.Id == id) : null;
        return (hit ?? skills.FirstOrDefault())?.LevelUpNotificationSound;
    }

    // Top centre, under the game's top bar.
    void Place()
    {
        _box.ResetSize();
        Vector2 screen = _box.GetViewportRect().Size;
        _box.Position = new Vector2((screen.X - _box.Size.X) / 2, 96);
    }
}

/// <summary>F8: which alerts you get and how, the Discord webhook, and the last alerts.</summary>
sealed class BeaconWindow
{
    readonly Watcher _w;
    readonly Toasts _toasts;
    readonly KitWindow _win;
    readonly ItemList _recent;
    readonly List<(Kind Kind, OptionButton Pick)> _picks = new();

    public BeaconWindow(Watcher w, Toasts toasts)
    {
        _w = w;
        _toasts = toasts;
        _win = new KitWindow("Beacon", "tells you when something needs you", new Vector2(1000, 0));
        _win.SetHelp(Help.Text);

        var card = new PanelContainer { ThemeTypeVariation = "Card" };
        var box = new VBoxContainer();
        card.AddChild(box);
        _win.Body.AddChild(card);

        var grid = new GridContainer { Columns = 9 };
        grid.AddThemeConstantOverride("h_separation", 12);
        foreach (string h in new[] { "Tell me when", "On screen", "Sound", "", "Volume", "Taskbar", "Discord", "At", "" })
            grid.AddChild(Kit.Label(h, Kit.Parchment, 14, "Heading"));
        foreach (Kind k in Enum.GetValues<Kind>())
        {
            Rule r = w.Options.Of(k);
            Kind kind = k;
            grid.AddChild(Kit.Label(Watcher.Names[k], null, 13));
            grid.AddChild(Check(r.Show, v => r.Show = v));
            var pick = new OptionButton { CustomMinimumSize = new Vector2(230, 0), FitToLongestItem = false };
            pick.ItemSelected += i => { r.Chime = pick.GetItemMetadata((int)i).AsString(); _w.SaveOptions(); Preview(kind); };
            _picks.Add((k, pick));
            grid.AddChild(pick);
            grid.AddChild(WithTip(Kit.Btn("▶", () => Preview(kind)), "Play this sound"));
            var rv = new HSlider { MinValue = 0, MaxValue = 100, Step = 5, Value = r.Volume * 100, CustomMinimumSize = new Vector2(90, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            rv.TooltipText = $"{r.Volume * 100:0}% of the Beacon volume";
            rv.ValueChanged += v => { r.Volume = v / 100; rv.TooltipText = $"{v:0}% of the Beacon volume"; _w.SaveOptions(); };
            rv.DragEnded += _ => Preview(kind);
            grid.AddChild(rv);
            grid.AddChild(WithTip(Check(r.Flash, v => r.Flash = v), "Flash the taskbar when the game is in the background"));
            grid.AddChild(Check(r.Discord, v => r.Discord = v));
            grid.AddChild(k switch
            {
                Kind.LowFood => Spin(w.Options.LowFood, 1, 10000, "left", v => w.Options.LowFood = v),
                Kind.BankFull => Spin(w.Options.BankSlots, 0, 100, "slots left", v => w.Options.BankSlots = v),
                Kind.Idle => Spin(w.Options.IdleSeconds, 10, 3600, "s", v => w.Options.IdleSeconds = v),
                _ => new Control(),
            });
            grid.AddChild(WithTip(Kit.Btn("Test", () => _w.Test(kind)), "Show, play, flash and post this alert as if it happened"));
        }
        box.AddChild(grid);

        box.AddChild(Kit.Label("Sound", Kit.Parchment, 15, "Heading"));
        var vol = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = w.Options.Volume * 100, CustomMinimumSize = new Vector2(220, 0) };
        var volText = Kit.Label($"{w.Options.Volume * 100:0}%", null, 13);
        vol.ValueChanged += v => { _w.Options.Volume = v / 100; volText.Text = $"{v:0}%"; _w.SaveOptions(); };
        vol.DragEnded += _ => Preview(Kind.Pet);
        var muted = new CheckBox { Text = "Play alert sounds even while the game is muted in the background", ButtonPressed = w.Options.EvenWhenMuted };
        muted.Toggled += v => { _w.Options.EvenWhenMuted = v; _w.SaveOptions(); };
        box.AddChild(Kit.Row(Kit.Label("Beacon volume", null, 13), vol, volText, Kit.Spacer(), muted));
        var soundNote = Kit.Label("This volume is Beacon's own, apart from the game's sound settings; each alert's slider is a share of it. Add your own sounds by putting .ogg, .wav or .mp3 files in mods\\Beacon\\sounds, then press Find new sounds.", null, 12, "Dim");
        soundNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        soundNote.CustomMinimumSize = new Vector2(760, 0);
        box.AddChild(Kit.Row(soundNote, Kit.Btn("Find new sounds", () => { _toasts.Sounds.Rescan(); FillPicks(); })));

        box.AddChild(Kit.Label("Discord", Kit.Parchment, 15, "Heading"));
        var hook = new LineEdit { Text = w.Options.Webhook, PlaceholderText = "Webhook URL (Server settings > Integrations > Webhooks > Copy URL)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Secret = true };
        hook.TextChanged += t => { _w.Options.Webhook = t.Trim(); _w.SaveOptions(); };
        box.AddChild(Kit.Row(hook, Kit.Btn("Send a test message", () => _w.Post("Test from Astrolabe Beacon"))));
        var note = Kit.Label("Alerts with Discord ticked are posted to that channel. The URL is kept on this computer only, in mods\\Beacon\\options.json.", null, 12, "Dim");
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.CustomMinimumSize = new Vector2(800, 0);
        box.AddChild(note);

        box.AddChild(Kit.Label("Recent alerts", Kit.Parchment, 15, "Heading"));
        _recent = new ItemList { CustomMinimumSize = new Vector2(0, 160) };
        box.AddChild(_recent);

        _win.Opened += () => { FillPicks(); Refresh(); };
        _w.Raised += (_, _) => { if (_win.Visible) Refresh(); };
    }

    CheckBox Check(bool on, Action<bool> set)
    {
        var c = new CheckBox { ButtonPressed = on };
        c.Toggled += v => { set(v); _w.SaveOptions(); };
        return c;
    }

    SpinBox Spin(int value, int min, int max, string suffix, Action<int> set)
    {
        var s = Kit.Spin(new SpinBox { MinValue = min, MaxValue = max, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(140, 0) });
        s.ValueChanged += v => { set((int)v); _w.SaveOptions(); };
        return s;
    }

    static T WithTip<T>(T c, string tip) where T : Control { c.TooltipText = tip; return c; }

    void Preview(Kind k)
    {
        Rule r = _w.Options.Of(k);
        Game? game = GameAccess.CurrentGame;
        AudioStream? skill = game?.LeveledEntities.FirstOrDefault()?.LevelUpNotificationSound;
        _toasts.Sounds.Play(r.Chime ?? Toasts.DefaultChime(k, _toasts.Sounds), skill, _w.Options.Volume * r.Volume, false);
    }

    void FillPicks()
    {
        var all = _toasts.Sounds.All();
        foreach (var (k, pick) in _picks)
        {
            Rule r = _w.Options.Of(k);
            string current = r.Chime ?? Toasts.DefaultChime(k, _toasts.Sounds);
            pick.Clear();
            void Add(string text, string id) { pick.AddItem(text); pick.SetItemMetadata(pick.ItemCount - 1, id); if (id == current) pick.Select(pick.ItemCount - 1); }
            Add("No sound", Sounds.None);
            if (k == Kind.LevelUp) Add("The skill's own level-up sound", Sounds.SkillOwn);
            foreach (Sounds.Choice c in all) Add(c.Name, c.Id);
            pick.TooltipText = _toasts.Sounds.NameOf(current);
        }
    }

    void Refresh()
    {
        _recent.Clear();
        foreach (Alert a in _w.Recent) _recent.AddItem($"{a.Time:MMM d HH:mm}  {a.Text}");
        if (_w.Recent.Count == 0) _recent.AddItem("Nothing yet.");
    }

    public void Toggle() => _win.Toggle();
    public void SetKeyLabel(string key) => _win.CloseText = key.Length > 0 ? $"Close ({key})" : "Close";
}
