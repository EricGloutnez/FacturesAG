using System.Windows;
using System.Windows.Threading;

namespace FacturesAG;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Toute erreur imprévue est affichée plutôt que de fermer l'application sans prévenir.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Factures AG", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
