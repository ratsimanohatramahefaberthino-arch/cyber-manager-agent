/// <summary>Identité visuelle du cyber telle qu'affichée par l'Écran (envoyée par le Gardien).</summary>
public record BrandingData(string Nom, string Slogan, string Couleur, byte[]? Logo);

/// <summary>
/// L'Écran ne contient AUCUN secret : le jeton et l'adresse du serveur sont chez le Gardien (ProgramData).
/// Cette classe ne donne plus que le dossier de travail de l'utilisateur (journal, données WebView2).
/// </summary>
public static class AgentConfig
{
    /// <summary>Chemin « fictif » dont seul le dossier est utilisé (compatibilité ClientWindow / Program).</summary>
    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CyberManagerAgent");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ecran.dat");
        }
    }

    public static BrandingData BrandingParDefaut => new("Cyber Manager", "Gestion de cybercafé", "#7c3aed", null);

    /// <summary>Convertit le message « branding » du Gardien (logo en base64) en données d'affichage.</summary>
    public static BrandingData DepuisMessage(MessageCanal m)
    {
        byte[]? logo = null;
        try { if (!string.IsNullOrEmpty(m.Logo)) logo = Convert.FromBase64String(m.Logo); } catch { }

        return new BrandingData(
            string.IsNullOrWhiteSpace(m.Nom) ? "Cyber Manager" : m.Nom,
            m.Slogan ?? "",
            string.IsNullOrWhiteSpace(m.Couleur) ? "#7c3aed" : m.Couleur,
            logo);
    }
}