using System.Runtime.InteropServices;

public enum NiveauToast { Info, Message, Alerte, Danger }

/// <summary>
/// Notification flottante non bloquante (bas-droite, au-dessus du widget), avec animation
/// d'entrée / sortie. N'a jamais le focus. Thread UI uniquement.
/// </summary>
public class ToastForm : Form
{
    private static readonly List<ToastForm> Actifs = new();

    private static readonly Color Fond = Color.FromArgb(19, 28, 58);
    private static readonly Color Texte = Color.FromArgb(226, 232, 240);

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _vie;
    private readonly string _titre, _texte;
    private readonly Color _accent;
    private readonly float _k;
    private int _etape, _cibleX;
    private bool _sortie;

    /// <summary>Appelé si l'utilisateur clique sur la notification.</summary>
    public Action? Clic { get; set; }

    private ToastForm(string titre, string texte, NiveauToast niveau, int dureeMs)
    {
        _k = Math.Max(1f, DeviceDpi / 96f);
        _titre = titre;
        _texte = texte;
        _accent = niveau switch
        {
            NiveauToast.Danger => Color.FromArgb(220, 38, 38),
            NiveauToast.Alerte => Color.FromArgb(245, 158, 11),
            NiveauToast.Message => Color.FromArgb(139, 92, 246),
            _ => Color.FromArgb(56, 189, 248)
        };

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Fond;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        ClientSize = new Size(S(310), S(88));
        Opacity = 0;

        _anim.Tick += (_, _) => Animer();
        _vie = new System.Windows.Forms.Timer { Interval = Math.Max(1500, dureeMs) };
        _vie.Tick += (_, _) => { _vie.Stop(); Fermer(false); };
    }

    /// <summary>Affiche une notification (3 maximum à l'écran).</summary>
    public static ToastForm Afficher(string titre, string texte, NiveauToast niveau = NiveauToast.Info,
        int dureeMs = 7000, Action? clic = null)
    {
        if (Actifs.Count >= 3) Actifs[0].Fermer(true);

        var t = new ToastForm(titre, texte, niveau, dureeMs) { Clic = clic };
        Actifs.Add(t);
        Repositionner();
        t.Show();
        t._anim.Start();
        t._vie.Start();

        try
        {
            if (niveau is NiveauToast.Alerte or NiveauToast.Danger) System.Media.SystemSounds.Exclamation.Play();
            else if (niveau == NiveauToast.Message) System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
        return t;
    }

    public static void FermerTous()
    {
        foreach (var t in Actifs.ToArray()) t.Fermer(true);
    }

    public void Fermer(bool immediat)
    {
        if (IsDisposed) return;
        if (immediat) { Close(); return; }
        _sortie = true;
        _anim.Start();
    }

    // ------------------------------------------------------------------

    private static void Repositionner()
    {
        var zone = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        var y = 0;
        // Les plus récentes en bas, au-dessus de la zone du widget (180 px + marges)
        for (var i = Actifs.Count - 1; i >= 0; i--)
        {
            var t = Actifs[i];
            var bas = zone.Bottom - t.S(12 + 180 + 8) - y;
            t._cibleX = zone.Right - t.Width - t.S(12);
            t.Location = new Point(t._cibleX + (t._etape < 14 ? t.S(40) : 0), bas - t.Height);
            y += t.Height + t.S(8);
        }
    }

    private void Animer()
    {
        if (IsDisposed) return;
        _etape += _sortie ? -1 : 1;
        var t = Math.Clamp(_etape / 14.0, 0, 1);
        var e = 1 - Math.Pow(1 - t, 3);
        Opacity = e;
        Left = _cibleX + (int)Math.Round(S(40) * (1 - e));

        if (_sortie && _etape <= 0) { Close(); return; }
        if (!_sortie && _etape >= 14) _anim.Stop();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000;   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        try { Clic?.Invoke(); } catch { }
        Fermer(false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var accent = new SolidBrush(_accent);
        e.Graphics.FillRectangle(accent, 0, 0, S(5), Height);
        using var pen = new Pen(Color.FromArgb(90, 139, 92, 246));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        using var fTitre = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var fTexte = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(e.Graphics, _titre, fTitre,
            new Rectangle(S(16), S(8), Width - S(26), S(20)), Color.White,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _texte, fTexte,
            new Rectangle(S(16), S(30), Width - S(26), Height - S(38)), Texte,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Actifs.Remove(this);
        _anim.Dispose();
        _vie.Dispose();
        Repositionner();
        base.OnFormClosed(e);
    }

    private int S(int v) => (int)Math.Round(v * _k);
}