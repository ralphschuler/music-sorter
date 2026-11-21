using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
