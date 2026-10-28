using System.Diagnostics;

namespace FacturesAG.Core;

/// <summary>
/// Installation sur l'ordinateur (comme le Calculateur de vacances) : au premier lancement du .exe téléchargé,
/// copie dans %LOCALAPPDATA%\Programs\FacturesAG et raccourcis « Factures AG » sur le Bureau et dans le menu Démarrer.
/// Aucun droit d'administrateur ; ce dossier n'est pas bloqué par Windows Defender, ce qui permet les mises à jour.
/// </summary>
public static class Installer
{
    public const string AppName = "Factures AG";
    public const string ExeName = "Factures AG.exe";
    public const string InstalledArgument = "--installe";

    /// <summary>Dossier d'installation (modifiable avec FACTURES_AG_INSTALL_DIR, pour les essais).</summary>
    public static string InstallDirectory =>
        Environment.GetEnvironmentVariable("FACTURES_AG_INSTALL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FacturesAG");

    public static string InstalledExe(string? installDir = null) => Path.Combine(installDir ?? InstallDirectory, ExeName);

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai quand on lance le .exe téléchargé (ailleurs que dans le dossier d'installation).</summary>
    public static bool ShouldInstall(string currentExe, string? installDir = null) => !SamePath(currentExe, InstalledExe(installDir));

    /// <summary>Version d'un .exe déjà installé (lue dans le fichier), ou null.</summary>
    public static Version? VersionOf(string exe)
    {
        if (!File.Exists(exe)) return null;
        var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        return Updater.ParseTag(v?.Split('+')[0]);
    }

    /// <summary>
    /// Copie ce .exe dans le dossier d'installation et retourne le chemin installé.
    /// Une version installée plus récente n'est pas écrasée par un vieux téléchargement ;
    /// si la copie installée est ouverte (verrouillée), on la garde.
    /// </summary>
    public static string Install(string currentExe, Version currentVersion, Version? installedVersion, string? installDir = null)
    {
        var target = InstalledExe(installDir);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var installedIsNewer = File.Exists(target) && installedVersion is not null && installedVersion > Updater.Normalize(currentVersion);
        if (!installedIsNewer)
        {
            try { File.Copy(currentExe, target, overwrite: true); }
            catch (IOException) when (File.Exists(target)) { }
            catch (UnauthorizedAccessException) when (File.Exists(target)) { }
        }
        return target;
    }

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>
    /// Script PowerShell qui crée les raccourcis. PowerShell trouve le vrai Bureau (même redirigé vers OneDrive)
    /// et n'est pas bloqué par la protection « Accès contrôlé aux dossiers ».
    /// <paramref name="shortcutDir"/> remplace Bureau et menu Démarrer (essais).
    /// </summary>
    public static string ShortcutScript(string target, string? shortcutDir = null)
    {
        var folders = shortcutDir is null
            ? "@([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))"
            : $"@({Ps(shortcutDir)})";
        return $$"""
            $shell = New-Object -ComObject WScript.Shell
            foreach ($dossier in {{folders}}) {
                New-Item -ItemType Directory -Force -Path $dossier | Out-Null
                $lien = $shell.CreateShortcut((Join-Path $dossier {{Ps(AppName + ".lnk")}}))
                $lien.TargetPath = {{Ps(target)}}
                $lien.WorkingDirectory = {{Ps(Path.GetDirectoryName(target)!)}}
                $lien.IconLocation = {{Ps(target + ",0")}}
                $lien.Description = 'Facturation des assemblées'
                $lien.Save()
            }
            """;
    }

    /// <summary>Crée les raccourcis (Bureau et menu Démarrer, ou FACTURES_AG_SHORTCUT_DIR pour les essais).</summary>
    public static void CreateShortcuts(string target)
    {
        var script = ShortcutScript(target, Environment.GetEnvironmentVariable("FACTURES_AG_SHORTCUT_DIR"));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UserException("PowerShell est introuvable : raccourcis non créés.");
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { p.Kill(); throw new UserException("La création des raccourcis a pris trop de temps."); }
        if (p.ExitCode != 0) throw new UserException($"Raccourcis non créés : {err.Trim()}");
    }
}
