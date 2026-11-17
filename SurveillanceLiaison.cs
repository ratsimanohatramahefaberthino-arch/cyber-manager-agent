using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CyberManager.Agent;

public enum EtatLiaison { Ok, CableDebranche, ServeurInjoignable }

/// <summary>
/// Surveille la liaison avec le serveur. Le délai de grâce (15 s par défaut, fourni par le serveur
/// dans « delai_grace_agent_s ») est inférieur à la tolérance du serveur (30 s).
/// L'événement EtatChange est levé sur un thread du pool : le consommateur doit repasser sur le thread UI.
/// </summary>
public sealed class SurveillanceLiaison : IDisposable
{
    private readonly object _verrou = new();
    private DateTime _dernierSucces = DateTime.UtcNow;
    private EtatLiaison _etat = EtatLiaison.Ok;
    private System.Threading.Timer? _minuteur;

    public TimeSpan DelaiGrace { get; set; } = TimeSpan.FromSeconds(15);

    public event Action<EtatLiaison, string>? EtatChange;

    public EtatLiaison Etat { get { lock (_verrou) return _etat; } }

    /// <summary>Faux dès que la grâce est dépassée : chrono et montant figés, poste verrouillé.</summary>
    public bool UsageAutorise => Etat == EtatLiaison.Ok;

    public void Demarrer() => _minuteur ??= new System.Threading.Timer(_ => Evaluer(), null, 1000, 1000);

    /// <summary>À appeler après chaque heartbeat réussi (en passant delai_grace_agent_s de la réponse).</summary>
    public void SignalerSucces(int? delaiGraceServeurSecondes = null)
    {
        lock (_verrou) _dernierSucces = DateTime.UtcNow;
        if (delaiGraceServeurSecondes is int s && s > 0) DelaiGrace = TimeSpan.FromSeconds(s);
        Evaluer();
    }

    private void Evaluer()
    {
        TimeSpan silence;
        lock (_verrou) silence = DateTime.UtcNow - _dernierSucces;

        var nouveau = silence < DelaiGrace
            ? EtatLiaison.Ok
            : (ReseauLocalPresent() ? EtatLiaison.ServeurInjoignable : EtatLiaison.CableDebranche);

        lock (_verrou)
        {
            if (nouveau == _etat) return;
            _etat = nouveau;
        }

        var message = nouveau switch
        {
            EtatLiaison.CableDebranche     => "Câble réseau débranché ou Wi-Fi coupé. Le poste est verrouillé, votre temps est suspendu. Reprise automatique au retour du réseau.",
            EtatLiaison.ServeurInjoignable => "Le serveur de la caisse ne répond pas. Le poste est verrouillé, votre temps est suspendu. Reprise automatique au retour du serveur.",
            _                              => "Connexion rétablie.",
        };
        EtatChange?.Invoke(nouveau, message);
    }

    private static bool ReseauLocalPresent()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch { /* en cas de doute : on considère le réseau présent */ return true; }
        return false;
    }

    public void Dispose() => _minuteur?.Dispose();
}