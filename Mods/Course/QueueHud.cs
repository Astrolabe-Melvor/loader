using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// A small panel showing the current step and how far along it is, plus short notices (finished, skipped,
/// bank full). Starts in the empty strip beside the game's logo; drag it anywhere (the spot is kept), fold it to one
/// line with "–", click it to open the queue. Also plays the alert sound.
/// </summary>
sealed class QueueHud
{
    readonly Queue _queue;
    readonly CanvasLayer _layer;
    readonly PanelContainer _panel;
    readonly VBoxContainer _lines;
    readonly Label _notice, _count;
    readonly Button _fold;
    readonly AudioStreamPlayer _sound;
    double _noticeUntil, _next;
    bool _down, _moved;
    Vector2 _grab, _downAt;
    static readonly Vector2 s_home = new(76, 6);   // between the logo and the Bank button

    public event Action? Clicked;

    public QueueHud(Queue queue)
    {
        _queue = queue;
        _layer = new CanvasLayer { Layer = 55, Name = "CourseHud" };
        _panel = new PanelContainer { Theme = Kit.Theme, ThemeTypeVariation = "Card", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _panel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        _panel.TooltipText = "Course: click to open, drag to move";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        var title = Kit.Label("Course", Kit.Parchment, 14, "Heading");
        title.MouseFilter = Control.MouseFilterEnum.Ignore;
        _count = Kit.Label("", Kit.Dim, 12);
        _count.MouseFilter = Control.MouseFilterEnum.Ignore;
        _count.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _fold = new Button { Text = "–", Flat = true, Theme = Kit.Theme, CustomMinimumSize = new Vector2(24, 20), FocusMode = Control.FocusModeEnum.None };
        _fold.Pressed += () => { _queue.SetOption(s => s.HudMin = !s.HudMin); Update(KitGame.Current, force: true); };
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(title);
        head.AddChild(_count);
        head.AddChild(_fold);
        box.AddChild(head);
        _lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _lines.AddThemeConstantOverride("separation", 0);
        box.AddChild(_lines);
        _notice = Kit.Label("", Kit.Gold, 13);
        _notice.Visible = false;
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.CustomMinimumSize = new Vector2(300, 0);
        box.AddChild(_notice);
        _panel.AddChild(box);
        _panel.GuiInput += OnInput;
        _layer.AddChild(_panel);
        _sound = new AudioStreamPlayer { Bus = "Master" };
        _layer.AddChild(_sound);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_layer);
    }

    // Click opens the queue; press and move more than a few pixels drags instead.
    void OnInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            Vector2 m = _panel.GetGlobalMousePosition();
            if (mb.Pressed) { _down = true; _moved = false; _downAt = m; _grab = m - _panel.Position; }
            else if (_down)
            {
                _down = false;
                if (_moved) _queue.SetOption(s => { s.HudX = _panel.Position.X; s.HudY = _panel.Position.Y; });
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

    /// <summary>Sound, taskbar flash and a notice on the panel.</summary>
    public void Alert(Game? game, string text)
    {
        _notice.Text = text;
        _notice.Visible = true;
        _noticeUntil = Time.GetTicksMsec() / 1000.0 + 12;
        try
        {
            // The game's own level-up chime.
            AudioStream? chime = game?.LeveledEntities.FirstOrDefault()?.LevelUpNotificationSound;
            if (chime != null) { _sound.Stream = chime; _sound.Play(); }
            DisplayServer.WindowRequestAttention();
        }
        catch { }
        Update(game, force: true);
    }

    public void Update(Game? game, bool force = false)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (!force && now < _next) return;
        _next = now + 0.5;

        if (_notice.Visible && now > _noticeUntil) _notice.Visible = false;
        var st = _queue.State;
        bool showSteps = game != null && st.Running && st.Hud;
        _panel.Visible = showSteps || _notice.Visible;
        if (!_panel.Visible) return;

        int done = st.Steps.Count(x => x.Done);
        _count.Text = showSteps ? $"{done}/{st.Steps.Count} done" : "";
        _fold.Text = st.HudMin ? "+" : "–";
        _fold.TooltipText = st.HudMin ? "Show the steps" : "Fold to one line";
        _fold.Visible = showSteps;
        foreach (Node n in _lines.GetChildren()) n.QueueFree();
        if (showSteps && !st.HudMin)
            foreach (Step s in _queue.CurrentSteps())
            {
                var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                var icon = new TextureRect
                {
                    Texture = Kit.Icon(s.IsSell || s.IsShop ? Catalog.ItemIcon(game!, s.Item) : Catalog.ActionIcon(game!, s.Effective), 18),
                    StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                    CustomMinimumSize = new Vector2(20, 20),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                row.AddChild(icon);
                var text = Kit.Label(Queue.Label(s), s.State.StartsWith("waiting") ? Kit.Gold : Kit.Text, 13);
                text.MouseFilter = Control.MouseFilterEnum.Ignore;
                row.AddChild(text);
                string p = _queue.Progress(game, s);
                if (s.State.StartsWith("waiting")) p = s.State + (p.Length > 0 ? "  " + p : "");
                var prog = Kit.Label(p, Kit.Good, 12);
                prog.MouseFilter = Control.MouseFilterEnum.Ignore;
                row.AddChild(prog);
                _lines.AddChild(row);
            }
        _lines.Visible = showSteps && !st.HudMin;

        // Where the player left it, or the empty strip beside the logo. Not while it is being dragged.
        if (_down) return;
        Callable.From(() =>
        {
            _panel.ResetSize();
            _panel.Position = Clamp(st.HudX < 0 ? s_home : new Vector2(st.HudX, st.HudY));
        }).CallDeferred();
    }
}
