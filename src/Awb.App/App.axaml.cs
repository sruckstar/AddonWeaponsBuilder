using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Awb.App.Services;
using Awb.App.ViewModels;
using Awb.App.Views;

namespace Awb.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(Settings.Load());
            ApplyTheme(vm.IsDark);
            vm.ThemeChanged += ApplyTheme;
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(bool dark) =>
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
}
