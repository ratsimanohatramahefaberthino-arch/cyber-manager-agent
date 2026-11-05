using System.Text.Json;
using System.Text.Json.Serialization;

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
}