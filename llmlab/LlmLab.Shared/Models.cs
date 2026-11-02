namespace LlmLab.Shared.Models;

// ============================================================================
// Modelli di base della conversazione (stessi concetti dell'API OpenAI-compatible)
// ============================================================================

// Un messaggio della conversazione. L'intera "memoria" dell'agente è una lista di
// questi oggetti: il modello non ha memoria propria, ogni round gli rimandiamo
// tutta la lista da capo.
public record ChatMessage(
    string Role,
    string Content,
    List<ToolCall>? ToolCalls = null,
    string? ToolCallId = null
);

// Una singola richiesta di funzione generata dal modello: nome + argomenti (stringa
// JSON grezza) + id univoco usato per riabbinare il risultato.
public record ToolCall(string FunctionName, string ArgumentsJson, string Id);

// ============================================================================
// Eventi del flusso: sono il "linguaggio" con cui il backend comunica al frontend
// ogni passaggio in tempo reale. Ogni evento ha un tipo (EventType) e un payload
// (Data) serializzato in JSON. Il frontend li disegna come una timeline.
// ============================================================================

// Tipi di evento emessi durante l'esecuzione dell'agente.
public enum LabEventType
{
    // --- Ciclo di vita ---
    RunStarted,        // l'esecuzione è iniziata
    RunCompleted,      // l'esecuzione è terminata (con esito)
    RunFailed,         // l'esecuzione è fallita con un'eccezione

    // --- Round ---
    RoundStarted,      // inizia un round (una chiamata al modello)
    RoundCompleted,    // termina un round

    // --- Prompt / contesto ---
    PromptSent,        // la history completa inviata al modello (con token stimati)

    // --- Streaming ---
    Token,             // un frammento di testo generato dal modello (token-by-token)
    TokenProb,         // un token con la sua probabilità e le top-N alternative (solo se richiesto)
    ToolCallDelta,     // un frammento di una tool call in costruzione (id/name/args)

    // --- Tool ---
    ToolCallDetected,  // il modello ha chiesto di eseguire un tool (nome + argomenti)
    ToolExecuting,     // il nostro codice sta eseguendo il tool
    ToolResult,        // il tool ha prodotto un risultato (o un errore)

    // --- Risposta finale ---
    FinalAnswer        // la risposta finale dell'agente
}

// Un singolo evento del flusso. `Data` è un oggetto arbitrario (tipicamente un
// record tipizzato) che viene serializzato in JSON e inviato al frontend.
public record LabEvent(
    LabEventType Type,
    int? Round = null,
    long TimestampMs = 0,
    object? Data = null
);

// Payload tipizzati per i vari eventi (usati in Data).
public record RunStartedData(string Prompt, int ToolCount, string[] ToolNames);
public record RunCompletedData(int TotalRounds, long TotalDurationMs, string? FinalMessage);
public record RunFailedData(string Error, string? Details);

public record RoundStartedData(int Round, int HistoryCount, int ToolCount);
public record RoundCompletedData(int Round, long DurationMs, bool HasToolCalls);

// La history completa inviata al modello in questo round.
public record PromptSentData(
    int Round,
    int MessageCount,
    int EstimatedTokens,
    List<PromptMessageView> Messages,
    string[] ToolNames
);

// Vista leggibile di un messaggio della history (per il pannello "prompt").
public record PromptMessageView(string Role, string Content, List<ToolCallView>? ToolCalls = null, string? ToolCallId = null);
public record ToolCallView(string FunctionName, string ArgumentsJson, string Id);

public record TokenData(string Text);

// Un token con la sua probabilità (0..1) e le alternative più probabili al suo posto.
// `prob` è exp(logprob) del token scelto; `alternatives` sono le top-N esclusive,
// ordinate per probabilità decrescente (il token scelto non c'è).
public record TokenProbData(string Token, double Prob, List<TokenAlternative> Alternatives);
public record TokenAlternative(string Token, double Prob);

public record ToolCallDeltaData(int Index, string? Id, string? Name, string ArgumentsDelta);

public record ToolCallDetectedData(string FunctionName, string ArgumentsJson, string Id);
public record ToolExecutingData(string FunctionName, string ArgumentsJson, string Id);
public record ToolResultData(string FunctionName, string Id, string Output, bool IsError, long DurationMs);

public record FinalAnswerData(string Content, int TotalRounds, long TotalDurationMs);

// ============================================================================
// Interfacce per le fasi future: embedding e reranking.
// Sono già definite così che l'architettura sia pronta: quando vorrai aggiungere
// RAG (retrieval-augmented generation), basterà implementare queste interfacce e
// registrarle nel DI — il resto del flusso non cambia.
// ============================================================================

// Genera vettori (embedding) da testo. In futuro: Ollama /v1/embeddings, LM Studio,
// o un modello dedicato (es. nomic-embed-text, bge-small).
public interface IEmbeddingClient
{
    // Restituisce un vettore float per ogni testo in input.
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

// Reranker: dato un query e una lista di documenti candidati (con score grezzi,
// es. da ricerca vettoriale), li riordina per rilevanza. In futuro: un modello
// cross-encoder (es. bge-reranker) o un endpoint dedicato.
public interface IReranker
{
    Task<IReadOnlyList<RerankedDocument>> RerankAsync(
        string query,
        IReadOnlyList<CandidateDocument> candidates,
        int topK = 5,
        CancellationToken ct = default);
}

public record CandidateDocument(string Id, string Text, double Score);
public record RerankedDocument(string Id, string Text, double Score, int Rank);
