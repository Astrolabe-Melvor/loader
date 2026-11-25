using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace Astrolabe.UI;

/// <summary>
/// A game-styled window: crafting-panel frame, teal title ribbon, drag by the title bar, close button.
/// Put your controls in <see cref="Body"/>; buttons for the title bar go in <see cref="HeaderExtras"/>.
/// The window fits its content but never the screen's edges (the body scrolls instead), can be resized from the
/// corner grip (remembered per window), and follows the mod window size set in the Mods menu.
/// </summary>
public sealed partial class KitWindow
{
    readonly CanvasLayer _layer;
    readonly Label _title;
    readonly Label _sub;
    readonly Button _close;
    readonly ScrollContainer _scroll;
    readonly Vector2 _baseMin;
    readonly string _key;
    bool _dragging, _resizing;
    bool _placed;
    Vector2 _resizeFrom, _sizeFrom;

    // Windows stack in the order they were last opened or clicked (canvas layers 64 and up; Godot caps them at 128).
    static readonly System.Collections.Generic.List<KitWindow> s_stack = new();
    static readonly System.Collections.Generic.List<KitWindow> s_all = new();
    static bool s_guard, s_wasDown;

    /// <summary>A click anywhere that is not the focused text or number box takes the focus off it (cards and labels
    /// swallow clicks, so the window frame alone can't see them).</summary>
    static void FocusGuard()
    {
        bool down = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right);
        bool pressed = down && !s_wasDown;
        s_wasDown = down;
        if (!pressed) return;
        Viewport? vp = ((SceneTree)Engine.GetMainLoop()).Root;
        Control? f = vp?.GuiGetFocusOwner();
        if (f == null || f is not (LineEdit or TextEdit)) return;
        KitWindow? w = s_all.Find(x => x._layer.Visible && x.Root.IsAncestorOf(f));
        if (w == null) return;
        Control? hovered = vp!.GuiGetHoveredControl();
        // Still on the box (or its SpinBox around it): keep the focus.
        if (hovered != null && (hovered == f || f.IsAncestorOf(hovered) || (hovered is SpinBox && hovered.IsAncestorOf(f)))) return;
        f.ReleaseFocus();
    }

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

        _key = title;
        _baseMin = size ?? new Vector2(760, 520);
        Root = new PanelContainer { Theme = Kit.Theme, CustomMinimumSize = _baseMin, MouseFilter = Control.MouseFilterEnum.Stop };
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

        _tourBtn = Kit.Btn("Tour", () => StartTour());
        _tourBtn.Visible = false;
        _tourBtn.TooltipText = "A short walk through this window, pointing at each part";
        bar.AddChild(_tourBtn);

        _help = Kit.Btn("Help", ToggleHelp);
        _help.Visible = false;
        _help.TooltipText = "How this works";
        bar.AddChild(_help);

        _close = Kit.Btn("Close", Close);
        bar.AddChild(_close);

        Root.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true }) return;
            BringToFront();
            // A click on empty space takes the focus off a text or number box, as it does in the game's own panels.
            DropFocus();
        };
        bar.GuiInput += OnBarInput;
        ribbon.GuiInput += OnBarInput;
        spacer.GuiInput += OnBarInput;

        // Help: a scrolling panel above the body, shown by the Help button once SetHelp has been called.
        _helpCard = new PanelContainer { ThemeTypeVariation = "Card", Visible = false };
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 260), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _helpText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ScrollActive = false };
        _helpText.AddThemeFontSizeOverride("normal_font_size", 14);
        _helpText.AddThemeFontSizeOverride("bold_font_size", 15);
        _helpText.MetaClicked += meta => OS.ShellOpen(meta.AsString());
        scroll.AddChild(_helpText);
        _helpCard.AddChild(scroll);
        column.AddChild(_helpCard);

        // The body scrolls when the window is smaller than its content (small screens, or resized by hand).
        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Body = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _scroll.AddChild(Body);
        column.AddChild(_scroll);
        // Content that grows (a longer list, a new row) grows the window; it only shrinks back when reopened.
        Body.MinimumSizeChanged += () => { if (_layer.Visible && !Sized) Callable.From(() => Fit(shrink: false)).CallDeferred(); };

        // Corner grip: drag to resize, double-click to go back to fitting the content.
        var grip = new Control
        {
            CustomMinimumSize = new Vector2(18, 18),
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = "Drag to resize. Double-click to fit the content again.",
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        grip.Draw += () =>
        {
            for (int i = 0; i < 3; i++)
            {
                float o = 5 + i * 5;
                grip.DrawLine(new Vector2(18, o), new Vector2(o, 18), Kit.Dim, 2);
            }
        };
        grip.GuiInput += OnGripInput;
        var gripRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        Control gs = Kit.Spacer();
        gs.MouseFilter = Control.MouseFilterEnum.Ignore;
        gripRow.AddChild(gs);
        gripRow.AddChild(grip);
        gripRow.AddThemeConstantOverride("separation", 0);
        column.AddChild(gripRow);

        _layer.Scale = Vector2.One * WindowPrefs.Scale;
        WindowPrefs.ScaleChanged += OnScale;
        s_all.Add(this);
        if (!s_guard)
        {
            s_guard = true;
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame += FocusGuard;
        }
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_layer);
    }

    /// <summary>True once the window has been resized by hand: it keeps that size instead of fitting its content.</summary>
    bool Sized => WindowPrefs.SizeOf(_key) != null;

    /// <summary>The screen in this window's units (the Mods menu scale applied).</summary>
    Vector2 Screen => Root.GetViewportRect().Size / WindowPrefs.Scale;

    void DropFocus()
    {
        Control? f = Root.GetViewport()?.GuiGetFocusOwner();
        if (f != null && Root.IsAncestorOf(f)) f.ReleaseFocus();
    }

    void OnScale()
    {
        _layer.Scale = Vector2.One * WindowPrefs.Scale;
        if (_layer.Visible) { Layout(); Callable.From(KeepOnScreen).CallDeferred(); }
    }

    /// <summary>Sizes the window: the saved size, or the content's size; either way no bigger than the screen.</summary>
    void Layout()
    {
        Vector2 screen = Screen;
        Vector2 room = new(Mathf.Max(320, screen.X - 24), Mathf.Max(240, screen.Y - 24));
        if (WindowPrefs.SizeOf(_key) is Vector2 saved)
        {
            Root.CustomMinimumSize = Vector2.Zero;
            _scroll.CustomMinimumSize = Vector2.Zero;
            Root.Size = new Vector2(Mathf.Min(saved.X, room.X), Mathf.Min(saved.Y, room.Y));
            return;
        }
        Fit();
    }

    void Fit() => Fit(shrink: true);

    void Fit(bool shrink)
    {
        if (Sized) return;
        Vector2 screen = Screen;
        Vector2 want = Body.GetCombinedMinimumSize();
        if (!shrink) want = new Vector2(Mathf.Max(want.X, _scroll.Size.X), Mathf.Max(want.Y, _scroll.Size.Y));
        // Room for the body: the screen less the frame, title bar, help and grip around it.
        float chromeY = Root.GetCombinedMinimumSize().Y - _scroll.CustomMinimumSize.Y + 8;
        float chromeX = 48;
        _scroll.CustomMinimumSize = new Vector2(
            Mathf.Min(want.X, Mathf.Max(200, screen.X - 24 - chromeX)),
            Mathf.Min(want.Y, Mathf.Max(160, screen.Y - 24 - chromeY)));
        Root.CustomMinimumSize = new Vector2(Mathf.Min(_baseMin.X, screen.X - 24), Mathf.Min(_baseMin.Y, screen.Y - 24));
        Root.ResetSize();
        if (_placed) Callable.From(KeepOnScreen).CallDeferred();
    }

    void OnGripInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.DoubleClick)
            {
                _resizing = false;
                WindowPrefs.SetSize(_key, null);
                Layout();
                Callable.From(KeepOnScreen).CallDeferred();
                return;
            }
            _resizing = mb.Pressed;
            if (mb.Pressed)
            {
                BringToFront();
                _resizeFrom = Root.GetGlobalMousePosition();
                _sizeFrom = Root.Size;
                Root.CustomMinimumSize = Vector2.Zero;
                _scroll.CustomMinimumSize = Vector2.Zero;
            }
            else WindowPrefs.SetSize(_key, Root.Size);
        }
        else if (e is InputEventMouseMotion && _resizing)
        {
            Vector2 screen = Screen;
            Vector2 s = _sizeFrom + Root.GetGlobalMousePosition() - _resizeFrom;
            Root.Size = new Vector2(
                Mathf.Clamp(s.X, 360, Mathf.Max(360, screen.X - Root.Position.X)),
                Mathf.Clamp(s.Y, 240, Mathf.Max(240, screen.Y - Root.Position.Y))).Round();
        }
    }

    readonly Button _help;
    readonly PanelContainer _helpCard;
    readonly RichTextLabel _helpText;

    /// <summary>Adds a Help button to the title bar that shows this text (BBCode: [b], [i], [url=...]) above the window's body.</summary>
    public void SetHelp(string bbcode)
    {
        _helpText.Text = bbcode;
        _help.Visible = bbcode.Length > 0;
    }

    void ToggleHelp()
    {
        _helpCard.Visible = !_helpCard.Visible;
        _help.Text = _helpCard.Visible ? "Hide help" : "Help";
        if (!Sized) Fit();
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
        Layout();   // the saved size, or shrink back to the content's size
        // Containers settle over a frame or two; place after that, and keep the window on screen.
        if (!_placed) ((SceneTree)Engine.GetMainLoop()).CreateTimer(0.05).Timeout += Center;
        else Callable.From(KeepOnScreen).CallDeferred();
        Opened?.Invoke();
        OfferTour();
    }

    public void Close()
    {
        if (!_layer.Visible) return;
        DropFocus();
        _layer.Visible = false;
        _dragging = _resizing = false;
        EndTour();
        Closed?.Invoke();
    }

    public void Toggle()
    {
        if (_layer.Visible) Close(); else Open();
    }

    /// <summary>Removes the window for good.</summary>
    public void Free()
    {
        EndTour();
        s_stack.Remove(this);
        s_all.Remove(this);
        WindowPrefs.ScaleChanged -= OnScale;
        _layer.QueueFree();
    }

    void Center()
    {
        Vector2 screen = Screen;
        // Where it was last dragged to, else the middle of the screen.
        Root.Position = WindowPrefs.PositionOf(_key) ?? ((screen - Root.Size) / 2).Floor();
        _placed = true;
        KeepOnScreen();
    }

    void KeepOnScreen()
    {
        Vector2 screen = Screen;
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

    Vector2 _grab;   // where in the window the mouse grabbed it

    void OnBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (_dragging && !mb.Pressed) WindowPrefs.SetPosition(_key, Root.Position);   // opens here next time
            _dragging = mb.Pressed;
            if (mb.Pressed)
            {
                BringToFront();
                _grab = Root.GetGlobalMousePosition() - Root.Position;
            }
        }
        else if (e is InputEventMouseMotion && _dragging)
        {
            // Follow the mouse from the grab point (not by adding deltas, which drift once the window hits an edge).
            Vector2 screen = Screen;
            Vector2 p = Root.GetGlobalMousePosition() - _grab;
            Root.Position = new Vector2(Mathf.Clamp(p.X, -Root.Size.X + 80, screen.X - 80), Mathf.Clamp(p.Y, 0, screen.Y - 40)).Round();
        }
    }
}

/// <summary>Mod window settings shared by every mod: the size scale (Mods menu) and each window's hand-set size.
/// Saved in mods\windows.json.</summary>
public static class WindowPrefs
{
    sealed class Data
    {
        public float Scale { get; set; } = 1f;
        public Dictionary<string, float[]> Sizes { get; set; } = new();
        public Dictionary<string, float[]> Positions { get; set; } = new();
    }

    static Data? s_data;
    // Not cached until the loader knows where the mods folder is.
    static Data D => s_data ?? (MelvorModLoader.Startup.ModsFolder.Length == 0 ? new Data() : s_data = Load());
    static string File => Path.Combine(MelvorModLoader.Startup.ModsFolder, "windows.json");

    public static event Action? ScaleChanged;

    /// <summary>Mod windows' size on top of the game's own UI scale: 0.7 to 1.5.</summary>
    public static float Scale => Math.Clamp(D.Scale, 0.7f, 1.5f);

    public static void SetScale(float scale)
    {
        Data d = D;
        d.Scale = Math.Clamp(scale, 0.7f, 1.5f);
        Save();
        ScaleChanged?.Invoke();
    }

    public static Vector2? SizeOf(string window) =>
        D.Sizes.TryGetValue(window, out float[]? s) && s.Length == 2 ? new Vector2(s[0], s[1]) : null;

    public static void SetSize(string window, Vector2? size)
    {
        if (size is Vector2 v) D.Sizes[window] = new[] { v.X, v.Y };
        else D.Sizes.Remove(window);
        Save();
    }

    public static Vector2? PositionOf(string window) =>
        D.Positions.TryGetValue(window, out float[]? p) && p.Length == 2 ? new Vector2(p[0], p[1]) : null;

    public static void SetPosition(string window, Vector2 at)
    {
        D.Positions[window] = new[] { at.X, at.Y };
        Save();
    }

    /// <summary>Every window back to fitting its content, centred on screen.</summary>
    public static void ResetSizes()
    {
        D.Sizes.Clear();
        D.Positions.Clear();
        Save();
        ScaleChanged?.Invoke();
    }

    static Data Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(File)) ?? new() : new(); }
        catch { return new(); }
    }

    static void Save()
    {
        try { System.IO.File.WriteAllText(File, JsonSerializer.Serialize(D)); }
        catch { }
    }
}
