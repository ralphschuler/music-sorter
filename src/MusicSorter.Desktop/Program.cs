using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MusicSorter.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<MusicSorterApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class MusicSorterApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        var theme = new FluentTheme();
        theme.Palettes.Add(ThemeVariant.Light, new ColorPaletteResources { Accent = Color.Parse("#2549D8") });
        Styles.Add(theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
