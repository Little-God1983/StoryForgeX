using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    /// <summary>How long Settings waits after the last keystroke before saving.</summary>
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>How often the provider statuses are checked while the app is open.</summary>
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);

    private IHost? _host;
    private DispatcherTimer? _statusTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // Content root is the exe's folder, not the working directory: a shortcut or file
            // association can start the app anywhere, and the host watches its content root.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Services.AddStoryForgeEngine(options =>
            {
                options.DataDirectory = DataDirectory();
                options.DefaultProjectsFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StoryForge X");
            });
            builder.Services.AddSingleton<ProviderStatusBoard>();
            builder.Services.AddSingleton(services => new MainViewModel(
                services.GetRequiredService<Client.IStoryForgeClient>(),
                services.GetRequiredService<ProviderStatusBoard>(),
                SettingsSaveDelay));
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();

            // Starting the host creates or migrates the database before the window appears.
            await _host.StartAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            await window.ViewModel.LoadAsync();
            StartStatusRefresh(_host.Services.GetRequiredService<ProviderStatusBoard>());
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
        _statusTimer?.Stop();
        _host?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Keeps the pills and cards live: a provider started or stopped shows up within the interval.</summary>
    private void StartStatusRefresh(ProviderStatusBoard board)
    {
        _statusTimer = new DispatcherTimer { Interval = StatusRefreshInterval };
        _statusTimer.Tick += async (_, _) => await board.RefreshAsync();
        _statusTimer.Start();
    }

    private static string DataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForgeX");
}
