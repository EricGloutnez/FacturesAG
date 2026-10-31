using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FacturesAG;

/// <summary>
/// Mot de passe d'application Gmail, chiffré par Windows pour votre seule session (DPAPI) : il n'est lisible que par vous,
/// sur cet ordinateur. Il est rangé dans le dossier par défaut (AppData), jamais avec la base de données,
/// qui peut être copiée ou synchronisée ailleurs.
/// </summary>
public static class GmailPassword
{
    private static readonly byte[] Entropy = "FacturesAG-gmail"u8.ToArray();

    private static string File => Path.Combine(AppRuntime.DefaultDataDir, "gmail-mot-de-passe.dat");

    public static bool Exists => System.IO.File.Exists(File);

    public static string? Read()
    {
        try
        {
            return Exists
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (CryptographicException) { return null; } // chiffré par un autre compte Windows : à entrer de nouveau
    }

    public static void Save(string password)
    {
        Directory.CreateDirectory(AppRuntime.DefaultDataDir);
        System.IO.File.WriteAllBytes(File, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Delete()
    {
        if (Exists) System.IO.File.Delete(File);
    }
}
