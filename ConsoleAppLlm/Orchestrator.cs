namespace LocalLLM.Core;

using System.Text;
using LocalLLM.Models;
using LocalLLM.Services;
using LocalLLM.Tools;

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
    
    public async Task<RunResult> RunAsync(string input, CancellationToken ct)
    {
        var sb = new StringBuilder();
        _history.Add(new ChatMessage("user", input));
        int round = 0;
        string? finalMessage = null;

        // Tetto di sicurezza: senza un limite, un modello che continua a chiedere tool
        // (magari sempre lo stesso, in loop, per un bug di prompt o di parsing) manderebbe
        // l'agente in un ciclo infinito di chiamate HTTP. Se lo si raggiunge è quasi
        // sempre sintomo di un problema a monte, non un comportamento "normale".
        const int maxRounds = 10;
        while (round < maxRounds)
        {
            round++;

            // 1. Streaming della risposta
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"\n🤖 LLM [round {round}]: ");
            Console.ResetColor();

            await foreach (var token in _client.StreamChatAsync(_history, ct, _toolsDefinition))
            {
                Console.Write(token);
                sb.Append(token);
            }
            Console.WriteLine();

            // 2. Verifica se ci sono Tool Calls (raccolti dallo streaming)
            var streamResult = await _client.CollectStreamResultAsync(ct);
            var lastResponse = new ChatMessage("assistant", streamResult.FullContent, streamResult.ToolCalls);
            _history.Add(lastResponse);

            if (streamResult.ToolCalls?.Count == 0 || streamResult.ToolCalls == null)
            {
                break;
            }

            // Il modello ha chiesto di eseguire dei tool.
            foreach (var tc in lastResponse.ToolCalls)
            {
                if (!_tools.TryGetValue(tc.FunctionName, out var tool))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n⚠️  Tool '{tc.FunctionName}' non trovato (il modello ha 'allucinato')");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: tool '{tc.FunctionName}' non esiste.", ToolCallId: tc.Id));
                    continue;
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n🔧 Tool: {tc.FunctionName}({tc.ArgumentsJson})");
                Console.ResetColor();

                try
                {
                    var result = await tool.ExecuteAsync(tc.ArgumentsJson);
                    var preview = result.Length > 200 ? result[..200] + $"…[troncato, {result.Length} caratteri totali]" : result;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✅ {preview}");
                    Console.ResetColor();

                    _history.Add(new ChatMessage("tool", result, ToolCallId: tc.Id));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"❌ Errore: {ex.Message}");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: {ex.Message}", ToolCallId: tc.Id));
                }
            }
            // Il ciclo 'while' ricomincia: l'LLM riceve i risultati dei tool e genera la risposta finale
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[AGENT DONE] Total rounds: {round}");
        
        if (round >= maxRounds)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[AGENT ERROR] Max rounds ({maxRounds}) reached - possible loop detected");
            Console.ResetColor();
            finalMessage = $"Error: max rounds ({maxRounds}) reached";
        }
        
        return new RunResult(round, finalMessage);
    }

}