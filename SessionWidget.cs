using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Petit widget flottant affiché pendant une session : temps, montant, heure de début.
/// Le serveur reste la référence : le widget reçoit un instantané à chaque heartbeat
/// et ne fait qu'avancer le chrono localement entre deux heartbeats.
/// Étape 5 : alerte « 5 minutes restantes », détection locale de fin de forfait, bouton messages.
/// Doit être créé et manipulé sur le thread UI (ClientWindow s'en charge).
/// </summary>
public class SessionWidget : Form
{
    public Func<Task<ResultatAction>>? SuspendreHandler { get; set; }
    public Func<Task<ResultatAction>>? QuitterHandler { get; set; }

    /// <summary>Déclenché une fois quand il reste 5 minutes ou moins (forfait).</summary>
    public event Action? CinqMinutes;
    /// <summary>Déclenché une fois quand le temps du forfait est écoulé localement.</summary>
    public event Action? ForfaitTermine;
    /// <summary>Clic sur le bouton ✉.</summary>
    public event Action? MessagesDemandes;

    private static readonly Color Fond = Color.FromArgb(11, 18, 38);
    private static readonly Color Texte = Color.FromArgb(226, 232, 240);
    private static readonly Color Discret = Color.FromArgb(148, 163, 184);
    private static readonly Color Ambre = Color.FromArgb(245, 158, 11);
    private static readonly Color VioletClair = Color.FromArgb(167, 139, 250);
    private static readonly Color Violet = Color.FromArgb(124, 58, 237);
    private static readonly Color Rouge = Color.FromArgb(220, 38, 38);
    private static readonly Color Ardoise = Color.FromArgb(51, 65, 85);

    private readonly float _k;                       // facteur d'échelle DPI
    private readonly Label _lblCaption, _lblTemps, _lblMontant, _lblDebut;
    private readonly Button _btnCacher, _btnSuspendre, _btnQuitter, _btnMsg;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;

    private int _sessionId = -1;
    private string _mode = "postpaye";
    private int _ecouleBase;
    private int? _restantBase;
    private int _montant;
    private string _debut = "—";
    private long _recuTick = Environment.TickCount64;

    private bool _masque, _occupe, _positionne, _fermetureAutorisee;
    private bool _alerte5, _finSignalee;
    private int _ticks, _nonLus;

    public SessionWidget()
    {
        _k = Math.Max(1f, DeviceDpi / 96f);

        Text = "Cyber Manager";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Fond;
        ClientSize = new Size(S(200), S(180));
        DoubleBuffered = true;
        Deplacable(this);

        Etiquette("CYBER MANAGER", 8f, FontStyle.Bold, VioletClair, 0, 4, 200, 18);
        _lblCaption = Etiquette("TEMPS ÉCOULÉ", 7.5f, FontStyle.Bold, Discret, 0, 24, 200, 16);
        _lblTemps = Etiquette("00:00:00", 20f, FontStyle.Bold, Ambre, 0, 38, 200, 36, "Consolas");
        _lblMontant = Etiquette("", 9f, FontStyle.Bold, Texte, 0, 76, 200, 20);
        _lblDebut = Etiquette("", 8.5f, FontStyle.Regular, Discret, 0, 96, 200, 18);

        _btnCacher = Bouton("Cacher", Ardoise, 8, (_, _) => Cacher());
        _btnSuspendre = Bouton("Suspendre", Violet, 71, async (_, _) => await ExecuterAsync(
            SuspendreHandler,
            "Suspendre votre session ?\n\nLe chronomètre sera mis en pause et l'écran sera verrouillé.\nAdressez-vous à l'accueil pour reprendre.",
            "Suspendre la session"));
        _btnQuitter = Bouton("Quitter", Rouge, 134, async (_, _) => await ExecuterAsync(
            QuitterHandler,
            "Terminer votre session ?\n\nVotre consommation sera arrêtée et l'écran sera verrouillé.",
            "Quitter la session"));

        // Bouton messages (coin haut-droit) ; au premier plan car les étiquettes le recouvrent
        _btnMsg = new Button
        {
            Text = "✉",
            FlatStyle = FlatStyle.Flat,
            BackColor = Ardoise,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Symbol", 8f, FontStyle.Bold),
            Bounds = new Rectangle(S(150), S(4), S(44), S(18)),
            TabStop = false,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        _btnMsg.FlatAppearance.BorderSize = 0;
        _btnMsg.Click += (_, _) => MessagesDemandes?.Invoke();
        Controls.Add(_btnMsg);
        _btnMsg.BringToFront();

        // Icône de la zone de notification : visible uniquement quand le widget est caché
        var menu = new ContextMenuStrip();
        menu.Items.Add("Afficher le compteur", null, (_, _) => Reafficher());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Cyber Manager — session en cours",
            ContextMenuStrip = menu,
            Visible = false
        };
        _tray.DoubleClick += (_, _) => Reafficher();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            Rafraichir();
            // Garde le widget au-dessus des autres fenêtres (sans lui donner le focus)
            if (++_ticks % 5 == 0 && Visible && IsHandleCreated)
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        };
        _timer.Start();

        Rafraichir();
    }

    // ------------------------------------------------------------------
    // API publique (thread UI)
    // ------------------------------------------------------------------

    /// <summary>Nombre de messages non lus (affiché sur le bouton ✉).</summary>
    public int NonLus
    {
        set
        {
            _nonLus = Math.Max(0, value);
            _btnMsg.Text = _nonLus > 0 ? $"✉ {_nonLus}" : "✉";
            _btnMsg.BackColor = _nonLus > 0 ? Ambre : Ardoise;
        }
    }

    /// <summary>Reçoit l'instantané de session du serveur (heartbeat).</summary>
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
            _masque = false;     // nouvelle session : on réaffiche toujours
            _alerte5 = false;
            _finSignalee = false;
        }
        Rafraichir();
        if (!_masque) Afficher();
    }

    /// <summary>Ferme définitivement le widget (verrouillage, fin de session…).</summary>
    public void Fermer()
    {
        _fermetureAutorisee = true;
        if (!IsDisposed) Close();
    }

    public static string FormaterAr(int montant) =>
        montant.ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))
            .Replace('\u202f', ' ').Replace('\u00a0', ' ') + " Ar";

    // ------------------------------------------------------------------
    // Comportement de la fenêtre
    // ------------------------------------------------------------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW : absent d'Alt+Tab
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 ne ferme pas le widget : seule la fermeture pilotée par l'agent est acceptée
        if (!_fermetureAutorisee && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(110, 139, 92, 246));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Afficher()
    {
        if (IsDisposed) return;
        if (!_positionne)
        {
            var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            Location = new Point(zone.Right - Width - S(12), zone.Bottom - Height - S(12));
            _positionne = true;
        }
        _tray.Visible = false;
        if (!Visible) Show();
        TopMost = true;
    }

    private void Cacher()
    {
        _masque = true;
        _tray.Visible = true;
        Hide();
    }

    private void Reafficher()
    {
        _masque = false;
        Afficher();
    }

    // ------------------------------------------------------------------
    // Affichage + alertes
    // ------------------------------------------------------------------

    private void Rafraichir()
    {
        var delta = (int)Math.Max(0, (Environment.TickCount64 - _recuTick) / 1000);
        var forfait = _mode == "forfait" && _restantBase.HasValue;

        _lblCaption.Text = forfait ? "TEMPS RESTANT" : "TEMPS ÉCOULÉ";
        var secondes = forfait ? Math.Max(0, _restantBase!.Value - delta) : _ecouleBase + delta;
        _lblTemps.Text = Duree(secondes);
        _lblTemps.ForeColor = forfait && secondes <= 300 ? Color.FromArgb(248, 113, 113) : Ambre;

        _lblMontant.Text = _mode == "libre" ? "Sans facturation" : "À payer : " + FormaterAr(_montant);
        _lblDebut.Text = "Début : " + _debut;

        if (!forfait) return;

        // Alerte « 5 minutes restantes » (une fois ; réarmée si le forfait est rechargé)
        if (secondes > 300) _alerte5 = false;
        else if (secondes > 0 && !_alerte5)
        {
            _alerte5 = true;
            CinqMinutes?.Invoke();
        }

        // Fin de forfait détectée localement (même si le serveur est injoignable)
        if (secondes > 0) _finSignalee = false;
        else if (!_finSignalee)
        {
            _finSignalee = true;
            ForfaitTermine?.Invoke();
        }
    }

    private static string Duree(int secondes)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, secondes));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string FormaterHeure(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : "—";

    // ------------------------------------------------------------------
    // Actions Suspendre / Quitter
    // ------------------------------------------------------------------

    private async Task ExecuterAsync(Func<Task<ResultatAction>>? handler, string question, string titre)
    {
        if (_occupe || handler == null) return;

        var choix = MessageBox.Show(this, question, titre,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (choix != DialogResult.Yes || IsDisposed) return;

        DefinirOccupe(true);
        try
        {
            var r = await handler();
            if (IsDisposed) return;   // le widget a été fermé entre-temps (succès ou ordre du serveur)
            if (!r.Succes)
                MessageBox.Show(this, r.Message, titre, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ClientWindow.Journal($"[Widget] Erreur : {ex.Message}");
            if (!IsDisposed)
                MessageBox.Show(this, "Erreur inattendue. Réessayez.", titre, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed) DefinirOccupe(false);
        }
    }

    private void DefinirOccupe(bool occupe)
    {
        _occupe = occupe;
        _btnCacher.Enabled = !occupe;
        _btnSuspendre.Enabled = !occupe;
        _btnQuitter.Enabled = !occupe;
        Cursor = occupe ? Cursors.WaitCursor : Cursors.Default;
    }

    // ------------------------------------------------------------------
    // Construction de l'interface
    // ------------------------------------------------------------------

    private int S(int valeur) => (int)Math.Round(valeur * _k);

    private Label Etiquette(string texte, float taille, FontStyle style, Color couleur,
        int x, int y, int l, int h, string famille = "Segoe UI")
    {
        var lbl = new Label
        {
            Text = texte,
            AutoSize = false,
            Bounds = new Rectangle(S(x), S(y), S(l), S(h)),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = couleur,
            BackColor = Color.Transparent,
            Font = new Font(famille, taille, style)
        };
        Deplacable(lbl);
        Controls.Add(lbl);
        return lbl;
    }

    private Button Bouton(string texte, Color fond, int x, EventHandler clic)
    {
        var b = new Button
        {
            Text = texte,
            FlatStyle = FlatStyle.Flat,
            BackColor = fond,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Bounds = new Rectangle(S(x), S(128), S(58), S(42)),
            TabStop = false,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += clic;
        Controls.Add(b);
        return b;
    }

    private void Deplacable(Control c) =>
        c.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !IsHandleCreated) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, 2, 0);   // WM_NCLBUTTONDOWN + HTCAPTION
        };

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}