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

    // Trasforma gli eventi grezzi del sink in una timeline didattica di passi.
    // Ogni evento interessante diventa un EvalStep leggibile dal frontend:
    // round → "Round N", tool call → nome + argomenti, tool result → output (troncato),
    // final answer → testo (troncato). Gli eventi puramente tecnici (Token, PromptSent,
    // ToolCallDelta...) sono saltati perché già impliciti nei passi precedenti.
    private static List<EvalStep> BuildSteps(IReadOnlyList<LabEvent> events)
    {
        var steps = new List<EvalStep>();
        string? lastRoundText = null; // testo generato nell'ultimo round (per il passo "cosa ha prodotto")

        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case LabEventType.RunStarted:
                    if (evt.Data is RunStartedData rsn)
                        steps.Add(new EvalStep("start", 0, "▶ Inizio esecuzione",
                            $"Prompt: «{Truncate(rsn.Prompt)}» — tool disponibili: {string.Join(", ", rsn.ToolNames)}"));
                    break;

                case LabEventType.RoundStarted:
                    if (evt.Data is RoundStartedData rs)
                        steps.Add(new EvalStep("round", rs.Round, $"🔄 Round {rs.Round}: il modello riceve il contesto",
                            $"History inviata: {rs.HistoryCount} messaggi (ogni round il modello rivede TUTTO quello che c'è già)."));
                    break;

                case LabEventType.PromptSent:
                    if (evt.Data is PromptSentData ps)
                        steps.Add(new EvalStep("context", ps.Round, $"📥 Contesto inviato al modello (~{ps.EstimatedTokens} token)",
                            BuildContextSummary(ps.Messages)));
                    break;

                case LabEventType.RoundCompleted:
                    if (evt.Data is RoundCompletedData rc && lastRoundText != null)
                        steps.Add(new EvalStep("generated", rc.Round, $"💬 Il modello ha risposto ({rc.DurationMs} ms)",
                            Truncate(lastRoundText)));
                    lastRoundText = null;
                    break;

                case LabEventType.Token:
                    if (evt.Data is TokenData tk)
                        lastRoundText = (lastRoundText ?? "") + tk.Text;
                    break;

                case LabEventType.ToolCallDetected:
                    if (evt.Data is ToolCallDetectedData tc)
                        steps.Add(new EvalStep("toolCall", evt.Round ?? 0,
                            $"🔧 Il modello decide di usare il tool «{tc.FunctionName}»",
                            $"Invece di rispondere subito, chiede un'azione esterna. Argomenti: {Truncate(tc.ArgumentsJson)}"));
                    break;

                case LabEventType.ToolResult:
                    if (evt.Data is ToolResultData tr)
                        steps.Add(new EvalStep("toolResult", evt.Round ?? 0,
                            tr.IsError ? $"❌ Il tool «{tr.FunctionName}» ha dato errore" : $"✅ Il tool «{tr.FunctionName}» ha risposto ({tr.DurationMs} ms)",
                            $"Il risultato torna nel contesto e il modello lo vedrà nel round successivo. Output: {Truncate(tr.Output)}"));
                    break;

                case LabEventType.FinalAnswer:
                    if (evt.Data is FinalAnswerData fa)
                        steps.Add(new EvalStep("answer", evt.Round ?? 0,
                            $"🏁 Risposta finale (dopo {fa.TotalRounds} round, {fa.TotalDurationMs} ms)",
                            Truncate(fa.Content)));
                    break;
            }
        }

        return steps;
    }

    // Riepilogo leggibile della history inviata al modello (per il passo "context").
    private static string BuildContextSummary(List<PromptMessageView> messages)
    {
        const int maxPerMsg = 80;
        var parts = new List<string>();
        foreach (var m in messages)
        {
            var content = string.IsNullOrWhiteSpace(m.Content) ? "(vuoto)" : Truncate(m.Content, maxPerMsg);
            parts.Add($"[{m.Role}] {content}");
        }
        return string.Join(" → ", parts);
    }

    private static string? Truncate(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= 300 ? text : text[..300] + "…";
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
                    null, new List<string>(), 0, 0,
                    new List<EvalStep>()));
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
                    runResult.DurationMs,
                    BuildSteps(sink.Events)));
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
                    null, sink.ToolCallsDetected, 0, 0,
                    BuildSteps(sink.Events)));
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
