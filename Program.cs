// ============================================================================
//  Cyber Manager Agent — point d'entrée
//  Options :  --server=http://IP:PORT   (mémorisée dans la config)
//             --nom="POSTE 3"           (nom proposé à l'enregistrement)
//  Variable : AGENT_DEV=1  → mode développement (fenêtre normale, pas de blocage clavier)
// ============================================================================

// --- Instance unique -------------------------------------------------------
using var mutexUnique = new Mutex(true, @"Global\CyberManagerAgent", out var premiereInstance);
if (!premiereInstance) return;

// --- Journal (dans le dossier de données, jamais le token) -----------------
var dossierDonnees = Path.GetDirectoryName(AgentConfig.ConfigPath)!;
var logPath = Path.Combine(dossierDonnees, "agent.log");
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

static int Backoff(int echecs) => Math.Min(30_000, 2_000 << Math.Min(echecs, 4));   // 4s, 8s, 16s, 30s…

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
const string ArgsExtinction = "/s /t 0";   // ajouter /f pour forcer la fermeture des applications bloquantes

var devMode = Environment.GetEnvironmentVariable("AGENT_DEV") == "1";

try
{
    Log($">>> Démarrage de l'agent Cyber Manager {ApiClient.Version} — mode {(devMode ? "DEV" : "PRODUCTION")}");

    // --- Configuration + options en ligne de commande ----------------------
    var config = AgentConfig.LoadOrCreate();

    foreach (var arg in args)
    {
        if (arg.StartsWith("--server=", StringComparison.OrdinalIgnoreCase))
        {
            var url = arg[9..].Trim().TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https"))
            {
                config.ServerUrl = url;
                config.Save();
            }
            else Log($"Option --server ignorée (URL invalide) : {url}");
        }
        else if (arg.StartsWith("--nom=", StringComparison.OrdinalIgnoreCase))
        {
            var nom = arg[6..].Trim();
            if (nom.Length is > 0 and <= 100) { config.NomPoste = nom; config.Save(); }
        }
    }

    Log($"Config : poste='{config.NomPoste}', serveur='{config.ServerUrl}', id='{config.IdentifiantUnique[..8]}…'");
    Log($"Dossier de données : {dossierDonnees}");

    var api = new ApiClient(config);

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
    Log("Fenêtre prête. Démarrage de la communication avec le serveur.");

    // Heartbeat immédiat demandé (recharge, désactivation, fin de forfait…)
    var reveil = 0;
    void Reveiller() => Volatile.Write(ref reveil, 1);
    fenetreUi.ReveillerHeartbeat = Reveiller;

    // État partagé entre les boucles
    var serveurOk = false;
    var etatServeur = "serveur_injoignable";

    // Clic sur ACTIVER → appel serveur
    fenetreUi.ActivationHandler = async () =>
    {
        Log("Activation demandée par le client…");
        var r = await api.ActiverAsync();
        Log(r.Succes
            ? $"Activation OK{(r.Avertissement != null ? " (avertissement : " + r.Avertissement + ")" : "")}"
            : $"Activation refusée : {r.Message}");
        return r;
    };

    // Widget : Suspendre / Quitter → appel serveur
    fenetreUi.SuspendreHandler = async () =>
    {
        Log("Suspension/Reprise demandée…");
        // On regarde si le poste est actuellement suspendu pour savoir s'il faut reprendre
        // (Le client n'a que le bouton Reprendre quand il est suspendu)
        if (etatServeur == "suspendu") {
            var r = await api.ReprendreSessionAsync(); // À ajouter dans ApiClient.cs
            Log(r.Succes ? "Reprise OK" : $"Reprise refusée : {r.Message}");
            return r;
        } else {
            var r = await api.SuspendreSessionAsync();
            Log(r.Succes ? "Suspension OK" : $"Suspension refusée : {r.Message}");
            return r;
        }
    };

    fenetreUi.QuitterHandler = async () =>
    {
        Log("Fermeture de session demandée…");
        var r = await api.FermerSessionAsync();
        Log(r.Succes ? "Fermeture OK" : $"Fermeture refusée : {r.Message}");
        return r;
    };

    // Chat client → accueil
    fenetreUi.MessageHandler = async texte =>
    {
        var r = await api.EnvoyerMessageAsync(texte);
        Log(r.Succes
            ? $"Message envoyé à l'accueil ({texte.Length} caractères)."
            : $"Message non envoyé : {r.Message}");
        return r;
    };

    // Extinction réelle de Windows (appelée après le décompte, si non annulé)
    fenetreUi.ExtinctionHandler = () =>
    {
        try
        {
            Log($"Extinction du poste : shutdown {ArgsExtinction}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", ArgsExtinction)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            Log($"Extinction impossible : {ex.Message}");
            return false;
        }
    };



    void PousserInfos() =>
        fenetreUi.MettreAJourInfos(config.NomPoste, api.Reseau.Ip, serveurOk ? etatServeur : "serveur_injoignable");

    void DefinirServeur(bool ok)
    {
        if (ok != serveurOk)
        {
            serveurOk = ok;
            Log(ok ? "Serveur joignable." : "Serveur injoignable — nouvelles tentatives avec délai croissant.");
        }
    }

    PousserInfos();

    // --- Boucle Heartbeat + handshake (5 s, backoff si panne) --------------
    _ = Task.Run(async () =>
    {
        await Task.Delay(2000);
        var echecs = 0;
        while (true)
        {
            var delai = 5_000;
            try
            {
                if (string.IsNullOrEmpty(config.AgentToken))
                {
                    var st = await api.HandshakeAsync();
                    if (st == ApiStatut.Ok)
                    {
                        Log($"Handshake OK (poste '{config.NomPoste}').");
                        echecs = 0;
                        delai = 300;
                    }
                    else
                    {
                        echecs++;
                        DefinirServeur(false);
                        delai = Backoff(echecs);
                        Log($"Handshake échoué ({st}). Nouvel essai dans {delai / 1000}s.");
                    }
                }
                else
                {
                    var r = await api.SendHeartbeatAsync();
                    switch (r.Statut)
                    {
                        case ApiStatut.Ok:
                            echecs = 0;
                            DefinirServeur(true);
                            etatServeur = r.Donnees!.Etat ?? "disponible";

                            if (!string.IsNullOrWhiteSpace(r.Donnees.NomPoste) && r.Donnees.NomPoste != config.NomPoste)
                            {
                                config.NomPoste = r.Donnees.NomPoste;
                                config.Save();
                            }
                            fenetreUi.MettreAJourSession(r.Donnees.Session);
                            fenetreUi.SyncFromServer(r.Donnees.DoitEtreVerrouille);
                            break;

                        case ApiStatut.NonAutorise:
                            Log("Token refusé par le serveur : nouveau handshake.");
                            config.AgentToken = "";
                            delai = 200;
                            break;

                        default:   // injoignable ou erreur serveur : on garde l'état courant (mode dégradé)
                            echecs++;
                            DefinirServeur(false);
                            delai = Backoff(echecs);
                            break;
                    }
                }
                PousserInfos();
            }
            catch (Exception ex) { Log($"Heartbeat : erreur inattendue : {ex.Message}"); }

            // Attente par tranches : un réveil (recharge, désactivation…) déclenche un heartbeat immédiat
            for (var attente = 0; attente < delai && Volatile.Read(ref reveil) == 0; attente += 250)
                await Task.Delay(250);
            Interlocked.Exchange(ref reveil, 0);
        }
    });

    // --- Boucle Commandes (3 s, backoff si panne) ---------------------------
    _ = Task.Run(async () =>
    {
        await Task.Delay(3_000);
        var echecs = 0;
        while (true)
        {
            var delai = 3_000;
            try
            {
                if (!string.IsNullOrEmpty(config.AgentToken))
                {
                    var r = await api.GetCommandsAsync();
                    if (r.Statut == ApiStatut.Ok)
                    {
                        echecs = 0;
                        foreach (var cmd in r.Donnees!)
                        {
                            Log($"Commande reçue : {cmd.Type} (id={cmd.Id})");
                            var (ok, detail) = Executer(cmd, fenetreUi);
                            await api.ConfirmerCommandeAsync(cmd.Id, ok, detail);
                        }
                    }
                    else if (r.Statut != ApiStatut.NonAutorise)
                    {
                        echecs++;
                        delai = Backoff(echecs);
                    }
                }
            }
            catch (Exception ex) { Log($"Commandes : erreur inattendue : {ex.Message}"); }

            await Task.Delay(delai);
        }
    });

    // Exécution d'une commande reçue du serveur
    (bool ok, string detail) Executer(AgentCommand cmd, ClientWindow fenetre)
    {
        try
        {
            switch (cmd.Type)
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
                    var minutes = LireEntier(cmd.Payload, "minutes_ajoutees");
                    fenetre.NotifierRecharge(minutes);
                    Reveiller();   // le widget se recale tout de suite sur le temps du serveur
                    return (true, "Temps ajouté");
                }

                case "message":
                {
                    var texte = LireTexte(cmd.Payload, "message");
                    if (string.IsNullOrWhiteSpace(texte)) return (false, "Message vide");
                    fenetre.RecevoirMessage(texte.Trim());
                    return (true, "Message affiché");
                }

                default:
                    return (false, $"Commande inconnue : {cmd.Type}");
            }
        }
        catch (Exception ex)
        {
            Log($"Exécution de '{cmd.Type}' échouée : {ex.Message}");
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