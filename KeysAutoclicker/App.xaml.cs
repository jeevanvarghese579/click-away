using System.Windows;

namespace KeysAutoclicker;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        System.Windows.MessageBox.Show(
            "Click Away v1.7.1\n\nDeveloped By Jeevan Varghese, St. Gemma's GHSS Malappuram.\n\n" +
            "Visit itsjeevanvarghese.web.app for more softwares.",
            "About Click Away",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
