using Microsoft.Data.Sqlite;

namespace FacturesAG.Core;

/// <summary>
/// Emplacement des données choisi par l'utilisateur. Le choix est noté dans un petit fichier du dossier par défaut
/// (emplacement-donnees.txt) ; sans ce fichier, les données restent dans le dossier par défaut.
/// </summary>
public static class DataLocation
{
    public const string DatabaseName = "factures-ag.db";
    public const string PointerName = "emplacement-donnees.txt";

    public static string PointerFile(string defaultDir) => Path.Combine(defaultDir, PointerName);

    /// <summary>Dossier choisi, ou null si les données sont dans le dossier par défaut.</summary>
    public static string? ReadCustom(string defaultDir)
    {
        try
        {
            var p = File.ReadAllText(PointerFile(defaultDir)).Trim();
            return p.Length == 0 || SamePath(p, defaultDir) ? null : p;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    public static void SetCustom(string defaultDir, string? dir)
    {
        Directory.CreateDirectory(defaultDir);
        if (dir is null || SamePath(dir, defaultDir)) { File.Delete(PointerFile(defaultDir)); return; }
        File.WriteAllText(PointerFile(defaultDir), Path.GetFullPath(dir));
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Vérifie que l'application peut écrire dans le dossier (Windows Defender bloque par exemple Documents et le Bureau).
    /// Lève une <see cref="UserException"/> explicite sinon.
    /// </summary>
    public static void EnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".essai-ecriture-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UserException($"Factures AG ne peut pas écrire dans ce dossier :\n{dir}\n\n" +
                "Windows protège peut-être ce dossier (protection « Accès contrôlé aux dossiers » de Windows Defender, " +
                "qui bloque notamment Documents et le Bureau). Choisissez un autre dossier.\n\nDétail : " + e.Message);
        }
    }

    /// <summary>Dossier synchronisé par un service en ligne : à n'ouvrir que sur un seul ordinateur à la fois.</summary>
    public static bool IsCloudFolder(string dir) =>
        new[] { "OneDrive", "iCloud", "Dropbox", "Google Drive", "GoogleDrive" }
            .Any(k => dir.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Résumé d'une base existante (pour choisir laquelle garder) ; null si absente ou illisible.</summary>
    public static (DateTime Modified, int Invoices, string NextNumber)? Describe(string dir)
    {
        var file = Path.Combine(dir, DatabaseName);
        if (!File.Exists(file)) return null;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM invoices WHERE status != 'draft'), (SELECT value FROM settings WHERE key = 'next_number')";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (File.GetLastWriteTime(file), (int)r.GetInt64(0), r.IsDBNull(1) ? "?" : r.GetString(1));
        }
        catch (SqliteException) { return (File.GetLastWriteTime(file), -1, "?"); }
    }

    /// <summary>
    /// Copie les données vers <paramref name="target"/> : base (copie cohérente, depuis la base ouverte),
    /// PDF et sauvegardes. Une base déjà présente dans la cible est d'abord mise de côté dans ses Sauvegardes.
    /// Rien n'est supprimé dans le dossier d'origine.
    /// </summary>
    public static void CopyData(Store store, string sourceDir, string target)
    {
        EnsureWritable(target);
        var targetDb = Path.Combine(target, DatabaseName);
        if (File.Exists(targetDb))
        {
            var keep = Path.Combine(target, "Sauvegardes");
            Directory.CreateDirectory(keep);
            File.Copy(targetDb, Path.Combine(keep, $"factures-ag-{DateTime.Now:yyyyMMdd-HHmmss}-remplacee.db"), overwrite: true);
        }
        store.CopyDatabaseTo(targetDb);
        foreach (var sub in new[] { "Factures", "Sauvegardes" })
            CopyDirectory(Path.Combine(sourceDir, sub), Path.Combine(target, sub));
    }

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest);
        }
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
