using System.Windows;
using System.Windows.Threading;

namespace USBGuardianControl
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"Unhandled error:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "USB Guardian — Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true; // don't close the app
            };
        }
    }
}
