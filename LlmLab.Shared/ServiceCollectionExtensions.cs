namespace LlmLab.Shared;

using LlmLab.Shared.Core;
using LlmLab.Shared.Services;
using LlmLab.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================
// Helper per il Dependency Injection: registra client LLM, tool e orchestrator
// in un colpo solo. L'host (API) chiama AddLlmLab(...) e poi configura l'HttpClient
// "llm" con l'indirizzo del server LLM.
// ============================================================================

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmLab(this IServiceCollection services)
    {
        services.AddSingleton<ILLMClient, OpenAILocalClient>();

        // Tool disponibili. Per aggiungerne uno nuovo: registra qui la classe
        // E aggiungi la voce corrispondente in BuildToolsDefinition().
        services.AddSingleton<ILocalTool, GetSystemTimeTool>();
        services.AddSingleton<ILocalTool, ReadFileTool>();
        services.AddSingleton<ILocalTool, WriteFileTool>();
        services.AddSingleton<ILocalTool, ShellCommandTool>();

        // (Futuro) Embedding e Reranking: quando implementerai le interfacce,
        // registra qui le implementazioni concrete, es.:
        // services.AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>();
        // services.AddSingleton<IReranker, CrossEncoderReranker>();

        services.AddSingleton<AgentOrchestrator>(sp =>
        {
            var client = sp.GetRequiredService<ILLMClient>();
            var tools = sp.GetServices<ILocalTool>().ToList();
            var systemPrompt = "Sei un assistente esperto. Usa i tool quando necessario per rispondere alle domande dell'utente.";
            return new AgentOrchestrator(client, tools, systemPrompt, BuildToolsDefinition());
        });

        return services;
    }

    // Schema JSON (formato function calling OpenAI) di ogni tool: è la SOLA cosa
    // che il modello vede per decidere se/come chiamarlo.
    public static object BuildToolsDefinition()
    {
        return new object[]
        {
            new {
                type = "function",
                @function = new {
                    name = "get_system_time",
                    description = "Restituisce l'ora corrente del sistema.",
                    parameters = new { type = "object", properties = new { }, required = new string[0] }
                }
            },
            new {
                type = "function",
                @function = new {
                    name = "read_file",
                    description = "Legge e restituisce il contenuto testuale di un file dato il suo percorso.",
                    parameters = new {
                        type = "object",
                        properties = new { path = new { type = "string", description = "Percorso del file da leggere." } },
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
                    name = "shell_command",
                    description = "Esegue un comando PowerShell e restituisce stdout, stderr e il codice di uscita.",
                    parameters = new {
                        type = "object",
                        properties = new { command = new { type = "string", description = "Il comando PowerShell da eseguire." } },
                        required = new[] { "command" }
                    }
                }
            }
        };
    }
}
