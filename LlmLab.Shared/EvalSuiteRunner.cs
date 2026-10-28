namespace LlmLab.Shared.Eval;

using LlmLab.Shared;
using LlmLab.Shared.Core;
using LlmLab.Shared.Models;
using LlmLab.Shared.Services;
using LlmLab.Shared.Tools;

// ============================================================================
// EvalSuiteRunner: esegue una suite di casi (eval) con una configurazione data.
// È il metodo comune riusato sia da /api/lab/eval sia da /api/lab/eval/compare:
// ogni caso viene eseguito con un orchestratore FRESCO (history pulita) e un
// sink in-memory; la valutazione dei check è delegata a EvalChecker (pura).
// ============================================================================

// Configurazione opzionale di una run della suite: i campi null usano i valori
// attuali (default dell'orchestrator / del server LLM).
public record EvalRunConfig(double? Temperature = null, string? SystemPrompt = null, int? MaxRounds = null);

public class EvalSuiteRunner
{
    private readonly ILLMClient _client;
    private readonly List<ILocalTool> _tools;
    private readonly object _toolsDefinition;

    public EvalSuiteRunner(ILLMClient client, IEnumerable<ILocalTool> tools, object toolsDefinition)
    {
        _client = client;
        _tools = tools.ToList();
        _toolsDefinition = toolsDefinition;
    }

    // Esegue la suite in sequenza con la configurazione data (null = valori attuali).
    public async Task<EvalSuiteResult> RunSuiteAsync(List<EvalCase> cases, EvalRunConfig? config, CancellationToken ct)
    {
        var systemPrompt = !string.IsNullOrWhiteSpace(config?.SystemPrompt) ? config!.SystemPrompt!
            : "Sei un assistente esperto. Usa i tool quando necessario per rispondere alle domande dell'utente.";
        var maxRounds = config?.MaxRounds is > 0 ? config.MaxRounds.Value : 10;

        var caseResults = new List<EvalCaseResult>();

        foreach (var evalCase in cases)
        {
            if (string.IsNullOrWhiteSpace(evalCase.Prompt))
            {
                caseResults.Add(new EvalCaseResult(
                    evalCase.Name, false,
                    new List<EvalCheckResult> { new("prompt", "", false, "Il prompt del caso è vuoto.") },
                    null, new List<string>(), 0, 0));
                continue;
            }

            // Orchestratore fresco per ogni caso: la history non deve essere condivisa.
            var orchestrator = new AgentOrchestrator(_client, _tools, systemPrompt, _toolsDefinition, maxRounds, config?.Temperature);

            var sink = new InMemoryEventSink();
            try
            {
                var runResult = await orchestrator.RunAsync(evalCase.Prompt, sink, ct);
                var toolsCalled = sink.ToolCallsDetected;
                var checkResults = EvalChecker.Evaluate(evalCase, runResult.FinalMessage, toolsCalled, runResult.Rounds);

                caseResults.Add(new EvalCaseResult(
                    evalCase.Name,
                    EvalChecker.AllPassed(checkResults),
                    checkResults,
                    runResult.FinalMessage,
                    toolsCalled,
                    runResult.Rounds,
                    runResult.DurationMs));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // il client ha chiuso la connessione: propagare come normale cancellazione.
            }
            catch (Exception ex)
            {
                caseResults.Add(new EvalCaseResult(
                    evalCase.Name, false,
                    new List<EvalCheckResult> { new("run", "", false, $"Errore durante l'esecuzione: {ex.Message}") },
                    null, sink.ToolCallsDetected, 0, 0));
            }
        }

        var passedCount = caseResults.Count(r => r.Passed);
        var totalDurationMs = caseResults.Sum(r => r.DurationMs);
        return new EvalSuiteResult(caseResults, passedCount, caseResults.Count - passedCount, totalDurationMs);
    }
}

// ============================================================================
// InMemoryEventSink: IEventSink che accumula gli eventi in memoria (niente SSE).
// Per l'eval serve solo estrarre i tool chiamati (eventi ToolCallDetected).
// ============================================================================
public class InMemoryEventSink : IEventSink
{
    private readonly List<LabEvent> _events = new();

    public IReadOnlyList<LabEvent> Events => _events;

    // Nomi dei tool effettivamente chiamati dal modello, in ordine di chiamata.
    public List<string> ToolCallsDetected => _events
        .Where(e => e.Type == LabEventType.ToolCallDetected && e.Data is ToolCallDetectedData)
        .Select(e => ((ToolCallDetectedData)e.Data!).FunctionName)
        .ToList();

    public Task EmitAsync(LabEvent evt, CancellationToken ct)
    {
        lock (_events) _events.Add(evt);
        return Task.CompletedTask;
    }
}
