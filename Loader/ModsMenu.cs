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
    static VBoxContainer s_status = null!;
    static Label s_update = null!;
    static Button s_release = null!;
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

        // Each mod's version and whether it is healthy; anything wrong is spelled out with a Report button.
        s_status = new VBoxContainer();
        s_win.Body.AddChild(s_status);

        // Updates: a daily look at the newest GitHub release. Nothing is downloaded.
        s_update = Kit.Label("", null, 13, "Dim");
        s_update.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        s_update.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s_update.CustomMinimumSize = new Vector2(300, 0);
        var auto = new CheckBox { Text = "Check daily", ButtonPressed = LoaderState.Get().CheckUpdates };
        auto.TooltipText = "Once a day, ask GitHub for the newest Astrolabe release. Only the release list is read.";
        auto.Toggled += on => { LoaderState.Get().CheckUpdates = on; LoaderState.Save(); if (on) Updates.CheckNow(announce: false); else RefreshUpdate(); };
        Button now = Kit.Btn("Check now", () => { Updates.CheckNow(announce: false); RefreshUpdate(); });
        s_release = Kit.Btn("Get updates", () => OS.ShellOpen(Updates.ReleasePage), action: true);
        s_release.TooltipText = "Opens the release page. Download the zips for the mods listed and unzip them over the old ones (close the game first).";
        Button report = Kit.Btn("Report a bug", () => BugReport.Open());
        report.TooltipText = "Puts together your versions, mods and the log, so a bug can be found and fixed.";
        HBoxContainer updateRow = Kit.Row(Kit.Label("Updates:", null, 0, "Dim"), s_update, s_release, now, auto, report);
        s_win.Body.AddChild(updateRow);
        Updates.Changed += () => { if (s_win.Visible) { RefreshUpdate(); RebuildStatus(); } };

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

        // Mod window size, on top of the game's own UI Scale setting (which mod windows follow too).
        var scale = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
        float[] steps = { 0.7f, 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f };
        foreach (float f in steps) scale.AddItem($"{f * 100:0}%");
        scale.Select(Array.IndexOf(steps, steps.OrderBy(f => Math.Abs(f - WindowPrefs.Scale)).First()));
        scale.ItemSelected += i => WindowPrefs.SetScale(steps[i]);
        var reset = Kit.Btn("Fit all windows again", WindowPrefs.ResetSizes);
        reset.TooltipText = "Forget the sizes you dragged windows to; each fits its content again.";
        s_win.Body.AddChild(Kit.Row(Kit.Label("Mod window size:", null, 0, "Dim"), scale,
            Kit.Label("on top of the game's UI Scale. Drag a window's corner to resize it.", null, 13, "Dim"), Kit.Spacer(), reset));

        s_win.SetTour(
            new TourStep("Astrolabe", "This menu lists every mod, its window and its key. Each mod window has its own Help and Tour in its title bar."),
            new TourStep("Your mods", "Each mod's version and whether it is working. If one says something other than ok, hover it for details, and [b]Report[/b] sends them to be fixed.", () => s_status),
            new TourStep("Updates", "Once a day the loader checks for newer versions. When there are some, [b]Get updates[/b] opens the download page. [b]Report a bug[/b] copies everything needed to find a problem.", () => updateRow),
            new TourStep("Windows and keys", "Click a mod's name to open its window. Click its key to change it (press the new key, Esc cancels); [b]Off[/b] removes it.", () => scroll),
            new TourStep("This menu's key", "Change the key that opens this menu, open the log or the mods folder.", () => s_menuKey.GetParent() as Control));

        s_win.Opened += Rebuild;
        s_win.Closed += StopListening;
        Hotkeys.ListChanged += () => { if (s_win.Visible) Rebuild(); };

        // The loader's own entry: a preview of the shared look, for checking the theme in game.
        Hotkeys.Register(Loader.Name, "Theme preview", ThemePreview.Toggle, KeyChord.None);
    }

    internal static void Toggle() => s_win.Toggle();

    static void RefreshUpdate()
    {
        s_update.Text = Updates.Status.Length > 0 ? Updates.Status : LoaderState.Get().CheckUpdates ? "Not checked yet." : "Daily check is off.";
        s_update.RemoveThemeColorOverride("font_color");
        if (Updates.Newer.Count > 0) s_update.AddThemeColorOverride("font_color", Kit.Link);
        s_release.Visible = Updates.Newer.Count > 0;
    }

    static void RebuildStatus()
    {
        foreach (Node n in s_status.GetChildren()) n.QueueFree();
        foreach (string line in Loader.Notices().Where(l => l.StartsWith("The game updated")))
            s_status.AddChild(Kit.Label(line + " If something stopped working, Report a bug says which mod.", Kit.Gold, 13));
        foreach (ModInfo m in Loader.Mods.OrderBy(m => m.Name))
        {
            (string state, Color color, string detail) =
                m.Failed.Length > 0 ? ("did not load", Kit.Bad, m.Failed)
                : m.Stopped ? ("paused after repeated errors", Kit.Bad, m.LastError)
                : m.Missing.Count > 0 ? ($"{m.Missing.Count} game feature(s) gone in this game version", Kit.Gold, "Not found: " + string.Join("\n", m.Missing.Take(20)))
                : m.Errors > 0 ? ($"{m.Errors} error(s) this session", Kit.Gold, m.LastError)
                : ("ok", Kit.Good, "");
            var newer = Updates.Newer.FirstOrDefault(n => n.Name == m.Name);
            var row = Kit.Row(Kit.Label(m.Name, Kit.Parchment, 14), Kit.Label(m.Version, null, 13, "Dim"));
            Label st = Kit.Label(state, color, 13);
            if (detail.Length > 0) { st.TooltipText = detail; st.MouseFilter = Control.MouseFilterEnum.Pass; }
            row.AddChild(st);
            if (newer.Name != null) row.AddChild(Kit.Label($"{newer.Latest} out", Kit.Link, 13));
            row.AddChild(Kit.Spacer());
            if (state != "ok")
            {
                string name = m.Name;
                row.AddChild(Kit.Btn("Report", () => BugReport.Open(name)));
            }
            s_status.AddChild(row);
        }
    }

    static void Rebuild()
    {
        s_win.Subtitle = $"{Loader.Name} {Loader.Version}   game {GameWatch.Version}";
        RefreshUpdate();
        RebuildStatus();
        s_win.CloseText = Hotkeys.MenuKey.IsNone ? "Close" : $"Close ({Hotkeys.MenuKey})";
        s_menuKey.Text = Hotkeys.MenuKey.ToString();
        s_loaded.Text = Loader.Mods.Count == 0
            ? "No mods found. Unzip each mod into its own folder under mods\\ (Open mods folder, below)."
            : "Every mod window and its key. Click a key to change it; each window has Help and a Tour in its title bar.";

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
