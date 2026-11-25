using System;
using System.Collections.Generic;
using Godot;
using MelvorModLoader;

namespace Astrolabe.UI;

/// <summary>
/// One stop on a window's tour: a gold outline around <paramref name="Target"/> (or none, for an overview step)
/// with a card saying what it is. <paramref name="Before"/> runs when the step opens, to switch to the right tab,
/// say, or open a section first. Targets are looked up when the step opens, so rows built later still work.
/// </summary>
public sealed record TourStep(string Title, string Text, Func<Control?>? Target = null, Action? Before = null);

public sealed partial class KitWindow
{
    readonly Button _tourBtn;
    TourStep[] _tour = Array.Empty<TourStep>();
    int _step = -1;
    Control? _overlay;
    Panel? _ring;
    PanelContainer? _card;
    Label? _cardTitle, _cardCount;
    RichTextLabel? _cardText;
    Button? _back, _next;
    bool _offered;
    Control? _target;

    /// <summary>Adds a Tour button to the title bar. Until the window has been opened and closed once, the button is
    /// green and says "Take the tour" (it takes no room in the window, so nothing moves).</summary>
    public void SetTour(params TourStep[] steps)
    {
        _tour = steps;
        _tourBtn.Visible = steps.Length > 0;
    }

    public bool TourRunning => _step >= 0;

    void OfferTour()
    {
        if (_tour.Length == 0 || _offered) return;
        if (LoaderState.Get().Toured.Contains(_key)) return;
        _offered = true;
        _tourBtn.Text = "Take the tour";
        _tourBtn.ThemeTypeVariation = "ActionButton";
        _tourBtn.TooltipText = $"New to {Title}? A short walk through this window, pointing at each part. After this the button just says Tour.";
    }

    // Offered once: the first close (or starting the tour) puts the button back to plain "Tour".
    void Toured()
    {
        if (!_offered) return;
        _offered = false;
        LoaderState st = LoaderState.Get();
        if (!st.Toured.Contains(_key)) { st.Toured.Add(_key); LoaderState.Save(); }
        _tourBtn.Text = "Tour";
        _tourBtn.ThemeTypeVariation = "";
        _tourBtn.TooltipText = "A short walk through this window, pointing at each part";
    }

    /// <summary>Starts the tour from the first step (opens the window if it is closed).</summary>
    public void StartTour()
    {
        if (_tour.Length == 0) return;
        if (!_layer.Visible) Open();
        Toured();
        if (_helpCard.Visible) ToggleHelp();
        BuildOverlay();
        Go(0);
    }

    public void EndTour()
    {
        if (_step < 0) return;
        _step = -1;
        _target = null;
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Follow;
        _overlay?.QueueFree();
        _overlay = null;
    }

    void BuildOverlay()
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) return;
        _overlay = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = Kit.Theme };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var ring = new StyleBoxFlat { BgColor = new Color(Kit.Gold, 0.08f), BorderColor = Kit.Gold, DrawCenter = true };
        ring.SetBorderWidthAll(3);
        ring.SetCornerRadiusAll(4);
        _ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        _ring.AddThemeStyleboxOverride("panel", ring);
        _overlay.AddChild(_ring);

        _card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(380, 0) };
        var edge = new StyleBoxFlat { BgColor = Color.FromHtml("#1b1b1b"), BorderColor = Kit.Gold };
        edge.SetBorderWidthAll(2);
        edge.SetCornerRadiusAll(4);
        edge.ContentMarginLeft = edge.ContentMarginRight = 14;
        edge.ContentMarginTop = edge.ContentMarginBottom = 10;
        _card.AddThemeStyleboxOverride("panel", edge);
        var col = new VBoxContainer();
        _cardTitle = Kit.Label("", Kit.Gold, 17, "Heading");
        _cardCount = Kit.Label("", null, 12, "Dim");
        col.AddChild(Kit.Row(_cardTitle, Kit.Spacer(), _cardCount));
        _cardText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(380, 0) };
        _cardText.AddThemeFontSizeOverride("normal_font_size", 14);
        _cardText.AddThemeFontSizeOverride("bold_font_size", 14);
        col.AddChild(_cardText);
        _back = Kit.Btn("Back", () => Go(_step - 1));
        _next = Kit.Btn("Next", () => { if (_step + 1 >= _tour.Length) EndTour(); else Go(_step + 1); }, action: true);
        col.AddChild(Kit.Row(Kit.Btn("End tour", EndTour), Kit.Spacer(), _back, _next));
        _card.AddChild(col);
        _overlay.AddChild(_card);

        _layer.AddChild(_overlay);
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Follow;
    }

    void Go(int i)
    {
        if (i < 0 || i >= _tour.Length) return;
        _step = i;
        TourStep s = _tour[i];
        try { s.Before?.Invoke(); } catch (Exception e) { Log.Write($"Tour step '{s.Title}': {e.Message}"); }
        _cardTitle!.Text = s.Title;
        _cardCount!.Text = $"{i + 1} of {_tour.Length}";
        _cardText!.Text = s.Text;
        _back!.Disabled = i == 0;
        _next!.Text = i + 1 >= _tour.Length ? "Done" : "Next";
        _target = null;
        // Look the target up after Before's changes have settled.
        Callable.From(() =>
        {
            if (_step != i) return;
            try { _target = s.Target?.Invoke(); } catch { _target = null; }
            if (_target != null && GodotObject.IsInstanceValid(_target) && _scroll.IsAncestorOf(_target)) _scroll.EnsureControlVisible(_target);
        }).CallDeferred();
    }

    // Every frame: keep the outline on the target and the card beside it, so scrolling or moving the window is fine.
    void Follow()
    {
        if (_overlay == null || _card == null || _ring == null || !GodotObject.IsInstanceValid(_overlay)) return;
        Vector2 screen = Screen;
        bool shown = _target != null && GodotObject.IsInstanceValid(_target) && _target.IsVisibleInTree();
        _ring.Visible = shown;
        Vector2 card = _card.GetCombinedMinimumSize();
        _card.Size = card;
        Vector2 at;
        if (shown)
        {
            Rect2 r = _target!.GetGlobalRect().Grow(4);
            // Clip to the window, so a target half scrolled away doesn't outline empty space.
            r = r.Intersection(Root.GetGlobalRect());
            _ring.Position = r.Position;
            _ring.Size = r.Size;
            // Below the target if it fits, else above, else to the side; always on screen.
            if (r.End.Y + 10 + card.Y <= screen.Y) at = new Vector2(r.Position.X, r.End.Y + 10);
            else if (r.Position.Y - 10 - card.Y >= 0) at = new Vector2(r.Position.X, r.Position.Y - 10 - card.Y);
            else at = new Vector2(r.End.X + 10, r.Position.Y);
        }
        else at = Root.Position + (Root.Size - card) / 2;
        _card.Position = new Vector2(Mathf.Clamp(at.X, 4, Mathf.Max(4, screen.X - card.X - 4)), Mathf.Clamp(at.Y, 4, Mathf.Max(4, screen.Y - card.Y - 4))).Round();
    }
}
