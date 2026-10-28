using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FacturesAG;

/// <summary>
/// Environnement d'exécution : dossier des données, mode essai, contexte « empaqueté » et instance unique.
/// </summary>
public static class AppRuntime
{
    /// <summary>Données d'essai : FACTURES_AG_DATA désigne un autre dossier que les vraies données.</summary>
    public static bool IsTestData => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FACTURES_AG_DATA"));

    /// <summary>Dossier par défaut : AppData\Local\Factures AG (jamais bloqué par Windows Defender), ou le dossier d'essai.</summary>
    public static string DefaultDataDir =>
        Environment.GetEnvironmentVariable("FACTURES_AG_DATA") is { Length: > 0 } test
            ? Path.GetFullPath(test)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Factures AG");

    /// <summary>Dossier choisi dans les Réglages (« Changer de dossier »), ou null.</summary>
    public static string? CustomDataDir => FacturesAG.Core.DataLocation.ReadCustom(DefaultDataDir);

    /// <summary>Dossier des données utilisé : celui choisi dans les Réglages, sinon le dossier par défaut.</summary>
    public static string DataDir => CustomDataDir ?? DefaultDataDir;

    public static string WindowTitle => IsTestData ? "Factures AG — ESSAI (données de test)" : "Factures AG";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    /// <summary>
    /// Vrai si l'application a été lancée depuis une application « empaquetée » (ex. une application du Microsoft Store) :
    /// Windows lui montre alors une copie séparée de AppData, et donc pas les vraies données.
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                var len = 0;
                return GetCurrentPackageFullName(ref len, null) != 15700; // 15700 = APPMODEL_ERROR_NO_PACKAGE
            }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    /// <summary>Emplacement réel d'un dossier sur le disque (après redirections éventuelles de Windows), ou null.</summary>
    public static string? RealPath(string directory)
    {
        using var h = CreateFile(directory, 0, 7 /* partage total */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS : ouvrir un dossier */, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var len = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
        if (len == 0 || len >= sb.Capacity) return null;
        var p = sb.ToString();
        return p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..] : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    /// <summary>
    /// Vrai si Windows redirige le dossier des données vers la copie privée d'une autre application
    /// (…\AppData\Local\Packages\…\LocalCache\…) : c'est ce qui affichait des réglages « disparus ».
    /// </summary>
    public static bool IsRedirected(string directory) =>
        RealPath(directory) is string real
        && real.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase)
        && real.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);

    private static Mutex? _instance;

    /// <summary>
    /// Une seule copie à la fois par dossier de données. <paramref name="wait"/> laisse le temps à une copie qui se ferme
    /// (redémarrage après installation ou mise à jour) de libérer la place.
    /// </summary>
    public static bool AcquireSingleInstance(TimeSpan wait)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDir.ToLowerInvariant())))[..16];
        _instance = new Mutex(false, $"Local\\FacturesAG-{key}");
        try { return _instance.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // l'ancienne copie s'est fermée brusquement
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Ramène au premier plan la fenêtre de la copie déjà ouverte.</summary>
    public static void ActivateExisting()
    {
        var title = WindowTitle;
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (IsWindowVisible(h) && sb.ToString() == title)
            {
                ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
                return false;
            }
            return true;
        }, IntPtr.Zero);
    }
}
