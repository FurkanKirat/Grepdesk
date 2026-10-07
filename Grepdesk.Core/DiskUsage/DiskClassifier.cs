using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grepdesk.Core.DiskUsage;

/// <summary>
/// What a file is "for", from the user's point of view. Eight kinds plus Other:
/// the chart gives each its own color, and more than eight colors can't be
/// told apart reliably, so anything rarer folds into Other.
/// The order here is the color order and must not change with the data.
/// </summary>
public enum DiskCategory { Videos, Games, Images, Documents, Apps, Developer, System, Audio, Other }

/// <param name="GroupLength">
/// Length of the path prefix naming the thing the file belongs to (a game, an
/// app, a node_modules folder...), or 0 when it was classified by extension
/// and belongs to nothing larger. <c>path[..GroupLength]</c> is that folder.
/// </param>
public readonly record struct Classification(DiskCategory Category, int GroupLength);

/// <summary>
/// Puts a file into a <see cref="DiskCategory"/>. Where it lives wins over
/// what it is: a video inside a game's folder is part of the game, an image
/// inside node_modules is developer clutter. Only files outside every known
/// location are judged by extension.
/// </summary>
public static class DiskClassifier
{
    // Checked in order; markers are path fragments with '\' (normalized per OS below).
    // A trailing '*' means "each subfolder is its own item" (every game under
    // steamapps\common, every app under Program Files); without it the marked
    // folder itself is the item (each node_modules, each .git, the whole .nuget).
    private static readonly (DiskCategory Category, string[] Markers)[] LocationRules =
    [
        (DiskCategory.Games, [
            @"\steamapps\common\*", @"\steamapps\*", @"\Epic Games\*", @"\XboxGames\*", @"\Riot Games\*",
            @"\GOG Games\*", @"\GOG Galaxy\Games\*", @"\Ubisoft Game Launcher\games\*", @"\EA Games\*",
            @"\Origin Games\*", @"\Rockstar Games\*", @"\Battle.net\", @"\Blizzard Entertainment\*",
            @"\Program Files\EA\*", @"\.steam\steam\steamapps\common\*", @"\.steam\*", @"\Lutris\*", @"\Heroic\*"]),
        (DiskCategory.Developer, [
            // Local AI models are usually the single biggest developer item.
            @"\.ollama\", @"\.lmstudio\", @"\LM Studio\", @"\huggingface\", @"\.git\",
            @"\node_modules\", @"\.nuget\", @"\.gradle\", @"\.m2\", @"\.cargo\", @"\.rustup\",
            @"\Android\Sdk\", @"\.android\", @"\AppData\Local\Docker\", @"\.docker\", @"\.vscode\",
            @"\JetBrains\*", @"\go\pkg\", @"\AppData\Local\pip\", @"\.cache\pip\", @"\.conda\",
            @"\anaconda3\", @"\miniconda3\", @"\Microsoft Visual Studio\*", @"\Windows Kits\",
            @"\AppData\Local\NuGet\", @"\.npm\", @"\AppData\Local\npm-cache\", @"\AppData\Local\Yarn\"]),
        (DiskCategory.System, [
            @"\Windows\*", @"\$Recycle.Bin\", @"\System Volume Information\", @"\Recovery\",
            @"\$WinREAgent\", @"\AppData\Local\Temp\", @"\ProgramData\Microsoft\Windows\*",
            @"\AppData\Local\CrashDumps\", @"\.Trash\", @"\.local\share\Trash\", @"\var\cache\", @"\tmp\",
            // Caches: GPU shader caches and browser/Electron caches (mostly extension-less files).
            @"\NVIDIA\DXCache\", @"\NVIDIA\GLCache\", @"\NVIDIA Corporation\NV_Cache\", @"\D3DSCache\",
            @"\AMD\DxCache\", @"\AMD\DxcCache\", @"\Cache\Cache_Data\", @"\Code Cache\", @"\GPUCache\",
            @"\Service Worker\CacheStorage\", @"\INetCache\", @"\.cache\*"]),
        (DiskCategory.Apps, [
            @"\Program Files\*", @"\Program Files (x86)\*", @"\AppData\Local\Programs\*", @"\WindowsApps\*",
            @"\ProgramData\*", @"\Applications\*", @"\usr\*", @"\opt\*", @"\snap\*", @"\flatpak\*"]),
    ];

    private readonly record struct Marker(string Text, bool ByChild);

    private static readonly (DiskCategory Category, Marker[] Markers)[] Rules = LocationRules
        .Select(r => (r.Category, r.Markers.Select(m =>
        {
            var text = Path.DirectorySeparatorChar == '\\' ? m : m.Replace('\\', '/');
            return text.EndsWith('*') ? new Marker(text[..^1], true) : new Marker(text, false);
        }).ToArray()))
        .ToArray();

    // Large system files that sit at the drive root, outside any folder.
    private static readonly HashSet<string> SystemFileNames =
        new(["pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SystemFileNameLookup =
        SystemFileNames.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly Dictionary<string, DiskCategory> ByExtension = BuildExtensionMap();
    private static readonly Dictionary<string, DiskCategory>.AlternateLookup<ReadOnlySpan<char>> ByExtensionLookup =
        ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    private static Dictionary<string, DiskCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, DiskCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(DiskCategory c, params string[] exts) { foreach (var e in exts) map[e] = c; }

        Add(DiskCategory.Videos, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp");
        Add(DiskCategory.Images, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff",
            ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".psd", ".svg", ".ico");
        Add(DiskCategory.Audio, ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".aiff");
        Add(DiskCategory.Documents, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods",
            ".odp", ".rtf", ".txt", ".md", ".csv", ".epub", ".pages", ".numbers", ".key");
        Add(DiskCategory.Apps, ".exe", ".msi", ".dll", ".appx", ".msix", ".dmg", ".pkg", ".deb", ".rpm", ".AppImage");
        Add(DiskCategory.Developer, ".vhdx", ".vmdk", ".vdi", ".qcow2", ".pdb", ".nupkg", ".jar", ".class", ".o", ".obj", ".lib", ".a",
            ".gguf", ".safetensors", ".ckpt", ".pt", ".pth", ".onnx");
        Add(DiskCategory.System, ".nvph", ".tmp", ".dmp");
        return map;
    }

    public static DiskCategory Classify(string path) => ClassifyDetailed(path).Category;

    public static Classification ClassifyDetailed(string path)
    {
        foreach (var (category, markers) in Rules)
        {
            foreach (var marker in markers)
            {
                var index = path.IndexOf(marker.Text, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                var afterMarker = index + marker.Text.Length;
                if (!marker.ByChild)
                    return new Classification(category, afterMarker - 1); // the marked folder, without its trailing separator

                // The first folder (or file) below the marker.
                var next = path.IndexOf(Path.DirectorySeparatorChar, afterMarker);
                return new Classification(category, next < 0 ? path.Length : next);
            }
        }

        var name = Path.GetFileName(path.AsSpan());
        if (SystemFileNameLookup.Contains(name))
            return new Classification(DiskCategory.System, path.Length); // pagefile.sys etc. are items on their own

        return new Classification(
            ByExtensionLookup.TryGetValue(Path.GetExtension(path.AsSpan()), out var byExtension) ? byExtension : DiskCategory.Other,
            0);
    }
}
