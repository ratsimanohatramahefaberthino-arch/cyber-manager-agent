using System.Drawing.Drawing2D;
using System.Drawing.Text;

/// <summary>
/// Voyant « Serveur : connecté ✓ / injoignable ». Alimenté UNIQUEMENT par l'état que le Gardien tire du heartbeat :
/// il ne dépend jamais de la sonde NCSI de Windows.
/// Anti-clignotement : vert immédiat ; rouge seulement après 3 s d'injoignabilité ; redessin uniquement si l'état change.
/// </summary>
public sealed class VoyantServeur : Control
{
    private enum Etat { Inconnu, Connecte, Injoignable }

    private static readonly Color Fond = Color.FromArgb(19, 28, 58);
    private static readonly Color Vert = Color.FromArgb(34, 197, 94);
    private static readonly Color Rouge = Color.FromArgb(239, 68, 68);
    private static readonly Color Gris = Color.FromArgb(148, 163, 184);

    private readonly Font _police;
    private readonly System.Windows.Forms.Timer _delai = new() { Interval = 3000 };
    private Etat _etat = Etat.Inconnu;
    private bool _dernierConnecte;

    public VoyantServeur(float taillePolice)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _police = new Font("Segoe UI", taillePolice, FontStyle.Bold);
        TabStop = false;
        AccessibleName = "État de la liaison avec le serveur";
        _delai.Tick += (_, _) =>
        {
            _delai.Stop();
            if (!_dernierConnecte) Appliquer(Etat.Injoignable);
        };
    }

    /// <summary>À appeler sur le thread UI. « immediat » : pas de délai avant le rouge (création du widget).</summary>
    public void Definir(bool connecte, bool immediat = false)
    {
        _dernierConnecte = connecte;
        if (connecte) { _delai.Stop(); Appliquer(Etat.Connecte); return; }
        if (immediat) { _delai.Stop(); Appliquer(Etat.Injoignable); return; }
        if (_etat != Etat.Injoignable && !_delai.Enabled) _delai.Start();
    }

    private void Appliquer(Etat e)
    {
        if (_etat == e) return;
        _etat = e;
        Invalidate();
    }

    private static GraphicsPath Pilule(Rectangle r)
    {
        var p = new GraphicsPath();
        var d = Math.Max(2, r.Height);
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var chemin = Pilule(new Rectangle(0, 0, Width, Height));
        var ancienne = Region;
        Region = new Region(chemin);
        ancienne?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Fond);

        var couleur = _etat switch { Etat.Connecte => Vert, Etat.Injoignable => Rouge, _ => Gris };
        var texte = _etat switch
        {
            Etat.Connecte => "Serveur : connecté",
            Etat.Injoignable => "Serveur : injoignable",
            _ => "Serveur : vérification…",
        };

        using (var bordure = new Pen(couleur, 2f))
        using (var chemin = Pilule(new Rectangle(1, 1, Width - 3, Height - 3)))
            g.DrawPath(bordure, chemin);

        var d = Math.Max(8, Height - 10);
        var cercle = new Rectangle(8, (Height - d) / 2, d, d);

        if (_etat == Etat.Inconnu)
        {
            using var p = new Pen(couleur, 2f);
            g.DrawEllipse(p, cercle);
        }
        else
        {
            using (var b = new SolidBrush(couleur)) g.FillEllipse(b, cercle);
            using var trait = new Pen(Color.White, Math.Max(1.5f, d / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (_etat == Etat.Connecte)
            {
                g.DrawLines(trait, new[]
                {
                    new PointF(cercle.X + d * 0.26f, cercle.Y + d * 0.52f),
                    new PointF(cercle.X + d * 0.43f, cercle.Y + d * 0.70f),
                    new PointF(cercle.X + d * 0.74f, cercle.Y + d * 0.32f),
                });
            }
            else
            {
                g.DrawLine(trait, cercle.X + d * 0.3f, cercle.Y + d * 0.3f, cercle.X + d * 0.7f, cercle.Y + d * 0.7f);
                g.DrawLine(trait, cercle.X + d * 0.7f, cercle.Y + d * 0.3f, cercle.X + d * 0.3f, cercle.Y + d * 0.7f);
            }
        }

        var zone = new Rectangle(cercle.Right + 8, 0, Math.Max(10, Width - cercle.Right - 14), Height);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var pinceau = new SolidBrush(_etat == Etat.Inconnu ? Gris : Color.White);
        g.DrawString(texte, _police, pinceau, zone, format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _delai.Dispose(); _police.Dispose(); }
        base.Dispose(disposing);
    }
}