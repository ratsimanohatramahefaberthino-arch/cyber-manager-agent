using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;


/// <summary>
/// Fenêtre plein écran de verrouillage.
///
/// Principe : on mémorise l'ÉTAT SOUHAITÉ (verrouillé / déverrouillé) et on l'applique dès que
/// la fenêtre est prête. Une demande de verrouillage ne peut donc jamais être perdue, même si
/// WebView2 n'a pas fini de charger : la fenêtre sombre couvre déjà l'écran (échec « fermé »).
///
/// Étape 5 : chat (toasts + panneau du widget + écran de verrouillage), alerte 5 min, fin de forfait
/// avec verrouillage local de sécurité, extinction avec décompte annulable.
/// </summary>
public class ClientWindow : Form
{
    public static ClientWindow? Instance { get; private set; }
    public static Action<string> Journal { get; set; } = Console.WriteLine;

    /// <summary>Appelé quand le client clique sur ACTIVER.</summary>
    public Func<Task<ActivationResult>>? ActivationHandler { get; set; }

    /// <summary>Appelés par le widget flottant (Suspendre / Quitter).</summary>
    public Func<Task<ResultatAction>>? SuspendreHandler { get; set; }
    public Func<Task<ResultatAction>>? QuitterHandler { get; set; }

    /// <summary>Envoie un message du client à l'accueil.</summary>
    public Func<string, Task<ResultatAction>>? MessageHandler { get; set; }

    /// <summary>Exécute l'extinction réelle de Windows ; retourne false en cas d'échec.</summary>
    public Func<bool>? ExtinctionHandler { get; set; }

    /// <summary>Demande un heartbeat immédiat (après recharge, désactivation…).</summary>
    public Action? ReveillerHeartbeat { get; set; }

    public bool IsLocked => _locked;

    private const int MaxHistorique = 60;

    private readonly bool _devMode;
    private readonly WebView2 _web;
    private readonly Label _attente;
    private readonly System.Windows.Forms.Timer _garde;
    private readonly System.Windows.Forms.Timer _finTimer;     // verrouillage local de sécurité après fin de forfait
    private readonly System.Windows.Forms.Timer _arretTimer;   // décompte avant extinction

    private volatile bool _desiredLocked = true;   // au démarrage : verrouillé
    private bool _locked;
    private bool _webReady;
    private bool _activationEnCours;
    private long _graceTicks;                       // ignore les ordres de verrouillage « heartbeat » un court instant

    private long _graceVerrouTicks;                 // ignore les « déverrouille » d'un heartbeat parti juste avant un verrouillage
    private SessionWidget? _widget;
    private ChatPanel? _chat;
    private SessionInfo? _session;                  // dernière session connue (heartbeat)

    private readonly List<MessageChat> _historique = new();
    private int _nonLus;
    private string? _messageVerrou;                 // bandeau affiché sur l'écran de verrouillage (ex : forfait terminé)

    private volatile bool _arretEnCours;
    private int _arretRestant;

    private string _poste = "", _ip = "", _etat = "serveur_injoignable";

    private readonly LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;

    public ClientWindow(bool devMode)
    {
        Instance = this;
        _devMode = devMode;

        Journal("[Diagnostic Window] Début constructeur ClientWindow");

        Text = "Cyber Manager Agent";
        BackColor = Color.FromArgb(11, 18, 38);

        // Ces propriétés sont fixées UNE fois : les modifier plus tard recréerait le handle de la fenêtre
        if (devMode)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 700);
            ShowInTaskbar = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            ShowInTaskbar = false;
            TopMost = true;
        }

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(11, 18, 38)
        };
        Journal("[Diagnostic Window] new WebView2() terminé");
        
        _attente = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(11, 18, 38),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 16),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Cyber Manager — démarrage…"
        };
        Controls.Add(_web);
        Journal("[Diagnostic Window] Controls.Add(_web) terminé");
        Controls.Add(_attente);
        _attente.BringToFront();

        // Remet la fenêtre au premier plan si quelque chose passe devant
        _garde = new System.Windows.Forms.Timer { Interval = 1500 };
        _garde.Tick += (_, _) =>
        {
            if (_locked && !_devMode)
            {
                if (!TopMost) TopMost = true;
                if (!ContainsFocus) { BringToFront(); Activate(); }
            }
        };
        _garde.Start();

        // Fin de forfait : si le serveur n'a pas pris la main dans les 6 s, on verrouille nous-mêmes
        _finTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        _finTimer.Tick += (_, _) =>
        {
            _finTimer.Stop();
            if (!_desiredLocked && _session?.Mode == "forfait")
            {
                Journal("[Session] Forfait terminé : verrouillage local (le serveur n'a pas répondu à temps).");
                LockScreen();
            }
        };

        _arretTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _arretTimer.Tick += (_, _) => TickArret();

        if (!devMode)
        {
            _proc = HookCallback;
            _hook = PoserHook(_proc);
        }
    }

    // ------------------------------------------------------------------
    // API publique (appelable depuis n'importe quel thread)
    // ------------------------------------------------------------------

    public void LockScreen()
    {
        _desiredLocked = true;
        Interlocked.Exchange(ref _graceVerrouTicks, DateTime.UtcNow.AddSeconds(6).Ticks);
        DemanderApplication();
    }

    public void UnlockScreen()
    {
        _desiredLocked = false;
        Interlocked.Exchange(ref _graceTicks, DateTime.UtcNow.AddSeconds(6).Ticks);
        DemanderApplication();
    }

    /// <summary>Applique l'ordre du serveur reçu via heartbeat.</summary>
    public void SyncFromServer(bool doitEtreVerrouille)
    {
        if (doitEtreVerrouille)
        {
            // Un heartbeat parti juste avant l'activation peut revenir avec l'ancien état
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _graceTicks)) return;
            _desiredLocked = true;
            DemanderApplication();   // idempotent : sert aussi d'auto-réparation
        }
        else if (_desiredLocked)
        {
            // Idem dans l'autre sens : un heartbeat parti avant Suspendre/Quitter ne doit pas rouvrir l'écran
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _graceVerrouTicks)) return;
            UnlockScreen();
        }
    }

    public void MettreAJourInfos(string poste, string ip, string etat)
    {
        _poste = poste; _ip = ip; _etat = etat;
        SurUi(PousserInfos);
    }

    /// <summary>Reçoit la session en cours (ou null) depuis le heartbeat ; pilote le widget flottant.</summary>
    public void MettreAJourSession(SessionInfo? session)
    {
        var precedente = _session;
        _session = session;
        SurUi(() =>
        {
            // Forfait rechargé : on annule la fin locale
            if (session?.RestantS is > 0) AnnulerFinForfait();
            // Session terminée : la discussion du client suivant repart de zéro
            if (precedente != null && session == null) ViderHistorique();
            MettreAJourWidget();
        });
    }

    /// <summary>Message de l'accueil (commande « message »).</summary>
    public void RecevoirMessage(string texte) => SurUi(() => SurMessageRecu(texte));

    /// <summary>Notification de recharge (commande « recharger »). Le widget se recale via le heartbeat.</summary>
    public void NotifierRecharge(int? minutes) => SurUi(() =>
    {
        if (_desiredLocked) return;
        var texte = minutes is > 0
            ? $"{minutes} minute(s) ont été ajoutées à votre forfait."
            : "Votre forfait a été rechargé.";
        ToastForm.Afficher("Temps ajouté", texte, NiveauToast.Info, 7000);
    });

    /// <summary>Extinction demandée par le serveur : écran verrouillé + décompte annulable.</summary>
    public void DemanderArret(int secondes)
    {
        _arretRestant = Math.Max(1, secondes);
        _arretEnCours = true;
        Journal($"[Arrêt] Extinction demandée par le serveur dans {_arretRestant} s.");
        SurUi(DemarrerArret);
    }

    // ------------------------------------------------------------------
    // Application de l'état (thread UI uniquement)
    // ------------------------------------------------------------------

    private void SurUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void DemanderApplication()
    {
        if (IsDisposed || !IsHandleCreated) return;   // OnShown appliquera l'état souhaité
        if (InvokeRequired) BeginInvoke(new Action(AppliquerEtatSouhaite));
        else AppliquerEtatSouhaite();
    }

    private void AppliquerEtatSouhaite()
    {
        if (IsDisposed) return;

        if (_desiredLocked)
        {
            var etaitVerrouille = _locked;
            var etaitCache = !Visible;
            if (!_devMode)
            {
                var cible = SystemInformation.VirtualScreen;
                if (Bounds != cible) Bounds = cible;
                TopMost = true;
            }
            if (etaitCache) Show();
            _locked = true;

            if (etaitCache || !_devMode)
            {
                BringToFront();
                Activate();
                if (_webReady) _web.Focus();
            }

            if (!etaitVerrouille)
            {
                // Les messages passent désormais par l'écran de verrouillage
                if (_chat is { IsDisposed: false }) _chat.Hide();
                ToastForm.FermerTous();
                PousserInfos();
            }
        }
        else
        {
            _locked = false;
            _messageVerrou = null;
            TopMost = false;
            if (Visible) Hide();
        }

        MettreAJourWidget();   // écran verrouillé → widget fermé ; déverrouillé + session → widget affiché
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AppliquerEtatSouhaite();
        if (_arretEnCours) DemarrerArret();
        await InitialiserWebViewAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 & co. impossibles tant que le poste est verrouillé
        if (_locked && !_devMode && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _garde.Dispose();
            _finTimer.Dispose();
            _arretTimer.Dispose();
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            if (_widget is { IsDisposed: false }) _widget.Dispose();
            _widget = null;
            if (_chat is { IsDisposed: false }) _chat.Fermer();
            _chat = null;
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------
    // Widget flottant de session (thread UI uniquement)
    // ------------------------------------------------------------------

    private void MettreAJourWidget()
    {
        if (IsDisposed) return;

        var session = _session;
        if (_desiredLocked || session == null)
        {
            FermerWidget();
            return;
        }

        if (_widget == null || _widget.IsDisposed)
        {
            var w = new SessionWidget
            {
                SuspendreHandler = SuspendreDepuisWidgetAsync,
                QuitterHandler = QuitterDepuisWidgetAsync
            };
            w.CinqMinutes += SurCinqMinutes;
            w.ForfaitTermine += SurForfaitTermine;
            w.MessagesDemandes += BasculerChat;
            w.NonLus = _nonLus;
            w.FormClosed += (_, _) => { if (ReferenceEquals(_widget, w)) _widget = null; };
            _widget = w;
        }
        _widget.MettreAJour(session);
    }

    private void FermerWidget()
    {
        if (_chat is { IsDisposed: false }) _chat.Hide();
        var w = _widget;
        _widget = null;
        if (w != null && !w.IsDisposed) w.Fermer();
    }

    private async Task<ResultatAction> SuspendreDepuisWidgetAsync()
    {
        var handler = SuspendreHandler;
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas encore prêt. Réessayez.");

        var r = await handler();
        if (r.Succes)
        {
            _session = null;
            LockScreen();   // verrouille l'écran et ferme le widget
        }
        return r;
    }

    private async Task<ResultatAction> QuitterDepuisWidgetAsync()
    {
        var handler = QuitterHandler;
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas encore prêt. Réessayez.");

        var r = await handler();
        if (r.Succes)
        {
            // Le client doit connaître son montant avant que l'écran ne se verrouille
            if (r.Montant is > 0)
            {
                var texte = $"Session terminée.\n\nMontant à payer : {SessionWidget.FormaterAr(r.Montant.Value)}\n\nMerci de régler à l'accueil.";
                if (_widget is { IsDisposed: false } w)
                    MessageBox.Show(w, texte, "Cyber Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(texte, "Cyber Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            _session = null;
            ViderHistorique();
            LockScreen();
        }
        return r;
    }

    // ------------------------------------------------------------------
    // Alertes de forfait (thread UI)
    // ------------------------------------------------------------------

    private void SurCinqMinutes()
    {
        Journal("[Session] Il reste 5 minutes ou moins sur le forfait.");
        ToastForm.Afficher("Il reste 5 minutes",
            "Votre forfait se termine bientôt. Pensez à enregistrer votre travail.",
            NiveauToast.Alerte, 10000);
    }

    private void SurForfaitTermine()
    {
        Journal("[Session] Forfait terminé (détecté localement).");
        _messageVerrou = "Votre forfait est terminé. Adressez-vous à l'accueil pour recharger ou régler.";
        ToastForm.Afficher("Forfait terminé",
            "Votre forfait est terminé. Le poste va être verrouillé.",
            NiveauToast.Danger, 6000);

        // Le serveur est la référence : on lui laisse 6 s pour verrouiller (ou prolonger) ;
        // sinon (serveur injoignable) c'est l'agent qui verrouille.
        _finTimer.Stop();
        _finTimer.Start();
        ReveillerHeartbeat?.Invoke();
    }

    private void AnnulerFinForfait()
    {
        if (_finTimer.Enabled) Journal("[Session] Forfait prolongé : verrouillage annulé.");
        _finTimer.Stop();
        if (!_desiredLocked) _messageVerrou = null;
    }

    // ------------------------------------------------------------------
    // Chat (thread UI)
    // ------------------------------------------------------------------

    private void SurMessageRecu(string texte)
    {
        var m = new MessageChat(false, texte, DateTime.Now.ToString("HH:mm"));
        AjouterHistorique(m);
        Journal($"[Chat] Message reçu de l'accueil ({texte.Length} caractères).");

        if (_desiredLocked) return;   // l'écran de verrouillage ouvre sa fenêtre de discussion

        if (_chat is { IsDisposed: false, Visible: true }) return;   // déjà sous les yeux du client

        _nonLus++;
        if (_widget is { IsDisposed: false } w) w.NonLus = _nonLus;
        ToastForm.Afficher("Message de l'accueil", texte, NiveauToast.Message, 9000, OuvrirChat);
    }

    private void AjouterHistorique(MessageChat m)
    {
        _historique.Add(m);
        if (_historique.Count > MaxHistorique) _historique.RemoveAt(0);
        if (_chat is { IsDisposed: false }) _chat.Ajouter(m);
        Poster(new { type = "message", moi = m.DeMoi, texte = m.Texte, heure = m.Heure });
    }

    private void ViderHistorique()
    {
        _historique.Clear();
        _nonLus = 0;
        if (_chat is { IsDisposed: false }) { _chat.Hide(); _chat.Vider(); }
        if (_widget is { IsDisposed: false } w) w.NonLus = 0;
        Poster(new { type = "historique_vide" });
    }

    private ChatPanel AssurerChat()
    {
        if (_chat == null || _chat.IsDisposed)
        {
            var c = new ChatPanel { EnvoyerHandler = EnvoyerMessageClientAsync };
            c.Charger(_historique);
            _chat = c;
        }
        return _chat;
    }

    private void BasculerChat()
    {
        if (_chat is { IsDisposed: false, Visible: true }) _chat.Hide();
        else OuvrirChat();
    }

    private void OuvrirChat()
    {
        if (_desiredLocked || IsDisposed) return;

        var c = AssurerChat();
        Rectangle? ancre = _widget is { IsDisposed: false } w ? w.Bounds : null;
        c.Placer(ancre);

        _nonLus = 0;
        if (_widget is { IsDisposed: false } w2) w2.NonLus = 0;
        c.AfficherAnime();
    }

    /// <summary>Envoi d'un message du client (panneau du widget ou écran de verrouillage).</summary>
    private async Task<ResultatAction> EnvoyerMessageClientAsync(string texte)
    {
        texte = (texte ?? "").Trim();
        if (texte.Length == 0) return new ResultatAction(false, "Message vide.");
        if (texte.Length > 500) texte = texte[..500];

        var handler = MessageHandler;
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas encore prêt. Réessayez.");

        var r = await handler(texte);
        if (r.Succes) AjouterHistorique(new MessageChat(true, texte, DateTime.Now.ToString("HH:mm")));
        return r;
    }

    // ------------------------------------------------------------------
    // Extinction (thread UI)
    // ------------------------------------------------------------------

    private void DemarrerArret()
    {
        if (!_arretEnCours || IsDisposed) return;

        _desiredLocked = true;
        Interlocked.Exchange(ref _graceVerrouTicks, DateTime.UtcNow.AddSeconds(6).Ticks);
        AppliquerEtatSouhaite();

        Poster(new { type = "arret", restant = _arretRestant });
        _arretTimer.Stop();
        _arretTimer.Start();
    }

    private void TickArret()
    {
        if (!_arretEnCours) { _arretTimer.Stop(); return; }

        _arretRestant--;
        if (_arretRestant > 0)
        {
            Poster(new { type = "arret", restant = _arretRestant });
            return;
        }

        _arretTimer.Stop();
        _arretEnCours = false;
        Poster(new { type = "arret", restant = 0 });

        var ok = false;
        try { ok = ExtinctionHandler?.Invoke() ?? false; }
        catch (Exception ex) { Journal($"[Arrêt] Erreur : {ex.Message}"); }

        // Succès : on reste verrouillé jusqu'à ce que Windows ferme l'agent (pas de bureau visible)
        if (!ok) Poster(new { type = "arret", restant = -2 });
    }

    private void AnnulerArret()
    {
        if (!_arretEnCours) return;
        _arretTimer.Stop();
        _arretEnCours = false;
        Journal("[Arrêt] Extinction annulée depuis le poste.");
        Poster(new { type = "arret", restant = -1 });
    }

    // ------------------------------------------------------------------
    // WebView2
    // ------------------------------------------------------------------

    private async Task InitialiserWebViewAsync()
    {
        while (!IsDisposed)
        {
            try
            {
                var dossier = Path.Combine(Path.GetDirectoryName(AgentConfig.ConfigPath)!, "WebView2");
                Journal($"[Diagnostic WebView2] Dossier UDF : {dossier}");
                Journal("[Diagnostic WebView2] Appel CoreWebView2Environment.CreateAsync...");
                
                var env = await CoreWebView2Environment.CreateAsync(null, dossier);
                
                Journal("[Diagnostic WebView2] CreateAsync terminé. Appel EnsureCoreWebView2Async...");
                await _web.EnsureCoreWebView2Async(env);
                Journal("[Diagnostic WebView2] EnsureCoreWebView2Async terminé.");

                var s = _web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = _devMode;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;

                _web.CoreWebView2.NewWindowRequested += (_, a) => a.Handled = true;
                _web.CoreWebView2.NavigationStarting += (_, a) => { if (a.IsUserInitiated) a.Cancel = true; };
                _web.CoreWebView2.WebMessageReceived += OnWebMessage;
                _web.NavigationCompleted += (_, a) =>
                {
                    if (!a.IsSuccess) { Journal($"[WebView2] Navigation échouée : {a.WebErrorStatus}"); return; }
                    _webReady = true;
                    _attente.Visible = false;
                    PousserInfos();
                    foreach (var m in _historique)
                        Poster(new { type = "message", moi = m.DeMoi, texte = m.Texte, heure = m.Heure });
                    if (_arretEnCours) Poster(new { type = "arret", restant = _arretRestant });
                    if (_desiredLocked) _web.Focus();
                    Journal("[WebView2] Écran de verrouillage chargé.");
                };

                _web.NavigateToString(LockScreenHtml.Page);
                return;
            }
            catch (Exception ex)
            {
                Journal($"[WebView2] Initialisation impossible : {ex.Message}. Nouvel essai dans 5 s.");
                _attente.Text = "Cyber Manager\nInitialisation de l'écran de verrouillage…\n(WebView2 Runtime requis)";
                await Task.Delay(5000);
            }
        }
    }

    private void PousserInfos() =>
        Poster(new { type = "infos", poste = _poste, ip = _ip, etat = _etat, bandeau = _messageVerrou });

    private void Poster(object donnees)
    {
        try
        {
            if (_webReady && _web.CoreWebView2 != null)
                _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(donnees));
        }
        catch (Exception ex) { Journal($"[WebView2] Envoi impossible : {ex.Message}"); }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); } catch { return; }

        if (message == "activer") { await ActiverAsync(); return; }
        if (message == "annuler_arret") { AnnulerArret(); return; }
        if (message.StartsWith("msg:", StringComparison.Ordinal)) await EnvoyerDepuisVerrouAsync(message[4..]);
    }

    private async Task EnvoyerDepuisVerrouAsync(string texte)
    {
        try
        {
            var r = await EnvoyerMessageClientAsync(texte);
            Poster(new { type = "message_statut", ok = r.Succes, erreur = r.Succes ? "" : r.Message });
        }
        catch (Exception ex)
        {
            Journal($"[Chat] Erreur d'envoi : {ex.Message}");
            Poster(new { type = "message_statut", ok = false, erreur = "Erreur inattendue. Réessayez." });
        }
    }

    private async Task ActiverAsync()
    {
        if (_activationEnCours) return;

        _activationEnCours = true;
        try
        {
            Poster(new { type = "activation", statut = "encours" });

            var handler = ActivationHandler;
            var res = handler == null
                ? ActivationResult.Echec("L'agent n'est pas encore prêt. Réessayez.")
                : await handler();

            if (res.Succes)
            {
                Poster(new { type = "activation", statut = "ok", message = res.Message });
                await Task.Delay(700);   // laisse voir « ACTIVÉ » un instant
                UnlockScreen();
            }
            else
            {
                Poster(new { type = "activation", statut = "erreur", message = res.Message });
            }
        }
        catch (Exception ex)
        {
            Journal($"[Activation] Erreur : {ex.Message}");
            Poster(new { type = "activation", statut = "erreur", message = "Erreur inattendue. Réessayez." });
        }
        finally { _activationEnCours = false; }
    }

    // ------------------------------------------------------------------
    // Blocage clavier (Win, Alt+Tab, Alt+F4, Alt+Échap, Ctrl+Échap) — inactif en mode DEV
    // Ctrl+Alt+Suppr ne peut pas être bloqué par une application (limite Windows).
    // ------------------------------------------------------------------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private static IntPtr PoserHook(LowLevelKeyboardProc proc)
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        using var m = p.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(m!.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _locked)
        {
            var msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var vk = Marshal.ReadInt32(lParam);          // vkCode
                var flags = Marshal.ReadInt32(lParam, 8);    // flags
                var alt = (flags & 0x20) != 0;               // LLKHF_ALTDOWN
                var ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;

                var bloquer =
                    vk is 0x5B or 0x5C or 0x5D                    // Win gauche/droite, touche Menu
                    || (alt && vk is (0x09 or 0x1B or 0x73 or 0x20)) // Alt+Tab / Échap / F4 / Espace
                    || (ctrl && vk == 0x1B);                       // Ctrl+Échap
                if (bloquer) return (IntPtr)1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}