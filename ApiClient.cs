using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

/// <summary>
/// Client du Gardien (canal local). Remplace l'ancien client HTTP : le serveur et le jeton
/// ne sont connus que du Gardien. Se reconnecte tout seul si le Gardien redémarre.
/// </summary>
public class ApiClient
{
    public const string Version = Constantes.VersionEcran;

    private readonly Action<string> _journal;
    private readonly object _verrou = new();
    private StreamWriter? _ecrivain;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MessageCanal>> _attentes = new();
    private int _prochainId;

    /// <summary>Message du Gardien : etat, branding ou cmd.</summary>
    public event Action<MessageCanal>? MessageRecu;
    public event Action? Connecte;
    public event Action? Deconnecte;

    public ApiClient(Action<string> journal) { _journal = journal; }

    public async Task BoucleAsync()
    {
        while (true)
        {
            var etaitConnecte = false;
            try
            {
                using var pipe = new NamedPipeClientStream(".", Constantes.NomPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000);

                var utf8 = new UTF8Encoding(false);
                using var lecteur = new StreamReader(pipe, utf8);
                var ecrivain = new StreamWriter(pipe, utf8) { AutoFlush = true };
                lock (_verrou) _ecrivain = ecrivain;
                etaitConnecte = true;

                _journal("Connecté au Gardien.");
                Envoyer(new MessageCanal { Type = "pret", Texte = Version });
                try { Connecte?.Invoke(); } catch { }

                string? ligne;
                while ((ligne = await lecteur.ReadLineAsync()) != null) Traiter(ligne);
            }
            catch { /* Gardien absent ou redémarrage : on réessaie */ }
            finally
            {
                lock (_verrou) _ecrivain = null;
                foreach (var a in _attentes.Values)
                    a.TrySetResult(new MessageCanal { Succes = false, Message = "Liaison avec le Gardien perdue." });
                if (etaitConnecte)
                {
                    _journal("Liaison avec le Gardien perdue.");
                    try { Deconnecte?.Invoke(); } catch { }
                }
            }

            await Task.Delay(1000);
        }
    }

    private void Traiter(string ligne)
    {
        var m = ProtocoleCanal.Decoder(ligne);
        if (m == null) return;

        if (m.Type == "rep")
        {
            if (_attentes.TryRemove(m.Id, out var a)) a.TrySetResult(m);
            return;
        }
        try { MessageRecu?.Invoke(m); } catch (Exception ex) { _journal($"Message du Gardien : {ex.Message}"); }
    }

    public bool Envoyer(MessageCanal message)
    {
        try
        {
            lock (_verrou)
            {
                if (_ecrivain == null) return false;
                _ecrivain.WriteLine(ProtocoleCanal.Encoder(message));
                return true;
            }
        }
        catch { return false; }
    }

    private async Task<MessageCanal> RequeteAsync(string action, string? texte = null)
    {
        var id = Interlocked.Increment(ref _prochainId);
        var tcs = new TaskCompletionSource<MessageCanal>(TaskCreationOptions.RunContinuationsAsynchronously);
        _attentes[id] = tcs;

        if (!Envoyer(new MessageCanal { Type = "req", Id = id, Action = action, Texte = texte }))
        {
            _attentes.TryRemove(id, out _);
            return new MessageCanal { Succes = false, Message = "Le Gardien n'est pas joignable. Réessayez dans quelques instants." };
        }

        var fini = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(75)));
        _attentes.TryRemove(id, out _);
        return fini == tcs.Task ? tcs.Task.Result : new MessageCanal { Succes = false, Message = "Le Gardien ne répond pas." };
    }

    public async Task<ActivationResult> ActiverAsync()
    {
        var r = await RequeteAsync("activer");
        return new ActivationResult(r.Succes == true, r.Message ?? (r.Succes == true ? "Poste activé." : "Activation refusée."), r.Avertissement);
    }

    public Task<ResultatAction> SuspendreSessionAsync() => ActionAsync("suspendre", "Session suspendue.");
    public Task<ResultatAction> FermerSessionAsync() => ActionAsync("fermer", "Session terminée.");
    public Task<ResultatAction> ReprendreSessionAsync() => ActionAsync("reprendre", "Session reprise.");
    public Task<ResultatAction> EnvoyerMessageAsync(string texte) => ActionAsync("message", "Message envoyé.", texte);

    private async Task<ResultatAction> ActionAsync(string action, string messageParDefaut, string? texte = null)
    {
        var r = await RequeteAsync(action, texte);
        return new ResultatAction(r.Succes == true, r.Message ?? (r.Succes == true ? messageParDefaut : "Action refusée."), r.Montant);
    }
}