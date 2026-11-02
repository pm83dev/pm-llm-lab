namespace LlmLab.Shared.Core;

using System.Diagnostics;
using LlmLab.Shared.Models;
using LlmLab.Shared.Services;
using LlmLab.Shared.Tools;

// ============================================================================
// AgentOrchestrator: il "cervello" del ciclo agentico, versione STRUMENTATA.
// Stesso pattern della versione console (chiama LLM -> esegue tool -> ripete),
// ma invece di stampare in console emette eventi via IEventSink, così il
// frontend può mostrare ogni passaggio in tempo reale.
// ============================================================================

public class AgentOrchestrator
{
    private readonly ILLMClient _client;
    private readonly Dictionary<string, ILocalTool> _tools;
    private readonly List<ChatMessage> _history;
    private readonly object _toolsDefinition;
    private readonly string _systemPrompt;
    private readonly int _maxRounds;
    private readonly double? _temperature;

    public AgentOrchestrator(ILLMClient client, IEnumerable<ILocalTool> tools, string systemPrompt, object toolsDefinition, int maxRounds = 10, double? temperature = null)
    {
        _client = client;
        _tools = tools.ToDictionary(t => t.Name);
        _history = new List<ChatMessage> { new ChatMessage("system", systemPrompt) };
        _toolsDefinition = toolsDefinition;
        _systemPrompt = systemPrompt;
        _maxRounds = maxRounds;
        _temperature = temperature;
    }

    public record RunResult(int Rounds, string? FinalMessage, long DurationMs);

    public async Task<RunResult> RunAsync(string input, IEventSink sink, CancellationToken ct, bool includeLogprobs = false)
    {
        var sw = Stopwatch.StartNew();
        _history.Add(new ChatMessage("user", input));

        await sink.EmitAsync(new LabEvent(LabEventType.RunStarted, Data: new RunStartedData(
            input, _tools.Count, _tools.Keys.ToArray())), ct);

        int round = 0;
        string? finalMessage = null;

        while (round < _maxRounds)
        {
            round++;
            var roundSw = Stopwatch.StartNew();

            await sink.EmitAsync(new LabEvent(LabEventType.RoundStarted, round, Data: new RoundStartedData(
                round, _history.Count, _tools.Count)), ct);

            // Emettiamo la history completa che stiamo per inviare al modello.
            var estTokens = _history.Sum(m => TokenEstimator.Estimate(m.Content)
                + (m.ToolCalls?.Sum(tc => TokenEstimator.Estimate(tc.FunctionName) + TokenEstimator.Estimate(tc.ArgumentsJson)) ?? 0));
            await sink.EmitAsync(new LabEvent(LabEventType.PromptSent, round, Data: new PromptSentData(
                round, _history.Count, estTokens,
                _history.Select(m => new PromptMessageView(m.Role, m.Content,
                    m.ToolCalls?.Select(tc => new ToolCallView(tc.FunctionName, tc.ArgumentsJson, tc.Id)).ToList(),
                    m.ToolCallId)).ToList(),
                _tools.Keys.ToArray())), ct);

            // 1. Streaming della risposta: i token arrivano come eventi Token (e TokenProb se richiesti).
            await _client.StreamChatAsync(_history, _toolsDefinition, sink, ct, includeLogprobs, _temperature);

            // 2. Raccolta del risultato del round.
            var streamResult = await _client.CollectStreamResultAsync(ct);
            var lastResponse = new ChatMessage("assistant", streamResult.FullContent, streamResult.ToolCalls);
            _history.Add(lastResponse);

            if (streamResult.ToolCalls == null || streamResult.ToolCalls.Count == 0)
            {
                // Risposta finale: il modello non ha chiesto tool.
                finalMessage = streamResult.FullContent;
                await sink.EmitAsync(new LabEvent(LabEventType.FinalAnswer, round, Data: new FinalAnswerData(
                    streamResult.FullContent, round, sw.ElapsedMilliseconds)), ct);
                await sink.EmitAsync(new LabEvent(LabEventType.RoundCompleted, round, Data: new RoundCompletedData(
                    round, roundSw.ElapsedMilliseconds, false)), ct);
                break;
            }

            // 3. Il modello ha chiesto tool: li eseguiamo uno a uno.
            foreach (var tc in streamResult.ToolCalls)
            {
                await sink.EmitAsync(new LabEvent(LabEventType.ToolCallDetected, round, Data: new ToolCallDetectedData(
                    tc.FunctionName, tc.ArgumentsJson, tc.Id)), ct);

                if (!_tools.TryGetValue(tc.FunctionName, out var tool))
                {
                    var err = $"Error: tool '{tc.FunctionName}' non esiste.";
                    await sink.EmitAsync(new LabEvent(LabEventType.ToolResult, round, Data: new ToolResultData(
                        tc.FunctionName, tc.Id, err, true, 0)), ct);
                    _history.Add(new ChatMessage("tool", err, ToolCallId: tc.Id));
                    continue;
                }

                await sink.EmitAsync(new LabEvent(LabEventType.ToolExecuting, round, Data: new ToolExecutingData(
                    tc.FunctionName, tc.ArgumentsJson, tc.Id)), ct);

                var toolSw = Stopwatch.StartNew();
                try
                {
                    var result = await tool.ExecuteAsync(tc.ArgumentsJson);
                    toolSw.Stop();
                    await sink.EmitAsync(new LabEvent(LabEventType.ToolResult, round, Data: new ToolResultData(
                        tc.FunctionName, tc.Id, result, false, toolSw.ElapsedMilliseconds)), ct);
                    _history.Add(new ChatMessage("tool", result, ToolCallId: tc.Id));
                }
                catch (Exception ex)
                {
                    toolSw.Stop();
                    var err = $"Error: {ex.Message}";
                    await sink.EmitAsync(new LabEvent(LabEventType.ToolResult, round, Data: new ToolResultData(
                        tc.FunctionName, tc.Id, err, true, toolSw.ElapsedMilliseconds)), ct);
                    _history.Add(new ChatMessage("tool", err, ToolCallId: tc.Id));
                }
            }

            await sink.EmitAsync(new LabEvent(LabEventType.RoundCompleted, round, Data: new RoundCompletedData(
                round, roundSw.ElapsedMilliseconds, true)), ct);
        }

        sw.Stop();

        if (round >= _maxRounds && finalMessage == null)
        {
            finalMessage = $"Error: max rounds ({_maxRounds}) reached";
            await sink.EmitAsync(new LabEvent(LabEventType.RunFailed, Data: new RunFailedData(
                "Max rounds reached", "Possibile loop di tool call")), ct);
        }
        else
        {
            await sink.EmitAsync(new LabEvent(LabEventType.RunCompleted, Data: new RunCompletedData(
                round, sw.ElapsedMilliseconds, finalMessage)), ct);
        }

        return new RunResult(round, finalMessage, sw.ElapsedMilliseconds);
    }
}
