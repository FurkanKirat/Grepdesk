using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Editor;

namespace Grepdesk.UI.Browse;

/// <summary>
/// Hosts one or two <see cref="BrowsePane"/>s (single-pane or dual-pane), each with tabs,
/// and persists/restores the layout across sessions.
/// </summary>
public partial class BrowseWorkspace : UserControl
{
    private readonly TextBlock _fallbackStatus = new();
    private readonly PreviewPane _fallbackPreview = new();
    private BrowsePane _activePane;
    private bool _dualPane;
    private bool _restoring;
    private bool _previewAllowed = true;

    public IPlatformShell? Shell
    {
        get => LeftPane.Shell;
        set { LeftPane.Shell = value; RightPane.Shell = value; }
    }

    public IReadOnlyList<AvailableEditor> Editors
    {
        get => LeftPane.Editors;
        set { LeftPane.Editors = value; RightPane.Editors = value; }
    }

    public BrowseView? ActiveView => _activePane.ActiveView ?? LeftPane.ActiveView;
    public TextBlock StatusLine => ActiveView?.StatusLine ?? _fallbackStatus;
    public PreviewPane PreviewPanel => ActiveView?.PreviewPanel ?? _fallbackPreview;

    public event Action<ResultItem>? ViewRequested;
    public event Action<ResultItem?>? SelectionFollowed;
    public event Action<string, string>? SearchRequested;
    public event Action<string>? OrganizeRequested;
    public event Action<IReadOnlyList<string>>? Removed;

    public BrowseWorkspace()
    {
        InitializeComponent();
        _activePane = LeftPane;

        LeftPane.DualPaneButton.IsVisible = true;
        LeftPane.DualPaneButton.IsCheckedChanged += (_, _) =>
        {
            if (_restoring) return;
            _ = SetDualPaneAsync(LeftPane.DualPaneButton.IsChecked == true);
        };

        WirePane(LeftPane);
        WirePane(RightPane);

        PaneSplitter.DragCompleted += (_, _) => SaveSession();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void WirePane(BrowsePane pane)
    {
        pane.Activated += p => SetActivePane(p);
        pane.SessionChanged += () => { if (!_restoring) SaveSession(); };
        pane.ViewRequested += item => ViewRequested?.Invoke(item);
        pane.SelectionFollowed += item => SelectionFollowed?.Invoke(item);
        pane.SearchRequested += (f, q) => SearchRequested?.Invoke(f, q);
        pane.OrganizeRequested += f => OrganizeRequested?.Invoke(f);
        pane.Removed += paths => Removed?.Invoke(paths);
        pane.OpenInNewTabRequested += path => _ = pane.AddTabAsync(path, select: true);
    }

    private void SetActivePane(BrowsePane pane)
    {
        if (_activePane == pane) return;
        _activePane = pane;
        UpdateChrome();
        if (!_restoring) SaveSession();
    }

    private void UpdateChrome()
    {
        LeftPane.SetActivePane(_activePane == LeftPane);
        RightPane.SetActivePane(_activePane == RightPane);
        if (_dualPane)
        {
            // Keep Places on the left pane and Preview on the right pane to maximize list space.
            LeftPane.SetChrome(showPlaces: true, showPreview: false, showActiveHighlight: true);
            RightPane.SetChrome(showPlaces: false, showPreview: _previewAllowed, showActiveHighlight: true);
        }
        else
        {
            LeftPane.SetChrome(showPlaces: true, showPreview: _previewAllowed, showActiveHighlight: false);
        }
    }

    public void SetPreviewVisible(bool visible)
    {
        _previewAllowed = visible;
        UpdateChrome();
    }

    public void ApplyLanguage()
    {
        LeftPane.ApplyLanguage();
        RightPane.ApplyLanguage();
    }

    public void FocusList(bool selectFirst = false) => ActiveView?.FocusList(selectFirst);
    public void FocusFilter() => ActiveView?.FocusFilter();

    public async Task NavigateAsync(string folder, string? select = null)
    {
        if (ActiveView is { } view)
            await view.NavigateAsync(folder, select);
        else
            await _activePane.AddTabAsync(folder, select: true);
    }

    public async Task RestoreOrInitAsync(string? startFolder)
    {
        _restoring = true;
        try
        {
            var s = AppSettings.Current;
            var fallback = ValidTabOrFallback(s.LastBrowseFolder);

            var leftPaths = s.LeftPaneTabs.Where(IsValidBrowseTarget).ToList();
            if (leftPaths.Count == 0) leftPaths.Add(fallback);

            foreach (var path in leftPaths)
                await LeftPane.AddTabAsync(path, select: false);
            LeftPane.SelectTab(Math.Clamp(s.LeftActiveTabIndex, 0, LeftPane.TabCount - 1));

            if (s.DualPaneEnabled)
            {
                var rightPaths = s.RightPaneTabs.Where(IsValidBrowseTarget).ToList();
                if (rightPaths.Count == 0) rightPaths.Add(leftPaths[0]);
                foreach (var path in rightPaths)
                    await RightPane.AddTabAsync(path, select: false);
                RightPane.SelectTab(Math.Clamp(s.RightActiveTabIndex, 0, RightPane.TabCount - 1));
                ApplyDualPaneLayout(true, s.DualPaneSplitRatio);
                _activePane = s.ActivePaneIndex == 1 ? RightPane : LeftPane;
            }
            else
            {
                ApplyDualPaneLayout(false, s.DualPaneSplitRatio);
                _activePane = LeftPane;
            }

            LeftPane.DualPaneButton.IsChecked = _dualPane;
            UpdateChrome();
        }
        finally
        {
            _restoring = false;
        }

        if (!string.IsNullOrWhiteSpace(startFolder))
            await _activePane.AddTabAsync(startFolder, select: true);

        SaveSession();
    }

    public async Task SetDualPaneAsync(bool enabled)
    {
        if (_dualPane == enabled) return;
        if (enabled && RightPane.TabCount == 0)
        {
            var seed = LeftPane.ActiveView?.CurrentFolder ?? ValidTabOrFallback(AppSettings.Current.LastBrowseFolder);
            await RightPane.AddTabAsync(seed, select: true);
        }
        ApplyDualPaneLayout(enabled, AppSettings.Current.DualPaneSplitRatio);
        if (!enabled) _activePane = LeftPane;
        LeftPane.DualPaneButton.IsChecked = enabled;
        UpdateChrome();
        SaveSession();
    }

    private void ApplyDualPaneLayout(bool enabled, double ratio)
    {
        _dualPane = enabled;
        var clamped = Math.Clamp(ratio, 0.2, 0.8);
        PaneSplitter.IsVisible = enabled;
        RightPane.IsVisible = enabled;
        if (enabled)
        {
            PaneGrid.ColumnDefinitions[0].Width = new GridLength(clamped, GridUnitType.Star);
            PaneGrid.ColumnDefinitions[2].Width = new GridLength(1 - clamped, GridUnitType.Star);
        }
        else
        {
            PaneGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            PaneGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.T when ctrl && !shift:
            {
                var folder = ActiveView?.CurrentFolder ?? ValidTabOrFallback(AppSettings.Current.LastBrowseFolder);
                _ = _activePane.AddTabAsync(folder, select: true);
                e.Handled = true;
                break;
            }
            case Key.W when ctrl && !shift:
            {
                if (_activePane.CloseActiveTab())
                    e.Handled = true;
                break;
            }
            case Key.Tab when ctrl:
            {
                _activePane.CycleTab(shift ? -1 : 1);
                e.Handled = true;
                break;
            }
            case Key.F3 when e.KeyModifiers == KeyModifiers.None:
            {
                _ = SetDualPaneAsync(!_dualPane);
                e.Handled = true;
                break;
            }
            case Key.F6 when e.KeyModifiers == KeyModifiers.None && _dualPane:
            {
                SetActivePane(_activePane == LeftPane ? RightPane : LeftPane);
                _activePane.ActiveView?.FocusList(selectFirst: false);
                e.Handled = true;
                break;
            }
        }
    }

    public void SaveSession()
    {
        var s = AppSettings.Current;
        s.DualPaneEnabled = _dualPane;
        s.ActivePaneIndex = _activePane == RightPane ? 1 : 0;
        if (_dualPane)
        {
            var w0 = PaneGrid.ColumnDefinitions[0].ActualWidth;
            var w2 = PaneGrid.ColumnDefinitions[2].ActualWidth;
            if (w0 + w2 > 0)
                s.DualPaneSplitRatio = Math.Clamp(w0 / (w0 + w2), 0.2, 0.8);
        }
        s.LeftPaneTabs = LeftPane.GetTabPaths().ToList();
        s.LeftActiveTabIndex = Math.Max(0, LeftPane.ActiveIndex);
        if (RightPane.TabCount > 0)
        {
            s.RightPaneTabs = RightPane.GetTabPaths().ToList();
            s.RightActiveTabIndex = Math.Max(0, RightPane.ActiveIndex);
        }
        s.Save();
    }

    public void Cleanup()
    {
        SaveSession();
        LeftPane.Cleanup();
        RightPane.Cleanup();
    }

    private static bool IsValidBrowseTarget(string path) =>
        path == BrowseView.ThisPc || Directory.Exists(path) || ZipBrowsePath.IsZipPath(path);

    private static string ValidTabOrFallback(string? candidate) =>
        candidate is not null && IsValidBrowseTarget(candidate)
            ? candidate
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
