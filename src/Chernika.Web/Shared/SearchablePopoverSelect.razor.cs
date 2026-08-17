using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Chernika.Web.Shared;

/// <summary>
/// Переиспользуемый searchable popover-select (UI kit v2, раздел «Popover и dropdown»).
/// Presentation-слой: данные приходят через параметры, компонент не обращается к сервисам.
/// Позиционирование/outside-click — через общий wwwroot/js/popoverPositioning.js.
/// </summary>
public partial class SearchablePopoverSelect<TItem> : IAsyncDisposable where TItem : class
{
    private const int PopoverMaxHeightPx = 320;

    [Parameter, EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = Array.Empty<TItem>();
    [Parameter, EditorRequired] public Func<TItem, string> DisplayTextSelector { get; set; } = null!;
    [Parameter, EditorRequired] public Func<TItem, string, bool> SearchPredicate { get; set; } = null!;
    [Parameter, EditorRequired] public RenderFragment<TItem> ItemTemplate { get; set; } = null!;
    [Parameter] public EventCallback<TItem> OnSelected { get; set; }
    [Parameter] public TItem? Selected { get; set; }
    [Parameter] public string Placeholder { get; set; } = "Поиск...";
    [Parameter] public string EmptyText { get; set; } = "Ничего не найдено";
    [Parameter] public bool AllowQuickCreate { get; set; }
    [Parameter] public string QuickCreateLabel { get; set; } = "Добавить новый";
    [Parameter] public RenderFragment? QuickCreateModalTemplate { get; set; }
    [Parameter] public bool Disabled { get; set; }

    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Nav { get; set; } = null!;

    private ElementReference _anchorRef;
    private ElementReference _popoverRef;
    private ElementReference _inputRef;
    private DotNetObjectReference<SearchablePopoverSelect<TItem>>? _dotNetRef;
    private bool _isOpen;
    private bool _listenersAttached;
    private bool _quickCreateOpen;
    private string _placement = "bottom";
    private string _searchText = string.Empty;
    private string _displayText = string.Empty;
    private int _highlightedIndex = -1;
    private bool _disposed;

    private string AriaExpanded => _isOpen ? "true" : "false";

    private List<TItem> _filteredItems => Items
        .Where(i => string.IsNullOrWhiteSpace(_searchText) || SearchPredicate(i, _searchText))
        .ToList();

    protected override void OnInitialized()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        Nav.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        if (!_isOpen)
            _displayText = Selected is null ? string.Empty : DisplayTextSelector(Selected);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_isOpen && !_listenersAttached)
        {
            _listenersAttached = true;
            try
            {
                _placement = await JS.InvokeAsync<string>("popoverPositioning.getPlacement", _anchorRef, PopoverMaxHeightPx);
                await JS.InvokeVoidAsync("popoverPositioning.addOutsideClickListener", _anchorRef, _popoverRef, _dotNetRef, nameof(CloseFromOutsideClick));
                StateHasChanged();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private Task OnFocusAsync() => OpenAsync();

    private Task OnTriggerClickAsync() => OpenAsync();

    private Task OpenAsync()
    {
        if (Disabled || _isOpen)
            return Task.CompletedTask;

        _isOpen = true;
        _listenersAttached = false;
        _highlightedIndex = -1;
        _searchText = string.Empty;
        _displayText = Selected is null ? string.Empty : DisplayTextSelector(Selected);
        return Task.CompletedTask;
    }

    private async Task CloseAsync(bool restoreFocus = true)
    {
        if (!_isOpen)
            return;

        _isOpen = false;
        _listenersAttached = false;
        _highlightedIndex = -1;
        _searchText = string.Empty;
        _displayText = Selected is null ? string.Empty : DisplayTextSelector(Selected);
        await RemoveListenersAsync();
        if (restoreFocus)
            await FocusTriggerAsync();
        StateHasChanged();
    }

    [JSInvokable]
    public Task CloseFromOutsideClick() => CloseAsync(restoreFocus: false);

    private Task OnInputAsync(ChangeEventArgs e)
    {
        _searchText = e.Value?.ToString() ?? string.Empty;
        _displayText = _searchText;
        _highlightedIndex = -1;
        return _isOpen ? Task.CompletedTask : OpenAsync();
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        if (Disabled)
            return;

        var items = _filteredItems;
        switch (e.Key)
        {
            case "ArrowDown":
                if (!_isOpen)
                {
                    await OpenAsync();
                    return;
                }
                if (items.Count > 0)
                    _highlightedIndex = _highlightedIndex < items.Count - 1 ? _highlightedIndex + 1 : 0;
                break;

            case "ArrowUp":
                if (!_isOpen)
                {
                    await OpenAsync();
                    return;
                }
                if (items.Count > 0)
                    _highlightedIndex = _highlightedIndex > 0 ? _highlightedIndex - 1 : items.Count - 1;
                break;

            case "Enter":
                if (!_isOpen)
                {
                    await OpenAsync();
                    return;
                }
                if (_highlightedIndex >= 0 && _highlightedIndex < items.Count)
                    await SelectItemAsync(items[_highlightedIndex]);
                break;

            case " ":
                if (!_isOpen)
                    await OpenAsync();
                break;

            case "Escape":
                await CloseAsync(restoreFocus: true);
                break;
        }
    }

    private async Task SelectItemAsync(TItem item)
    {
        _isOpen = false;
        _listenersAttached = false;
        _highlightedIndex = -1;
        _searchText = string.Empty;
        _displayText = DisplayTextSelector(item);
        await RemoveListenersAsync();
        await OnSelected.InvokeAsync(item);
        await FocusTriggerAsync();
        StateHasChanged();
    }

    private Task OpenQuickCreateAsync()
    {
        _quickCreateOpen = true;
        return CloseAsync(restoreFocus: false);
    }

    /// <summary>
    /// Закрывает quick-create модалку без создания (вызывается родителем по отмене).
    /// </summary>
    public void CloseQuickCreate()
    {
        _quickCreateOpen = false;
        StateHasChanged();
    }

    /// <summary>
    /// Сообщает компоненту об успешном создании элемента через quick-create:
    /// закрывает модалку, выбирает элемент и уведомляет родителя через <see cref="OnSelected"/>.
    /// </summary>
    public Task NotifyQuickCreatedAsync(TItem item)
    {
        _quickCreateOpen = false;
        return SelectItemAsync(item);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_disposed || (!_isOpen && !_quickCreateOpen))
            return;

        _ = InvokeAsync(async () =>
        {
            _quickCreateOpen = false;
            await CloseAsync(restoreFocus: false);
        });
    }

    private async Task RemoveListenersAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("popoverPositioning.removeOutsideClickListener");
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task FocusTriggerAsync()
    {
        try
        {
            await _inputRef.FocusAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Nav.LocationChanged -= OnLocationChanged;
        await RemoveListenersAsync();
        _dotNetRef?.Dispose();
    }
}
