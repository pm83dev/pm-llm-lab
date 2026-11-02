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

        const int maxRounds = 10;

        // ──────────────────────────────────────────────────────────────
        // LEZIONE 1: la storia della conversazione
        // Un LLM non ha memoria propria. Gli mandiamo tutta la storia
        // ad ogni round. Ogni messaggio ha un "role" che dice al modello
        // chi ha parlato: system, user, assistant o tool.
        // ──────────────────────────────────────────────────────────────
        PrintSeparator("LEZIONE 1 — LA STORIA DELLA CONVERSAZIONE");
        Console.WriteLine("  Un LLM non ha memoria propria. Gli rimandiamo tutta la storia ad ogni round.");
        Console.WriteLine("  Ad ogni messaggio assegniamo un 'role':");
        Console.WriteLine();
        foreach (var msg in _history)
        {
            var roleColor = msg.Role switch
            {
                "system" => ConsoleColor.DarkGray,
                "user" => ConsoleColor.White,
                "assistant" => ConsoleColor.Cyan,
                "tool" => ConsoleColor.DarkGreen,
                _ => ConsoleColor.Gray
            };
            Console.ForegroundColor = roleColor;
            Console.Write($"    [{msg.Role,-7}] ");
            Console.ResetColor();

            var preview = msg.Content.Length > 120 ? msg.Content[..120] + "…" : msg.Content;
            Console.WriteLine(preview);

            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"      ↳ tool_call: {tc.FunctionName}({tc.ArgumentsJson})  id={tc.Id}");
                    Console.ResetColor();
                }
            }
        }
        Console.WriteLine();

        while (round < maxRounds)
        {
            round++;

            // ──────────────────────────────────────────────────────────
            // LEZIONE 2a: IL TOKENIZER — da testo a numeri
            // Prima che il modello veda il tuo input, un componente
            // chiamato "tokenizer" lo converte in una lista di interi.
            // Ogni token è un frammento di parola (non un carattere!).
            // Es: "Hello world" → [15496, 995]  (2 numeri, non 10 caratteri)
            // Il tokenizer usa un vocabolario fisso di ~32.000-250.000 token
            // (dipende dal modello). I token più comuni hanno ID bassi.
            // ──────────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  LEZIONE 2a — IL TOKENIZER (testo → numeri)              ║");
            Console.WriteLine($"╠══════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Il testo viene spezzato in token. Esempio:              ║");
            Console.WriteLine($"║                                                          ║");
            Console.WriteLine($"║  \"Hello world\"     → [15496, 995]   (2 token)           ║");
            Console.WriteLine($"║  \"Ciao mondo\"      → [~token_id, ~token_id]  (simile)   ║");
            Console.WriteLine($"║  \"unbelievable\"    → [~un, ~be, ~lie, ~va, ~ble]        ║");
            Console.WriteLine($"║                                                          ║");
            Console.WriteLine($"║  • Token ≠ carattere. Un token è un frammento di parola. ║");
            Console.WriteLine($"║  • Il vocabolario è fisso: il modello conosce solo       ║");
            Console.WriteLine($"║    ~32K-250K token (dipende dal modello).               ║");
            Console.WriteLine($"║  • Token sconosciuti → spezzati in sotto-token.          ║");
            Console.WriteLine($"║  • Ogni messaggio ha un ID di segmento per separarli.    ║");
            Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // ──────────────────────────────────────────────────────────
            // LEZIONE 2b: LA RICHIESTA HTTP — cosa mandiamo al server
            // Costruiamo il JSON che viene inviato al server LLM.
            // Il formato è l'API "chat/completions" di OpenAI, ma
            // funziona con qualsiasi server compatibile (Ollama, LM Studio...).
            // ──────────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  LEZIONE 2b — RICHIESTA AL SERVER                        ║");
            Console.WriteLine($"╠══════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Inviamo {_history.Count} messaggi + {_tools.Count} tool disponibili.    ║");
            Console.WriteLine($"╠══════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Il modello NON esegue nulla. Può solo:                  ║");
            Console.WriteLine($"║  (a) scrivere testo in chiaro                            ║");
            Console.WriteLine($"║  (b) emettere una 'tool call' strutturata                ║");
            Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // ──────────────────────────────────────────────────────────
            // LEZIONE 3: IL TRANSFORMER — il motore decisionale
            // Dentro il modello c'è un'architettura "Transformer" che:
            // 1) Prende tutti i token in input (la tua storia conversazionale)
            // 2) Li proietta in vettori di dimensioni (~4096 per GPT-2, ~8192 per GPT-4)
            // 3) Usa "self-attention" per capire quali token si influenzano a vicenda
            // 4) Produce un vettore di output per ogni posizione
            // 5) L'ultimo vettore viene passato a un "classificatore" che dice:
            //    "qual è la probabilità di ogni possibile token next?"
            // ──────────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  LEZIONE 3 — IL TRANSFORMER (cervello del modello)       ║");
            Console.WriteLine($"╠══════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║                                                          ║");
            Console.WriteLine($"║  INPUT: lista di token ID                                ║");
            Console.WriteLine($"║    ↓                                                     ║");
            Console.WriteLine($"║  Embedding layer: token ID → vettore float               ║");
            Console.WriteLine($"║    ↓                                                     ║");
            Console.WriteLine($"║  Strati Transformer (N layers, es. 12 per GPT-2)        ║");
            Console.WriteLine($"║    • Self-Attention: ogni token guarda gli altri         ║");
            Console.WriteLine($"║    • Feed-Forward: trasforma le informazioni             ║");
            Console.WriteLine($"║    • Layer Normalization + Residual Connection           ║");
            Console.WriteLine($"║    ↓                                                     ║");
            Console.WriteLine($"║  Logits: vettore di probabilità su tutto il vocabolario  ║");
            Console.WriteLine($"║    es: [0.02, 0.45, 0.01, 0.12, ...] → ~32K valori      ║");
            Console.WriteLine($"║    ↓                                                     ║");
            Console.WriteLine($"║  Sampling (softmax + temperatura)                        ║");
            Console.WriteLine($"║    ↓                                                     ║");
            Console.WriteLine($"║  OUTPUT: singolo token ID                                ║");
            Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // 1. Streaming della risposta
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("🤖 Risposta: ");
            Console.ResetColor();

            await foreach (var token in _client.StreamChatAsync(_history, ct, _toolsDefinition))
            {
                Console.Write(token);
                sb.Append(token);
            }
            Console.WriteLine();

            // ──────────────────────────────────────────────────────────
            // LEZIONE 4: SAMPLING — come viene scelto il token successivo
            // Dopo che il Transformer produce le probabilità (logits),
            // un processo di "sampling" sceglie UN solo token. I metodi:
            //
            // TEMPERATURE (t):
            //   t < 1  → più deterministico, sceglie i token più probabili
            //   t = 1  → nessuna modifica alle probabilità originali
            //   t > 1  → più casuale, appiana le probabilità
            //
            // TOP-P (nucleare):
            //   Si prendono solo i token la cui probabilità cumulata
            //   raggiunge P (es. 0.9). Es: se P=0.9 e i token sono
            //   [A:40%, B:25%, C:15%, D:10%, ...], si prendono A,B,C
            //   (40+25+15=80%) → si aggiunge D (80+10=90%) → stop.
            //   Solo questi 3 concorrono alla selezione finale.
            //
            // QUESTO LOOP avviene per OGNI token generato:
            //   1. Transformer → logits su 32K token
            //   2. Sampling → sceglie 1 token
            //   3. Il token viene aggiunto all'input
            //   4. Si riparte da 1 con un input più lungo
            // ──────────────────────────────────────────────────────────

            // Prima raccolgo il risultato dallo streaming (il client ha già
            // accumulato tutto durante lo yield return)
            var streamResult = await _client.CollectStreamResultAsync(ct);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine();
            PrintSeparator("LEZIONE 4 — SAMPLING (scelta del token)");
            Console.WriteLine($"  Il modello ha generato \"{streamResult.FullContent?.Length ?? 0}\" caratteri.");
            if (streamResult.ToolCalls != null && streamResult.ToolCalls.Count > 0)
            {
                Console.WriteLine($"  Ha anche emesso {streamResult.ToolCalls.Count} tool call(s).");
            }
            Console.WriteLine();
            Console.WriteLine("  Ogni token è stato scelto con questo processo:");
            Console.WriteLine();
            Console.WriteLine("    ┌─────────────┐     ┌──────────┐     ┌─────────┐");
            Console.WriteLine("    │   Logits    │────▶│  Top-P   │────▶│ Sampling│");
            Console.WriteLine("    │ ~32K valori │     │ Filtra   │     │ +Temp.  │");
            Console.WriteLine("    │ (probabilità)│    │ nel nucleo│     │ (scelto)│");
            Console.WriteLine("    └─────────────┘     └──────────┘     └────┬────┘");
            Console.WriteLine("                                               │");
            Console.WriteLine("                                               ▼");
            Console.WriteLine("                                         1 solo token!");
            Console.WriteLine();

            // ──────────────────────────────────────────────────────────
            // LEZIONE 4b: METRICHE DI PERFORMANCE (dal server LLM)
            // I backend come Ollama tornano nel response SSE i conteggi
            // dei token usati e il tempo impiegato. Questi dati servono
            // per capire quanto costa (in tempo) ogni richiesta.
            // ──────────────────────────────────────────────────────────
            if (streamResult.PromptTokens.HasValue || streamResult.TotalTimeMs.HasValue)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                PrintSeparator("LEZIONE 4b — METRICHE DI PERFORMANCE (dal server LLM)");
                Console.WriteLine($"  🔢 Prompt tokens:    {streamResult.PromptTokens ?? 0}  (input: la tua domanda + storia + tool)");
                Console.WriteLine($"  ✍️  Completion tokens:{streamResult.CompletionTokens ?? 0}  (output generato dal modello)");
                Console.WriteLine($"  📊 Total tokens:     {streamResult.TotalTokens ?? 0}  (prompt + completion)");
                if (streamResult.TotalTimeMs.HasValue)
                {
                    var ms = streamResult.TotalTimeMs.Value;
                    var secs = ms / 1000.0;
                    var tps = streamResult.CompletionTokens.HasValue && streamResult.CompletionTokens.Value > 0
                        ? (streamResult.CompletionTokens.Value / (ms / 1000.0)).ToString("F1")
                        : "N/A";
                    Console.WriteLine($"  ⏱️  Tempo totale:      {ms} ms ({secs:F2}s)");
                    Console.WriteLine($"  🚀 Velocità:           {tps} tokens/sec (completion tokens / secondi)");
                }
                else
                {
                    Console.WriteLine($"  ⏱️  Tempo totale:      non disponibile (backend non lo espone)");
                }
                Console.WriteLine();
                Console.WriteLine("  Come funziona:");
                Console.WriteLine("  • Ollama/LM Studio inseriscono \"usage\" in ogni chunk SSE");
                Console.WriteLine("  • Il client C# legge \"prompt_tokens\", \"completion_tokens\"");
                Console.WriteLine("  • total_duration è in nanosecondi → convertito in millisecondi");
                Console.WriteLine("  • tokens/sec = completion_tokens ÷ tempo_in_secondi");
                Console.WriteLine();
                Console.ResetColor();
            }

            // Creiamo il messaggio assistant con i dati raccolti
            var lastResponse = new ChatMessage("assistant", streamResult.FullContent, streamResult.ToolCalls);
            _history.Add(lastResponse);



            // ──────────────────────────────────────────────────────────
            // LEZIONE 5: decisione del modello
            // Il modello ha scelto se rispondere in chiaro o chiamare
            // un tool. Se chiama un tool, il nostro codice deve
            // interpretare la richiesta e eseguirla.
            // ──────────────────────────────────────────────────────────
            if (streamResult.ToolCalls == null || streamResult.ToolCalls.Count == 0)
            {
                Console.WriteLine();
                PrintSeparator("LEZIONE 5 — DECISIONE DEL MODELLO");
                Console.WriteLine("  ✅ Il modello ha risposto in linguaggio naturale.");
                Console.WriteLine("  Nessun tool chiamato. La conversazione termina qui.");
                break;
            }

            PrintSeparator($"LEZIONE 5 — IL MODELLO HA CHIESTO {streamResult.ToolCalls.Count} TOOL(S)");
            Console.WriteLine("  Il modello ha generato una 'richiesta di funzione' invece di una risposta.");
            Console.WriteLine("  NOSSO CODICE ora interpreta la richiesta e la esegue.");
            Console.WriteLine();

            // 3. Esecuzione Tool
            foreach (var tc in lastResponse.ToolCalls)
            {
                PrintSeparator($"LEZIONE 6 — TOOL CALL: {tc.FunctionName}");
                Console.WriteLine($"  ID chiamata:  {tc.Id}");
                Console.WriteLine($"  Funzione:     {tc.FunctionName}");
                Console.WriteLine($"  Argomenti:    {tc.ArgumentsJson}");
                Console.WriteLine();

                if (!_tools.TryGetValue(tc.FunctionName, out var tool))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  ❌ Tool non trovato nel dizionario locale.");
                    Console.WriteLine("     Il modello ha 'allucinato' un nome mai dichiarato.");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: tool '{tc.FunctionName}' non esiste.", ToolCallId: tc.Id));
                    continue;
                }

                // ──────────────────────────────────────────────────────
                // LEZIONE 6: esecuzione del tool
                // Il modello genera SOLO testo. Siamo NOI che eseguiamo
                // il codice C# corrispondente. Il modello non ha accesso
                // al filesystem, ai processi o a qualsiasi altra risorsa.
                // ──────────────────────────────────────────────────────
                Console.WriteLine($"  🔧 Eseguo: {tool.GetType().Name}.ExecuteAsync()");
                Console.WriteLine("     (il modello NON esegue nulla, siamo noi che lo facciamo)");
                Console.WriteLine();

                try
                {
                    var result = await tool.ExecuteAsync(tc.ArgumentsJson);

                    PrintSeparator($"LEZIONE 7 — RISULTATO DEL TOOL");
                    var preview = result.Length > 300 ? result[..300] + $"…[{result.Length} caratteri totali]" : result;
                    Console.WriteLine($"  Output: {preview}");
                    Console.WriteLine();

                    // ──────────────────────────────────────────────────
                    // LEZIONE 7: il messaggio 'tool'
                    // Il risultato viene rimandato al modello con role='tool'
                    // e tool_call_id per collegarlo alla richiesta originale.
                    // Questo è il "collante" che permette al modello di sapere
                    // a quale chiamata si riferisce ogni risultato.
                    // ──────────────────────────────────────────────────
                    PrintSeparator("LEZIONE 8 — RISULTATO RIMANDATO AL MODELLO");
                    Console.WriteLine($"  Aggiungo alla storia un messaggio:");
                    Console.WriteLine($"    role   = 'tool'");
                    Console.WriteLine($"    tool_call_id = '{tc.Id}'  ← collega il risultato alla chiamata");
                    var contentPreview = result.Length > 100 ? result[..100] + "…" : result;
                    Console.WriteLine($"    content = '{contentPreview}'");
                    Console.WriteLine();
                    Console.WriteLine("  Al prossimo round, il modello vedrà questo risultato e deciderà:");
                    Console.WriteLine("  - Rispondere direttamente (se ha abbastanza informazioni)");
                    Console.WriteLine("  - Chiamare altri tool (se serve più dati)");
                    Console.WriteLine();

                    _history.Add(new ChatMessage("tool", result, ToolCallId: tc.Id));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ❌ Errore esecuzione: {ex.Message}");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", $"Error: {ex.Message}", ToolCallId: tc.Id));
                }
            }

            // Il ciclo ricomincia
        }

        if (round >= maxRounds)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[AGENT ERROR] Max rounds ({maxRounds}) raggiunto — possibile loop infinito.");
            Console.ResetColor();
            finalMessage = $"Error: max rounds ({maxRounds}) reached";
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[AGENT DONE] Total rounds: {round}");

        return new RunResult(round, finalMessage);
    }

    private static void PrintSeparator(string title)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"┌─ {title}");
        Console.ResetColor();
    }

}
