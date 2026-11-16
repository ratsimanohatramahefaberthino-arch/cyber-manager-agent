// ============================================================================
//  Cyber Manager Écran — fenêtre de verrouillage, widget, notifications.
//  Lancé et relancé par le Gardien dans la session du client. Aucun secret ici.
//  Variable : AGENT_DEV=1  → mode développement (fenêtre normale, pas de blocage clavier)
// ============================================================================

// --- Instance unique -------------------------------------------------------
using var mutexUnique = new Mutex(true, @"Global\CyberManagerAgent", out var premiereInstance);
if (!premiereInstance) return;

// --- Journal (dossier de l'utilisateur, jamais de secret) -------------------
var dossierDonnees = Path.GetDirectoryName(AgentConfig.ConfigPath)!;
var logPath = Path.Combine(dossierDonnees, "ecran.log");
var logVerrou = new object();

try
{
    if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_000_000)
        File.Move(logPath, logPath + ".old", overwrite: true);
}
catch { }

void Log(string msg)
{
    var ligne = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
    Console.WriteLine(ligne);
    lock (logVerrou)
    {
        try { File.AppendAllText(logPath, ligne + Environment.NewLine); } catch { }
    }
}

// Lecture d'un champ du payload JSON d'une commande (jamais journalisé)
static string? LireTexte(string? json, string champ)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(champ, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;
    }
    catch { return null; }
}

static int? LireEntier(string? json, string champ)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(champ, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetInt32() : null;
    }
    catch { return null; }
}

const int DelaiArretSecondes = 5;

var devMode = Environment.GetEnvironmentVariable("AGENT_DEV") == "1";

try
{
    Log($">>> Démarrage de l'Écran Cyber Manager {ApiClient.Version} — mode {(devMode ? "DEV" : "PRODUCTION")}");
    Log($"Dossier de données : {dossierDonnees}");

    // --- Thread UI (WinForms STA) instrumenté pour diagnostic précis ------
    var fenetrePrete = new ManualResetEventSlim(false);
    var uiThread = new Thread(() =>
    {
        try
        {
            Log("[Diagnostic UI 1/6] Démarrage du thread UI STA");
            ApplicationConfiguration.Initialize();
            Log("[Diagnostic UI 2/6] ApplicationConfiguration.Initialize terminé");

            ClientWindow.Journal = Log;
            Log("[Diagnostic UI 3/6] Début instanciation ClientWindow");
            var fenetre = new ClientWindow(devMode);
            Log("[Diagnostic UI 4/6] Fin instanciation ClientWindow");

            fenetrePrete.Set();
            Log("[Diagnostic UI 5/6] fenetrePrete.Set() signalé");

            Application.Run(fenetre);
            Log("[Diagnostic UI 6/6] Application.Run terminé");
        }
        catch (Exception ex)
        {
            Log($">>> ERREUR UI : {ex}");
            fenetrePrete.Set();
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();

    Log("Attente de la création de la fenêtre (peut prendre du temps au premier lancement)...");
    if (!fenetrePrete.Wait(TimeSpan.FromMinutes(5)) || ClientWindow.Instance is null)
    {
        Log(">>> La fenêtre n'a pas pu être créée dans le délai imparti. Arrêt.");
        return;
    }
    var fenetreUi = ClientWindow.Instance;
    Log("Fenêtre prête. Connexion au Gardien.");

    // --- Liaison avec le Gardien ---------------------------------------------
    var gardien = new ApiClient(Log);

    // État partagé (alimenté par le Gardien)
    var poste = "";
    var ip = "";
    var etatServeur = "serveur_injoignable";

    void PousserInfos() => fenetreUi.MettreAJourInfos(poste, ip, etatServeur);

    // Heartbeat immédiat demandé (recharge, désactivation, fin de forfait…)
    void Reveiller() => gardien.Envoyer(new MessageCanal { Type = "reveil" });
    fenetreUi.ReveillerHeartbeat = Reveiller;

    // Clic sur ACTIVER → Gardien → serveur
    fenetreUi.ActivationHandler = () => gardien.ActiverAsync();

    // Widget : Suspendre / Reprendre / Quitter → Gardien → serveur
    fenetreUi.SuspendreHandler = async () =>
    {
        // Le client n'a que le bouton Reprendre quand le poste est suspendu
        if (etatServeur == "suspendu") return await gardien.ReprendreSessionAsync();
        return await gardien.SuspendreSessionAsync();
    };

    fenetreUi.QuitterHandler = () => gardien.FermerSessionAsync();

    // Chat client → accueil
    fenetreUi.MessageHandler = texte => gardien.EnvoyerMessageAsync(texte);

    // Fin du décompte : c'est le Gardien qui éteint réellement Windows (et seulement si le serveur l'a demandé)
    fenetreUi.ExtinctionHandler = () =>
    {
        Log("Décompte terminé : demande d'extinction envoyée au Gardien.");
        return gardien.Envoyer(new MessageCanal { Type = "extinction" });
    };

    // Affichage immédiat avec l'identité par défaut ; le Gardien envoie la vraie dès la connexion
    fenetreUi.MettreAJourBranding(AgentConfig.BrandingParDefaut);
    PousserInfos();

    // --- Messages du Gardien ---------------------------------------------------
    gardien.Connecte += () => { /* le Gardien répond en poussant branding + état */ };

    gardien.Deconnecte += () =>
    {
        etatServeur = "serveur_injoignable";
        PousserInfos();
    };

    gardien.MessageRecu += m =>
    {
        switch (m.Type)
        {
            case "etat":
                poste = m.Poste ?? poste;
                ip = m.Ip ?? ip;
                etatServeur = m.Etat ?? "serveur_injoignable";
                PousserInfos();
                // Verrou absent = serveur injoignable : on garde l'état courant (mode dégradé)
                if (m.Verrou is bool doitEtreVerrouille)
                {
                    fenetreUi.MettreAJourSession(m.Session);
                    fenetreUi.SyncFromServer(doitEtreVerrouille);
                }
                break;

            case "branding":
                fenetreUi.MettreAJourBranding(AgentConfig.DepuisMessage(m));
                break;

            case "cmd":
            {
                var (ok, detail) = Executer(m.Commande ?? "", m.Payload, fenetreUi);
                gardien.Envoyer(new MessageCanal { Type = "repcmd", Id = m.Id, Succes = ok, Message = detail });
                break;
            }
        }
    };

    _ = Task.Run(gardien.BoucleAsync);

    // Exécution d'une commande reçue du serveur (via le Gardien)
    (bool ok, string detail) Executer(string type, string? payload, ClientWindow fenetre)
    {
        Log($"Commande reçue : {type}");
        try
        {
            switch (type)
            {
                case "demarrer":
                case "debloquer":
                case "reprendre":
                    fenetre.UnlockScreen();
                    return (true, "Poste déverrouillé");

                case "verrouiller":
                case "suspendre":
                case "fermer":
                    fenetre.LockScreen();
                    return (true, "Poste verrouillé");

                case "desactiver":
                    // L'écran affiche tout de suite « Poste désactivé » (ACTIVER devient INDISPONIBLE)
                    etatServeur = "desactive";
                    PousserInfos();
                    fenetre.LockScreen();   // ferme aussi le widget s'il est ouvert
                    Reveiller();
                    return (true, "Poste désactivé et verrouillé");

                case "arreter":
                    fenetre.DemanderArret(DelaiArretSecondes);
                    return (true, $"Extinction dans {DelaiArretSecondes} s");

                case "recharger":
                {
                    var minutes = LireEntier(payload, "minutes_ajoutees");
                    fenetre.NotifierRecharge(minutes);
                    Reveiller();   // le widget se recale tout de suite sur le temps du serveur
                    return (true, "Temps ajouté");
                }

                case "message":
                {
                    var texte = LireTexte(payload, "message");
                    if (string.IsNullOrWhiteSpace(texte)) return (false, "Message vide");
                    fenetre.RecevoirMessage(texte.Trim());
                    return (true, "Message affiché");
                }

                default:
                    return (false, $"Commande inconnue : {type}");
            }
        }
        catch (Exception ex)
        {
            Log($"Exécution de '{type}' échouée : {ex.Message}");
            return (false, "Erreur d'exécution");
        }
    }

    uiThread.Join();
    Log(">>> Application terminée.");
}
catch (Exception ex)
{
    Log($">>> ERREUR FATALE : {ex}");
}