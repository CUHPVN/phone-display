using System.Windows;

namespace PhoneDisplay.Host;

public partial class App : System.Windows.Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Windows.MessageBox.Show(
                e.ExceptionObject?.ToString() ?? "Unknown fatal error",
                "PhoneDisplay Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            System.Windows.MessageBox.Show(
                e.Exception.ToString(),
                "PhoneDisplay UI Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        app.InitializeComponent();
        var window = new UI.MainWindow();
        app.Run(window);
    }
}
