using System.Drawing.Drawing2D;
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
    private readonly VoyantServeur _voyant;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;

    private int _sessionId = -1;
    private string _mode = "postpaye";
    private int _ecouleBase;
    private int? _restantBase;
    private int _montant;
    private int _forfaitTotal;
    private TarifInfo _tarif = new();
    private string _debut = "—";
    private long _recuTick = Environment.TickCount64;

    private bool _modeReduit, _occupe, _positionne, _fermetureAutorisee;
    private bool _alerte5, _finSignalee;
    private int _ticks;

    private static Icon? _iconeApp;
    
    private static string _nomCyber = "Cyber Manager";
    private static string _couleurHex = "#7c3aed";
    private static byte[]? _logoOctets;
    private Label? _lblTitre;

    public SessionWidget()
    {
        _k = Math.Max(1f, DeviceDpi / 96f);

        Text = _nomCyber;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;                      // icône dans la barre des tâches (clic = ramener le widget)
        Icon = IconeApp();                         // icône personnalisée (écran violet) au lieu de l'engrenage par défaut
        MinimizeBox = true;
        TopMost = false;                           // ne passe jamais devant Word, Chrome, etc.
        BackColor = Fond;
        ClientSize = new Size(S(240), S(250));
        DoubleBuffered = true;
        Deplacable(this);

        _lblTitre = Etiquette(_nomCyber.ToUpperInvariant(), 8f, FontStyle.Bold, VioletClair, 0, 4, 240, 18);
        _lblCaption = Etiquette("TEMPS ÉCOULÉ", 8f, FontStyle.Bold, Discret, 0, 24, 240, 16);
        _lblTemps = Etiquette("00:00:00", 16f, FontStyle.Bold, Ambre, 0, 42, 240, 30, "Consolas");
        _lblMontant = Etiquette("", 10f, FontStyle.Bold, Texte, 0, 74, 240, 20);
        _lblDebut = Etiquette("", 8f, FontStyle.Regular, Discret, 0, 96, 240, 16);

        // Voyant « Serveur : connecté / injoignable » (visible aussi en mode réduit)
        _voyant = new VoyantServeur(7.5f) { Bounds = new Rectangle(S(6), S(114), S(228), S(20)) };
        Controls.Add(_voyant);

        _chatBox = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Segoe UI", 8f),
            Bounds = new Rectangle(S(6), S(138), S(228), S(42))
        };
        _chatBox.BackColor = Color.FromArgb(19, 28, 58);
        _chatBox.ForeColor = Texte;
        Deplacable(_chatBox);
        Controls.Add(_chatBox);

        _chatSaisie = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(19, 28, 58), ForeColor = Color.White,
            Font = new Font("Segoe UI", 8f), PlaceholderText = "Écrire...",
            Bounds = new Rectangle(S(6), S(184), S(180), S(22))
        };
        _chatSaisie.KeyDown += async (_, e) => {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await EnvoyerChatAsync(); }
        };
        Controls.Add(_chatSaisie);

        _btnEnvoyer = new Button
        {
            Text = "➤", FlatStyle = FlatStyle.Flat, BackColor = Violet, ForeColor = Color.White,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold), Bounds = new Rectangle(S(188), S(184), S(46), S(22)),
            TabStop = false, Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        _btnEnvoyer.FlatAppearance.BorderSize = 0;
        _btnEnvoyer.Click += async (_, _) => await EnvoyerChatAsync();
        Controls.Add(_btnEnvoyer);

        _btnCacher = Bouton("Réduire", Ardoise, 6, (_, _) => BasculerReduction());
        _btnSuspendre = Bouton("Suspendre", Violet, 82, async (_, _) => await ExecuterAsync(SuspendreHandler, "Suspendre votre session ?", "Suspendre"));
        _btnQuitter = Bouton("Quitter", Rouge, 158, async (_, _) => await ExecuterAsync(QuitterHandler, "Terminer votre session ?", "Quitter"));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Afficher le compteur", null, (_, _) => BasculerReduction(false));
        _tray = new NotifyIcon
        {
            Icon = IconeApp(), Text = "Cyber Manager", ContextMenuStrip = menu, Visible = false
        };
        _tray.DoubleClick += (_, _) => BasculerReduction(false);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => { Rafraichir(); if (Visible) PlacerBasDroite(); };
        _timer.Start();

        Rafraichir();
    }

    /// <summary>État de la liaison avec le serveur (voyant). « immediat » : pas de délai avant le rouge.</summary>
    public void DefinirServeur(bool connecte, bool immediat = false)
    {
        if (IsDisposed) return;
        _voyant.Definir(connecte, immediat);
    }

    /// <summary>Applique l'identité du cyber (appelé par ClientWindow, une seule fois par changement).</summary>
    public static void DefinirBranding(string nom, string couleurHex, byte[]? logo)
    {
        _nomCyber = string.IsNullOrWhiteSpace(nom) ? "Cyber Manager" : nom;
        _couleurHex = string.IsNullOrWhiteSpace(couleurHex) ? "#7c3aed" : couleurHex;
        _logoOctets = logo;
        _iconeApp = null;   // l'icône sera redessinée au prochain appel
    }

    /// <summary>Met à jour un widget déjà ouvert (titre, icône barre des tâches et zone de notification).</summary>
    public void AppliquerBranding()
    {
        if (IsDisposed) return;
        Text = _nomCyber;
        if (_lblTitre != null) _lblTitre.Text = _nomCyber.ToUpperInvariant();
        try { Icon = IconeApp(); _tray.Icon = IconeApp(); } catch { }
    }

    private static Color CouleurAccent()
    {
        try { return ColorTranslator.FromHtml(_couleurHex); }
        catch { return Color.FromArgb(124, 58, 237); }
    }

    private static Color Assombrir(Color c, float f) =>
        Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

    /// <summary>
    /// Icône de l'application : le logo du cyber (sur fond blanc arrondi) s'il existe,
    /// sinon un écran blanc sur fond dégradé à la couleur du cyber. Mise en cache.
    /// </summary>
    public static Icon IconeApp()
    {
        if (_iconeApp != null) return _iconeApp;
        try
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                var rect = new Rectangle(2, 2, 60, 60);
                const int r = 14;
                using var path = new GraphicsPath();
                path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();

                var logoDessine = false;
                if (_logoOctets is { Length: > 0 })
                {
                    try
                    {
                        using var ms = new MemoryStream(_logoOctets);
                        using var img = Image.FromStream(ms);
                        using (var blanc = new SolidBrush(Color.White)) g.FillPath(blanc, path);
                        var ratio = Math.Min(52f / img.Width, 52f / img.Height);
                        var w = img.Width * ratio;
                        var h = img.Height * ratio;
                        g.DrawImage(img, (64 - w) / 2f, (64 - h) / 2f, w, h);
                        logoDessine = true;
                    }
                    catch { logoDessine = false; }
                }

                if (!logoDessine)
                {
                    var accent = CouleurAccent();
                    using (var fond = new LinearGradientBrush(rect, accent, Assombrir(accent, 0.65f), 45f))
                        g.FillPath(fond, path);

                    using var stylo = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawRectangle(stylo, 14, 15, 36, 24);   // écran
                    g.DrawLine(stylo, 32, 40, 32, 47);        // pied
                    g.DrawLine(stylo, 22, 48, 42, 48);        // socle
                }
            }

            var hIcone = bmp.GetHicon();
            try
            {
                using var temporaire = Icon.FromHandle(hIcone);
                _iconeApp = (Icon)temporaire.Clone();
            }
            finally { DestroyIcon(hIcone); }
        }
        catch
        {
            _iconeApp = SystemIcons.Application;
        }
        return _iconeApp;
    }

    public void MettreAJour(SessionInfo s)
    {
        var nouvelle = s.Id != _sessionId;
        var premiereFois = _sessionId == -1;   // widget tout juste créé : son historique vient d'être rechargé
        _sessionId = s.Id;
        _mode = s.Mode ?? "postpaye";
        _ecouleBase = Math.Max(0, s.EcouleS);
        _restantBase = s.RestantS;
        _montant = Math.Max(0, s.MontantActuel);
        _forfaitTotal = s.ForfaitTotal;
        _tarif = s.Tarif ?? new TarifInfo();
        _debut = FormaterHeure(s.DateHeureDebut);
        _recuTick = Environment.TickCount64;

        if (nouvelle)
        {
            _modeReduit = false; _alerte5 = false; _finSignalee = false;
            // CORRECTIF : on n'efface PAS le chat quand le widget vient d'être (re)créé
            // (reprise après suspension) : l'historique conservé par ClientWindow vient d'y être injecté.
            if (!premiereFois) _chatBox.Clear();
            _positionne = false;
            Afficher();
        }
        Rafraichir();
        if (!_modeReduit) Afficher();
    }

    public void AjouterMessageChat(MessageChat m)
    {
        _chatBox.Select(_chatBox.TextLength, 0);
        _chatBox.SelectionFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _chatBox.SelectionColor = Discret;
        _chatBox.SelectedText = (m.DeMoi ? "Moi" : "Accueil") + " · " + m.Heure + "\n";

        _chatBox.SelectionFont = new Font("Segoe UI", 8f);
        _chatBox.SelectionColor = m.DeMoi ? VioletClair : Texte;
        _chatBox.SelectedText = m.Texte + "\n\n";
        _chatBox.ScrollToCaret();
    }

    public static string FormaterAr(int montant) =>
        montant.ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))
            .Replace('\u202f', ' ').Replace('\u00a0', ' ') + " Ar";

    public void Fermer() { _fermetureAutorisee = true; Close(); }

    protected override bool ShowWithoutActivation => true;   // à l'apparition, ne vole pas le focus

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000;       // WS_MINIMIZEBOX : le clic sur l'icône de la barre des tâches peut réduire / restaurer
            cp.ExStyle |= 0x00040000;     // WS_EX_APPWINDOW : bouton dans la barre des tâches
            cp.ExStyle &= ~0x80;          // retire WS_EX_TOOLWINDOW (qui cachait le widget de la barre des tâches)
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { if (!_fermetureAutorisee && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; } base.OnFormClosing(e); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (IsHandleCreated) PlacerBasDroite(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Color.FromArgb(110, 139, 92, 246)); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); } base.Dispose(disposing); }

    /// <summary>Position fixe : coin inférieur DROIT de la zone de travail (au-dessus de la barre des tâches).</summary>
    private void PlacerBasDroite()
    {
        if (IsDisposed || WindowState != FormWindowState.Normal) return;
        var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        var cible = new Point(zone.Right - Width, zone.Bottom - Height);
        if (Location != cible) Location = cible;
    }

    private void Afficher()
    {
        if (IsDisposed) return;
        if (!_positionne)
        {
            PlacerBasDroite();
            _positionne = true;
        }
        _tray.Visible = false;
        if (!Visible) Show();
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
        _voyant.Visible = true;   // le voyant reste visible, même réduit
        _btnCacher.Text = _modeReduit ? "Afficher" : "Réduire";

        if (_modeReduit)
        {
            ClientSize = new Size(S(240), S(102));
            _lblMontant.Bounds = new Rectangle(S(0), S(6), S(240), S(24));
            _lblMontant.TextAlign = ContentAlignment.MiddleCenter;
            _voyant.Bounds = new Rectangle(S(6), S(32), S(228), S(20));

            _btnCacher.Bounds = new Rectangle(S(6), S(58), S(72), S(35));
            _btnSuspendre.Bounds = new Rectangle(S(82), S(58), S(72), S(35));
            _btnQuitter.Bounds = new Rectangle(S(158), S(58), S(72), S(35));
        }
        else
        {
            ClientSize = new Size(S(240), S(250));
            _lblMontant.Bounds = new Rectangle(S(0), S(74), S(240), S(20));
            _lblMontant.TextAlign = ContentAlignment.MiddleCenter;
            _voyant.Bounds = new Rectangle(S(6), S(114), S(228), S(20));

            _btnCacher.Bounds = new Rectangle(S(6), S(210), S(72), S(35));
            _btnSuspendre.Bounds = new Rectangle(S(82), S(210), S(72), S(35));
            _btnQuitter.Bounds = new Rectangle(S(158), S(210), S(72), S(35));
        }

        PlacerBasDroite();
        if (!Visible) Afficher();
    }

    /// <summary>
    /// Montant temps réel : STRICTEMENT max(minimum, minutes × prix/minute).
    /// Le paramètre « arrondi » des tarifs n'est volontairement PAS appliqué ici
    /// (même formule que le panneau admin et que PosteSessionService::montant() côté Laravel).
    /// </summary>
    private int MontantPourMinutes(int minutes)
    {
        var t = _tarif;
        long brut = (long)Math.Max(0, minutes) * t.ParMinute;
        return (int)Math.Max((long)t.Minimum, brut);
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
        else if (_mode == "forfait") _lblMontant.Text = $"Forfait : {FormaterAr(_forfaitTotal)}";
        else {
            var min = secondes / 60;
            _lblMontant.Text = "À payer : " + FormaterAr(MontantPourMinutes(min));
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
        var b = new Button { Text = texte, FlatStyle = FlatStyle.Flat, BackColor = fond, ForeColor = Color.White, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Bounds = new Rectangle(S(x), S(210), S(72), S(35)), TabStop = false, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; b.Click += clic; Controls.Add(b); return b;
    }

    // Le widget est FIXE (bas-droite) : le déplacement par glisser-déposer est volontairement désactivé.
    private void Deplacable(Control c) { }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool DestroyIcon(IntPtr hIcon);
}