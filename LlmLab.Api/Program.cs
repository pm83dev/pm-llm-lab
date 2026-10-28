using System.Text.Json;
using LlmLab.Shared;
using LlmLab.Shared.Core;
using LlmLab.Shared.Eval;
using LlmLab.Shared.Models;
using LlmLab.Shared.Services;
using LlmLab.Shared.Tools;
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
    // Validazione del prompt PRIMA di aprire lo stream SSE: se non è valida la
    // risposta è un semplice JSON 400, non uno stream.
    if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        return Results.BadRequest(new { error = "Il prompt non può essere vuoto o composto solo da spazi." });

    if (request.Prompt.Length > 4000)
        return Results.BadRequest(new { error = $"Il prompt supera il limite di 4000 caratteri (ricevuti: {request.Prompt.Length})." });

    http.Response.Headers["Content-Type"] = "text/event-stream";
    http.Response.Headers["Cache-Control"] = "no-cache";
    http.Response.Headers["Connection"] = "keep-alive";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // Sink che serializza ogni evento in formato SSE e lo scrive nella risposta.
    var sink = new SseEventSink(http, ct);

    try
    {
        await orchestrator.RunAsync(request.Prompt, sink, ct, request.IncludeLogprobs);
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

    // Lo stream è già stato scritto direttamente nella risposta: nessun IResult da restituire.
    return Results.Empty;
});

// ============================================================================
// POST /api/lab/eval  ->  Suite di test (eval) del modello, in sequenza.
// Il client invia { cases: [{ name, prompt, checks }] }; ogni caso viene
// eseguito con un orchestratore FRESCO (history pulita) e un sink in-memory
// (niente SSE): gli eventi vengono raccolti per estrarre i tool chiamati.
// La valutazione dei check è delegata a EvalChecker (classe pura in Shared).
// ============================================================================
app.MapPost("/api/lab/eval", async (
    [FromBody] LabEvalRequest request,
    IServiceProvider sp,
    CancellationToken ct) =>
{
    if (request is null || request.Cases is null || request.Cases.Count == 0)
        return Results.BadRequest(new { error = "La suite deve contenere almeno un caso (campo 'cases')." });

    // La logica della suite è in EvalSuiteRunner (Shared), riusata anche da /eval/compare.
    var runner = new EvalSuiteRunner(
        sp.GetRequiredService<ILLMClient>(),
        sp.GetServices<ILocalTool>(),
        ServiceCollectionExtensions.BuildToolsDefinition());

    var result = await runner.RunSuiteAsync(request.Cases, null, ct);
    return Results.Ok(result);
});

// ============================================================================
// POST /api/lab/eval/compare  ->  Confronto A/B della suite di test.
// Riceve { cases, configA, configB } (config opzionali: temperature, systemPrompt,
// maxRounds; se assenti si usano i valori attuali). Esegue la stessa suite una volta
// per configurazione riusingo EvalSuiteRunner e restituisce { a, b, diff } dove diff
// è per caso: nome + esito A + esito B + "migliorato"/"peggiorato"/"uguale".
// ============================================================================
app.MapPost("/api/lab/eval/compare", async (
    [FromBody] LabEvalCompareRequest request,
    IServiceProvider sp,
    CancellationToken ct) =>
{
    if (request is null || request.Cases is null || request.Cases.Count == 0)
        return Results.BadRequest(new { error = "La suite deve contenere almeno un caso (campo 'cases')." });

    var runner = new EvalSuiteRunner(
        sp.GetRequiredService<ILLMClient>(),
        sp.GetServices<ILocalTool>(),
        ServiceCollectionExtensions.BuildToolsDefinition());

    // Le due configurazioni sono indipendenti: si eseguono in sequenza per non
    // sovrapporre gli stream sullo stesso client LLM (non thread-safe).
    var a = await runner.RunSuiteAsync(request.Cases, request.ConfigA, ct);
    var b = await runner.RunSuiteAsync(request.Cases, request.ConfigB, ct);

    var diff = new List<EvalCompareCaseDiff>();
    for (var i = 0; i < a.Cases.Count; i++)
    {
        var ca = a.Cases[i];
        var cb = b.Cases[i];
        string variation;
        if (cb.Passed && !ca.Passed) variation = "migliorato";
        else if (ca.Passed && !cb.Passed) variation = "peggiorato";
        else variation = "uguale";

        diff.Add(new EvalCompareCaseDiff(ca.Name, ca.Passed, cb.Passed, variation));
    }

    return Results.Ok(new EvalCompareResult(a, b, diff));
});

// Endpoint di health-check semplice.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// IncludeLogprobs: se true, il backend chiede al server LLM i logprobs (top-5) e li
// restituisce come eventi TokenProb. Default false: il flusso resta invariato.
public record LabRunRequest(string Prompt, bool IncludeLogprobs = false);

// Richiesta della suite di test: lista di casi da eseguire in sequenza.
public record LabEvalRequest(List<EvalCase> Cases);

// ============================================================================
// Modelli per il confronto A/B (/api/lab/eval/compare). Le config sono opzionali:
// i campi null usano i valori attuali (default orchestrator / server LLM).
// ============================================================================
public record LabEvalCompareRequest(
    List<EvalCase> Cases,
    EvalRunConfig? ConfigA = null,
    EvalRunConfig? ConfigB = null);

// Esito del confronto per un singolo caso.
public record EvalCompareCaseDiff(string Name, bool PassedA, bool PassedB, string Variation);

// Risposta completa del confronto: risultati delle due suite + diff per caso.
public record EvalCompareResult(EvalSuiteResult A, EvalSuiteResult B, List<EvalCompareCaseDiff> Diff);

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
