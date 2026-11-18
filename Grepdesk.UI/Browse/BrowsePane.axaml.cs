using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Editor;

namespace Grepdesk.UI.Browse;

/// <summary>
/// A single browser pane holding one or more <see cref="BrowseView"/> tabs.
/// </summary>
public partial class BrowsePane : UserControl
{
    private static readonly IBrush ActiveTabBg = Brush.Parse("#313244");
    private static readonly IBrush InactiveTabBg = Brush.Parse("#1e1e2e");
    private static readonly IBrush ActiveTabFg = Brush.Parse("#cdd6f4");
    private static readonly IBrush InactiveTabFg = Brush.Parse("#a6adc8");
    private static readonly IBrush ActivePaneBorder = Brush.Parse("#89b4fa");
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly List<BrowseView> _tabs = [];
    private int _activeIndex = -1;
    private bool _showPlaces = true;
    private bool _showPreview = true;
    private bool _isActivePane;
    private bool _showActiveHighlight;

    public IPlatformShell? Shell { get; set; }
    public IReadOnlyList<AvailableEditor> Editors { get; set; } = [];

    public int TabCount => _tabs.Count;
    public int ActiveIndex => _activeIndex;
    public BrowseView? ActiveView => _activeIndex >= 0 && _activeIndex < _tabs.Count ? _tabs[_activeIndex] : null;
    public ToggleButton DualPaneButton => DualPaneToggle;

    public event Action<BrowsePane>? Activated;
    public event Action? SessionChanged;
    public event Action<ResultItem>? ViewRequested;
    public event Action<ResultItem?>? SelectionFollowed;
    public event Action<string, string>? SearchRequested;
    public event Action<string>? OrganizeRequested;
    public event Action<IReadOnlyList<string>>? Removed;
    public event Action<string>? OpenInNewTabRequested;

    public BrowsePane()
    {
        InitializeComponent();
        NewTabButton.Click += (_, _) =>
        {
            Activated?.Invoke(this);
            var folder = ActiveView?.CurrentFolder ?? DefaultFolder();
            _ = AddTabAsync(folder, select: true);
        };
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(this), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        ToolTip.SetTip(NewTabButton, Loc.Get("BrowseNewTab"));
        ToolTip.SetTip(DualPaneToggle, Loc.Get("BrowseDualPane"));
        foreach (var tab in _tabs)
            tab.ApplyLanguage();
        RebuildTabStrip();
    }

    public void SetChrome(bool showPlaces, bool showPreview, bool showActiveHighlight)
    {
        _showPlaces = showPlaces;
        _showPreview = showPreview;
        _showActiveHighlight = showActiveHighlight;
        foreach (var tab in _tabs)
            tab.SetPaneChrome(showPlaces, showPreview);
        UpdatePaneBorder();
    }

    public void SetActivePane(bool active)
    {
        _isActivePane = active;
        UpdatePaneBorder();
    }

    private void UpdatePaneBorder() =>
        PaneFrame.BorderBrush = _showActiveHighlight && _isActivePane ? ActivePaneBorder : TransparentBrush;

    public IReadOnlyList<string> GetTabPaths() =>
        _tabs.Select(t => t.CurrentFolder ?? DefaultFolder()).ToList();

    public async Task<BrowseView> AddTabAsync(string folder, bool select = true)
    {
        var view = new BrowseView
        {
            Shell = Shell,
            Editors = Editors,
            IsVisible = false
        };
        view.SetPaneChrome(_showPlaces, _showPreview);
        view.ViewRequested += item => ViewRequested?.Invoke(item);
        view.SelectionFollowed += item => SelectionFollowed?.Invoke(item);
        view.SearchRequested += (f, q) => SearchRequested?.Invoke(f, q);
        view.OrganizeRequested += f => OrganizeRequested?.Invoke(f);
        view.Removed += paths => Removed?.Invoke(paths);
        view.OpenInNewTabRequested += path => OpenInNewTabRequested?.Invoke(path);
        view.FolderChanged += _ =>
        {
            RebuildTabStrip();
            SessionChanged?.Invoke();
        };

        _tabs.Add(view);
        TabHost.Children.Add(view);

        var idx = _tabs.Count - 1;
        if (select || _activeIndex < 0)
            SelectTab(idx);
        else
            RebuildTabStrip();

        await view.NavigateAsync(folder);
        SessionChanged?.Invoke();
        return view;
    }

    public void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _activeIndex = index;
        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].IsVisible = i == _activeIndex;
        RebuildTabStrip();
        SessionChanged?.Invoke();
        ActiveView?.FocusList(selectFirst: false);
    }

    public void CycleTab(int delta)
    {
        if (_tabs.Count <= 1) return;
        var next = (_activeIndex + delta % _tabs.Count + _tabs.Count) % _tabs.Count;
        SelectTab(next);
    }

    public bool CloseTab(int index)
    {
        if (_tabs.Count <= 1 || index < 0 || index >= _tabs.Count) return false;
        var closing = _tabs[index];
        _tabs.RemoveAt(index);
        TabHost.Children.Remove(closing);
        closing.Cleanup();

        if (_activeIndex >= _tabs.Count)
            _activeIndex = _tabs.Count - 1;
        else if (index < _activeIndex)
            _activeIndex--;

        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].IsVisible = i == _activeIndex;
        RebuildTabStrip();
        SessionChanged?.Invoke();
        ActiveView?.FocusList(selectFirst: false);
        return true;
    }

    public bool CloseActiveTab() => CloseTab(_activeIndex);

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var idx = i;
            var view = _tabs[i];
            var active = i == _activeIndex;
            var path = view.CurrentFolder ?? "";
            var label = FormatTabTitle(path);

            var titleBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = active ? ActiveTabFg : InactiveTabFg,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(titleBlock);

            if (_tabs.Count > 1)
            {
                var closeBtn = new Button { Classes = { "tabClose" }, Content = "×" };
                ToolTip.SetTip(closeBtn, Loc.Get("BrowseCloseTab"));
                closeBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Activated?.Invoke(this);
                    CloseTab(idx);
                };
                row.Children.Add(closeBtn);
            }

            var tab = new Border
            {
                Background = active ? ActiveTabBg : InactiveTabBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = row
            };
            if (!string.IsNullOrEmpty(path))
                ToolTip.SetTip(tab, path);

            tab.PointerPressed += (_, e) =>
            {
                Activated?.Invoke(this);
                var props = e.GetCurrentPoint(tab).Properties;
                if (props.IsMiddleButtonPressed)
                {
                    CloseTab(idx);
                    e.Handled = true;
                }
                else if (props.IsLeftButtonPressed)
                {
                    SelectTab(idx);
                    e.Handled = true;
                }
            };
            TabStrip.Children.Add(tab);
        }
    }

    private static string FormatTabTitle(string path)
    {
        if (string.IsNullOrEmpty(path)) return Loc.Get("BrowseThisPc");
        var name = Path.GetFileName(PathUtil.Normalize(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string DefaultFolder()
    {
        var last = AppSettings.Current.LastBrowseFolder;
        return last is not null && Directory.Exists(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public void Cleanup()
    {
        foreach (var tab in _tabs)
            tab.Cleanup();
        _tabs.Clear();
        TabHost.Children.Clear();
    }
}
