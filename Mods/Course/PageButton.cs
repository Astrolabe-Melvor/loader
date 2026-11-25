using System;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Views;
using Godot;
using Astrolabe.UI;

namespace Astrolabe.Course;

/// <summary>
/// A "Queue" button in the top-right corner of every skill and combat page the game shows. It opens Course's
/// Add a step tab with that skill picked, and the recipe selected on the page (or the action running there) filled
/// in, so a step is one more click away. The button lives on the page itself, so it goes away with the page.
/// </summary>
sealed class PageButton
{
    const string NodeName = "AstrolabeQueueButton";
    readonly Action<ActionRef> _open;
    double _next;
    Node? _page;

    public PageButton(Action<ActionRef> open) => _open = open;

    /// <summary>Takes the button off the page shown now (the option was switched off).</summary>
    public void Clear()
    {
        if (_page != null && GodotObject.IsInstanceValid(_page) && _page.GetNodeOrNull(NodeName) is Node b) b.QueueFree();
        _page = null;
    }

    /// <summary>Called every frame; looks at the page four times a second.</summary>
    public void Tick(Game? game, double now)
    {
        if (game == null || now < _next) return;
        _next = now + 0.25;
        try { Check(game); }
        catch (Exception e) { _next = now + 30; MelvorModLoader.Log.Write("[Course] Queue button: " + e.Message); }
    }

    void Check(Game game)
    {
        var view = KitGame.View();
        Node? container = view?.EntityViewContainer;
        Node? page = container?.GetChildren().OfType<BaseActiveEntityView>().LastOrDefault();
        if (page == _page && (page == null || page.HasNode(NodeName))) return;
        _page = page;
        if (page is not Control control) return;
        GameEntity? entity = EntityOf(page);
        if (entity == null || Catalog.Skills(game).All(s => s.Id != entity.Id)) return;   // bank, shop, passive skills

        var b = Kit.Btn("Queue", () => _open(Pick(game, entity)), action: true);
        b.Name = NodeName;
        b.Theme = Kit.Theme;
        b.TooltipText = $"Add a {entity.DisplayName} step to Course: opens its Add a step tab with this page's action picked";
        b.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        b.GrowHorizontal = Control.GrowDirection.Begin;
        b.OffsetLeft = -122;
        b.OffsetRight = -12;
        b.OffsetTop = 8;
        b.OffsetBottom = 48;
        control.AddChild(b);
    }

    static GameEntity? EntityOf(Node page)
    {
        for (Type? t = page.GetType(); t != null; t = t.BaseType)
            if (t.GetProperty("Entity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetValue(page) is GameEntity e)
                return e;
        return null;
    }

    /// <summary>The page's selected recipe, else what this skill is doing now, else just the skill.</summary>
    static ActionRef Pick(Game game, GameEntity entity)
    {
        ActionComponent? comp = entity.Components.Values.OfType<ActionComponent>().FirstOrDefault();
        object? selected = comp?.GetType().GetProperty("SelectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(comp);
        string? id = IdOf(selected);
        if (id != null) return new ActionRef { Entity = entity.Id, Target = id };
        if (Starter.Current(game) is { } now && now.Entity == entity.Id) return now;
        return new ActionRef { Entity = entity.Id };
    }

    static string? IdOf(object? o)
    {
        if (o == null) return null;
        if (o is IDescribedObject d) return d.Id;
        foreach (string p in new[] { "Recipe", "Data" })
            if (o.GetType().GetProperty(p)?.GetValue(o) is NamespacedObject n) return n.Id;
        return null;
    }
}
