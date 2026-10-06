using System.Windows;
using StoryForge.App.Interop;
using StoryForge.App.ViewModels;

namespace StoryForge.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    public MainViewModel ViewModel { get; }
}
