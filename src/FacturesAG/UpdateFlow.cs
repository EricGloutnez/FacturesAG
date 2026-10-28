using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FacturesAG.Core;

namespace FacturesAG;

/// <summary>
/// Mise à jour obligatoire : si une version plus récente est publiée, elle est installée avant de pouvoir
/// continuer. Sans Internet, l'application s'ouvre normalement et vérifiera à la prochaine occasion.
/// </summary>
public static class UpdateFlow
{
    public static Version CurrentVersion =>
        Updater.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>Vrai pour le .exe publié (Release) ; faux en développement (Debug).</summary>
    private static bool IsReleaseExe
    {
        get
        {
#if DEBUG
            return false;
#else
            return ExePath is string p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }

    /// <summary>Mises à jour : désactivées en développement ou avec la variable FACTURES_AG_NO_UPDATE=1.</summary>
    public static bool IsEnabled => IsReleaseExe && Environment.GetEnvironmentVariable("FACTURES_AG_NO_UPDATE") != "1";

    /// <summary>Installation : désactivée en développement ou avec la variable FACTURES_AG_NO_INSTALL=1.</summary>
    private static bool IsInstallEnabled => IsReleaseExe && Environment.GetEnvironmentVariable("FACTURES_AG_NO_INSTALL") != "1";

    /// <summary>
    /// Premier lancement du .exe téléchargé : l'installe dans AppData\Local\Programs\FacturesAG, crée les raccourcis,
    /// lance la copie installée et retourne vrai (cette copie-ci doit alors se fermer).
    /// En cas d'échec, prévient et retourne faux : l'application s'ouvre quand même, sans installation.
    /// </summary>
    public static bool InstallIfDownloaded()
    {
        if (!IsInstallEnabled || ExePath is not string exe) return false;
        if (!Installer.ShouldInstall(exe)) return false;
        string target;
        try
        {
            target = Installer.Install(exe, CurrentVersion, Installer.VersionOf(Installer.InstalledExe()));
        }
        catch (Exception e)
        {
            MessageBox.Show($"Installation impossible :\n{e.Message}\n\nL'application va s'ouvrir quand même, sans raccourci.",
                "Factures AG", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var shortcutsOk = true;
        try { Installer.CreateShortcuts(target); }
        catch (Exception) { shortcutsOk = false; } // l'application installée fonctionne quand même
        var start = new ProcessStartInfo(target) { UseShellExecute = false, ArgumentList = { Installer.InstalledArgument } };
        if (!shortcutsOk) start.ArgumentList.Add("--sans-raccourci");
        Process.Start(start);
        return true;
    }

    /// <summary>Message affiché par la copie installée juste après l'installation.</summary>
    public static void WelcomeAfterInstall(Window owner, string[] args)
    {
        if (!args.Contains(Installer.InstalledArgument)) return;
        var shortcuts = args.Contains("--sans-raccourci")
            ? "Les raccourcis n'ont pas pu être créés : vous pouvez lancer l'application depuis\n" + Installer.InstallDirectory
            : "Un raccourci « Factures AG » a été créé sur le Bureau et dans le menu Démarrer.";
        MessageBox.Show(owner,
            $"Factures AG est installé sur cet ordinateur.\n\n{shortcuts}\n\nVous pouvez supprimer le fichier que vous avez téléchargé. " +
            "Vos données ne changent pas, et les mises à jour s'installeront toutes seules.",
            "Installation terminée", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void CleanupPreviousVersion()
    {
        if (IsEnabled && ExePath is string exe) Updater.CleanupOld(exe);
    }

    /// <summary>Recherche sans afficher d'erreur. Retourne null s'il n'y a rien à faire ou pas d'Internet.</summary>
    public static async Task<UpdateInfo?> CheckQuietlyAsync(TimeSpan timeout)
    {
        if (!IsEnabled) return null;
        try
        {
            using var http = Updater.CreateHttpClient();
            using var cts = new CancellationTokenSource(timeout);
            return await Updater.CheckAsync(http, CurrentVersion, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installe la mise à jour (fenêtre bloquante, sans possibilité de la reporter) puis redémarre l'application.
    /// Retourne seulement si l'utilisateur choisit de quitter après un échec ; l'application doit alors se fermer.
    /// </summary>
    public static void InstallAndRestart(UpdateInfo update, Window? owner)
    {
        var win = new UpdateWindow(update) { Owner = owner };
        win.ShowDialog();
    }

    /// <summary>Téléchargement vérifié, remplacement du .exe et redémarrage.</summary>
    internal static async Task InstallAsync(UpdateInfo update, IProgress<double> progress)
    {
        var exe = ExePath ?? throw new UserException("Emplacement de l'application introuvable.");
        // Téléchargé à côté du .exe : le remplacement est alors un simple renommage, sur le même disque.
        var download = exe + ".download";
        try
        {
            using var http = Updater.CreateHttpClient();
            await Updater.DownloadAsync(http, update, download, progress);
            Updater.Swap(exe, download);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UserException($"Factures AG n'a pas le droit de se mettre à jour dans ce dossier :\n{Path.GetDirectoryName(exe)}\n\nDéplacez « {Path.GetFileName(exe)} » dans un dossier personnel (par exemple Documents ou le Bureau), puis relancez-le.");
        }
        catch (HttpRequestException)
        {
            throw new UserException("Le téléchargement de la mise à jour a échoué. Vérifiez votre connexion Internet, puis réessayez.");
        }
        finally
        {
            try { if (File.Exists(download)) File.Delete(download); } catch (IOException) { }
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }
}

/// <summary>Fenêtre de mise à jour : progression, puis « Réessayer » ou « Quitter » en cas d'échec. Aucun report possible.</summary>
public sealed class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1 };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private bool _done;

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;
        Title = "Mise à jour de Factures AG";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.Resources["Surface"];
        ShowInTaskbar = true;

        var title = new TextBlock { Text = $"Mise à jour obligatoire vers la version {update.Version}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(title);
        body.Children.Add(_status);
        body.Children.Add(_bar);

        var quit = new Button { Content = "Quitter", Style = (Style)Application.Current.Resources["Ghost"], Margin = new Thickness(0, 0, 8, 0) };
        quit.Click += (_, _) => { _done = true; Close(); };
        var retry = new Button { Content = "Réessayer", Style = (Style)Application.Current.Resources["Primary"] };
        retry.Click += async (_, _) => await RunAsync();
        _buttons.Children.Add(quit);
        _buttons.Children.Add(retry);
        body.Children.Add(_buttons);
        Content = body;

        Loaded += async (_, _) => await RunAsync();
        // Fermer la fenêtre pendant l'installation ne permet pas de passer outre : l'application se ferme.
        Closing += (_, e) => { if (!_done) e.Cancel = true; };
    }

    private async Task RunAsync()
    {
        _buttons.Visibility = Visibility.Collapsed;
        _bar.Value = 0;
        _status.Text = "Téléchargement et vérification de la nouvelle version… L'application redémarrera automatiquement. Vos données sont conservées.";
        try
        {
            await UpdateFlow.InstallAsync(_update, new Progress<double>(p => _bar.Value = p));
            _done = true;
            Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            _status.Text = (e is UserException ? e.Message : $"La mise à jour a échoué : {e.Message}")
                           + "\n\nCette mise à jour est obligatoire : réessayez, ou quittez et relancez Factures AG plus tard.";
            _buttons.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Après « Quitter », l'application doit se fermer.</summary>
    public bool QuitRequested => _done;
}
