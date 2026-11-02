namespace LocalLLMBase;

using System.Text;
using System.Text.Json;

// Il "cervello" del ciclo agentico: alterna chiamate al modello ed esecuzioni di tool
// finché il modello non risponde con solo testo (o si raggiunge maxRounds). Questo
// pattern — chiama LLM, se chiede un tool eseguilo e ridagli il risultato, ripeti —
// è il cuore di qualunque agente basato su function calling, a prescindere dal
// provider (OpenAI, Anthropic, modelli locali...).
public class AgentOrchestrator
{
    private readonly ILLMClient _client;

    // Indicizzato per Name: permette di risalire dal ToolCall.FunctionName ricevuto dal
    // modello (una semplice stringa) all'implementazione C# reale da eseguire.
    private readonly Dictionary<string, ILocalTool> _tools;

    // L'unica "memoria" dell'agente: cresce ad ogni round (user, assistant, tool, ...)
    // e viene rimandata per intero al modello ogni volta (vedi LlmClient.StreamChatAsync).
    private readonly List<ChatMessage> _history;

    // Schema JSON dei tool (name/description/parameters) da allegare a OGNI richiesta,
    // così il modello sa sempre quali funzioni ha a disposizione. Tipizzato "object"
    // perché costruito con tipi anonimi in Program.cs, solo per essere serializzato.
    private readonly object _toolsDefinition;

    public AgentOrchestrator(ILLMClient client, IEnumerable<ILocalTool> tools, string systemPrompt, object toolsDefinition)
    {
        _client = client;
        _tools = tools.ToDictionary(t => t.Name);
        // Il primo messaggio è sempre il system prompt: definisce il "carattere" e le
        // istruzioni generali dell'agente, resta in history per tutta la sessione.
        _history = new List<ChatMessage> { new ChatMessage("system", systemPrompt) };
        _toolsDefinition = toolsDefinition;
    }

    public record RunResult(int Rounds, string? FinalMessage);

    public async Task<RunResult> RunAsync(string userPrompt, CancellationToken ct)
    {
        _history.Add(new ChatMessage("user", userPrompt));

        var round = 0;
        var lastMessage = "";

        while (true)
        {
            round++;
            _history.Add(new ChatMessage("assistant", lastMessage));

            // Il modello riceve tutta la history e può rispondere con testo o tool_calls.
            // Se include tool_calls, non li inviamo subito: li accumuliamo e li eseguiamo
            // dopo aver letto l'intero stream (vedi Orchestrator.RunAsync).
            var messages = _history.ToList();

            await foreach (var token in _client.StreamChatAsync(messages, ct, _toolsDefinition))
            {
                lastMessage += token;
            }

            // Recupera i tool_calls dallo stream (se il modello ha richiesto l'uso di tool).
            var streamResult = await _client.CollectStreamResultAsync(ct);
            if (streamResult.ToolCalls != null && streamResult.ToolCalls.Count > 0)
            {
                // Aggiorna l'ultimo messaggio assistant con i tool_calls raccolti dallo streaming.
                _history[_history.Count - 1] = new ChatMessage("assistant", streamResult.FullContent, streamResult.ToolCalls);
            }

            // Estraiamo i tool_calls dal messaggio assistant (se presenti) e li eseguiamo.
            var toolCalls = _history.Last().ToolCalls;

            if (toolCalls == null || toolCalls.Count == 0)
            {
                // Il modello ha risposto con solo testo: fine del round.
                _history.Add(new ChatMessage("assistant", lastMessage));
                return new RunResult(round, lastMessage);
            }

            // Il modello ha chiesto uno o più tool: eseguiamoli in parallelo.
            var toolResults = new List<ToolResult>();
            foreach (var tc in toolCalls)
            {
                if (!_tools.ContainsKey(tc.FunctionName))
                {
                    toolResults.Add(new ToolResult(tc.Id, $"Errore: tool '{tc.FunctionName}' non trovato."));
                    continue;
                }

                try
                {
                    var result = await _tools[tc.FunctionName].ExecuteAsync(tc.ArgumentsJson);
                    toolResults.Add(new ToolResult(tc.Id, result));
                }
                catch (Exception ex)
                {
                    toolResults.Add(new ToolResult(tc.Id, $"Errore: {ex.Message}"));
                }
            }

            // Aggiungiamo i risultati dei tool alla history come messaggi tool.
            foreach (var tr in toolResults)
            {
                _history.Add(new ChatMessage("tool", tr.Output, null, tr.ToolCallId));
            }

            // Il modello ha bisogno di un round extra per rispondere ai tool.
            lastMessage = "";
        }
    }
}
