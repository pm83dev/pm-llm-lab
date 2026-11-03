using LocalLLM.Core;
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
builder.Services.AddSingleton<IPromptSchema, PromptSchema>();

builder.Services.AddSingleton<AgentOrchestrator>(sp =>
{
    var client = sp.GetRequiredService<ILLMClient>();
    var tools = sp.GetServices<ILocalTool>().ToList();
    var systemPrompt = sp.GetService<IPromptSchema>()?.SystemPrompt ?? "You are an AI assistant.";
    var toolsDef = sp.GetService<IPromptSchema>()?.ToolsDef;
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
