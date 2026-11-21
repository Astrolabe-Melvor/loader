using System;
using Godot;

namespace Mi2Kit;

/// <summary>
/// A game-styled window: crafting-panel frame, teal title ribbon, drag by the title bar, close button.
/// Put your controls in <see cref="Body"/>; buttons for the title bar go in <see cref="HeaderExtras"/>.
/// </summary>
public sealed class KitWindow
{
    readonly CanvasLayer _layer;
    readonly Label _title;
    readonly Label _sub;
    readonly Button _close;
    bool _dragging;
    bool _placed;

    // Windows stack in the order they were last opened or clicked (canvas layers 64 and up; Godot caps them at 128).
    static readonly System.Collections.Generic.List<KitWindow> s_stack = new();

    /// <summary>The window frame. Size it with CustomMinimumSize.</summary>
    public PanelContainer Root { get; }

    /// <summary>Where the window's content goes.</summary>
    public VBoxContainer Body { get; }

    /// <summary>Extra buttons shown in the title bar, left of Close.</summary>
    public HBoxContainer HeaderExtras { get; }

    public event Action? Opened;
    public event Action? Closed;

    public bool Visible => _layer.Visible;

    public KitWindow(string title, string subtitle = "", Vector2? size = null, int layer = 64)
    {
        _layer = new CanvasLayer { Layer = layer, Visible = false, Name = "KitWindow_" + title.Replace(' ', '_') };

        Root = new PanelContainer { Theme = Kit.Theme, CustomMinimumSize = size ?? new Vector2(760, 520), MouseFilter = Control.MouseFilterEnum.Stop };
        _layer.AddChild(Root);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        Root.AddChild(column);

        // Title bar: ribbon with the title, then extras and Close. Dragging it moves the window.
        var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move };
        column.AddChild(bar);

        var ribbon = new PanelContainer { ThemeTypeVariation = "Ribbon", MouseFilter = Control.MouseFilterEnum.Pass };
        var ribbonRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _title = Kit.Label(title, Colors.White, 20, "Heading");
        _title.MouseFilter = Control.MouseFilterEnum.Ignore;
        ribbonRow.AddChild(_title);
        ribbon.AddChild(ribbonRow);
        bar.AddChild(ribbon);

        _sub = Kit.Label(subtitle, null, 13, "Dim");
        _sub.MouseFilter = Control.MouseFilterEnum.Ignore;
        _sub.Visible = subtitle.Length > 0;
        bar.AddChild(_sub);

        Control spacer = Kit.Spacer();
        spacer.MouseFilter = Control.MouseFilterEnum.Pass;
        bar.AddChild(spacer);

        HeaderExtras = new HBoxContainer();
        bar.AddChild(HeaderExtras);

        _close = Kit.Btn("Close", Close);
        bar.AddChild(_close);

        Root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) BringToFront(); };
        bar.GuiInput += OnBarInput;
        ribbon.GuiInput += OnBarInput;
        spacer.GuiInput += OnBarInput;

        Body = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(Body);

        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_layer);
    }

    /// <summary>Text on the Close button, e.g. "Close (F6)".</summary>
    public string CloseText { get => _close.Text; set => _close.Text = value; }

    public string Title { get => _title.Text; set => _title.Text = value; }

    public string Subtitle
    {
        get => _sub.Text;
        set { _sub.Text = value; _sub.Visible = value.Length > 0; }
    }

    public void Open()
    {
        if (_layer.Visible) return;
        _layer.Visible = true;
        BringToFront();
        Root.ResetSize();   // shrink back to the content's size
        // Containers settle over a frame or two; place after that, and keep the window on screen.
        if (!_placed) ((SceneTree)Engine.GetMainLoop()).CreateTimer(0.05).Timeout += Center;
        else Callable.From(KeepOnScreen).CallDeferred();
        Opened?.Invoke();
    }

    public void Close()
    {
        if (!_layer.Visible) return;
        _layer.Visible = false;
        _dragging = false;
        Closed?.Invoke();
    }

    public void Toggle()
    {
        if (_layer.Visible) Close(); else Open();
    }

    /// <summary>Removes the window for good.</summary>
    public void Free()
    {
        s_stack.Remove(this);
        _layer.QueueFree();
    }

    void Center()
    {
        Vector2 screen = Root.GetViewportRect().Size;
        Root.Position = ((screen - Root.Size) / 2).Floor();
        _placed = true;
        KeepOnScreen();
    }

    void KeepOnScreen()
    {
        Vector2 screen = Root.GetViewportRect().Size;
        Vector2 max = new(Mathf.Max(0, screen.X - Root.Size.X), Mathf.Max(0, screen.Y - Root.Size.Y));
        Root.Position = new Vector2(Mathf.Clamp(Root.Position.X, 0, max.X), Mathf.Clamp(Root.Position.Y, 0, max.Y));
    }

    /// <summary>Puts this window above every other kit window.</summary>
    public void BringToFront()
    {
        if (s_stack.Count > 0 && s_stack[^1] == this) return;
        s_stack.Remove(this);
        s_stack.Add(this);
        for (int i = 0; i < s_stack.Count; i++) s_stack[i]._layer.Layer = Mathf.Min(64 + i, 127);
    }

    void OnBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _dragging = mb.Pressed;
            if (mb.Pressed) BringToFront();
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            Vector2 screen = Root.GetViewportRect().Size;
            Vector2 p = Root.Position + mm.Relative;
            Root.Position = new Vector2(Mathf.Clamp(p.X, -Root.Size.X + 80, screen.X - 80), Mathf.Clamp(p.Y, 0, screen.Y - 40));
        }
    }
}
