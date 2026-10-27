using System.Text.RegularExpressions;

namespace FacturesAG.Core;

/// <summary>
/// Règles de facturation des assemblées virtuelles : taux horaire en vigueur à la date de l'assemblée,
/// toute heure commencée est due, avec un minimum d'heures.
/// </summary>
public static partial class Billing
{
    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
    private static partial Regex HhMm();

    [GeneratedRegex(@"^(\d{1,2})[:h.](\d{2})?$|^(\d{1,2})$")]
    private static partial Regex HourWithSeparator();

    [GeneratedRegex(@"^(\d{1,2})(\d{2})$")]
    private static partial Regex HourCompact();

    public static int? ToMinutes(string? hhmm)
    {
        var m = HhMm().Match(hhmm ?? "");
        return m.Success ? int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) : null;
    }

    /// <summary>Durée en minutes ; une fin plus tôt que le début signifie qu'on a passé minuit.</summary>
    public static int? DurationMinutes(string? start, string? end)
    {
        if (ToMinutes(start) is not int a || ToMinutes(end) is not int b) return null;
        var d = b - a;
        return d < 0 ? d + 24 * 60 : d;
    }

    public static decimal BilledHours(int minutes, decimal minHours) =>
        Math.Max(minHours, (decimal)Math.Ceiling(minutes / 60.0));

    /// <summary>Taux dont l'entrée en vigueur est la plus récente sans dépasser la date donnée.</summary>
    public static Rate? RateFor(string date, IEnumerable<Rate> rates) =>
        rates.Where(r => string.CompareOrdinal(r.EffectiveFrom, date) <= 0)
             .OrderByDescending(r => r.EffectiveFrom, StringComparer.Ordinal)
             .FirstOrDefault();

    /// <summary>Montant calculé, ou null si on ne peut pas encore le calculer (heure de fin manquante, aucun taux).</summary>
    public static decimal? ComputeAmount(string date, string? start, string? end, bool quorum, IEnumerable<Rate> rates)
    {
        if (RateFor(date, rates) is not Rate rate) return null;
        if (!quorum) return rate.MinHours * rate.HourlyRate;
        if (DurationMinutes(start, end) is not int minutes) return null;
        return BilledHours(minutes, rate.MinHours) * rate.HourlyRate;
    }

    /// <summary>
    /// Accepte « 20:34 », « 20h34 », « 2034 », « 834 », « 20h » ou « 20 ».
    /// Retourne « HH:MM », une chaîne vide si rien n'est saisi, ou null si la saisie est invalide.
    /// </summary>
    public static string? NormalizeTime(string? input)
    {
        var s = Regex.Replace((input ?? "").Trim().ToLowerInvariant(), @"\s+", "");
        if (s.Length == 0) return "";
        int h, min;
        var m = HourWithSeparator().Match(s);
        if (m.Success)
        {
            h = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value);
            min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        }
        else if ((m = HourCompact().Match(s)).Success)
        {
            h = int.Parse(m.Groups[1].Value);
            min = int.Parse(m.Groups[2].Value);
        }
        else return null;
        if (h > 23 || min > 59) return null;
        return $"{h:00}:{min:00}";
    }

    public static string FormatHour(string? hhmm) =>
        ToMinutes(hhmm) is int m ? $"{m / 60}h{m % 60:00}" : "";

    public static string FormatDuration(int minutes) => $"{minutes / 60} h {minutes % 60:00}";

    public static string DescribeLine(string entity, string? start, string? end, bool quorum)
    {
        var text = entity;
        if (!string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end)) text += $" ({FormatHour(start)} - {FormatHour(end)})";
        if (!quorum) text += " - Quorum non atteint";
        return text;
    }
}
