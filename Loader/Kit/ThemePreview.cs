using Godot;

namespace Mi2Kit;

/// <summary>Every kit control in one window, for checking the look in game (Mods menu > Theme preview).</summary>
static class ThemePreview
{
    static KitWindow? s_win;

    public static void Toggle()
    {
        s_win ??= Build();
        s_win.Toggle();
    }

    static KitWindow Build()
    {
        var w = new KitWindow("Theme preview", "how mod windows look", new Vector2(720, 0));
        w.HeaderExtras.AddChild(Kit.Btn("Header button", () => { }));

        w.Body.AddChild(Kit.Heading("Headings use the game's bold face"));
        w.Body.AddChild(Kit.Label("Body text uses the game's own font, so mod windows match the rest of the UI."));
        w.Body.AddChild(Kit.Label("Dim text is for hints and secondary values.", variation: "Dim"));
        w.Body.AddChild(Kit.Row(
            Kit.Label("Good", Kit.Good), Kit.Label("Bad", Kit.Bad), Kit.Label("Gold", Kit.Gold), Kit.Label("Link", Kit.Link)));

        w.Body.AddChild(new HSeparator());
        var disabled = Kit.Btn("Disabled", () => { });
        disabled.Disabled = true;
        w.Body.AddChild(Kit.Row(Kit.Btn("Button", () => { }), Kit.Btn("Action button", () => { }, action: true), disabled));

        var field = new LineEdit { PlaceholderText = "Search the bank...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var pick = new OptionButton();
        pick.AddItem("Woodcutting");
        pick.AddItem("Fishing");
        pick.AddItem("Mining");
        w.Body.AddChild(Kit.Row(field, pick, new CheckBox { Text = "Check box" }));

        var card = new PanelContainer { ThemeTypeVariation = "Card" };
        var inner = new VBoxContainer();
        inner.AddChild(Kit.Label("A card", Kit.Parchment, 16, "Heading"));
        inner.AddChild(Kit.Label("Cards group rows, like the tooltip panels in the game.", variation: "Dim"));
        card.AddChild(inner);
        w.Body.AddChild(card);
        return w;
    }
}
