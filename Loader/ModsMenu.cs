using System;
using System.Linq;
using Godot;
using Astrolabe.UI;

namespace MelvorModLoader;

/// <summary>The F1 window: every mod window, its key, and the loader's own tools.</summary>
static class ModsMenu
{
    static KitWindow s_win = null!;
    static VBoxContainer s_list = null!;
    static Label s_loaded = null!;
    static Button s_menuKey = null!;
    static Button? s_listening;
    static Action<KeyChord>? s_onKey;

    internal static void Install(SceneTree tree)
    {
        s_win = new KitWindow("Mods", "", new Vector2(640, 460), 70);

        // Autowrap labels need a fixed width or Godot sizes them one word per line (a window taller than the screen).
        s_loaded = Kit.Label("", null, 13, "Dim");
        s_loaded.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        s_loaded.CustomMinimumSize = new Vector2(600, 0);
        s_win.Body.AddChild(s_loaded);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 260),
        };
        s_list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(s_list);
        s_win.Body.AddChild(scroll);

        s_menuKey = KeyButton(() => Hotkeys.MenuKey.ToString(), k => { if (!k.IsNone) Hotkeys.SetMenuKey(k); });
        s_win.Body.AddChild(Kit.Row(
            Kit.Label("This menu:", null, 0, "Dim"), s_menuKey, Kit.Spacer(),
            Kit.Btn("Open log", () => OS.ShellOpen(Log.FilePath)),
            Kit.Btn("Open mods folder", () => OS.ShellOpen(Startup.ModsFolder)),
            Kit.Btn("Reset all keys", Hotkeys.ResetAll)));

        s_win.Opened += Rebuild;
        s_win.Closed += StopListening;
        Hotkeys.ListChanged += () => { if (s_win.Visible) Rebuild(); };

        // The loader's own entry: a preview of the shared look, for checking the theme in game.
        Hotkeys.Register(Loader.Name, "Theme preview", ThemePreview.Toggle, KeyChord.None);
    }

    internal static void Toggle() => s_win.Toggle();

    static void Rebuild()
    {
        s_win.Subtitle = $"{Loader.Name} {Loader.Version}";
        s_win.CloseText = Hotkeys.MenuKey.IsNone ? "Close" : $"Close ({Hotkeys.MenuKey})";
        s_menuKey.Text = Hotkeys.MenuKey.ToString();
        s_loaded.Text = Loader.LoadedMods.Count == 0
            ? "No mods loaded. Put each mod in its own folder under mods\\."
            : $"Loaded: {string.Join(", ", Loader.LoadedMods)}";

        foreach (Node n in s_list.GetChildren()) n.QueueFree();

        foreach (var group in Hotkeys.Windows.GroupBy(w => w.Mod).OrderBy(g => g.Key == Loader.Name ? 1 : 0).ThenBy(g => g.Key))
        {
            s_list.AddChild(Kit.Label(group.Key, Kit.Parchment, 15, "Heading"));
            foreach (ModWindow w in group)
            {
                var card = new PanelContainer { ThemeTypeVariation = "Card" };
                Button open = Kit.Btn(w.Title, w.Press, action: true);
                open.CustomMinimumSize = new Vector2(220, 0);
                open.Alignment = HorizontalAlignment.Left;

                ModWindow win = w;
                Button key = KeyButton(() => win.KeyLabel.Length > 0 ? win.KeyLabel : "No key", k => Hotkeys.Rebind(win, k));
                Button off = Kit.Btn("Off", () => Hotkeys.Rebind(win, KeyChord.None));
                off.Disabled = w.Key.IsNone;

                var row = Kit.Row(open, Kit.Spacer());
                string clash = string.Join(", ", Hotkeys.Clashes(w).Select(o => o.Title));
                if (clash.Length > 0) row.AddChild(Kit.Label("same key as " + clash, Kit.Gold, 13));
                row.AddChild(key);
                row.AddChild(off);
                card.AddChild(row);
                s_list.AddChild(card);
            }
        }

        if (Hotkeys.Windows.Count == 0)
            s_list.AddChild(Kit.Label("No mod has a window yet.", null, 0, "Dim"));
    }

    // A button that, when clicked, waits for the next key press (with Ctrl/Shift/Alt) and hands it over. Esc cancels.
    static Button KeyButton(Func<string> label, Action<KeyChord> set)
    {
        var b = new Button { Text = label(), CustomMinimumSize = new Vector2(110, 0), FocusMode = Control.FocusModeEnum.All };
        b.Pressed += () =>
        {
            StopListening();
            s_listening = b;
            s_onKey = set;
            Hotkeys.Capturing = true;
            b.Text = "Press a key";
            b.GrabFocus();
        };
        b.GuiInput += e =>
        {
            if (s_listening != b || e is not InputEventKey { Pressed: true, Echo: false } k) return;
            b.AcceptEvent();
            Key code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
            if (code is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta) return;   // wait for the real key
            Action<KeyChord>? apply = s_onKey;
            StopListening();
            if (code == Key.Escape) { b.Text = label(); return; }
            apply?.Invoke(new KeyChord(code, k.CtrlPressed, k.ShiftPressed, k.AltPressed));
            b.Text = label();
        };
        b.FocusExited += () => { if (s_listening == b) { StopListening(); b.Text = label(); } };
        return b;
    }

    static void StopListening()
    {
        Button? was = s_listening;
        s_listening = null;
        s_onKey = null;
        // Release on the next frame so the key that was just pressed does not also trigger a window.
        Callable.From(() => Hotkeys.Capturing = false).CallDeferred();
        if (was != null && GodotObject.IsInstanceValid(was)) was.ReleaseFocus();
    }
}
