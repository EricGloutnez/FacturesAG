using System.Windows;

namespace FacturesAG;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Toute erreur imprévue est affichée plutôt que de fermer l'application sans prévenir.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Factures AG", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);

        // Mise à jour obligatoire avant d'ouvrir l'application.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        UpdateFlow.CleanupPreviousVersion();
        if (await UpdateFlow.CheckQuietlyAsync(TimeSpan.FromSeconds(8)) is { } update)
        {
            UpdateFlow.InstallAndRestart(update, null);
            Shutdown(); // redémarrage lancé, ou « Quitter » après un échec
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
