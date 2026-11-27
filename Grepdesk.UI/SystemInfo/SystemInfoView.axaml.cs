using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Grepdesk.Core.SystemInfo;
using Grepdesk.UI.Helpers;

namespace Grepdesk.UI.SystemInfo;

/// <summary>
/// Hardware &amp; system overview page: shows clean CPU, GPU, RAM, Storage, Motherboard,
/// Displays, and OS cards that expand on click for full specifications.
/// </summary>
public partial class SystemInfoView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly string[] CardOrder = ["cpu", "gpu", "ram", "storage", "motherboard", "displays", "os"];

    private readonly ISystemInfoProvider _provider = PlatformShellFactory.CreateSystemInfoProvider();
    private readonly HashSet<string> _expandedIds = [];

    private SystemInfoSnapshot? _snapshot;
    private bool _loaded;
    private bool _loading;
    private Func<string>? _statusMessage;

    public TextBlock StatusLine => StatusText;

    public SystemInfoView()
    {
        InitializeComponent();

        ExpandAllButton.Click += (_, _) => ToggleExpandAll();
        CopyButton.Click += async (_, _) => await CopySummaryAsync();
        RefreshButton.Click += async (_, _) => await ReloadAsync();

        CopyButton.IsEnabled = false;
        ExpandAllButton.IsEnabled = false;

        ApplyLanguage();
    }

    public async Task OnShownAsync()
    {
        if (!_loaded && !_loading)
            await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        _statusMessage = () => Loc.Get("SystemInfoLoading");
        StatusText.Text = _statusMessage();

        try
        {
            var snap = await Task.Run(() => _provider.GetSystemInfo());
            _snapshot = snap;
            _loaded = true;
            _statusMessage = null;
            StatusText.Text = "";
            CopyButton.IsEnabled = true;
            ExpandAllButton.IsEnabled = true;
            UpdateExpandAllButtonText();
            RenderCards();
            SessionTrail.Note($"SystemInfo: loaded");
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "SystemInfoView.ReloadAsync");
            _statusMessage = () => Loc.Get("SystemInfoLoadFailed");
            StatusText.Text = _statusMessage();
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    public void ApplyLanguage()
    {
        TitleText.Text = Loc.Get("SystemInfoTab");
        SubtitleText.Text = Loc.Get("SystemInfoSubtitle");
        CopyButton.Content = Loc.Get("SystemInfoCopy");
        RefreshButton.Content = Loc.Get("SystemInfoRefresh");
        UpdateExpandAllButtonText();

        if (_statusMessage is not null)
            StatusText.Text = _statusMessage();

        if (_snapshot is not null)
            RenderCards();
    }

    private void UpdateExpandAllButtonText()
    {
        var allExpanded = CardOrder.All(id => _expandedIds.Contains(id));
        ExpandAllButton.Content = Loc.Get(allExpanded ? "SystemInfoCollapseAll" : "SystemInfoExpandAll");
    }

    private void ToggleExpandAll()
    {
        if (_snapshot is null) return;
        var allExpanded = CardOrder.All(id => _expandedIds.Contains(id));
        _expandedIds.Clear();
        if (!allExpanded)
        {
            foreach (var id in CardOrder)
                _expandedIds.Add(id);
        }
        UpdateExpandAllButtonText();
        RenderCards();
    }

    private async Task CopySummaryAsync()
    {
        if (_snapshot is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var detailed = _expandedIds.Count > 0;
        var text = SystemInfoFormatter.FormatPlainText(_snapshot, detailed);
        await clipboard.SetTextAsync(text);
        _statusMessage = () => Loc.Get("SystemInfoCopied");
        StatusText.Text = _statusMessage();
    }

    // =========================================================================
    // Card Rendering
    // =========================================================================

    private void RenderCards()
    {
        CardsHost.Children.Clear();
        if (_snapshot is null) return;

        var s = _snapshot;
        CardsHost.Children.Add(BuildCpuCard(s.Cpu));
        CardsHost.Children.Add(BuildGpuCard(s.Gpus));
        CardsHost.Children.Add(BuildRamCard(s.Memory));
        CardsHost.Children.Add(BuildStorageCard(s.StorageDrives));
        CardsHost.Children.Add(BuildMotherboardCard(s.Motherboard));
        CardsHost.Children.Add(BuildDisplaysCard(s.Displays));
        CardsHost.Children.Add(BuildOsCard(s.Os));
    }

    private Border BuildCpuCard(CpuInfo cpu)
    {
        var summaryParts = new List<string>
        {
            Loc.Get("SystemInfoCoresThreads", cpu.PhysicalCores, cpu.LogicalProcessors)
        };
        if (cpu.BaseClockMhz is > 0)
            summaryParts.Add(SystemInfoFormatter.FormatCpuClock(cpu.BaseClockMhz.Value));
        if (cpu.Socket is not null)
            summaryParts.Add(cpu.Socket);

        var rows = new List<(string Label, string Value)>
        {
            (Loc.Get("SystemInfoModel"), cpu.Name),
            (Loc.Get("SystemInfoArchitecture"), cpu.Architecture),
            (Loc.Get("SystemInfoCores"), cpu.PhysicalCores.ToString(CultureInfo.InvariantCulture)),
            (Loc.Get("SystemInfoThreads"), cpu.LogicalProcessors.ToString(CultureInfo.InvariantCulture)),
        };
        if (cpu.Manufacturer is not null) rows.Add((Loc.Get("SystemInfoManufacturer"), cpu.Manufacturer));
        if (cpu.BaseClockMhz is > 0) rows.Add((Loc.Get("SystemInfoBaseClock"), SystemInfoFormatter.FormatCpuClock(cpu.BaseClockMhz.Value)));
        if (cpu.MaxClockMhz is > 0) rows.Add((Loc.Get("SystemInfoMaxClock"), SystemInfoFormatter.FormatCpuClock(cpu.MaxClockMhz.Value)));
        if (cpu.Socket is not null) rows.Add((Loc.Get("SystemInfoSocket"), cpu.Socket));
        if (cpu.L2CacheBytes is > 0) rows.Add((Loc.Get("SystemInfoL2Cache"), SystemInfoFormatter.FormatBinarySize(cpu.L2CacheBytes.Value)));
        if (cpu.L3CacheBytes is > 0) rows.Add((Loc.Get("SystemInfoL3Cache"), SystemInfoFormatter.FormatBinarySize(cpu.L3CacheBytes.Value)));

        return BuildCard(
            id: "cpu",
            iconKey: "IconCpu",
            accentHex: "#89b4fa",
            category: Loc.Get("SystemInfoCpu"),
            headline: cpu.Name,
            summary: string.Join(" · ", summaryParts),
            badge: cpu.Architecture,
            buildDetails: () => BuildKeyValueGrid(rows));
    }

    private Border BuildGpuCard(IReadOnlyList<GpuInfo> gpus)
    {
        var primary = gpus.FirstOrDefault();
        var headline = primary?.Name ?? Loc.Get("SystemInfoUnknown");

        var summaryParts = new List<string>();
        if (primary?.DedicatedVramBytes is > 0)
            summaryParts.Add(Loc.Get("SystemInfoVramShort", SystemInfoFormatter.FormatBinarySize(primary.DedicatedVramBytes.Value)));
        if (primary?.DriverVersion is not null)
            summaryParts.Add(primary.DriverVersion);
        if (gpus.Count > 1)
            summaryParts.Add(string.Join(", ", gpus.Skip(1).Select(g => g.Name)));

        var badge = primary?.DedicatedVramBytes is > 0
            ? SystemInfoFormatter.FormatBinarySize(primary.DedicatedVramBytes.Value)
            : null;

        return BuildCard(
            id: "gpu",
            iconKey: "IconGpu",
            accentHex: "#cba6f7",
            category: Loc.Get("SystemInfoGpu"),
            headline: headline,
            summary: string.Join(" · ", summaryParts),
            badge: badge,
            buildDetails: () =>
            {
                var panel = new StackPanel { Spacing = 10 };
                if (gpus.Count == 0)
                {
                    panel.Children.Add(MutedNote(Loc.Get("SystemInfoUnknown")));
                    return panel;
                }

                for (var i = 0; i < gpus.Count; i++)
                {
                    var g = gpus[i];
                    var rows = new List<(string Label, string Value)>
                    {
                        (Loc.Get("SystemInfoModel"), g.Name)
                    };
                    if (g.Vendor is not null) rows.Add((Loc.Get("SystemInfoVendor"), g.Vendor));
                    if (g.DedicatedVramBytes is > 0) rows.Add((Loc.Get("SystemInfoDedicatedVram"), SystemInfoFormatter.FormatBinarySize(g.DedicatedVramBytes.Value)));
                    if (g.SharedMemoryBytes is > 0) rows.Add((Loc.Get("SystemInfoSharedMemory"), SystemInfoFormatter.FormatBinarySize(g.SharedMemoryBytes.Value)));
                    if (g.DriverVersion is not null) rows.Add((Loc.Get("SystemInfoDriverVersion"), g.DriverVersion));
                    if (g.DriverDate is not null) rows.Add((Loc.Get("SystemInfoDriverDate"), g.DriverDate));

                    if (gpus.Count > 1)
                        panel.Children.Add(SubSectionTitle($"GPU {i + 1}"));
                    panel.Children.Add(BuildKeyValueGrid(rows));
                }
                return panel;
            });
    }

    private Border BuildRamCard(MemoryInfo mem)
    {
        var totalStr = SystemInfoFormatter.FormatBinarySize(mem.InstalledBytes ?? mem.TotalUsableBytes);
        var headline = mem.MemoryType is not null ? $"{totalStr} {mem.MemoryType}" : totalStr;

        var summaryParts = new List<string>();
        if (mem.SpeedMhz is > 0)
            summaryParts.Add(SystemInfoFormatter.FormatMemorySpeed(mem.SpeedMhz.Value, mem.MemoryType));
        if (mem.TotalSlots is > 0 && mem.UsedSlots is not null)
            summaryParts.Add(Loc.Get("SystemInfoSlotsUsed", mem.UsedSlots.Value, mem.TotalSlots.Value));
        if (mem.InstalledBytes is > 0 && mem.TotalUsableBytes > 0 && mem.InstalledBytes != mem.TotalUsableBytes)
            summaryParts.Add($"{Loc.Get("SystemInfoUsableMemory")}: {SystemInfoFormatter.FormatBinarySize(mem.TotalUsableBytes)}");

        return BuildCard(
            id: "ram",
            iconKey: "IconRam",
            accentHex: "#a6e3a1",
            category: Loc.Get("SystemInfoRam"),
            headline: headline,
            summary: string.Join(" · ", summaryParts),
            badge: mem.MemoryType,
            buildDetails: () =>
            {
                var panel = new StackPanel { Spacing = 10 };
                var rows = new List<(string Label, string Value)>();
                if (mem.InstalledBytes is > 0)
                    rows.Add((Loc.Get("SystemInfoInstalledMemory"), SystemInfoFormatter.FormatBinarySize(mem.InstalledBytes.Value)));
                rows.Add((Loc.Get("SystemInfoUsableMemory"), SystemInfoFormatter.FormatBinarySize(mem.TotalUsableBytes)));
                if (mem.MemoryType is not null) rows.Add((Loc.Get("SystemInfoMemoryType"), mem.MemoryType));
                if (mem.SpeedMhz is > 0) rows.Add((Loc.Get("SystemInfoMemorySpeed"), SystemInfoFormatter.FormatMemorySpeed(mem.SpeedMhz.Value, mem.MemoryType)));
                if (mem.TotalSlots is > 0 && mem.UsedSlots is not null)
                    rows.Add((Loc.Get("SystemInfoMemorySlots"), Loc.Get("SystemInfoSlotsUsed", mem.UsedSlots.Value, mem.TotalSlots.Value)));

                panel.Children.Add(BuildKeyValueGrid(rows));

                if (mem.Modules.Count > 0)
                {
                    panel.Children.Add(SubSectionTitle(Loc.Get("SystemInfoMemoryModules")));
                    foreach (var mod in mem.Modules)
                    {
                        var modParts = new List<string> { SystemInfoFormatter.FormatBinarySize(mod.CapacityBytes) };
                        if (mod.Manufacturer is not null) modParts.Add(mod.Manufacturer);
                        if (mod.PartNumber is not null) modParts.Add(mod.PartNumber);
                        if (mod.SpeedMhz is > 0) modParts.Add(SystemInfoFormatter.FormatMemorySpeed(mod.SpeedMhz.Value, mod.MemoryType));
                        if (mod.FormFactor is not null) modParts.Add(mod.FormFactor);

                        panel.Children.Add(BuildKeyValueGrid([(mod.Slot ?? Loc.Get("SystemInfoSlot"), string.Join(" · ", modParts))]));
                    }
                }
                return panel;
            });
    }

    private Border BuildStorageCard(IReadOnlyList<StorageDriveInfo> drives)
    {
        var primary = drives.FirstOrDefault();
        var headline = primary?.Model ?? Loc.Get("SystemInfoUnknown");

        var summaryParts = new List<string>();
        foreach (var d in drives)
        {
            var cap = d.CapacityBytes > 0 ? SystemInfoFormatter.FormatStorageCapacity(d.CapacityBytes) : "";
            var tag = drives.Count == 1
                ? string.Join(" · ", new[] { cap, d.MediaType }.Where(x => !string.IsNullOrEmpty(x)))
                : $"{d.Model} ({cap} {d.MediaType})".Trim();
            if (!string.IsNullOrEmpty(tag))
                summaryParts.Add(tag);
        }

        var badge = drives.Count == 1
            ? primary?.MediaType
            : Loc.Get("SystemInfoDrivesCount", drives.Count);

        return BuildCard(
            id: "storage",
            iconKey: "IconDrive",
            accentHex: "#f9e2af",
            category: Loc.Get("SystemInfoStorage"),
            headline: headline,
            summary: string.Join("  |  ", summaryParts),
            badge: badge,
            buildDetails: () =>
            {
                var panel = new StackPanel { Spacing = 10 };
                if (drives.Count == 0)
                {
                    panel.Children.Add(MutedNote(Loc.Get("SystemInfoUnknown")));
                    return panel;
                }

                for (var i = 0; i < drives.Count; i++)
                {
                    var d = drives[i];
                    var rows = new List<(string Label, string Value)>
                    {
                        (Loc.Get("SystemInfoModel"), d.Model),
                        (Loc.Get("SystemInfoMediaType"), d.MediaType),
                    };
                    if (d.CapacityBytes > 0) rows.Add((Loc.Get("SystemInfoCapacity"), SystemInfoFormatter.FormatStorageCapacity(d.CapacityBytes)));
                    if (d.Interface is not null) rows.Add((Loc.Get("SystemInfoInterface"), d.Interface));
                    if (d.LogicalDrives.Count > 0) rows.Add((Loc.Get("SystemInfoLogicalDrives"), string.Join(", ", d.LogicalDrives)));
                    if (d.FirmwareVersion is not null) rows.Add((Loc.Get("SystemInfoFirmware"), d.FirmwareVersion));

                    if (drives.Count > 1)
                        panel.Children.Add(SubSectionTitle($"{Loc.Get("SystemInfoStorage")} {i + 1}"));
                    panel.Children.Add(BuildKeyValueGrid(rows));
                }
                return panel;
            });
    }

    private Border BuildMotherboardCard(MotherboardInfo mb)
    {
        var headline = mb.Model ?? mb.SystemModel ?? Loc.Get("SystemInfoUnknown");
        var summaryParts = new List<string>();
        if (mb.Manufacturer is not null) summaryParts.Add(mb.Manufacturer);
        if (mb.BiosVersion is not null) summaryParts.Add($"BIOS {mb.BiosVersion}");

        var rows = new List<(string Label, string Value)>();
        if (mb.Manufacturer is not null) rows.Add((Loc.Get("SystemInfoManufacturer"), mb.Manufacturer));
        if (mb.Model is not null) rows.Add((Loc.Get("SystemInfoModel"), mb.Model));
        if (mb.Version is not null) rows.Add((Loc.Get("SystemInfoBoardVersion"), mb.Version));
        if (mb.SystemModel is not null && mb.SystemModel != mb.Model)
            rows.Add((Loc.Get("SystemInfoSystemModel"), $"{mb.SystemManufacturer} {mb.SystemModel}".Trim()));
        if (mb.BiosVendor is not null) rows.Add((Loc.Get("SystemInfoBiosVendor"), mb.BiosVendor));
        if (mb.BiosVersion is not null) rows.Add((Loc.Get("SystemInfoBiosVersion"), mb.BiosVersion));
        if (mb.BiosReleaseDate is not null) rows.Add((Loc.Get("SystemInfoBiosDate"), mb.BiosReleaseDate));

        return BuildCard(
            id: "motherboard",
            iconKey: "IconMotherboard",
            accentHex: "#94e2d5",
            category: Loc.Get("SystemInfoMotherboard"),
            headline: headline,
            summary: string.Join(" · ", summaryParts),
            badge: null,
            buildDetails: () => rows.Count > 0 ? BuildKeyValueGrid(rows) : MutedNote(Loc.Get("SystemInfoUnknown")));
    }

    private Border BuildDisplaysCard(IReadOnlyList<DisplayInfo> displays)
    {
        var primary = displays.FirstOrDefault();
        var headline = primary?.Name ?? Loc.Get("SystemInfoUnknown");

        var summaryParts = new List<string>();
        if (primary is not null)
        {
            var hz = primary.RefreshRateHz is > 0
                ? string.Create(CultureInfo.InvariantCulture, $" @ {primary.RefreshRateHz.Value:0.##} Hz")
                : "";
            summaryParts.Add($"{primary.Width} × {primary.Height}{hz}");
        }
        if (displays.Count > 1)
            summaryParts.Add(Loc.Get("SystemInfoMoreCount", displays.Count - 1));

        var badge = displays.Count > 1
            ? Loc.Get("SystemInfoDisplaysCount", displays.Count)
            : primary?.ConnectionType;

        return BuildCard(
            id: "displays",
            iconKey: "IconDesktop",
            accentHex: "#74c7ec",
            category: Loc.Get("SystemInfoDisplays"),
            headline: headline,
            summary: string.Join(" · ", summaryParts),
            badge: badge,
            buildDetails: () =>
            {
                var panel = new StackPanel { Spacing = 10 };
                if (displays.Count == 0)
                {
                    panel.Children.Add(MutedNote(Loc.Get("SystemInfoUnknown")));
                    return panel;
                }

                for (var i = 0; i < displays.Count; i++)
                {
                    var d = displays[i];
                    var rows = new List<(string Label, string Value)>
                    {
                        (Loc.Get("SystemInfoModel"), d.Name),
                        (Loc.Get("SystemInfoResolution"), $"{d.Width} × {d.Height}"),
                    };
                    if (d.RefreshRateHz is > 0)
                        rows.Add((Loc.Get("SystemInfoRefreshRate"), string.Create(CultureInfo.InvariantCulture, $"{d.RefreshRateHz.Value:0.##} Hz")));
                    if (d.ConnectionType is not null)
                        rows.Add((Loc.Get("SystemInfoConnection"), d.ConnectionType));
                    rows.Add((Loc.Get("SystemInfoPrimary"), Loc.Get(d.IsPrimary ? "SystemInfoYes" : "SystemInfoNo")));

                    if (displays.Count > 1)
                        panel.Children.Add(SubSectionTitle($"{Loc.Get("SystemInfoDisplays")} {i + 1}"));
                    panel.Children.Add(BuildKeyValueGrid(rows));
                }
                return panel;
            });
    }

    private Border BuildOsCard(OsSystemInfo os)
    {
        var summaryParts = new List<string>();
        if (os.OsVersion is not null) summaryParts.Add(os.OsVersion);
        summaryParts.Add(os.MachineName);
        summaryParts.Add(Loc.Get("SystemInfoUptimeShort", SystemInfoFormatter.FormatUptime(os.Uptime)));

        var rows = new List<(string Label, string Value)>
        {
            (Loc.Get("SystemInfoOsName"), os.OsName),
        };
        if (os.OsVersion is not null) rows.Add((Loc.Get("SystemInfoOsVersion"), os.OsVersion));
        rows.Add((Loc.Get("SystemInfoArchitecture"), os.Architecture));
        rows.Add((Loc.Get("SystemInfoMachineName"), os.MachineName));
        rows.Add((Loc.Get("SystemInfoUptime"), SystemInfoFormatter.FormatUptime(os.Uptime)));

        return BuildCard(
            id: "os",
            iconKey: "IconSettings",
            accentHex: "#f5c2e7",
            category: Loc.Get("SystemInfoOs"),
            headline: os.OsName,
            summary: string.Join(" · ", summaryParts),
            badge: os.Architecture,
            buildDetails: () => BuildKeyValueGrid(rows));
    }

    // =========================================================================
    // Card Primitives
    // =========================================================================

    private Border BuildCard(
        string id,
        string iconKey,
        string accentHex,
        string category,
        string headline,
        string summary,
        string? badge,
        Func<Control> buildDetails)
    {
        var expanded = _expandedIds.Contains(id);
        var accent = Brush.Parse(accentHex);

        var card = new Border
        {
            Background = Brush.Parse("#181825"),
            BorderBrush = Brush.Parse(expanded ? "#45475a" : "#313244"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12)
        };

        var root = new StackPanel { Spacing = 0 };

        // Header (clickable to expand/collapse)
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var iconBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = Brush.Parse("#232334"),
            BorderBrush = Brush.Parse("#313244"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new PathIcon
            {
                Data = TryFindIcon(iconKey),
                Width = 18,
                Height = 18,
                Foreground = accent
            }
        };
        Grid.SetColumn(iconBorder, 0);
        headerGrid.Children.Add(iconBorder);

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = category,
            Foreground = Brush.Parse("#7f849c"),
            FontSize = 11,
            FontWeight = FontWeight.Medium
        });
        textStack.Children.Add(new TextBlock
        {
            Text = headline,
            Foreground = Brush.Parse("#cdd6f4"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(summary))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = summary,
                Foreground = Brush.Parse("#a6adc8"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(textStack, 1);
        headerGrid.Children.Add(textStack);

        if (!string.IsNullOrWhiteSpace(badge))
        {
            var badgeBorder = new Border
            {
                Background = Brush.Parse("#252538"),
                BorderBrush = Brush.Parse("#45475a"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3),
                Margin = new Thickness(12, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badge,
                    Foreground = accent,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold
                }
            };
            Grid.SetColumn(badgeBorder, 2);
            headerGrid.Children.Add(badgeBorder);
        }

        var chevron = new PathIcon
        {
            Data = TryFindIcon(expanded ? "IconChevronDown" : "IconChevronRight"),
            Width = 16,
            Height = 16,
            Foreground = Brush.Parse("#7f849c"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 2, 0)
        };
        Grid.SetColumn(chevron, 3);
        headerGrid.Children.Add(chevron);

        root.Children.Add(headerGrid);

        // Details section
        var detailsBorder = new Border
        {
            IsVisible = expanded,
            BorderBrush = Brush.Parse("#313244"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 11, 0, 0),
            Padding = new Thickness(48, 10, 0, 2),
            Child = expanded ? buildDetails() : null
        };
        root.Children.Add(detailsBorder);

        headerGrid.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(headerGrid).Properties.IsLeftButtonPressed) return;
            if (_expandedIds.Contains(id))
                _expandedIds.Remove(id);
            else
                _expandedIds.Add(id);

            var isOpen = _expandedIds.Contains(id);
            detailsBorder.IsVisible = isOpen;
            if (isOpen && detailsBorder.Child is null)
                detailsBorder.Child = buildDetails();

            chevron.Data = TryFindIcon(isOpen ? "IconChevronDown" : "IconChevronRight");
            card.BorderBrush = Brush.Parse(isOpen ? "#45475a" : "#313244");
            UpdateExpandAllButtonText();
        };

        card.Child = root;
        return card;
    }

    private static Grid BuildKeyValueGrid(IReadOnlyList<(string Label, string Value)> rows)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("175,*")
        };

        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = rows[i].Label,
                Foreground = Brush.Parse("#7f849c"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 12, 2),
                VerticalAlignment = VerticalAlignment.Top
            };
            var value = new SelectableTextBlock
            {
                Text = rows[i].Value,
                Foreground = Brush.Parse("#cdd6f4"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };

            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        return grid;
    }

    private static TextBlock SubSectionTitle(string title) => new()
    {
        Text = title,
        Foreground = Brush.Parse("#89b4fa"),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static TextBlock MutedNote(string text) => new()
    {
        Text = text,
        Foreground = Brush.Parse("#6c7086"),
        FontSize = 12
    };

    private static Geometry? TryFindIcon(string key) =>
        Application.Current?.TryFindResource(key, out var res) == true && res is Geometry g ? g : null;
}
