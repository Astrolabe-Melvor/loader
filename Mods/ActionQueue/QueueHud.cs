using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;
using Astrolabe.UI;

namespace Astrolabe.ActionQueue;

/// <summary>
/// A small panel in the top-right corner: the step each lane is on and how far along it is, plus short notices
/// (finished, skipped, bank full). Click it to open the queue. Also plays the alert sound.
/// </summary>
sealed class QueueHud
{
    readonly Queue _queue;
    readonly CanvasLayer _layer;
    readonly PanelContainer _panel;
    readonly VBoxContainer _lines;
    readonly Label _notice;
    readonly AudioStreamPlayer _sound;
    double _noticeUntil, _next;

    public event Action? Clicked;

    public QueueHud(Queue queue)
    {
        _queue = queue;
        _layer = new CanvasLayer { Layer = 55, Name = "ActionQueueHud" };
        _panel = new PanelContainer { Theme = Kit.Theme, ThemeTypeVariation = "Card", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _panel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        _panel.TooltipText = "Action Queue: click to open";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(Kit.Label("Action Queue", Kit.Parchment, 14, "Heading"));
        _lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _lines.AddThemeConstantOverride("separation", 0);
        box.AddChild(_lines);
        _notice = Kit.Label("", Kit.Gold, 13);
        _notice.Visible = false;
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.CustomMinimumSize = new Vector2(300, 0);
        box.AddChild(_notice);
        _panel.AddChild(box);
        _panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Clicked?.Invoke(); };
        _layer.AddChild(_panel);
        _sound = new AudioStreamPlayer { Bus = "Master" };
        _layer.AddChild(_sound);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_layer);
    }

    /// <summary>Sound, taskbar flash and a notice in the corner.</summary>
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

        foreach (Node n in _lines.GetChildren()) n.QueueFree();
        if (showSteps)
            foreach (Step s in _queue.CurrentSteps())
            {
                var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                var icon = new TextureRect
                {
                    Texture = Kit.Icon(s.IsSell ? Catalog.ItemIcon(game!, s.Item) : Catalog.ActionIcon(game!, s.Effective), 18),
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
        _lines.Visible = showSteps;

        // Top right, under the game's top bar.
        Callable.From(() =>
        {
            Vector2 screen = _panel.GetViewportRect().Size;
            _panel.ResetSize();
            _panel.Position = new Vector2(screen.X - _panel.Size.X - 16, 96);
        }).CallDeferred();
    }
}
