# ConsoleAppLlm

Applicazione console .NET per l'integrazione con modelli LLM locali (compatibile con API OpenAI).

## Struttura del progetto

```
ConsoleAppLlm/
├── ConsoleAppLlm.csproj    # Progetto .NET
├── Program.cs              # Entry point
├── Model.cs                # Modelli dati (ChatMessage, ToolCall, ToolResult)
├── ITools.cs               # Interfaccia ILocalTool e implementazioni
├── LlmClient.cs            # Interfaccia ILLMClient e implementazione OpenAILocalClient
├── Orchestrator.cs         # AgentOrchestrator: ciclo di tool use e gestione contesto
└── StreamResult.cs         # Helper per il trasporto di dati dallo streaming
```

## Componenti principali

### LlmClient
- **`ILLMClient`**: interfaccia per client LLM
- **`OpenAILocalClient`**: implementazione che comunica con server locali compatibili API OpenAI
- Supporta streaming (`StreamChatAsync`) e chiamate non-streaming
- Parsa automaticamente i `tool_calls` dallo streaming

### Orchestrator
- **`AgentOrchestrator`**: gestisce il ciclo di esecuzione agent
  1. Invia messaggio all'LLM con contesto e definizione tools
  2. Streaming della risposta con visualizzazione in tempo reale
  3. Rilevamento tool_calls e esecuzione tools
  4. Aggiornamento contesto con risultati
  5. Ripetizione fino a risposta testuale o max round

### Tools
- **`ILocalTool`**: interfaccia per tool personalizzati
- **`GetSystemTimeTool`**: tool di esempio che restituisce l'ora corrente

## Modelli dati

- **`ChatMessage`**: messaggio con ruolo, contenuto e tool_calls
- **`ToolCall`**: chiamata a funzione (nome, argomenti, ID)
- **`ToolResult`**: risultato di esecuzione tool

## Dipendenze

- .NET 8.0+
- System.Text.Json
- Microsoft.Extensions.Http (per IHttpClientFactory)

## Avvio

```bash
cd ConsoleAppLlm
dotnet run
```

## Limiti noti

- Il parsing dei tool_calls dallo streaming assume che il server invii `tool_calls` nei delta
- `_pendingResult` è un campo istanza: race condition se più stream sovrapposti sullo stesso client
- Il modello deve supportare l'API OpenAI per tool calling
