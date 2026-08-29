namespace LogAggregator.Common;

/// <summary>Shared preset color swatches, used by the LogType editor's color step and the
/// Source card's own color popup (both need the same palette so cards and LogTypes visually
/// line up), plus a small rotation helper so successive new items default to different colors
/// instead of always starting on the same one.</summary>
public static class ColorPresets
{
    public static readonly string[] Colors =
    {
        "#3DDC97", "#4C9BFF", "#E0A63D", "#E05C5C", "#B57DE0",
        "#3DC8E0", "#E0733D", "#7DE07E", "#E03D9C", "#9BA8E0"
    };

    private static int _counter = -1;

    public static string NextColor() => Colors[System.Threading.Interlocked.Increment(ref _counter) % Colors.Length];
}
