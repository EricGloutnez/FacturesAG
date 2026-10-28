namespace FacturesAG.Core;

/// <summary>
/// Identité visuelle fixe de Factures AG (logo « facture et horloge »), intégrée à l'application et non modifiable.
/// Le dessin source est dans assets/logo-icone.svg.
/// </summary>
public static class Brand
{
    public const string Name = "Factures AG";
    public const string Tagline = "CHAQUE HEURE COMPTE";
    public const string Teal = "#1F6F5C";
    public const string Amber = "#E0A23B";

    public const string IconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 80 80" width="80" height="80">
          <path d="M14 6h34l16 16v52H14z" fill="#FFFFFF" stroke="#1F6F5C" stroke-width="5" stroke-linejoin="round"/>
          <path d="M48 6v16h16" fill="none" stroke="#1F6F5C" stroke-width="5" stroke-linejoin="round"/>
          <path d="M24 32h22M24 43h14" fill="none" stroke="#1F6F5C" stroke-width="5" stroke-linecap="round"/>
          <circle cx="56" cy="60" r="17" fill="#E0A23B" stroke="#FFFFFF" stroke-width="3"/>
          <path d="M56 51v9l7 4" fill="none" stroke="#FFFFFF" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"/>
        </svg>
        """;
}
