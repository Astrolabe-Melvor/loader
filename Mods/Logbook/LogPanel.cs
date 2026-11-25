using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Logbook;

/// <summary>
/// A small panel: the skills you are training with XP per hour and time to the next level, and GP per hour.
/// Starts bottom-left beside the skill list; drag it anywhere (the spot is kept), "–" folds it, click opens the Logbook.
/// </summary>
sealed class LogPanel
{
    readonly Tracker _t;
    readonly PanelContainer _panel;
    readonly VBoxContainer _lines;
    readonly Label _time;
    readonly Button _fold;
    double _next;
    bool _down, _moved;
    Vector2 _grab, _downAt;

    public event Action? Clicked;

    public LogPanel(Tracker t)
    {
        _t = t;
        var layer = new CanvasLayer { Layer = 55, Name = "LogbookPanel" };
        _panel = new PanelContainer { Theme = Kit.Theme, ThemeTypeVariation = "Card", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _panel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        _panel.TooltipText = "Logbook: click to open, drag to move";
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        var title = Kit.Label("Logbook", Kit.Parchment, 14, "Heading");
        title.MouseFilter = Control.MouseFilterEnum.Ignore;
        _time = Kit.Label("", Kit.Dim, 12);
        _time.MouseFilter = Control.MouseFilterEnum.Ignore;
        _time.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _fold = new Button { Text = "–", Flat = true, Theme = Kit.Theme, CustomMinimumSize = new Vector2(24, 20), FocusMode = Control.FocusModeEnum.None };
        _fold.Pressed += () => { _t.SetOption(o => o.PanelMin = !o.PanelMin); Update(true); };
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(title);
        head.AddChild(_time);
        head.AddChild(_fold);
        box.AddChild(head);
        _lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _lines.AddThemeConstantOverride("separation", 0);
        box.AddChild(_lines);
        _panel.AddChild(box);
        _panel.GuiInput += OnInput;
        layer.AddChild(_panel);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(layer);
    }

    void OnInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            Vector2 m = _panel.GetGlobalMousePosition();
            if (mb.Pressed) { _down = true; _moved = false; _downAt = m; _grab = m - _panel.Position; }
            else if (_down)
            {
                _down = false;
                if (_moved) _t.SetOption(o => { o.PanelX = _panel.Position.X; o.PanelY = _panel.Position.Y; });
                else Clicked?.Invoke();
            }
            _panel.AcceptEvent();
        }
        else if (e is InputEventMouseMotion && _down)
        {
            Vector2 m = _panel.GetGlobalMousePosition();
            if (!_moved && (m - _downAt).Length() < 5) return;
            _moved = true;
            _panel.Position = Clamp(m - _grab);
            _panel.AcceptEvent();
        }
    }

    Vector2 Clamp(Vector2 p)
    {
        Vector2 screen = _panel.GetViewportRect().Size;
        return new Vector2(Math.Clamp(p.X, 0, Math.Max(0, screen.X - _panel.Size.X)), Math.Clamp(p.Y, 0, Math.Max(0, screen.Y - _panel.Size.Y)));
    }

    static Label Line(string text, Color color, int size = 13)
    {
        var l = Kit.Label(text, color, size);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        return l;
    }

    public void Update(bool force = false)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (!force && now < _next) return;
        _next = now + 2;

        Game? game = KitGame.Current;
        Session s = _t.Session;
        _panel.Visible = game != null && _t.Options.Panel && s.Character.Length > 0;
        if (!_panel.Visible) return;

        _time.Text = LogWindow.Span(s.Hours);
        _fold.Text = _t.Options.PanelMin ? "+" : "–";
        _fold.TooltipText = _t.Options.PanelMin ? "Show the rates" : "Fold to one line";
        foreach (Node n in _lines.GetChildren()) n.QueueFree();
        _lines.Visible = !_t.Options.PanelMin;
        if (!_t.Options.PanelMin)
        {
            // The skills gaining XP right now, fastest first.
            var training = s.Skills.Select(kv => (kv.Key, kv.Value, Rate: _t.RecentXpPerHour(kv.Key)))
                .Where(x => x.Rate > 0).OrderByDescending(x => x.Rate).Take(3).ToList();
            foreach (var (id, line, rate) in training)
            {
                var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                if (game!.Entities.TryGetObjectById(id, out GameEntity? e) && e != null)
                    row.AddChild(new TextureRect { Texture = Kit.Icon(e.Icon, 18), StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(20, 20), MouseFilter = Control.MouseFilterEnum.Ignore });
                row.AddChild(Line($"{LogWindow.Num(rate)} XP/hr", Kit.Text));
                double? h = _t.HoursToLevel(game, id);
                if (h != null) row.AddChild(Line($"level {line.Level + 1} in {LogWindow.Span(h.Value)}", Kit.Good, 12));
                _lines.AddChild(row);
            }
            foreach (var (id, g) in s.CurrencyGained.OrderByDescending(kv => kv.Value).Take(2))
                _lines.AddChild(Line($"{s.NameOf(id)}: {LogWindow.Num(g / s.Hours)} per hour", Kit.Gold));
            long worth = s.GainedValue;
            if (worth > 0) _lines.AddChild(Line($"Loot worth: {LogWindow.Num(worth / s.Hours)} GP per hour", Kit.Gold, 12));
            if (_lines.GetChildCount() == 0) _lines.AddChild(Line("Rates fill in as you train.", Kit.Dim, 12));
        }

        if (_down) return;
        Callable.From(() =>
        {
            _panel.ResetSize();
            Vector2 screen = _panel.GetViewportRect().Size;
            // First spot: bottom-left, beside the game's skill list and above its bottom bar.
            Vector2 home = new(196, screen.Y - _panel.Size.Y - 64);
            _panel.Position = Clamp(_t.Options.PanelX < 0 ? home : new Vector2(_t.Options.PanelX, _t.Options.PanelY));
        }).CallDeferred();
    }
}
