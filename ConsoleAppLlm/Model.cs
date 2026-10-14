namespace LocalLLM.Models;

// Rappresenta UN messaggio della conversazione, così come lo intende l'API OpenAI-compatible.
// L'intera "memoria" dell'agente è solo una List<ChatMessage>: non c'è nessuno stato nascosto
// nel modello, ogni round gli rimandiamo tutta la lista da capo (vedi AgentOrchestrator._history).
public record ChatMessage(
    // "system" = istruzioni di comportamento, "user" = input umano,
    // "assistant" = risposta del modello (testo e/o tool_calls), "tool" = risultato di un tool.
    string Role,
    string Content,
    // Valorizzato solo sui messaggi role="assistant" quando il modello, invece di rispondere
    // in chiaro, ha chiesto di eseguire una o più funzioni.
    List<ToolCall>? ToolCalls = null,
    // Valorizzato solo sui messaggi role="tool": collega il risultato alla specifica
    // tool call che lo ha generato (un turno può contenerne più di una in parallelo).
    string? ToolCallId = null // Necessario per la coerenza con le API OpenAI
);

// Una singola "richiesta di funzione" generata dal modello: nome + argomenti (come stringa JSON
// grezza, non ancora parsata) + un id univoco usato per riabbinare il risultato in ChatMessage.ToolCallId.
public record ToolCall(string FunctionName, string ArgumentsJson, string Id);

// Non usato dal flusso attuale (il risultato viaggia già dentro un ChatMessage con ToolCallId):
// tenuto come forma alternativa/di comodo per rappresentare l'esito di un tool.
public record ToolResult(string ToolCallId, string Output);
