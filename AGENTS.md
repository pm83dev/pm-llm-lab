# AGENTS.md

Questo file contiene le informazioni e le istruzioni per gli agenti di sviluppo che lavorano su questo progetto.

## Struttura del progetto

```
ConsoleAppLlm/
├── ConsoleAppLlm.csproj    # Progetto .NET 8.0+
├── Program.cs              # Entry point
├── Model.cs                # Modelli dati (ChatMessage, ToolCall, ToolResult)
├── ITools.cs               # Interfaccia ILocalTool e implementazioni
├── LlmClient.cs            # Interfaccia ILLMClient e OpenAILocalClient
├── Orchestrator.cs         # AgentOrchestrator: ciclo di tool use
└── StreamResult.cs         # Helper per dati streaming
```

## Componenti chiave

### LlmClient (LlmClient.cs)
- **`ILLMClient`**: interfaccia per client LLM
- **`OpenAILocalClient`**: implementazione API OpenAI compatibile
- **`StreamChatAsync`**: streaming con parsing tool_calls integrato
- **`CollectStreamResultAsync`**: recupero risultato post-streaming

### Orchestrator (Orchestrator.cs)
- **`AgentOrchestrator`**: ciclo agent con tool use
  - Max 10 round (configurabile)
  - Parsing tool_calls dallo streaming
  - Esecuzione tools e aggiornamento contesto

### Tools (ITools.cs)
- **`ILocalTool`**: interfaccia tool personalizzati
- **`GetSystemTimeTool`**: tool esempio

## Modelli dati (Model.cs)
- **`ChatMessage`**: record con Role, Content, ToolCalls, ToolCallId
- **`ToolCall`**: record con FunctionName, ArgumentsJson, Id
- **`ToolResult`**: record con ToolCallId, Output

## Istruzioni per modifiche

1. **Modifiche a LlmClient.cs**: attenzione a `_pendingResult` (race condition se stream sovrapposti)
2. **Modifiche a Orchestrator.cs**: il parsing tool_calls assume struttura OpenAI API
3. **Nuovi tools**: implementare `ILocalTool` in ITools.cs o file separati
4. **Build**: `dotnet build` o `dotnet run` da ConsoleAppLlm/

## Limiti noti
- Parsing tool_calls dallo streaming: funziona solo se server invia tool_calls nei delta
- `_pendingResult`: campo istanza, non thread-safe per stream multipli concorrenti
- Compatibilità: richiede server LLM locale con API OpenAI compatibile
