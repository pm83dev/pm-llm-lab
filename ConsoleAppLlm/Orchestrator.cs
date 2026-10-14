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

        Explain(
            "AVVIO CONVERSAZIONE",
            $"La history ora contiene {_history.Count} messaggi (system + user). " +
            "Ad ogni round invieremo TUTTA la history al modello: un LLM non ha memoria propria, " +
            "\"ricorda\" solo ciò che gli rimandiamo indietro nel prompt.");

        // Tetto di sicurezza: senza un limite, un modello che continua a chiedere tool
        // (magari sempre lo stesso, in loop, per un bug di prompt o di parsing) manderebbe
        // l'agente in un ciclo infinito di chiamate HTTP. Se lo si raggiunge è quasi
        // sempre sintomo di un problema a monte, non un comportamento "normale".
        const int maxRounds = 10;
        while (round < maxRounds)
        {
            round++;

            Explain(
                $"ROUND {round} — RICHIESTA AL MODELLO",
                $"Inviamo {_history.Count} messaggi + la lista dei {_tools.Count} tool disponibili ({string.Join(", ", _tools.Keys)}). " +
                "Il modello NON esegue nulla: può solo (a) scrivere testo in chiaro, oppure (b) emettere una " +
                "\"tool call\" strutturata (nome funzione + argomenti JSON) se pensa che un tool serva a rispondere.");

            // 1. Streaming della risposta
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"\n[LLM ROUND {round}]: ");
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
                Explain(
                    "RISPOSTA FINALE",
                    "Il modello non ha chiesto nessun tool: ha deciso di rispondere direttamente in linguaggio " +
                    "naturale con le informazioni che già aveva. Il ciclo termina qui.");
                break;
            }

            Explain(
                $"ROUND {round} — DECISIONE: USARE {streamResult.ToolCalls.Count} TOOL",
                "Il modello ha generato una richiesta di funzione invece di una risposta testuale. " +
                "Ora tocca al NOSTRO codice: leggiamo nome e argomenti, eseguiamo la funzione C# corrispondente " +
                "e restituiremo il risultato al modello nel prossimo round (il modello stesso non ha eseguito nulla).");

            // 3. Esecuzione Tool
            if (lastResponse.ToolCalls == null) break;
            foreach (var tc in lastResponse.ToolCalls)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  ① GREZZO DAL MODELLO (ricostruito dai frammenti di streaming):");
                Console.WriteLine($"     function.name      = \"{tc.FunctionName}\"");
                Console.WriteLine($"     function.arguments = {tc.ArgumentsJson}   (stringa JSON, non ancora parsata)");
                Console.WriteLine($"     id                 = {tc.Id}   (serve per riabbinare il risultato)");
                Console.ResetColor();

                if (!_tools.TryGetValue(tc.FunctionName, out var tool))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ② IL NOSTRO CODICE: '{tc.FunctionName}' non è nel Dictionary<string, ILocalTool> " +
                        "_tools -> il modello ha \"allucinato\" un nome mai dichiarato nello schema tools.");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: tool '{tc.FunctionName}' non esiste.", ToolCallId: tc.Id));
                    continue;
                }

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  ② IL NOSTRO CODICE: trovato in _tools[\"{tc.FunctionName}\"] -> chiamo " +
                    $"{tool.GetType().Name}.ExecuteAsync(argumentsJson), che fa il parsing del JSON e agisce " +
                    "sul filesystem/sistema reale. Il modello NON ha accesso diretto a nulla di tutto ciò.");
                Console.ResetColor();

                try
                {
                    var result = await tool.ExecuteAsync(tc.ArgumentsJson);
                    Console.ForegroundColor = ConsoleColor.Green;
                    var preview = result.Length > 200 ? result[..200] + $"…[troncato per la console, {result.Length} caratteri totali]" : result;
                    Console.WriteLine($"  ③ RISULTATO DA RIMANDARE AL MODELLO: {preview}");
                    Console.ResetColor();

                    // IMPORTANTE: Aggiungiamo il risultato con il ruolo 'tool'
                    // e l'ID corrispondente per far capire all'LLM a quale chiamata si riferisce
                    _history.Add(new ChatMessage("tool", result, ToolCallId: tc.Id));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ③ ECCEZIONE durante l'esecuzione: {ex.Message} " +
                        "-> la mandiamo comunque al modello come risultato, così può capire cos'è andato storto " +
                        "e magari riprovare con argomenti diversi.");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: {ex.Message}", ToolCallId: tc.Id));
                }
            }

            Explain(
                $"ROUND {round} — RISULTATO REINSERITO NELLA HISTORY",
                "Ogni risultato è stato aggiunto come messaggio con role=\"tool\" e lo stesso tool_call_id " +
                "della richiesta, così il modello può collegare \"a quale domanda risponde questo dato\". " +
                "Al prossimo giro il modello vedrà questi risultati e deciderà se rispondere o chiamare altri tool.");
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

    // Stampa un blocco didattico che spiega COSA sta per succedere e PERCHÉ,
    // per rendere visibile il meccanismo interno del ciclo agentico.
    private static void Explain(string title, string body)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\n┌─ 📘 {title}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"└─ {body}");
        Console.ResetColor();
    }
}