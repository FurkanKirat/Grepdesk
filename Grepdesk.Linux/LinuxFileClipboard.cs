using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Grepdesk.Core.Transfer;

namespace Grepdesk.Linux;

/// <summary>
/// Reads files copied or cut in a Linux file manager. There is no single
/// format:
/// <list type="bullet">
/// <item>GNOME-family managers (Nautilus, Nemo, Caja) use
///   <c>x-special/gnome-copied-files</c>: a "copy" or "cut" line, then file URIs.</item>
/// <item>KDE Dolphin uses <c>text/uri-list</c>, and marks a cut with
///   <c>application/x-kde-cutselection</c> = "1".</item>
/// </list>
/// The clipboard is read with <c>wl-paste</c> (wl-clipboard) on Wayland or
/// <c>xclip</c> on X11, whichever is installed.
/// </summary>
public sealed class LinuxFileClipboard : IFileClipboard
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);

    public ClipboardFiles? GetFiles()
    {
        if (Read("x-special/gnome-copied-files") is { } gnome && ParseGnomeCopiedFiles(gnome) is { } files)
            return files;

        if (Read("text/uri-list") is { } uris)
        {
            var cut = Read("application/x-kde-cutselection")?.Trim() == "1";
            return ParseUriList(uris, cut);
        }
        return null;
    }

    public void Clear()
    {
        if (UseWayland)
            Run("wl-copy", ["--clear"], out _);
        else
            Run("xclip", ["-selection", "clipboard", "-i", "/dev/null"], out _);
    }

    /// <summary>True when the tool this session needs (wl-paste or xclip) is installed.</summary>
    public static bool IsToolAvailable => FindOnPath(UseWayland ? "wl-paste" : "xclip") is not null;

    public static ClipboardFiles? ParseGnomeCopiedFiles(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || lines[0] is not ("copy" or "cut"))
            return null;
        var paths = lines.Skip(1).Select(ToLocalPath).OfType<string>().ToList();
        return paths.Count == 0 ? null : new ClipboardFiles(paths, lines[0] == "cut");
    }

    public static ClipboardFiles? ParseUriList(string text, bool isCut)
    {
        var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(ToLocalPath)
            .OfType<string>()
            .ToList();
        return paths.Count == 0 ? null : new ClipboardFiles(paths, isCut);
    }

    // file:///home/me/My%20File.txt → /home/me/My File.txt; other schemes (sftp://, …) aren't local files.
    private static string? ToLocalPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    private static bool UseWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string? Read(string mimeType)
    {
        var ok = UseWayland
            ? Run("wl-paste", ["--no-newline", "--type", mimeType], out var output)
            : Run("xclip", ["-selection", "clipboard", "-o", "-t", mimeType], out output);
        return ok && output.Length > 0 ? output : null;
    }

    private static bool Run(string tool, string[] arguments, out string output)
    {
        output = "";
        if (FindOnPath(tool) is not { } exe)
            return false;

        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill();
                return false;
            }
            output = read.Result;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}
