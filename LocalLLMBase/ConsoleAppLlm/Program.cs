using LocalLLM.Core;
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

// Punta a una classe che implementa ILLMClient (vedi LlmClient.cs)
// e di conseguenza una classe che implementa ILLMService (vedi LlmService.cs)
// (es. OpenAILocalClient, OpenAIOnlineClient...)
services.AddSingleton<ILLMClient, OpenAILocalClient>();

// Punta a una classe che implementa IPromptSchema che contiene system prompt e definizione dei tools
// che possono essere usati nel chat (vedi PromptSchema.cs)
services.AddSingleton<IPromptSchema, PromptSchema>();

// Ogni ILocalTool registrato qui viene raccolto da AgentOrchestrator (via
// IEnumerable<ILocalTool>) E deve avere una voce corrispondente in toolsDef sotto:
// le due liste sono tenute manualmente in sincrono, non generate l'una dall'altra.
services.AddSingleton<ILocalTool, GetSystemTimeTool>();
services.AddSingleton<ILocalTool, ReadFileTool>();
services.AddSingleton<ILocalTool, WriteFileTool>();
services.AddSingleton<ILocalTool, ShellCommandTool>();

// Ecco come viene registrato l'AgentOrchestrator:
// 1. Viene recuperato il ILLMClient.
// 2. Viene recuperato la lista di ILocalTools.
// 3. Viene recuperato il sistemaPrompt da IPromptSchema.
// 4. Viene recuperato il toolsDef da IPromptSchema.
// 5. Viene creato un nuovo AgentOrchestrator con i parametri ottenuti.
services.AddSingleton<AgentOrchestrator>(sp =>
{
    var client = sp.GetRequiredService<ILLMClient>();
    var tools = sp.GetServices<ILocalTool>().ToList();
    var systemPrompt = sp.GetService<IPromptSchema>()?.SystemPrompt ?? "You are an AI assistant.";
    var toolsDef = sp.GetService<IPromptSchema>()?.ToolsDef;
    Console.WriteLine($"SystemPrompt: {systemPrompt}");
    Console.WriteLine($"ToolsDef: {toolsDef}");
    Console.WriteLine($"Tools: {string.Join(", ", tools.Select(t => t.Name))}");
    Console.WriteLine("Initializing AgentOrchestrator...");
    return new AgentOrchestrator(client, tools, systemPrompt, toolsDef);
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

    var result = await orchestrator.RunAsync(input, cts.Token);
    Console.WriteLine($"\n🤖 LLM [round {result.Rounds}]: {result.FinalMessage}\n");

}
