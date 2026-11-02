namespace LocalLLM.Services;

using LocalLLM.Models;

// Accumulatore per UN round di streaming: OpenAILocalClient.StreamChatAsync riempie questo
// oggetto delta dopo delta man mano che leggiamo lo Server-Sent-Events dal server LLM.
// A fine streaming rappresenta "la risposta completa del modello per questo round",
// che può essere testo (FullContent), una richiesta di tool (ToolCalls), o entrambi.
public class StreamResult
{
    // Testo generato token per token (quello che viene stampato live in console).
    // Rimane vuoto se il modello ha risposto solo con tool call.
    public string FullContent { get; set; } = "";

    // Null finché il modello non inizia a emettere una function call nello stream;
    // valorizzato e riempito progressivamente (vedi LlmClient.cs) se lo fa.
    public List<ToolCall>? ToolCalls { get; set; } = null;

    // Timing: tempo totale della richiesta in millisecondi (solo Ollama, null per altri backend).
    public long? TotalTimeMs { get; set; } = null;

    // Prompt tokens ricevuti dal modello (solo Ollama, null se non disponibile).
    public int? PromptTokens { get; set; } = null;

    // Completion tokens generati dal modello (solo Ollama, null se non disponibile).
    public int? CompletionTokens { get; set; } = null;

    // Total tokens (prompt + completion).
    public int? TotalTokens { get; set; } = null;
}
