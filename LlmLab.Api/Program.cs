using System.Text.Json;
using LlmLab.Shared;
using LlmLab.Shared.Core;
using LlmLab.Shared.Models;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();

// Indirizzo del server LLM locale (LM Studio, Ollama, vLLM...).
builder.Services.AddHttpClient("llm", c =>
    c.BaseAddress = new Uri(builder.Configuration["LLM:BaseAddress"] ?? "http://localhost:9000/v1/chat/completions"));

// Registra client LLM, tool e orchestrator in un colpo solo.
builder.Services.AddLlmLab();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS: necessario perché il frontend Angular (porta 4200) chiama l'API (porta 5000+).
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod());

// ============================================================================
// POST /api/lab/run  ->  Server-Sent Events
// Il client invia { prompt } e riceve uno stream di eventi LabEvent in tempo
// reale: ogni passaggio del flusso (prompt, token, tool call, esecuzione,
// risultato, risposta finale) arriva come un evento SSE.
// ============================================================================
app.MapPost("/api/lab/run", async (
    [FromBody] LabRunRequest request,
    AgentOrchestrator orchestrator,
    HttpContext http,
    CancellationToken ct) =>
{
    http.Response.Headers["Content-Type"] = "text/event-stream";
    http.Response.Headers["Cache-Control"] = "no-cache";
    http.Response.Headers["Connection"] = "keep-alive";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // Sink che serializza ogni evento in formato SSE e lo scrive nella risposta.
    var sink = new SseEventSink(http, ct);

    try
    {
        await orchestrator.RunAsync(request.Prompt, sink, ct);
    }
    catch (OperationCanceledException)
    {
        // Il client ha chiuso la connessione: normale, non è un errore.
    }
    catch (Exception ex)
    {
        await sink.EmitAsync(new LabEvent(LabEventType.RunFailed, Data: new RunFailedData(
            "Errore durante l'esecuzione", ex.Message)), CancellationToken.None);
    }

    await sink.CompleteAsync();
});

// Endpoint di health-check semplice.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public record LabRunRequest(string Prompt);

// ============================================================================
// SseEventSink: implementazione di IEventSink che scrive gli eventi nel flusso
// HTTP in formato Server-Sent Events. Ogni evento diventa:
//   event: <tipo>
//   data: <json>
//   (blank line)
// ============================================================================
public class SseEventSink : IEventSink
{
    private readonly HttpContext _http;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SseEventSink(HttpContext http, CancellationToken ct)
    {
        _http = http;
        _ct = ct;
    }

    public async Task EmitAsync(LabEvent evt, CancellationToken ct)
    {
        var payload = new
        {
            type = evt.Type.ToString().ToLowerInvariant(),
            round = evt.Round,
            timestamp = evt.TimestampMs,
            data = evt.Data
        };

        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        var sse = $"event: {evt.Type.ToString().ToLowerInvariant()}\ndata: {json}\n\n";

        await _lock.WaitAsync(ct);
        try
        {
            await _http.Response.WriteAsync(sse, ct);
            await _http.Response.Body.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompleteAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await _http.Response.WriteAsync("event: done\ndata: {}\n\n");
            await _http.Response.Body.FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }
}
