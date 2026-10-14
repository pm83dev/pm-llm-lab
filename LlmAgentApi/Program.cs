using LocalLLM.Core;
using LocalLLM.Services;
using LocalLLM.Tools;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// builder.Services.AddOpenApi(); // Commentato - metodo non disponibile in questa versione
builder.Services.AddLogging();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Configuration.AddJsonFile("appsettings.json", optional: true);

builder.Services.AddHttpClient("llm", c =>
    c.BaseAddress = new Uri(builder.Configuration["LLM:BaseAddress"] ?? "http://localhost:9000/v1/chat/completions"));

builder.Services.AddSingleton<ILLMClient, OpenAILocalClient>();

builder.Services.AddTransient<ILocalTool, GetSystemTimeTool>();
builder.Services.AddTransient<ILocalTool, ReadFileTool>();
builder.Services.AddTransient<ILocalTool, WriteFileTool>();

var systemPrompt = "Sei un assistente esperto. Usa i tool quando necessario per rispondere alle domande dell'utente.";
var toolsDef = new object[] {
    new {
        type = "function",
        @function = new {
            name = "get_system_time",
            description = "Restituisce l'ora corrente del sistema.",
            parameters = new {
                type = "object",
                properties = new { },
                required = new string[0]
            }
        }
    },
    new {
        type = "function",
        @function = new {
            name = "read_file",
            description = "Legge e restituisce il contenuto testuale di un file dato il suo percorso.",
            parameters = new {
                type = "object",
                properties = new {
                    path = new { type = "string", description = "Percorso del file da leggere." }
                },
                required = new[] { "path" }
            }
        }
    },
    new {
        type = "function",
        @function = new {
            name = "write_file",
            description = "Scrive del contenuto testuale in un file al percorso specificato, sovrascrivendolo se già esiste.",
            parameters = new {
                type = "object",
                properties = new {
                    path = new { type = "string", description = "Percorso del file in cui scrivere." },
                    content = new { type = "string", description = "Contenuto testuale da scrivere nel file." }
                },
                required = new[] { "path", "content" }
            }
        }
    }
};

builder.Services.AddSingleton<AgentOrchestrator>(sp =>
{
    var client = sp.GetRequiredService<ILLMClient>();
    var tools = sp.GetServices<ILocalTool>().ToList();
    return new AgentOrchestrator(client, tools, systemPrompt, toolsDef);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/agent", async (
    [FromBody] AgentRequest request,
    AgentOrchestrator orchestrator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        logger.LogInformation("Agent richiesto per: {Prompt}", request.Prompt);
        
        var result = await orchestrator.RunAsync(request.Prompt, ct);
        
        logger.LogInformation("Agent completato. Round: {Rounds}", result.Rounds);
        
        return Results.Ok(new AgentResponse("completed", result.FinalMessage));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Errore durante l'esecuzione dell'agent");
        return Results.BadRequest(new ErrorResponse("Agent execution failed", ex.Message));
    }
})
.WithName("RunAgent");

app.Run();

public record AgentRequest(string Prompt);
public record AgentResponse(string Status, string? Message = null);
public record ErrorResponse(string Error, string? Details = null);
