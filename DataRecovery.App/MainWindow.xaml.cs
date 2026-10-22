using System.Windows;

namespace DataRecovery.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (DataContext is not MainViewModel vm || !vm.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        vm.StatusText = "Stopping and saving checkpoint before closing...";
        await vm.StopAsync();
        _closeConfirmed = true;
        Close();
    }
}
