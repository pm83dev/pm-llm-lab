using LocalLLM.Core;
using LocalLLM.Services;
using LocalLLM.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

// Entry point + composition root: qui si "cablano" insieme client HTTP, tool disponibili
// e orchestratore. Non c'è nulla di specifico dell'agente qui dentro — è pura
// configurazione di dependency injection.
var services = new ServiceCollection();

// Punta a un server LLM locale/in rete che espone l'endpoint OpenAI-compatible
// /v1/chat/completions (es. LM Studio, Ollama, vLLM...). "local" come nome modello
// nel body della richiesta (vedi LlmClient.cs) è un placeholder: molti server locali
// ignorano il campo "model" e servono comunque il modello caricato.
services.AddHttpClient("llm", c =>
    c.BaseAddress = new Uri("http://100.121.40.27:9005/v1/chat/completions"));

services.AddSingleton<ILLMClient, OpenAILocalClient>();

// Ogni ILocalTool registrato qui viene raccolto da AgentOrchestrator (via
// IEnumerable<ILocalTool>) E deve avere una voce corrispondente in toolsDef sotto:
// le due liste sono tenute manualmente in sincrono, non generate l'una dall'altra.
services.AddSingleton<ILocalTool, GetSystemTimeTool>();
services.AddSingleton<ILocalTool, ReadFileTool>();
services.AddSingleton<ILocalTool, WriteFileTool>();
services.AddSingleton<ILocalTool, ShellCommandTool>();
services.AddSingleton<AgentOrchestrator>(sp =>
{
    var client = sp.GetRequiredService<ILLMClient>();
    var tools = sp.GetServices<ILocalTool>();
    var toolsList = tools.ToList();
    // Istruzione di sistema: è l'unico posto dove si può orientare il "comportamento
    // di default" del modello (tono, quando usare i tool, ecc.) senza toccare codice.
    var systemPrompt = "Sei un assistente esperto. Usa i tool quando necessario per rispondere alle domande dell'utente.";
    // Schema JSON (formato "function calling" OpenAI) di ogni tool: è la SOLA cosa che
    // il modello vede per decidere se/come chiamarlo — non vede il codice C# in ITools.cs.
    // "properties"/"required" seguono JSON Schema: dicono al modello quali argomenti
    // generare e quali sono obbligatori nella stringa JSON di ArgumentsJson.
    var toolsDef = new object[] {
        new {
            type = "function",
            @function = new {
                name = "get_system_time",
                description = "Restituisce l'ora corrente del sistema.",
                parameters = new {
                    type = "object",
                    properties = new { },
                    required = new string[0]
                }
            }
        },
        new {
            type = "function",
            @function = new {
                name = "read_file",
                description = "Legge e restituisce il contenuto testuale di un file dato il suo percorso.",
                parameters = new {
                    type = "object",
                    properties = new {
                        path = new { type = "string", description = "Percorso del file da leggere." }
                    },
                    required = new[] { "path" }
                }
            }
        },
        new {
            type = "function",
            @function = new {
                name = "write_file",
                description = "Scrive del contenuto testuale in un file al percorso specificato, sovrascrivendolo se già esiste.",
                parameters = new {
                    type = "object",
                    properties = new {
                        path = new { type = "string", description = "Percorso del file in cui scrivere." },
                        content = new { type = "string", description = "Contenuto testuale da scrivere nel file." }
                    },
                    required = new[] { "path", "content" }
                }
            }
        },
        new {
            type = "function",
            @function = new {
                name = "search_file",
                description = "Ricerca una parola nel contenuto di un file e restituisce le linee in cui è presente o elencare tutti i file che la contengono.",
                parameters = new {
                    type = "object",
                    properties = new {
                        type = "object",
                        properties = new { word = new { type = "string", description = "Parola da cercare." } }, required = new string[0] }
                }
            }
        },
       new {
    type = "function",
    @function = new {
        name = "shell_command",
        description = "Esegue un comando PowerShell e restituisce stdout, stderr e il codice di uscita. Utile per esplorare il filesystem, installare pacchetti, gestire processi, ecc.",
        parameters = new {
            type = "object",
            properties = new {
                command = new { type = "string", description = "Il comando PowerShell da eseguire." }
            },
            required = new[] { "command" }
        }
    }
}
    };
    return new AgentOrchestrator(client, toolsList, systemPrompt, toolsDef);
});

var serviceProvider = services.BuildServiceProvider();

// Un solo AgentOrchestrator per tutta la sessione: la sua _history interna cresce
// messaggio dopo messaggio ad ogni RunAsync, quindi il modello "ricorda" le richieste
// precedenti nella stessa esecuzione del programma (non tra un avvio e l'altro).
var orchestrator = serviceProvider.GetRequiredService<AgentOrchestrator>();
var cts = new CancellationTokenSource();

Console.WriteLine("=== C# Agentic Console Ready ===");
// Semplice REPL: legge una riga, la passa all'agente (che internamente può fare più
// round e chiamare tool), aspetta che finisca, richiede input. Nessuna concorrenza:
// un solo turno alla volta.
while (true)
{
    Console.Write("\nUser > ");
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input) || input == "exit") break;

    await orchestrator.RunAsync(input, cts.Token);
}