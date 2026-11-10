using System.Globalization;
using System.Runtime.InteropServices;

public class SessionWidget : Form
{
    public Func<Task<ResultatAction>>? SuspendreHandler { get; set; }
    public Func<Task<ResultatAction>>? QuitterHandler { get; set; }
    public Func<string, Task<ResultatAction>>? MessageHandler { get; set; }

    public event Action? CinqMinutes;
    public event Action? ForfaitTermine;

    private static readonly Color Fond = Color.FromArgb(11, 18, 38);
    private static readonly Color Texte = Color.FromArgb(226, 232, 240);
    private static readonly Color Discret = Color.FromArgb(148, 163, 184);
    private static readonly Color Ambre = Color.FromArgb(245, 158, 11);
    private static readonly Color VioletClair = Color.FromArgb(167, 139, 250);
    private static readonly Color Violet = Color.FromArgb(124, 58, 237);
    private static readonly Color Rouge = Color.FromArgb(220, 38, 38);
    private static readonly Color Ardoise = Color.FromArgb(51, 65, 85);

    private readonly float _k;
    private readonly Label _lblCaption, _lblTemps, _lblMontant, _lblDebut;
    private readonly Button _btnCacher, _btnSuspendre, _btnQuitter, _btnEnvoyer;
    private readonly RichTextBox _chatBox;
    private readonly TextBox _chatSaisie;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;

    private int _sessionId = -1;
    private string _mode = "postpaye";
    private int _ecouleBase;
    private int? _restantBase;
    private int _montant;
    private string _debut = "—";
    private long _recuTick = Environment.TickCount64;

    private bool _modeReduit, _occupe, _positionne, _fermetureAutorisee;
    private bool _alerte5, _finSignalee;
    private int _ticks;

    public SessionWidget()
    {
        _k = Math.Max(1f, DeviceDpi / 96f);

        Text = "Cyber Manager";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Fond;
        ClientSize = new Size(S(340), S(360));
        DoubleBuffered = true;
        Deplacable(this);

        // Section 1: Les chronos et montants (Grands et lisibles)
        Etiquette("CYBER MANAGER", 10f, FontStyle.Bold, VioletClair, 0, 8, 340, 20);
        _lblCaption = Etiquette("TEMPS ÉCOULÉ", 10f, FontStyle.Bold, Discret, 0, 32, 340, 18);
        _lblTemps = Etiquette("00:00:00", 22f, FontStyle.Bold, Ambre, 0, 52, 340, 40, "Consolas");
        _lblMontant = Etiquette("", 14f, FontStyle.Bold, Texte, 0, 96, 340, 24);
        _lblDebut = Etiquette("", 10f, FontStyle.Regular, Discret, 0, 124, 340, 18);

        // Section 2: Chat
        _chatBox = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(S(8), S(148), S(324), S(120))
        };
        _chatBox.BackColor = Color.FromArgb(19, 28, 58);
        _chatBox.ForeColor = Texte;
        Deplacable(_chatBox);
        Controls.Add(_chatBox);

        _chatSaisie = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(19, 28, 58), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), PlaceholderText = "Écrire...",
            Bounds = new Rectangle(S(8), S(272), S(260), S(28))
        };
        _chatSaisie.KeyDown += async (_, e) => {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await EnvoyerChatAsync(); }
        };
        Controls.Add(_chatSaisie);

        _btnEnvoyer = new Button
        {
            Text = "➤", FlatStyle = FlatStyle.Flat, BackColor = Violet, ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Bounds = new Rectangle(S(270), S(272), S(62), S(28)),
            TabStop = false, Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        _btnEnvoyer.FlatAppearance.BorderSize = 0;
        _btnEnvoyer.Click += async (_, _) => await EnvoyerChatAsync();
        Controls.Add(_btnEnvoyer);

        // Section 3: Boutons d'action (toujours en bas)
        _btnCacher = Bouton("Réduire", Ardoise, 8, (_, _) => BasculerReduction());
        _btnSuspendre = Bouton("Suspendre", Violet, 116, async (_, _) => await ExecuterAsync(SuspendreHandler, "Suspendre votre session ?", "Suspendre"));
        _btnQuitter = Bouton("Quitter", Rouge, 224, async (_, _) => await ExecuterAsync(QuitterHandler, "Terminer votre session ?", "Quitter"));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Afficher le compteur", null, (_, _) => BasculerReduction(false));
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application, Text = "Cyber Manager", ContextMenuStrip = menu, Visible = false
        };
        _tray.DoubleClick += (_, _) => BasculerReduction(false);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            Rafraichir();
            if (++_ticks % 5 == 0 && Visible && IsHandleCreated)
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        };
        _timer.Start();

        Rafraichir();
    }

    public void MettreAJour(SessionInfo s)
    {
        var nouvelle = s.Id != _sessionId;
        _sessionId = s.Id;
        _mode = s.Mode ?? "postpaye";
        _ecouleBase = Math.Max(0, s.EcouleS);
        _restantBase = s.RestantS;
        _montant = Math.Max(0, s.MontantActuel);
        _debut = FormaterHeure(s.DateHeureDebut);
        _recuTick = Environment.TickCount64;

        if (nouvelle)
        {
            _modeReduit = false; _alerte5 = false; _finSignalee = false;
            _chatBox.Clear();
            // Recaler le widget en bas
            _positionne = false;
            Afficher();
        }
        Rafraichir();
        if (!_modeReduit) Afficher();
    }

    public void AjouterMessageChat(MessageChat m)
    {
        _chatBox.Select(_chatBox.TextLength, 0);
        _chatBox.SelectionFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        _chatBox.SelectionColor = Discret;
        _chatBox.SelectedText = (m.DeMoi ? "Moi" : "Accueil") + " · " + m.Heure + "\n";
        
        _chatBox.SelectionFont = new Font("Segoe UI", 9f);
        _chatBox.SelectionColor = m.DeMoi ? VioletClair : Texte;
        _chatBox.SelectedText = m.Texte + "\n\n";
        _chatBox.ScrollToCaret();
    }

    public static string FormaterAr(int montant) =>
        montant.ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))
            .Replace('\u202f', ' ').Replace('\u00a0', ' ') + " Ar";

    public void Fermer() { _fermetureAutorisee = true; Close(); }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (!_fermetureAutorisee && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; } base.OnFormClosing(e); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Color.FromArgb(110, 139, 92, 246)); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); } base.Dispose(disposing); }

    private void Afficher()
    {
        if (IsDisposed) return;
        if (!_positionne)
        {
            var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            // Toujours collé en bas à droite
            Location = new Point(zone.Right - Width - S(12), zone.Bottom - Height);
            _positionne = true;
        }
        _tray.Visible = false;
        if (!Visible) Show();
        TopMost = true;
    }

    private void BasculerReduction(bool? forceReduit = null)
    {
        _modeReduit = forceReduit ?? !_modeReduit;
        
        _lblCaption.Visible = !_modeReduit;
        _lblTemps.Visible = !_modeReduit;
        _lblDebut.Visible = !_modeReduit;
        _chatBox.Visible = !_modeReduit;
        _chatSaisie.Visible = !_modeReduit;
        _btnEnvoyer.Visible = !_modeReduit;
        
        _lblMontant.Visible = true;
        _btnCacher.Text = _modeReduit ? "Afficher" : "Réduire";

        var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;

        if (_modeReduit)
        {
            ClientSize = new Size(S(340), S(150));
            _lblMontant.Bounds = new Rectangle(S(0), S(32), S(340), S(30));
            _lblMontant.TextAlign = ContentAlignment.MiddleCenter;
            _lblMontant.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            
            _btnCacher.Bounds = new Rectangle(S(8), S(90), S(100), S(45));
            _btnSuspendre.Bounds = new Rectangle(S(116), S(90), S(100), S(45));
            _btnQuitter.Bounds = new Rectangle(S(224), S(90), S(100), S(45));
        }
        else
        {
            ClientSize = new Size(S(340), S(360));
            _lblMontant.Bounds = new Rectangle(S(0), S(96), S(340), S(24));
            _lblMontant.TextAlign = ContentAlignment.MiddleCenter;
            _lblMontant.Font = new Font("Segoe UI", 14f, FontStyle.Bold);

            _btnCacher.Bounds = new Rectangle(S(8), S(308), S(100), S(45));
            _btnSuspendre.Bounds = new Rectangle(S(116), S(308), S(100), S(45));
            _btnQuitter.Bounds = new Rectangle(S(224), S(308), S(100), S(45));
        }

        // Recaler en bas
        Location = new Point(zone.Right - Width - S(12), zone.Bottom - Height);

        if (!Visible) Afficher();
    }

    private void Rafraichir()
    {
        var delta = (int)Math.Max(0, (Environment.TickCount64 - _recuTick) / 1000);
        var forfait = _mode == "forfait" && _restantBase.HasValue;

        _lblCaption.Text = forfait ? "TEMPS RESTANT" : "TEMPS ÉCOULÉ";
        var secondes = forfait ? Math.Max(0, _restantBase!.Value - delta) : _ecouleBase + delta;
        _lblTemps.Text = Duree(secondes);
        _lblTemps.ForeColor = forfait && secondes <= 300 ? Color.FromArgb(248, 113, 113) : Ambre;

        if (_mode == "libre") _lblMontant.Text = "Sans facturation";
        else if (_mode == "forfait") _lblMontant.Text = $"Forfait : {FormaterAr(_montant)}";
        else {
            // Postpayé temps réel
            var min = (int)(secondes / 60);
            var tarif = 20; // Fallback si le serveur n'envoie pas le tarif, sinon on prend le montant renvoyé par le serveur
            var montantCalcule = Math.Max(300, min * tarif); 
            
            // Si le serveur a envoyé un montant plus grand, on l'utilise
            var affichage = Math.Max(_montant, montantCalcule);
            _lblMontant.Text = "À payer : " + FormaterAr(affichage);
        }

        _lblDebut.Text = "Début : " + _debut;

        if (!forfait) return;

        if (secondes > 300) _alerte5 = false;
        else if (secondes > 0 && !_alerte5) { _alerte5 = true; CinqMinutes?.Invoke(); }

        if (secondes > 0) _finSignalee = false;
        else if (!_finSignalee) { _finSignalee = true; ForfaitTermine?.Invoke(); }
    }

    private static string Duree(int secondes) { var t = TimeSpan.FromSeconds(Math.Max(0, secondes)); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"; }
    private static string FormaterHeure(string? iso) => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "—";

    private async Task EnvoyerChatAsync()
    {
        var texte = _chatSaisie.Text.Trim();
        if (texte.Length == 0 || MessageHandler == null || _occupe) return;
        _chatSaisie.Clear();
        await MessageHandler(texte);
    }

    private async Task ExecuterAsync(Func<Task<ResultatAction>>? handler, string question, string titre)
    {
        if (_occupe || handler == null) return;
        var choix = MessageBox.Show(this, question, titre, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (choix != DialogResult.Yes || IsDisposed) return;

        _occupe = true;
        try {
            var r = await handler();
            if (!IsDisposed && !r.Succes) MessageBox.Show(this, r.Message, titre, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } catch (Exception ex) { ClientWindow.Journal($"[Widget] Erreur : {ex.Message}"); }
        finally { if (!IsDisposed) _occupe = false; }
    }

    private int S(int valeur) => (int)Math.Round(valeur * _k);

    private Label Etiquette(string texte, float taille, FontStyle style, Color couleur, int x, int y, int l, int h, string famille = "Segoe UI")
    {
        var lbl = new Label { Text = texte, AutoSize = false, Bounds = new Rectangle(S(x), S(y), S(l), S(h)), TextAlign = ContentAlignment.MiddleCenter, ForeColor = couleur, BackColor = Color.Transparent, Font = new Font(famille, taille, style) };
        Deplacable(lbl); Controls.Add(lbl); return lbl;
    }

    private Button Bouton(string texte, Color fond, int x, EventHandler clic)
    {
        var b = new Button { Text = texte, FlatStyle = FlatStyle.Flat, BackColor = fond, ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Bounds = new Rectangle(S(x), S(308), S(100), S(45)), TabStop = false, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; b.Click += clic; Controls.Add(b); return b;
    }

    private void Deplacable(Control c) => c.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left || !IsHandleCreated) return; ReleaseCapture(); SendMessage(Handle, 0x00A1, 2, 0); };

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}