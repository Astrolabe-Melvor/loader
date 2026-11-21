using System;
using System.Collections.Generic;
using Godot;
using MelvorModLoader;

namespace Astrolabe.UI;

/// <summary>
/// The shared look: the game's own UI textures and fonts, so mod windows read as part of the game.
/// Use <see cref="Theme"/> on a window's root, or the helpers for one-off controls.
/// </summary>
public static class Kit
{
    // Game textures (res:// paths inside the game's pack). Each has a 9-slice margin in texture pixels.
    public const string Panel = "res://Assets/UI/Textures/TEX_Crafting_14.png";
    public const string Ribbon = "res://Assets/UI/Textures/TEX_FrameHeader_1.png";
    public const string Tooltip = "res://Assets/UI/Textures/Tooltips/TEX_Tooltip_Dark_Large.png";
    public const string Button = "res://Assets/UI/Textures/Buttons/TEX_Button_Normal.png";
    public const string ButtonHover = "res://Assets/UI/Textures/Buttons/TEX_Button_Hover.png";
    public const string ButtonPressed = "res://Assets/UI/Textures/Buttons/TEX_Button_Pressed.png";
    public const string ButtonDisabled = "res://Assets/UI/Textures/Buttons/TEX_Button_Disabled.png";
    public const string ActionButton = "res://Assets/UI/Textures/Buttons/TEX_ActionButton_Normal.png";

    // Slice margin as a share of the texture's width (measured on the game's art), and the border size on screen
    // in pixels (the same sizes the wiki uses).
    static readonly Dictionary<string, (float Share, float Screen)> s_slice = new()
    {
        [Panel] = (28f / 456, 14), [Ribbon] = (30f / 230, 16), [Tooltip] = (30f / 584, 12),
        [Button] = (20f / 256, 10), [ButtonHover] = (20f / 256, 10), [ButtonPressed] = (20f / 256, 10),
        [ButtonDisabled] = (20f / 256, 10), [ActionButton] = (20f / 256, 10),
    };

    // Palette, same as the wiki.
    public static readonly Color Text = Color.FromHtml("#ecebe6");
    public static readonly Color Dim = Color.FromHtml("#a7a49b");
    public static readonly Color Parchment = Color.FromHtml("#e9dcbf");
    public static readonly Color Gold = Color.FromHtml("#f0c348");
    public static readonly Color Good = Color.FromHtml("#8fd46f");
    public static readonly Color Bad = Color.FromHtml("#ec7f6c");
    public static readonly Color Link = Color.FromHtml("#8ec9ee");
    public static readonly Color Field = Color.FromHtml("#1c1c1c");
    public static readonly Color Line = Color.FromHtml("#3a3a3a");
    public static readonly Color RowColor = Color.FromHtml("#2a2a2a");
    public static readonly Color Night = Color.FromHtml("#141a1f");

    static readonly Dictionary<string, Texture2D?> s_tex = new();
    static Theme? s_theme;

    /// <summary>Body and heading fonts: the game's own "Default" face (Regular and Bold) when found.</summary>
    public static Font BodyFont { get; private set; } = ThemeDB.FallbackFont;
    public static Font HeadFont { get; private set; } = ThemeDB.FallbackFont;

    internal static void Init()
    {
        Fonts.Find(out Font? body, out Font? head);
        Font fallback = ThemeDB.GetProjectTheme()?.DefaultFont ?? ThemeDB.FallbackFont;
        BodyFont = body ?? fallback;
        HeadFont = head ?? BodyFont;   // never fake-embolden: Godot's synthetic bold looks scratched
        Log.Write($"Kit fonts: body {(body != null ? "game" : "fallback")}, head {(head != null ? "game" : "same as body")}");
    }

    /// <summary>A texture from the game's pack, or null if this game version does not have it.</summary>
    public static Texture2D? Tex(string path)
    {
        if (s_tex.TryGetValue(path, out Texture2D? t)) return t;
        t = ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        if (t == null) Log.Write("Kit: texture missing, using a flat colour: " + path);
        return s_tex[path] = t;
    }

    /// <summary>A 9-sliced box from a game texture, with <paramref name="padX"/>/<paramref name="padY"/> of room inside the border.</summary>
    public static StyleBox Box(string texture, int padX, int padY, Color? flat = null)
    {
        Texture2D? tex = Tex(texture);
        if (tex == null)
        {
            var f = new StyleBoxFlat { BgColor = flat ?? RowColor, BorderColor = Colors.Black };
            f.SetBorderWidthAll(2);
            f.SetCornerRadiusAll(3);
            Pad(f, padX, padY);
            return f;
        }
        // Godot draws 9-slice borders at texture size; scale a copy so the border lands at its on-screen size.
        (float share, float screen) = s_slice.TryGetValue(texture, out var v) ? v : (0.08f, 10f);
        float margin = share * tex.GetWidth();
        float scale = Mathf.Clamp(screen / margin, 0.1f, 1f);
        Texture2D drawn = Scaled(texture, tex, scale);
        float edge = Mathf.Max(1, Mathf.Round(margin * scale));
        var b = new StyleBoxTexture { Texture = drawn };
        b.SetTextureMarginAll(edge);
        Pad(b, padX + (int)(edge * 0.5f), padY + (int)(edge * 0.3f));
        return b;
    }

    static readonly Dictionary<string, Texture2D> s_scaled = new();

    static Texture2D Scaled(string path, Texture2D tex, float scale)
    {
        if (Mathf.IsEqualApprox(scale, 1f)) return tex;
        string key = path + "@" + scale;
        if (s_scaled.TryGetValue(key, out Texture2D? done)) return done;
        Texture2D result = tex;
        try
        {
            Image img = tex.GetImage();
            if (img.IsCompressed()) img.Decompress();
            img.Resize(Mathf.Max(1, (int)(img.GetWidth() * scale)), Mathf.Max(1, (int)(img.GetHeight() * scale)), Image.Interpolation.Lanczos);
            result = ImageTexture.CreateFromImage(img);
        }
        catch (Exception e) { Log.Write($"Kit: could not scale {path}: {e.Message}"); }
        return s_scaled[key] = result;
    }

    static void Pad(StyleBox b, int x, int y)
    {
        b.ContentMarginLeft = b.ContentMarginRight = x;
        b.ContentMarginTop = b.ContentMarginBottom = y;
    }

    static StyleBoxFlat Flat(Color bg, Color border, int width = 1, int radius = 3, int padX = 8, int padY = 4)
    {
        var f = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        f.SetBorderWidthAll(width);
        f.SetCornerRadiusAll(radius);
        Pad(f, padX, padY);
        return f;
    }

    /// <summary>
    /// The window theme. Type variations: "Heading", "Dim", "ActionButton", "Tab", "Card", "Ribbon".
    /// Set a control's ThemeTypeVariation to use one.
    /// </summary>
    public static Theme Theme => s_theme ??= Build();

    static Theme Build()
    {
        var t = new Theme { DefaultFont = BodyFont, DefaultFontSize = 15 };

        t.SetStylebox("panel", "PanelContainer", Box(Panel, 14, 12));
        t.SetStylebox("panel", "Panel", Box(Panel, 14, 12));

        t.SetTypeVariation("Card", "PanelContainer");
        t.SetStylebox("panel", "Card", Box(Tooltip, 10, 6));

        t.SetTypeVariation("Ribbon", "PanelContainer");
        t.SetStylebox("panel", "Ribbon", Box(Ribbon, 18, 3));

        t.SetColor("font_color", "Label", Text);
        t.SetTypeVariation("Dim", "Label");
        t.SetColor("font_color", "Dim", Dim);
        t.SetFontSize("font_size", "Dim", 13);
        t.SetTypeVariation("Heading", "Label");
        t.SetFont("font", "Heading", HeadFont);
        t.SetFontSize("font_size", "Heading", 20);
        t.SetColor("font_color", "Heading", Parchment);
        t.SetColor("font_shadow_color", "Heading", new Color(0, 0, 0, 0.6f));
        t.SetConstant("shadow_offset_y", "Heading", 2);

        ButtonStyle(t, "Button", Button);
        t.SetTypeVariation("ActionButton", "Button");
        ButtonStyle(t, "ActionButton", ActionButton);
        t.SetFont("font", "ActionButton", HeadFont);

        t.SetStylebox("normal", "LineEdit", Flat(Field, Color.FromHtml("#3c3c3c")));
        t.SetStylebox("focus", "LineEdit", Flat(Field, Gold));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", Dim);
        t.SetColor("caret_color", "LineEdit", Gold);

        t.SetStylebox("normal", "OptionButton", Box(Button, 10, 3));
        t.SetStylebox("hover", "OptionButton", Box(ButtonHover, 10, 3));
        t.SetStylebox("pressed", "OptionButton", Box(ButtonPressed, 10, 3));
        t.SetStylebox("panel", "PopupMenu", Box(Tooltip, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Flat(new Color(Gold, 0.18f), new Color(0, 0, 0, 0), 0, 2, 4, 2));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);

        t.SetStylebox("scroll", "VScrollBar", Flat(new Color(0, 0, 0, 0.25f), new Color(0, 0, 0, 0), 0, 3, 3, 0));
        t.SetStylebox("grabber", "VScrollBar", Flat(Color.FromHtml("#555"), new Color(0, 0, 0, 0), 0, 3, 3, 0));
        t.SetStylebox("grabber_highlight", "VScrollBar", Flat(Color.FromHtml("#777"), new Color(0, 0, 0, 0), 0, 3, 3, 0));
        t.SetStylebox("grabber_pressed", "VScrollBar", Flat(Gold, new Color(0, 0, 0, 0), 0, 3, 3, 0));

        t.SetStylebox("separator", "HSeparator", Flat(Line, Line, 0, 0, 0, 0));
        t.SetConstant("separation", "HSeparator", 8);
        t.SetConstant("separation", "VBoxContainer", 6);
        t.SetConstant("separation", "HBoxContainer", 8);

        // Tables (Tree): dark sheet, teal selection, parchment column titles, like the wiki's tables.
        t.SetStylebox("panel", "Tree", Flat(Color.FromHtml("#161616"), Colors.Black, 2, 3, 4, 4));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", Flat(new Color(0.18f, 0.5f, 0.53f, 0.55f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetStylebox("selected_focus", "Tree", Flat(new Color(0.18f, 0.5f, 0.53f, 0.7f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetStylebox("hovered", "Tree", Flat(new Color(1, 1, 1, 0.05f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetStylebox("title_button_normal", "Tree", Flat(Color.FromHtml("#1f1f1f"), Color.FromHtml("#0a0a0a"), 1, 0, 6, 3));
        t.SetStylebox("title_button_hover", "Tree", Flat(Color.FromHtml("#262626"), Color.FromHtml("#0a0a0a"), 1, 0, 6, 3));
        t.SetStylebox("title_button_pressed", "Tree", Flat(Color.FromHtml("#262626"), Color.FromHtml("#0a0a0a"), 1, 0, 6, 3));
        t.SetColor("title_button_color", "Tree", Parchment);
        t.SetColor("font_color", "Tree", Text);
        t.SetColor("font_selected_color", "Tree", Colors.White);
        t.SetColor("guide_color", "Tree", new Color(1, 1, 1, 0.04f));
        t.SetConstant("v_separation", "Tree", 6);
        t.SetConstant("h_separation", "Tree", 10);
        t.SetConstant("draw_guides", "Tree", 1);
        t.SetFont("title_button_font", "Tree", HeadFont);

        // Tab-like toggle buttons ("Tab" variation): flat until pressed.
        t.SetTypeVariation("Tab", "Button");
        ButtonStyle(t, "Tab", Button);
        t.SetStylebox("pressed", "Tab", Box(ActionButton, 12, 4));
        t.SetStylebox("hover_pressed", "Tab", Box(ActionButton, 12, 4));
        t.SetColor("font_pressed_color", "Tab", Colors.White);
        t.SetColor("font_hover_pressed_color", "Tab", Colors.White);

        t.SetStylebox("normal", "SpinBox", Flat(Field, Color.FromHtml("#3c3c3c")));

        // Tooltips: the game's own theme leaves them without a background.
        t.SetStylebox("panel", "TooltipPanel", Flat(Color.FromHtml("#101010"), Color.FromHtml("#3a3a3a"), 1, 3, 8, 5));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetFontSize("font_size", "TooltipLabel", 13);

        // Popups opened from a picker.
        t.SetStylebox("panel", "PopupPanel", Box(Tooltip, 6, 6));
        t.SetStylebox("panel", "ItemList", Flat(Color.FromHtml("#161616"), Colors.Black, 1, 3, 4, 4));
        t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("selected", "ItemList", Flat(new Color(0.18f, 0.5f, 0.53f, 0.55f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetStylebox("selected_focus", "ItemList", Flat(new Color(0.18f, 0.5f, 0.53f, 0.7f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetStylebox("hovered", "ItemList", Flat(new Color(1, 1, 1, 0.06f), new Color(0, 0, 0, 0), 0, 2, 0, 0));
        t.SetColor("font_color", "ItemList", Text);
        t.SetColor("font_selected_color", "ItemList", Colors.White);
        t.SetConstant("v_separation", "ItemList", 4);
        t.SetConstant("icon_margin", "ItemList", 6);

        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_color", "CheckButton", Text);
        return t;
    }

    static void ButtonStyle(Theme t, string type, string normal)
    {
        t.SetStylebox("normal", type, Box(normal, 12, 4));
        t.SetStylebox("hover", type, Box(ButtonHover, 12, 4));
        t.SetStylebox("pressed", type, Box(ButtonPressed, 12, 4));
        t.SetStylebox("hover_pressed", type, Box(ButtonPressed, 12, 4));
        t.SetStylebox("disabled", type, Box(ButtonDisabled, 12, 4));
        t.SetStylebox("focus", type, new StyleBoxEmpty());
        t.SetColor("font_color", type, Text);
        t.SetColor("font_hover_color", type, Colors.White);
        t.SetColor("font_pressed_color", type, Gold);
        t.SetColor("font_hover_pressed_color", type, Gold);
        t.SetColor("font_disabled_color", type, new Color(Dim, 0.6f));
    }

    // ---- icons ----

    static readonly Dictionary<ulong, Texture2D> s_icons = new();

    /// <summary>A copy of a game icon shrunk to <paramref name="size"/> pixels, for lists and tables. Cached.</summary>
    public static Texture2D? Icon(Texture2D? source, int size = 20)
    {
        if (source == null) return null;
        ulong key = source.GetInstanceId() * 31 + (ulong)size;
        if (s_icons.TryGetValue(key, out Texture2D? done)) return done;
        Texture2D result = source;
        try
        {
            Image? img = source.GetImage();
            if (img != null)
            {
                if (img.IsCompressed()) img.Decompress();
                float scale = size / (float)Mathf.Max(img.GetWidth(), img.GetHeight());
                img.Resize(Mathf.Max(1, (int)(img.GetWidth() * scale)), Mathf.Max(1, (int)(img.GetHeight() * scale)), Image.Interpolation.Lanczos);
                result = ImageTexture.CreateFromImage(img);
            }
        }
        catch { }
        return s_icons[key] = result;
    }

    // ---- small builders ----

    public static Label Label(string text, Color? color = null, int size = 0, string variation = "")
    {
        var l = new Label { Text = text, ThemeTypeVariation = variation, VerticalAlignment = VerticalAlignment.Center };
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        if (size > 0) l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeFontOverride("font", variation == "Heading" ? HeadFont : BodyFont);
        return l;
    }

    public static Label Heading(string text) => Label(text, variation: "Heading");

    public static Button Btn(string text, Action onPress, bool action = false)
    {
        var b = new Button { Text = text, ThemeTypeVariation = action ? "ActionButton" : "", FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPress;
        return b;
    }

    public static HBoxContainer Row(params Control[] children)
    {
        var h = new HBoxContainer();
        foreach (Control c in children) h.AddChild(c);
        return h;
    }

    public static Control Spacer()
    {
        return new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
    }
}
