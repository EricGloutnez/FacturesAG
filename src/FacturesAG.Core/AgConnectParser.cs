using System.Text.RegularExpressions;

namespace FacturesAG.Core;

/// <summary>
/// Lit le texte copié de la page « Mes invitations » d'ag-connect.ca. Chaque assemblée y apparaît ainsi :
/// Statut / Entité juridique / Type d'assemblée / Organisateur /
/// « 05 octobre 2026 à 18:30 (heure de Montreal) » / adresse ou « - » / « N participants »
/// </summary>
public static partial class AgConnectParser
{
    private static readonly Dictionary<string, int> Mois = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janvier"] = 1, ["février"] = 2, ["fevrier"] = 2, ["mars"] = 3, ["avril"] = 4, ["mai"] = 5, ["juin"] = 6,
        ["juillet"] = 7, ["août"] = 8, ["aout"] = 8, ["septembre"] = 9, ["octobre"] = 10, ["novembre"] = 11,
        ["décembre"] = 12, ["decembre"] = 12,
    };

    [GeneratedRegex(@"^(Ouverture|Clôturée|Cloturée|Convoquée|Non convoquée|Brouillon|En cours|Annulée|Planifiée)$", RegexOptions.IgnoreCase)]
    private static partial Regex Statut();

    [GeneratedRegex(@"^(\d{1,2})\s+(\p{L}+)\s+(\d{4})\s+à\s+(\d{1,2}):(\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"participants?$", RegexOptions.IgnoreCase)]
    private static partial Regex Participants();

    public static IReadOnlyList<AgConnectItem> Parse(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var result = new List<AgConnectItem>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = DateLine().Match(lines[i]);
            if (!m.Success || !Mois.TryGetValue(m.Groups[2].Value, out var month)) continue;

            // L'entité suit la ligne de statut ; à défaut, elle est 3 lignes au-dessus de la date.
            var entityIdx = i - 3;
            for (var k = i - 1; k >= Math.Max(0, i - 6); k--)
            {
                if (Statut().IsMatch(lines[k])) { entityIdx = k + 1; break; }
            }
            if (entityIdx < 0) continue;

            var next = i + 1 < lines.Count ? lines[i + 1] : "";
            var address = next == "-" || Participants().IsMatch(next) ? "" : Regex.Replace(next, @"\s*-\s*$", "");
            var status = entityIdx > 0 && Statut().IsMatch(lines[entityIdx - 1]) ? lines[entityIdx - 1] : "";
            result.Add(new AgConnectItem(
                Entity: lines[entityIdx],
                Date: $"{m.Groups[3].Value}-{month:00}-{int.Parse(m.Groups[1].Value):00}",
                Start: $"{int.Parse(m.Groups[4].Value):00}:{m.Groups[5].Value}",
                Address: address,
                Virtual: address.Length == 0,
                Status: status));
        }
        return result;
    }
}
