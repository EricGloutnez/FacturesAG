using System.Windows;
using System.IO;
using FacturesAG.Core;

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
            if (MainWindow is null) Shutdown(1); // sans fenêtre, ne pas rester ouvert en arrière-plan
        };
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            // Lancée depuis certaines applications (« empaquetées »), Factures AG verrait une copie séparée, vide,
            // de ses données. On vérifie où se trouve réellement le dossier des données avant d'y toucher.
            if (DataDirIsRedirected())
            {
                MessageBox.Show("Factures AG a été lancé depuis une autre application, qui l'empêcherait de voir vos vraies données.\n\n" +
                                "Lancez-le plutôt depuis son raccourci « Factures AG » (Bureau ou menu Démarrer).",
                    "Factures AG", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }

            // Une seule copie à la fois : sinon, on ramène celle qui est déjà ouverte.
            var restarting = e.Args.Contains(Installer.InstalledArgument) || e.Args.Contains(UpdateFlow.RestartArgument);
            if (!AppRuntime.AcquireSingleInstance(restarting ? TimeSpan.FromSeconds(30) : TimeSpan.Zero))
            {
                AppRuntime.ActivateExisting();
                Shutdown();
                return;
            }

            // Premier lancement du fichier téléchargé : installation, raccourcis, puis la copie installée prend le relais.
            if (UpdateFlow.InstallIfDownloaded(e.Args))
            {
                Shutdown();
                return;
            }

            // Mise à jour obligatoire avant d'ouvrir l'application.
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
            UpdateFlow.WelcomeAfterInstall(main, e.Args);
        }
        catch (Exception ex)
        {
            // Sans fenêtre principale, l'application ne doit pas rester ouverte en arrière-plan.
            MessageBox.Show($"Factures AG ne peut pas démarrer.\n\n{ex.Message}", "Factures AG", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Vrai si le dossier des données (ou, s'il n'existe pas encore, un dossier créé pour vérifier) se retrouve en réalité
    /// dans la copie privée d'une autre application. Le dossier créé pour la vérification est alors retiré.
    /// </summary>
    private static bool DataDirIsRedirected()
    {
        var dir = AppRuntime.DataDir;
        if (Directory.Exists(dir)) return AppRuntime.IsRedirected(dir);
        Directory.CreateDirectory(dir);
        if (!AppRuntime.IsRedirected(dir)) return false;
        try { Directory.Delete(dir); } catch (IOException) { }
        return true;
    }
}
