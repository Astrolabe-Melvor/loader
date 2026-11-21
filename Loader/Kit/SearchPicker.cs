using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Astrolabe.UI;

/// <summary>One entry of a <see cref="SearchPicker"/>.</summary>
public sealed record PickItem(string Id, string Text, Texture2D? Icon = null, bool Dim = false, string Note = "");

/// <summary>
/// A dropdown with a search box: a button showing the current choice; clicking it opens a list you can type to
/// filter. For long lists (every item, every Smithing recipe) where a plain dropdown is unusable.
/// </summary>
public sealed class SearchPicker
{
    readonly PopupPanel _popup;
    readonly LineEdit _search;
    readonly ItemList _list;
    readonly List<PickItem> _shown = new();
    List<PickItem> _items = new();
    const int MaxShown = 400;

    /// <summary>The button to put in a layout.</summary>
    public Button Button { get; }

    /// <summary>The chosen entry's id, or null.</summary>
    public string? SelectedId { get; private set; }
    public PickItem? Selected => _items.FirstOrDefault(i => i.Id == SelectedId);

    /// <summary>Raised when the player picks an entry.</summary>
    public event Action<PickItem>? Picked;

    public string Placeholder { get; set; } = "Choose...";

    public SearchPicker(float minWidth = 220)
    {
        Button = new Button
        {
            CustomMinimumSize = new Vector2(minWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Left,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            ClipText = true,
            FocusMode = Control.FocusModeEnum.None,
        };
        Button.Pressed += Open;

        _popup = new PopupPanel { Theme = Kit.Theme, Size = new Vector2I(380, 380) };
        var box = new VBoxContainer();
        _search = new LineEdit { PlaceholderText = "Type to search...", ClearButtonEnabled = true };
        _search.TextChanged += _ => Filter();
        _search.TextSubmitted += _ => { if (_shown.Count > 0) Choose(_shown[0]); };
        // Icons are drawn at a fixed small size, so callers can pass the game's full-size textures.
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 320), FixedIconSize = new Vector2I(22, 22) };
        _list.ItemClicked += (index, _, button) => { if (button == (long)MouseButton.Left && index >= 0 && index < _shown.Count) Choose(_shown[(int)index]); };
        box.AddChild(_search);
        box.AddChild(_list);
        _popup.AddChild(box);
        Button.AddChild(_popup);
        Show();
    }

    /// <summary>Picks the first entry when nothing is selected; off for pickers where a choice starts something.</summary>
    public bool AutoSelect { get; set; } = true;

    public void SetItems(IEnumerable<PickItem> items, string? select = null)
    {
        _items = items.ToList();
        SelectedId = select != null && _items.Any(i => i.Id == select) ? select
            : !AutoSelect ? null
            : _items.FirstOrDefault(i => !i.Dim)?.Id ?? _items.FirstOrDefault()?.Id;
        Show();
    }

    /// <summary>Selects by id without raising <see cref="Picked"/>.</summary>
    public void Select(string? id)
    {
        if (id != null && _items.Any(i => i.Id == id)) SelectedId = id;
        Show();
    }

    public bool Disabled { get => Button.Disabled; set => Button.Disabled = value; }
    public bool Visible { get => Button.Visible; set => Button.Visible = value; }

    void Show()
    {
        PickItem? p = Selected;
        Button.Text = p?.Text ?? (_items.Count == 0 ? "Nothing to choose" : Placeholder);
        Button.Icon = Kit.Icon(p?.Icon, 20);
        Button.TooltipText = p == null ? "" : p.Note.Length > 0 ? $"{p.Text} ({p.Note})" : p.Text;
    }

    void Open()
    {
        Rect2 r = Button.GetGlobalRect();
        int width = Mathf.Max(380, (int)r.Size.X);
        _search.Text = "";
        Filter();
        _popup.Popup(new Rect2I((int)r.Position.X, (int)(r.Position.Y + r.Size.Y), width, 380));
        _search.GrabFocus();
        int at = _shown.FindIndex(i => i.Id == SelectedId);
        if (at >= 0) { _list.Select(at); _list.EnsureCurrentIsVisible(); }
    }

    void Filter()
    {
        string q = _search.Text.Trim();
        _shown.Clear();
        IEnumerable<PickItem> hits = q.Length == 0 ? _items
            : _items.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Text.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        _shown.AddRange(hits.Take(MaxShown));
        _list.Clear();
        foreach (PickItem i in _shown)
        {
            int idx = _list.AddItem(i.Note.Length > 0 ? $"{i.Text}   {i.Note}" : i.Text, i.Icon);
            if (i.Dim) _list.SetItemCustomFgColor(idx, Kit.Dim);
        }
    }

    void Choose(PickItem item)
    {
        SelectedId = item.Id;
        Show();
        _popup.Hide();
        Picked?.Invoke(item);
    }
}
