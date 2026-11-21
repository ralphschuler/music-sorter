using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSorter.Desktop;

internal static class Studio
{
    // Keep the Fluent control templates, including keyboard focus and interaction states.
    internal static readonly IBrush Ink = Brush.Parse("#171923");
    internal static readonly IBrush Muted = Brush.Parse("#5A6272");
    internal static readonly IBrush Canvas = Brush.Parse("#F5F6F8");
    internal static readonly IBrush Paper = Brushes.White;
    internal static readonly IBrush Line = Brush.Parse("#DADEE5");
    internal static readonly FontFamily Mono = new("Menlo, Consolas, monospace");

    internal static Border Band(Control child, bool bottom) => new()
    {
        Background = Paper, BorderBrush = Line,
        BorderThickness = bottom ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0),
        Child = child
    };

    internal static Border Surface(Control child) => new()
    {
        Background = Paper, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = child
    };
}
