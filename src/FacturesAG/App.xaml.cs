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

        MainWindow main;
        try { main = new MainWindow(); }
        catch (Exception ex)
        {
            // Sans fenêtre principale, l'application ne doit pas rester ouverte en arrière-plan.
            MessageBox.Show($"Factures AG ne peut pas démarrer.\n\n{ex.Message}", "Factures AG", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
