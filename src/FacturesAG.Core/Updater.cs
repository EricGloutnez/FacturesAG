using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FacturesAG.Core;

/// <summary>Nouvelle version disponible sur GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string AssetUrl, string Sha256, long Size);

/// <summary>
/// Mises à jour obligatoires depuis les versions publiées sur GitHub :
/// recherche de la dernière version, téléchargement vérifié (SHA-256) puis remplacement du .exe.
/// </summary>
public static partial class Updater
{
    public const string Repository = "EricGloutnez/FacturesAG";
    public static readonly string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    [GeneratedRegex(@"^FacturesAG-[\w.\-]+-win-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    [GeneratedRegex(@"^sha256:([0-9a-f]{64})$", RegexOptions.IgnoreCase)]
    private static partial Regex Digest();

    /// <summary>« v0.2.0 » ou « 0.2.0 » → 0.2.0 ; null si l'étiquette n'est pas un numéro de version.</summary>
    public static Version? ParseTag(string? tag)
    {
        var s = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>Compare toujours sur 3 composants (0.2 == 0.2.0).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// Lit la réponse JSON de l'API GitHub « releases/latest ». Retourne la mise à jour si elle est plus récente
    /// que <paramref name="current"/> et contient le .exe Windows avec son empreinte SHA-256 ; sinon null.
    /// </summary>
    public static UpdateInfo? FindUpdate(string releaseJson, Version current)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (ParseTag(tag) is not Version version || version <= Normalize(current)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!AssetName().IsMatch(name)) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? Digest().Match(d.GetString()!) : Match.Empty;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            // Sans empreinte vérifiable, on n'installe rien.
            if (url is null || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) || !digest.Success || size <= 0) continue;
            return new UpdateInfo(version, tag!, name, url, digest.Groups[1].Value.ToLowerInvariant(), size);
        }
        return null;
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FacturesAG", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Interroge GitHub. Lève une exception si GitHub est injoignable (pas d'Internet).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null; // aucune version publiée
        res.EnsureSuccessStatusCode();
        return FindUpdate(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false), current);
    }

    /// <summary>Télécharge la mise à jour dans <paramref name="destFile"/> et vérifie taille et SHA-256.</summary>
    public static async Task DownloadAsync(HttpClient http, UpdateInfo update, string destFile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using (var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(destFile))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                progress?.Report(Math.Min(1.0, (double)done / update.Size));
            }
        }
        VerifyFile(destFile, update);
    }

    /// <summary>Supprime le fichier et lève une exception si sa taille ou son empreinte ne correspond pas.</summary>
    public static void VerifyFile(string file, UpdateInfo update)
    {
        string hash;
        long size;
        using (var f = File.OpenRead(file))
        {
            size = f.Length;
            hash = Convert.ToHexStringLower(SHA256.HashData(f));
        }
        if (size != update.Size || hash != update.Sha256)
        {
            File.Delete(file);
            throw new UserException("Le fichier téléchargé est incomplet ou altéré (empreinte SHA-256 différente). Réessayez.");
        }
    }

    /// <summary>
    /// Remplace l'application par la nouvelle version. Windows permet de renommer un .exe en cours d'exécution :
    /// l'ancien devient « .old » (supprimé au prochain démarrage) puis le nouveau prend sa place.
    /// En cas d'échec, l'ancien est remis en place.
    /// </summary>
    public static void Swap(string currentExe, string newExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try { File.Move(newExe, currentExe); }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Supprime l'ancienne version laissée par une mise à jour (sans erreur si elle est encore verrouillée).</summary>
    public static void CleanupOld(string currentExe)
    {
        try { if (File.Exists(currentExe + ".old")) File.Delete(currentExe + ".old"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
