using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Identité visuelle du cyber telle qu'affichée par l'agent.</summary>
public record BrandingData(string Nom, string Slogan, string Couleur, byte[]? Logo);

public class AgentConfig
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "http://127.0.0.1:8000";

    [JsonPropertyName("nom_poste")]
    public string NomPoste { get; set; } = Environment.MachineName;

    [JsonPropertyName("identifiant_unique")]
    public string IdentifiantUnique { get; set; } = "";

    [JsonPropertyName("agent_token")]
    public string AgentToken { get; set; } = "";

    /// <summary>Code d'installation donné par le gérant (page « Enregistrement » du serveur). Sert uniquement à enregistrer un NOUVEAU poste.</summary>
    [JsonPropertyName("code_installation")]
    public string CodeInstallation { get; set; } = "";

    // --- Identité du cyber (mise en cache pour fonctionner serveur éteint) ---
    [JsonPropertyName("nom_cyber")]
    public string NomCyber { get; set; } = "";

    [JsonPropertyName("slogan")]
    public string Slogan { get; set; } = "";

    [JsonPropertyName("couleur")]
    public string Couleur { get; set; } = "";

    [JsonPropertyName("logo_version")]
    public string LogoVersion { get; set; } = "";

    [JsonIgnore]
    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CyberManagerAgent");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "agent-config.json");
        }
    }

    /// <summary>Fichier local où est conservé le dernier logo téléchargé.</summary>
    [JsonIgnore]
    public static string LogoPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "logo-cyber.bin");

    public static AgentConfig LoadOrCreate()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AgentConfig>(json);
                if (config != null)
                {
                    if (string.IsNullOrEmpty(config.IdentifiantUnique))
                    {
                        config.IdentifiantUnique = Guid.NewGuid().ToString();
                        config.Save();
                    }
                    return config;
                }
            }
            catch { }
        }

        var nouveau = new AgentConfig
        {
            IdentifiantUnique = Guid.NewGuid().ToString()
        };
        nouveau.Save();
        return nouveau;
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, options));
    }

    /// <summary>
    /// Nouvelle identité : appelée quand le serveur détecte que cet ordinateur est un doublon (disque cloné).
    /// Le jeton est effacé ; le poste se réenregistrera comme un nouveau poste.
    /// </summary>
    public void RegenererIdentifiant()
    {
        IdentifiantUnique = Guid.NewGuid().ToString();
        AgentToken = "";
        Save();
    }

    /// <summary>Identité à afficher (valeurs par défaut si le serveur n'a jamais répondu).</summary>
    public BrandingData ToBranding()
    {
        byte[]? logo = null;
        try
        {
            if (!string.IsNullOrEmpty(LogoVersion) && File.Exists(LogoPath))
                logo = File.ReadAllBytes(LogoPath);
        }
        catch { }

        var jamaisSynchronise = string.IsNullOrWhiteSpace(NomCyber);
        return new BrandingData(
            jamaisSynchronise ? "Cyber Manager" : NomCyber,
            jamaisSynchronise ? "Gestion de cybercafé" : Slogan,
            string.IsNullOrWhiteSpace(Couleur) ? "#7c3aed" : Couleur,
            logo);
    }
}