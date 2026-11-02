namespace LlmLab.Shared;

using LlmLab.Shared.Models;

// ============================================================================
// IEventSink: il "canale" attraverso cui orchestrator e client LLM emettono
// gli eventi del flusso in tempo reale. L'implementazione concreta (nell'API)
// li trasmette al frontend via Server-Sent Events.
// ============================================================================

public interface IEventSink
{
    Task EmitAsync(LabEvent evt, CancellationToken ct);
}

// Stima grezza del numero di token di una stringa (~4 caratteri per token).
// Non è un tokenizer vero, ma è sufficiente per dare un'idea al frontend.
public static class TokenEstimator
{
    public static int Estimate(string? text)
        => text == null ? 0 : (int)Math.Ceiling(text.Length / 4.0);
}
