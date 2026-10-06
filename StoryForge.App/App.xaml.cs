using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StoryForge.App.ViewModels;
using StoryForge.Engine;

namespace StoryForge.App;

/// <summary>
/// The composition root: the one place in the app that knows the engine. Everything else talks to
/// it through IStoryForgeClient (pinned by ArchitectureTests).
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Services.AddStoryForgeEngine(options => options.DataDirectory = DataDirectory());
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();

            // Starting the host creates or migrates the database before the window appears.
            await _host.StartAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            await window.ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"StoryForge X could not start.\n\n{ex.Message}", "StoryForge X",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private static string DataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForgeX");
}
