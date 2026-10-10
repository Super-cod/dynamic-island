using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace WindowsDynamicBar.Core.Search;

public enum SearchCategory
{
    Calculator,
    Apps,
    Commands,
    Windows,
    Files,
    Web
}

public sealed class SearchResult
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required SearchCategory Category { get; init; }
    public ImageSource? Icon { get; set; }
    public string? FallbackLetter { get; set; }
    public Color? FallbackColor { get; set; }
    public string? KeyboardHint { get; set; }
    public bool IsRisky { get; set; }
    public Action? Action { get; set; }
    public int Score { get; set; }
    public List<int>? MatchedIndices { get; set; }
    public string? AppId { get; set; }
    public string? ExePath { get; set; }
    /// <summary>Window switcher row: the window to bring to the front.</summary>
    public IntPtr WindowHandle { get; set; }
}

public sealed class AppInfo
{
    public required string Name { get; init; }
    public required string AppId { get; init; }
    public required string ExePath { get; init; }
    public ImageSource? Icon { get; set; }
    public byte[]? IconBytes { get; set; }
    public int LaunchCount { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    /// <summary>Resolved shortcut target (the .exe), used to hide duplicate shortcuts and helper/doc links.</summary>
    public string? TargetPath { get; init; }
    public List<string> Aliases { get; init; } = new();
}
