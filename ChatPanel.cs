using System.Runtime.InteropServices;

/// <summary>Un message de la discussion client ↔ accueil.</summary>
public record MessageChat(bool DeMoi, string Texte, string Heure);

/// <summary>
/// Panneau de discussion affiché au-dessus du widget flottant : historique, saisie, envoi.
/// Apparition animée, fermable (✕ ou Échap = masqué, pas détruit). Thread UI uniquement.
/// </summary>
public class ChatPanel : Form
{
    public Func<string, Task<ResultatAction>>? EnvoyerHandler { get; set; }

    private static readonly Color Fond = Color.FromArgb(11, 18, 38);
    private static readonly Color Champ = Color.FromArgb(19, 28, 58);
    private static readonly Color Texte = Color.FromArgb(226, 232, 240);
    private static readonly Color Discret = Color.FromArgb(148, 163, 184);
    private static readonly Color VioletClair = Color.FromArgb(167, 139, 250);
    private static readonly Color Violet = Color.FromArgb(124, 58, 237);

    private readonly float _k;
    private readonly RichTextBox _liste;
    private readonly TextBox _saisie;
    private readonly Button _envoyer;
    private readonly Label _etat;
    private readonly Font _fPetit, _fNormal;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private Point _cible;
    private int _etape;
    private bool _occupe, _fermetureAutorisee;

    public ChatPanel()
    {
        _k = Math.Max(1f, DeviceDpi / 96f);

        Text = "Cyber Manager — Messages";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Fond;
        DoubleBuffered = true;
        ClientSize = new Size(S(300), S(330));
        Opacity = 0;

        _fPetit = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _fNormal = new Font("Segoe UI", 9.5f);

        var titre = new Label
        {
            Text = "MESSAGES — ACCUEIL", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(S(12), S(6), S(220), S(22)), ForeColor = VioletClair,
            BackColor = Color.Transparent, Font = new Font("Segoe UI", 8f, FontStyle.Bold)
        };
        Deplacable(titre);
        Deplacable(this);

        var fermer = new Button
        {
            Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Discret, BackColor = Fond,
            Bounds = new Rectangle(Width - S(34), S(4), S(28), S(24)), TabStop = false,
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Font = new Font("Segoe UI Symbol", 8.5f)
        };
        fermer.FlatAppearance.BorderSize = 0;
        fermer.Click += (_, _) => Hide();

        _liste = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, TabStop = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, Font = _fNormal,
            Bounds = new Rectangle(S(8), S(34), Width - S(16), Height - S(34) - S(80))
        };
        _liste.BackColor = Champ;
        _liste.ForeColor = Texte;

        _etat = new Label
        {
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(252, 165, 165),
            BackColor = Color.Transparent, Font = new Font("Segoe UI", 8f),
            Bounds = new Rectangle(S(10), Height - S(76), Width - S(20), S(18))
        };

        _saisie = new TextBox
        {
            MaxLength = 500, BorderStyle = BorderStyle.FixedSingle, BackColor = Champ, ForeColor = Color.White,
            Font = _fNormal, PlaceholderText = "Écrire à l'accueil…",
            Bounds = new Rectangle(S(8), Height - S(54), Width - S(16) - S(76), S(26))
        };
        _saisie.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = EnvoyerAsync(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
        };

        _envoyer = new Button
        {
            Text = "Envoyer", FlatStyle = FlatStyle.Flat, BackColor = Violet, ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TabStop = false, Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Bounds = new Rectangle(Width - S(8) - S(68), Height - S(55), S(68), S(28))
        };
        _envoyer.FlatAppearance.BorderSize = 0;
        _envoyer.Click += async (_, _) => await EnvoyerAsync();

        Controls.AddRange(new Control[] { titre, fermer, _liste, _etat, _saisie, _envoyer });

        _anim.Tick += (_, _) => Animer();
    }

    // ------------------------------------------------------------------
    // API (thread UI)
    // ------------------------------------------------------------------

    public void Charger(IEnumerable<MessageChat> historique)
    {
        _liste.Clear();
        foreach (var m in historique) Ajouter(m);
    }

    public void Vider() => _liste.Clear();

    public void Ajouter(MessageChat m)
    {
        _liste.Select(_liste.TextLength, 0);
        _liste.SelectionAlignment = m.DeMoi ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _liste.SelectionFont = _fPetit;
        _liste.SelectionColor = Discret;
        _liste.SelectedText = (m.DeMoi ? "Moi" : "Accueil") + " · " + m.Heure + "\n";

        _liste.SelectionAlignment = m.DeMoi ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _liste.SelectionFont = _fNormal;
        _liste.SelectionColor = m.DeMoi ? VioletClair : Texte;
        _liste.SelectedText = m.Texte + "\n\n";

        _liste.SelectionStart = _liste.TextLength;
        _liste.ScrollToCaret();
    }

    /// <summary>Calcule la position : juste au-dessus du widget (ou coin bas-droit s'il n'y en a pas).</summary>
    public void Placer(Rectangle? widget)
    {
        var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        var x = widget.HasValue ? widget.Value.Right - Width : zone.Right - Width - S(12);
        var y = widget.HasValue ? widget.Value.Top - Height - S(8) : zone.Bottom - Height - S(12);
        x = Math.Clamp(x, zone.Left, Math.Max(zone.Left, zone.Right - Width));
        y = Math.Clamp(y, zone.Top, Math.Max(zone.Top, zone.Bottom - Height));
        _cible = new Point(x, y);
        if (Visible) Location = _cible;
    }

    public void AfficherAnime()
    {
        if (IsDisposed) return;
        if (Visible) { _saisie.Focus(); return; }
        _etat.Text = "";
        Opacity = 0;
        Location = new Point(_cible.X, _cible.Y + S(24));
        _etape = 0;
        Show();
        _anim.Start();
    }

    public void Fermer()
    {
        _fermetureAutorisee = true;
        if (!IsDisposed) Close();
    }

    // ------------------------------------------------------------------

    private async Task EnvoyerAsync()
    {
        var texte = _saisie.Text.Trim();
        if (texte.Length == 0 || _occupe || EnvoyerHandler == null) return;

        _occupe = true;
        _envoyer.Enabled = false;
        _etat.Text = "";
        try
        {
            var r = await EnvoyerHandler(texte);
            if (IsDisposed) return;
            if (r.Succes) _saisie.Clear();
            else _etat.Text = r.Message;
        }
        catch
        {
            if (!IsDisposed) _etat.Text = "Erreur inattendue. Réessayez.";
        }
        finally
        {
            _occupe = false;
            if (!IsDisposed) { _envoyer.Enabled = true; _saisie.Focus(); }
        }
    }

    private void Animer()
    {
        if (IsDisposed) return;
        _etape++;
        var t = Math.Min(1.0, _etape / 12.0);
        var e = 1 - Math.Pow(1 - t, 3);
        Opacity = e;
        Location = new Point(_cible.X, _cible.Y + (int)Math.Round(S(24) * (1 - e)));
        if (t >= 1) _anim.Stop();
    }

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
        // Alt+F4 masque simplement le panneau
        if (!_fermetureAutorisee && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
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
            _anim.Dispose();
            _fPetit.Dispose();
            _fNormal.Dispose();
        }
        base.Dispose(disposing);
    }

    private int S(int v) => (int)Math.Round(v * _k);

    private void Deplacable(Control c) =>
        c.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !IsHandleCreated) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, 2, 0);   // WM_NCLBUTTONDOWN + HTCAPTION
        };

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}