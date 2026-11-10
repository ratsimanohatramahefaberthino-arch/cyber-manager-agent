using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Fenêtre plein écran de verrouillage.
/// </summary>
public class ClientWindow : Form
{
    public static ClientWindow? Instance { get; private set; }
    public static Action<string> Journal { get; set; } = Console.WriteLine;

    public Func<Task<ActivationResult>>? ActivationHandler { get; set; }
    public Func<Task<ResultatAction>>? SuspendreHandler { get; set; }
    public Func<Task<ResultatAction>>? QuitterHandler { get; set; }
    public Func<string, Task<ResultatAction>>? MessageHandler { get; set; }
    public Func<bool>? ExtinctionHandler { get; set; }
    public Action? ReveillerHeartbeat { get; set; }

    public bool IsLocked => _locked;

    private const int MaxHistorique = 60;

    private readonly bool _devMode;
    private readonly WebView2 _web;
    private readonly Label _attente;
    private readonly System.Windows.Forms.Timer _garde;
    private readonly System.Windows.Forms.Timer _finTimer;
    private readonly System.Windows.Forms.Timer _arretTimer;

    private volatile bool _desiredLocked = true;
    private bool _locked;
    private bool _webReady;
    private bool _activationEnCours;
    private long _graceTicks;
    private long _graceVerrouTicks;

    private SessionWidget? _widget;
    private ChatPanel? _chat;
    private SessionInfo? _session;

    private readonly List<MessageChat> _historique = new();
    private string? _messageVerrou;

    private volatile bool _arretEnCours;
    private int _arretRestant;

    private string _poste = "", _ip = "", _etat = "serveur_injoignable";

    private readonly LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;

    public ClientWindow(bool devMode)
    {
        Instance = this;
        _devMode = devMode;

        Text = "Cyber Manager Agent";
        BackColor = Color.FromArgb(11, 18, 38);

        if (devMode)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 700);
            ShowInTaskbar = true;
            TopMost = false; // Mode fenêtré classique
            MaximizeBox = true;
            MinimizeBox = true;
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
        Controls.Add(_attente);
        _attente.BringToFront();

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

        _finTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        _finTimer.Tick += (_, _) =>
        {
            _finTimer.Stop();
            if (!_desiredLocked && _session?.Mode == "forfait")
            {
                Journal("[Session] Forfait terminé : verrouillage local.");
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

    public void SyncFromServer(bool doitEtreVerrouille)
    {
        if (doitEtreVerrouille)
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _graceTicks)) return;
            _desiredLocked = true;
            DemanderApplication();
        }
        else if (_desiredLocked)
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _graceVerrouTicks)) return;
            UnlockScreen();
        }
    }

    public void MettreAJourInfos(string poste, string ip, string etat)
    {
        _poste = poste; _ip = ip; _etat = etat;
        SurUi(PousserInfos);
    }

    public void MettreAJourSession(SessionInfo? session)
    {
        var precedente = _session;
        _session = session;
        SurUi(() =>
        {
            if (session?.RestantS is > 0) AnnulerFinForfait();
            if (precedente != null && session == null) ViderHistorique();
            MettreAJourWidget();
        });
    }

    public void RecevoirMessage(string texte) => SurUi(() => SurMessageRecu(texte));

    public void NotifierRecharge(int? minutes) => SurUi(() =>
    {
        if (_desiredLocked) return;
        var texte = minutes is > 0 ? $"{minutes} minute(s) ont été ajoutées." : "Votre forfait a été rechargé.";
        ToastForm.Afficher("Temps ajouté", texte, NiveauToast.Info, 7000);
    });

    public void DemanderArret(int secondes)
    {
        _arretRestant = Math.Max(1, secondes);
        _arretEnCours = true;
        Journal($"[Arrêt] Extinction dans {_arretRestant} s.");
        SurUi(DemarrerArret);
    }

    private void SurUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void DemanderApplication()
    {
        if (IsDisposed || !IsHandleCreated) return;
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

        MettreAJourWidget();
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
                QuitterHandler = QuitterDepuisWidgetAsync,
                MessageHandler = EnvoyerMessageClientAsync
            };
            w.CinqMinutes += SurCinqMinutes;
            w.ForfaitTermine += SurForfaitTermine;
            
            foreach(var m in _historique) w.AjouterMessageChat(m);
            
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
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas prêt.");

        var r = await handler();
        if (r.Succes)
        {
            _session = null;
            LockScreen();
        }
        return r;
    }

    private async Task<ResultatAction> QuitterDepuisWidgetAsync()
    {
        var handler = QuitterHandler;
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas prêt.");

        var r = await handler();
        if (r.Succes)
        {
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

    private void SurCinqMinutes()
    {
        ToastForm.Afficher("Il reste 5 minutes", "Votre forfait se termine bientôt.", NiveauToast.Alerte, 10000);
    }

    private void SurForfaitTermine()
    {
        _messageVerrou = "Votre forfait est terminé.";
        ToastForm.Afficher("Forfait terminé", "Le poste va être verrouillé.", NiveauToast.Danger, 6000);
        _finTimer.Stop();
        _finTimer.Start();
        ReveillerHeartbeat?.Invoke();
    }

    private void AnnulerFinForfait()
    {
        _finTimer.Stop();
        if (!_desiredLocked) _messageVerrou = null;
    }

    private void SurMessageRecu(string texte)
    {
        var m = new MessageChat(false, texte, DateTime.Now.ToString("HH:mm"));
        AjouterHistorique(m);

        if (_desiredLocked) return; 
        if (_chat is { IsDisposed: false, Visible: true }) return;
        
        ToastForm.Afficher("Message de l'accueil", texte, NiveauToast.Message, 9000, OuvrirChat);
    }

    private void AjouterHistorique(MessageChat m)
    {
        _historique.Add(m);
        if (_historique.Count > MaxHistorique) _historique.RemoveAt(0);
        if (_chat is { IsDisposed: false }) _chat.Ajouter(m);
        if (_widget is { IsDisposed: false } w) w.AjouterMessageChat(m);
        Poster(new { type = "message", moi = m.DeMoi, texte = m.Texte, heure = m.Heure });
    }

    private void ViderHistorique()
    {
        _historique.Clear();
        if (_chat is { IsDisposed: false }) { _chat.Hide(); _chat.Vider(); }
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

    private void OuvrirChat()
    {
        if (_desiredLocked || IsDisposed) return;
        var c = AssurerChat();
        Rectangle? ancre = _widget is { IsDisposed: false } w ? w.Bounds : null;
        c.Placer(ancre);
        c.AfficherAnime();
    }

    private async Task<ResultatAction> EnvoyerMessageClientAsync(string texte)
    {
        texte = (texte ?? "").Trim();
        if (texte.Length == 0) return new ResultatAction(false, "Message vide.");
        if (texte.Length > 500) texte = texte[..500];

        var handler = MessageHandler;
        if (handler == null) return new ResultatAction(false, "L'agent n'est pas prêt.");

        var r = await handler(texte);
        if (r.Succes) AjouterHistorique(new MessageChat(true, texte, DateTime.Now.ToString("HH:mm")));
        return r;
    }

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
        catch { }
        if (!ok) Poster(new { type = "arret", restant = -2 });
    }

    private void AnnulerArret()
    {
        if (!_arretEnCours) return;
        _arretTimer.Stop();
        _arretEnCours = false;
        Poster(new { type = "arret", restant = -1 });
    }

    private async Task InitialiserWebViewAsync()
    {
        while (!IsDisposed)
        {
            try
            {
                var dossier = Path.Combine(Path.GetDirectoryName(AgentConfig.ConfigPath)!, "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, dossier);
                await _web.EnsureCoreWebView2Async(env);

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
                    if (!a.IsSuccess) return;
                    _webReady = true;
                    _attente.Visible = false;
                    PousserInfos();
                    foreach (var m in _historique)
                        Poster(new { type = "message", moi = m.DeMoi, texte = m.Texte, heure = m.Heure });
                    if (_arretEnCours) Poster(new { type = "arret", restant = _arretRestant });
                    if (_desiredLocked) _web.Focus();
                };

                _web.NavigateToString(LockScreenHtml.Page);
                return;
            }
            catch
            {
                _attente.Text = "Cyber Manager\nInitialisation de l'écran…";
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
        catch { }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); } catch { return; }

        if (message == "activer") { await ActiverAsync(); return; }
        if (message == "annuler_arret") { AnnulerArret(); return; }
        if (message.StartsWith("msg:", StringComparison.Ordinal)) await EnvoyerDepuisVerrouAsync(message[4..]);
        
        if (message == "reprendre" && SuspendreHandler != null) 
        { 
            await SuspendreHandler(); 
            return; 
        }
        if (message == "quitter" && QuitterHandler != null)
        {
            var r = await QuitterHandler();
            if (r.Succes && r.Montant is > 0)
                MessageBox.Show($"Montant à payer : {SessionWidget.FormaterAr(r.Montant.Value)}", "Session terminée");
            return;
        }
    }

    private async Task EnvoyerDepuisVerrouAsync(string texte)
    {
        try
        {
            var r = await EnvoyerMessageClientAsync(texte);
            Poster(new { type = "message_statut", ok = r.Succes, erreur = r.Succes ? "" : r.Message });
        }
        catch
        {
            Poster(new { type = "message_statut", ok = false, erreur = "Erreur inattendue." });
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
            var res = handler == null ? ActivationResult.Echec("L'agent n'est pas prêt.") : await handler();

            if (res.Succes)
            {
                Poster(new { type = "activation", statut = "ok", message = res.Message });
                await Task.Delay(700);
                UnlockScreen();
            }
            else
            {
                Poster(new { type = "activation", statut = "erreur", message = res.Message });
            }
        }
        catch
        {
            Poster(new { type = "activation", statut = "erreur", message = "Erreur inattendue." });
        }
        finally { _activationEnCours = false; }
    }

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
                var vk = Marshal.ReadInt32(lParam);
                var flags = Marshal.ReadInt32(lParam, 8);
                var alt = (flags & 0x20) != 0;
                var ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;

                var bloquer = vk is 0x5B or 0x5C or 0x5D 
                    || (alt && vk is (0x09 or 0x1B or 0x73 or 0x20)) 
                    || (ctrl && vk == 0x1B);
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