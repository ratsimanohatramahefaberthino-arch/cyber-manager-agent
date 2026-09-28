var builder = WebApplication.CreateBuilder(args);

var agentToken = builder.Configuration["AgentToken"]
    ?? Environment.GetEnvironmentVariable("AGENT_WINDOWS_TOKEN");

if (string.IsNullOrWhiteSpace(agentToken))
{
    throw new InvalidOperationException(
        "Le token de l'agent Windows n'est pas configuré."
    );
}

var app = builder.Build();

app.MapGet("/api/status", () =>
{
    return Results.Ok(new
    {
        succes = true,
        agent = "Cyber Manager Agent Windows",
        machine = Environment.MachineName,
        statut = "operationnel",
        date_heure = DateTime.Now
    });
});

app.MapPost("/api/commande", async (HttpRequest request, CommandeRequest requete) =>
{
    if (!request.Headers.TryGetValue("X-Agent-Token", out var token))
    {
        return Results.Unauthorized();
    }

    if (token != agentToken)
    {
        return Results.Unauthorized();
    }

    if (requete.Commande == "PING")
    {
        return Results.Ok(new
        {
            succes = true,
            commande = "PING",
            message = "Agent Windows accessible et opérationnel."
        });
    }

    if (requete.Commande == "STATUS")
    {
        return Results.Ok(new
        {
            succes = true,
            commande = "STATUS",
            machine = Environment.MachineName,
            statut = "operationnel"
        });
    }

    if (requete.Commande == "LOCK_TEST")
    {
        return Results.Ok(new
        {
            succes = true,
            commande = "LOCK_TEST",
            action = "simulation",
            message = "Le poste serait verrouillé.",
            machine = Environment.MachineName
        });
    }

    if (requete.Commande == "UNLOCK_TEST")
    {
        return Results.Ok(new
        {
            succes = true,
            commande = "UNLOCK_TEST",
            action = "simulation",
            message = "Le poste serait déverrouillé.",
            machine = Environment.MachineName
        });
    }

    return Results.BadRequest(new
    {
        succes = false,
        message = "Commande inconnue."
    });
});

app.Run("http://127.0.0.1:5005");

public record CommandeRequest(
    string Commande,
    Dictionary<string, object>? Parametres
);