using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public enum ApiStatut { Ok, NonAutorise, Injoignable, Erreur }

public record ApiReponse<T>(ApiStatut Statut, T? Donnees = default);

public record ActivationResult(bool Succes, string Message, string? Avertissement = null)
{
    public static ActivationResult Echec(string message) => new(false, message);
}

/// <summary>Résultat d'une action de session demandée depuis le widget (suspendre / quitter).</summary>
public record ResultatAction(bool Succes, string Message, int? Montant = null);

public record InfosReseau(string Ip, string Mac);

public class ApiClient
{
    public const string Version = "1.2.0";

    private readonly HttpClient _http;
    private readonly AgentConfig _config;
    private InfosReseau _reseau = new("", "");
    private DateTime _reseauLuLe = DateTime.MinValue;

    public ApiClient(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>IP et MAC de la carte réseau réellement utilisée pour joindre le serveur.</summary>
    public InfosReseau Reseau
    {
        get
        {
            if (DateTime.UtcNow - _reseauLuLe > TimeSpan.FromSeconds(60))
            {
                _reseau = LireReseau(_config.ServerUrl);
                _reseauLuLe = DateTime.UtcNow;
            }
            return _reseau;
        }
    }

    // ------------------------------------------------------------------
    // Appels API
    // ------------------------------------------------------------------

    public async Task<ApiStatut> HandshakeAsync()
    {
        var net = Reseau;
        var payload = new
        {
            identifiant_unique = _config.IdentifiantUnique,
            nom_poste = _config.NomPoste,
            adresse_ip = net.Ip,
            adresse_mac = net.Mac,
            agent_version = Version
        };

        var (statut, corps) = await EnvoyerAsync(HttpMethod.Post, "/api/agent/handshake", payload, avecToken: false);
        if (statut != ApiStatut.Ok) return statut;

        try
        {
            using var doc = JsonDocument.Parse(corps!);
            var root = doc.RootElement;
            var token = root.TryGetProperty("agent_token", out var t) ? t.GetString() : null;
            var nom = root.TryGetProperty("nom_poste", out var n) ? n.GetString() : null;

            if (string.IsNullOrEmpty(token)) return ApiStatut.Erreur;

            var modifie = token != _config.AgentToken;
            _config.AgentToken = token;
            if (!string.IsNullOrWhiteSpace(nom) && nom != _config.NomPoste)
            {
                _config.NomPoste = nom;   // le serveur fait foi pour le nom
                modifie = true;
            }
            if (modifie) _config.Save();
            return ApiStatut.Ok;
        }
        catch { return ApiStatut.Erreur; }
    }

    public async Task<ApiReponse<HeartbeatResponse>> SendHeartbeatAsync()
    {
        var (statut, corps) = await EnvoyerAsync(HttpMethod.Post, "/api/agent/heartbeat",
            new { adresse_ip = Reseau.Ip });
        if (statut != ApiStatut.Ok) return new(statut);

        try
        {
            var hb = JsonSerializer.Deserialize<HeartbeatResponse>(corps!);
            return hb is { Ok: true } ? new(ApiStatut.Ok, hb) : new(ApiStatut.Erreur);
        }
        catch { return new(ApiStatut.Erreur); }
    }

    public async Task<ApiReponse<List<AgentCommand>>> GetCommandsAsync()
    {
        var (statut, corps) = await EnvoyerAsync(HttpMethod.Get, "/api/agent/commandes");
        if (statut != ApiStatut.Ok) return new(statut);

        try
        {
            using var doc = JsonDocument.Parse(corps!);
            var commandes = new List<AgentCommand>();
            if (doc.RootElement.TryGetProperty("commandes", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    commandes.Add(new AgentCommand
                    {
                        Id = item.GetProperty("id").GetInt32(),
                        Type = item.GetProperty("type").GetString() ?? "",
                        Payload = item.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null
                            ? p.GetRawText() : null
                    });
                }
            }
            return new(ApiStatut.Ok, commandes);
        }
        catch { return new(ApiStatut.Erreur); }
    }

    public async Task ConfirmerCommandeAsync(int id, bool succes, string message = "")
    {
        await EnvoyerAsync(HttpMethod.Post, $"/api/agent/commandes/{id}/confirmer", new { succes, message });
    }

    /// <summary>Demande d'activation (bouton ACTIVER). Le serveur crée la session et connecte le poste.</summary>
    public async Task<ActivationResult> ActiverAsync()
    {
        var (statut, corps) = await EnvoyerAsync(HttpMethod.Post, "/api/agent/activer", new { });

        switch (statut)
        {
            case ApiStatut.Injoignable:
                return ActivationResult.Echec("Serveur injoignable. Réessayez dans quelques instants.");

            case ApiStatut.NonAutorise:
                _config.AgentToken = "";   // forcera un nouveau handshake
                return ActivationResult.Echec("Poste en cours de reconnexion au serveur. Réessayez dans 5 secondes.");

            case ApiStatut.Erreur:
                return ActivationResult.Echec(LireChamp(corps, "message") ?? "Activation refusée par le serveur.");

            default:
                return new ActivationResult(true,
                    LireChamp(corps, "message") ?? "Poste activé.",
                    LireChamp(corps, "avertissement"));
        }
    }

        /// <summary>Le client suspend sa session depuis le widget : chrono figé côté serveur.</summary>
    public Task<ResultatAction> SuspendreSessionAsync() =>
        ActionSessionAsync("/api/agent/session/suspendre", "Session suspendue.");

    /// <summary>Le client quitte depuis le widget : session clôturée, montant calculé, réseau coupé.</summary>
    public Task<ResultatAction> FermerSessionAsync() =>
        ActionSessionAsync("/api/agent/session/fermer", "Session terminée.");


    /// <summary>Le client écrit à l'accueil (chat client → admin).</summary>
    public Task<ResultatAction> EnvoyerMessageAsync(string texte) =>
        ActionSessionAsync("/api/agent/message", "Message envoyé.", new { texte });

    private async Task<ResultatAction> ActionSessionAsync(string chemin, string messageParDefaut, object? payload = null)
    {
        var (statut, corps) = await EnvoyerAsync(HttpMethod.Post, chemin, payload ?? new { });

        switch (statut)
        {
            case ApiStatut.Injoignable:
                return new ResultatAction(false, "Serveur injoignable. Réessayez dans quelques instants.");

            case ApiStatut.NonAutorise:
                _config.AgentToken = "";   // forcera un nouveau handshake
                return new ResultatAction(false, "Poste en cours de reconnexion au serveur. Réessayez dans 5 secondes.");

            case ApiStatut.Erreur:
                return new ResultatAction(false, LireChamp(corps, "message") ?? "Action refusée par le serveur.");

            default:
                return new ResultatAction(true, LireChamp(corps, "message") ?? messageParDefaut, LireEntier(corps, "montant"));
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<(ApiStatut statut, string? corps)> EnvoyerAsync(
        HttpMethod methode, string chemin, object? payload = null, bool avecToken = true)
    {
        try
        {
            using var req = new HttpRequestMessage(methode, _config.ServerUrl.TrimEnd('/') + chemin);
            req.Headers.Accept.ParseAdd("application/json");
            if (avecToken && !string.IsNullOrEmpty(_config.AgentToken))
                req.Headers.Add("X-Agent-Token", _config.AgentToken);

            if (methode != HttpMethod.Get)
                req.Content = new StringContent(
                    JsonSerializer.Serialize(payload ?? new { }), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req);
            var corps = await res.Content.ReadAsStringAsync();

            if (res.StatusCode == HttpStatusCode.Unauthorized) return (ApiStatut.NonAutorise, corps);
            return (res.IsSuccessStatusCode ? ApiStatut.Ok : ApiStatut.Erreur, corps);
        }
        catch
        {
            return (ApiStatut.Injoignable, null);
        }
    }

    private static string? LireChamp(string? json, string champ)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(champ, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

        private static int? LireEntier(string? json, string champ)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(champ, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Détermine l'IP locale utilisée pour joindre le serveur, puis la MAC de cette même carte
    /// (et non d'une carte virtuelle/VPN au hasard).
    /// </summary>
    private static InfosReseau LireReseau(string serverUrl)
    {
        string ip = "", mac = "";
        try
        {
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                s.Connect(uri.Host, uri.Port);   // UDP : aucun paquet envoyé
                var local = (s.LocalEndPoint as IPEndPoint)?.Address;
                if (local != null)
                {
                    ip = local.ToString();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local))) continue;
                        mac = FormaterMac(nic.GetPhysicalAddress().ToString());
                        if (mac != "") break;
                    }
                }
            }
        }
        catch { }

        // Repli : première carte active non loopback
        if (mac == "")
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    mac = FormaterMac(nic.GetPhysicalAddress().ToString());
                    if (mac != "") break;
                }
            }
            catch { }
        }

        return new InfosReseau(ip, mac);
    }

    public Task<ResultatAction> ReprendreSessionAsync() =>
        ActionSessionAsync("/api/agent/session/reprendre", "Session reprise.");

    private static string FormaterMac(string brut) =>
        brut.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => brut.Substring(i * 2, 2))).ToUpperInvariant()
            : "";
}

public class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("nom_poste")] public string? NomPoste { get; set; }
    [JsonPropertyName("etat")] public string? Etat { get; set; }
    [JsonPropertyName("doit_etre_verrouille")] public bool DoitEtreVerrouille { get; set; }
    [JsonPropertyName("session")] public SessionInfo? Session { get; set; }
}

public class SessionInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("date_heure_debut")] public string? DateHeureDebut { get; set; }
    [JsonPropertyName("date_heure_fin")] public string? DateHeureFin { get; set; }
    [JsonPropertyName("duree_prevue")] public int DureePrevue { get; set; }
    [JsonPropertyName("montant_actuel")] public int MontantActuel { get; set; }
    [JsonPropertyName("etat")] public string? Etat { get; set; }
    [JsonPropertyName("ecoule_s")] public int EcouleS { get; set; }
    [JsonPropertyName("restant_s")] public int? RestantS { get; set; }
}

public class AgentCommand
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? Payload { get; set; }
}