using System.Net;
using System.Net.Mail;

namespace FacturesAG.Core;

/// <summary>Serveur d'envoi des courriels. Ssl : connexion chiffrée (STARTTLS), toujours vrai pour Gmail.</summary>
public sealed record MailServer(string Host, int Port, bool Ssl)
{
    public static readonly MailServer Gmail = new("smtp.gmail.com", 587, true);

    /// <summary>Gmail, ou le serveur désigné par FACTURES_AG_SMTP (« hôte:port », sans chiffrement) pour les essais.</summary>
    public static MailServer Current =>
        Environment.GetEnvironmentVariable("FACTURES_AG_SMTP") is { Length: > 0 } test && test.Split(':') is [var host, var port]
            ? new(host, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), false)
            : Gmail;
}

/// <summary>Envoi d'une facture par courriel, avec son PDF en pièce jointe.</summary>
public static class InvoiceMail
{
    public const string DefaultSubject = "Facture n° {numéro}";

    public const string DefaultBody = """
        Bonjour,

        Vous trouverez ci-joint la facture n° {numéro} du {date}, au montant de {montant}, payable {échéance}.

        Merci,
        {nom}
        """;

    /// <summary>Mots remplacés dans l'objet et le message (affichés dans les Réglages).</summary>
    public const string Placeholders = "{numéro}  {date}  {montant}  {échéance}  {nom}  {client}";

    public static string Fill(string template, InvoiceDetail inv, IReadOnlyDictionary<string, string> settings)
    {
        var i = inv.Invoice;
        var due = i.PaymentDays is null or 0 || i.DueDate is null ? "sur réception" : $"au plus tard le {InvoicePdf.LongDate(i.DueDate)}";
        return template
            .Replace("{numéro}", i.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{date}", InvoicePdf.LongDate(i.InvoiceDate))
            .Replace("{montant}", InvoicePdf.Money(i.Total))
            .Replace("{échéance}", due)
            .Replace("{nom}", settings.GetValueOrDefault("issuer_name", ""))
            .Replace("{client}", settings.GetValueOrDefault("client_name", ""));
    }

    /// <summary>Google affiche le mot de passe d'application en 4 groupes (« abcd efgh ijkl mnop ») : les espaces sont retirés.</summary>
    public static string NormalizeAppPassword(string password) => string.Concat(password.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Une ou plusieurs adresses séparées par « ; » ou « , ».</summary>
    public static List<MailAddress> ParseRecipients(string text)
    {
        var parts = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new UserException("Entrez l'adresse courriel du destinataire.");
        return parts.Select(p => MailAddress.TryCreate(p, out var a) ? a : throw new UserException($"Adresse courriel invalide : « {p} ».")).ToList();
    }

    public static async Task SendAsync(MailServer server, string from, string fromName, string appPassword, string to,
        string subject, string body, string attachmentName, byte[] pdf, CancellationToken cancel = default)
    {
        if (!MailAddress.TryCreate(from.Trim(), fromName, out var sender))
            throw new UserException($"Votre adresse Gmail d'envoi est invalide : « {from} ».");
        using var message = new MailMessage { From = sender, Subject = subject, Body = body,
            SubjectEncoding = System.Text.Encoding.UTF8, BodyEncoding = System.Text.Encoding.UTF8 };
        foreach (var r in ParseRecipients(to)) message.To.Add(r);
        using var stream = new MemoryStream(pdf);
        var attachment = new Attachment(stream, attachmentName, "application/pdf");
        attachment.ContentDisposition!.FileName = attachmentName;
        message.Attachments.Add(attachment);

        using var client = new SmtpClient(server.Host, server.Port)
        {
            EnableSsl = server.Ssl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(sender.Address, NormalizeAppPassword(appPassword)),
            Timeout = 60_000,
        };
        try { await client.SendMailAsync(message, cancel); }
        catch (SmtpException e) when ((int)e.StatusCode is 530 or 534 or 535 || e.Message.Contains("5.7.", StringComparison.Ordinal))
        {
            throw new UserException("Gmail a refusé la connexion : vérifiez votre adresse Gmail d'envoi et le mot de passe d'application " +
                                    "(Réglages → Envoi par courriel). Ce n'est pas votre mot de passe Gmail habituel.");
        }
        catch (SmtpException e)
        {
            throw new UserException($"Le courriel n'a pas pu être envoyé. Vérifiez votre connexion Internet.\n\nDétail : {e.InnerException?.Message ?? e.Message}");
        }
    }
}
