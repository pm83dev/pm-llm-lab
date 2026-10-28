namespace LlmLab.Shared.Services;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LlmLab.Shared.Models;

// ============================================================================
// ILLMClient: interfaccia per parlare con un server LLM OpenAI-compatible.
// A differenza della versione console, qui NON stampiamo in console: emettiamo
// eventi (via IEventSink) così il frontend può mostrare ogni passaggio.
// ============================================================================

public interface ILLMClient
{
    // Avvia lo streaming di una risposta. I token e i delta delle tool call vengono
    // emessi come eventi (Token / ToolCallDelta). Se includeLogprobs è true, per ogni
    // token viene emesso anche un evento TokenProb con probabilità e top-5 alternative.
    // A fine streaming, CollectStreamResultAsync restituisce il risultato completo del round.
    // temperature: se non null viene aggiunto al corpo della richiesta; se null il server usa il suo default.
    Task StreamChatAsync(List<ChatMessage> messages, object? tools, IEventSink sink, CancellationToken ct, bool includeLogprobs = false, double? temperature = null);
    Task<StreamResult> CollectStreamResultAsync(CancellationToken ct);
}

// Accumulatore per un round di streaming (stesso concetto di prima).
public class StreamResult
{
    public string FullContent { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; } = null;
}

// Client per un server compatibile con l'API "chat/completions" di OpenAI
// (LM Studio, Ollama in modalità OpenAI-compatible, vLLM, ecc.).
public class OpenAILocalClient : ILLMClient
{
    private readonly HttpClient _httpClient;
    private StreamResult? _pendingResult;

    public OpenAILocalClient(IHttpClientFactory factory)
    {
        _httpClient = factory.CreateClient("llm");
    }

    public async Task StreamChatAsync(List<ChatMessage> messages, object? tools, IEventSink sink, CancellationToken ct, bool includeLogprobs = false, double? temperature = null)
    {
        _pendingResult = new StreamResult();

        // Ricostruiamo il JSON "messages" nel formato che l'API OpenAI-compatible si aspetta.
        var messagesJson = JsonSerializer.Serialize(messages.Select(m => (object)(
            m.Role switch
            {
                "assistant" when m.ToolCalls is { Count: > 0 } => new
                {
                    role = m.Role,
                    content = string.IsNullOrEmpty(m.Content) ? null : m.Content,
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.FunctionName, arguments = tc.ArgumentsJson }
                    })
                },
                "tool" => new
                {
                    role = m.Role,
                    content = m.Content,
                    tool_call_id = m.ToolCallId
                },
                _ => new { role = m.Role, content = m.Content }
            }
        )));

        // Se il client lo richiede, chiediamo al server i logprobs (top-5 alternative per token).
        // Il server llama.cpp rifiuta la combinazione tools + stream + logprobs (HTTP 400),
        // quindi quando i logprobs sono attivi non inviamo affatto i tool: il modello
        // risponde direttamente senza fare tool call.
        var toolsJson = includeLogprobs ? null : (tools != null ? JsonSerializer.Serialize(tools) : null);
        var logprobsJson = includeLogprobs ? ",\"logprobs\":true,\"top_logprobs\":5" : "";
        // Temperature opzionale: se specificata viene aggiunta al corpo, altrimenti il server usa il suo default.
        var tempJson = temperature.HasValue ? $",\"temperature\":{temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var requestBody = toolsJson != null
            ? $"{{\"model\":\"local\",\"messages\":{messagesJson},\"stream\":true,\"tools\":{toolsJson}{logprobsJson}{tempJson}}}"
            : $"{{\"model\":\"local\",\"messages\":{messagesJson},\"stream\":true{logprobsJson}{tempJson}}}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

        // Protocollo Server-Sent Events: ogni riga utile inizia con "data: ".
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line) || line == "data: [DONE]") continue;

            if (line.StartsWith("data: "))
            {
                var json = line["data: ".Length..];
                using var doc = JsonDocument.Parse(json);
                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");

                // Caso 1: testo normale -> emettiamo un evento Token.
                // Attenzione: i server OpenAI-compatible possono inviare "content": null
                // nel primo chunk (solo role): GetString() su null restituisce null, va gestito.
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        _pendingResult!.FullContent += text;
                        await sink.EmitAsync(new LabEvent(LabEventType.Token, Data: new TokenData(text)), ct);

                        // Se richiesti, estraiamo i logprobs del chunk e li convertiamo in probabilità.
                        if (includeLogprobs && doc.RootElement.TryGetProperty("choices", out var choices)
                            && choices.GetArrayLength() > 0
                            && choices[0].TryGetProperty("logprobs", out var logprobs)
                            && logprobs.ValueKind == JsonValueKind.Object
                            && logprobs.TryGetProperty("content", out var lpContent)
                            && lpContent.GetArrayLength() > 0)
                        {
                            foreach (var item in lpContent.EnumerateArray())
                            {
                                if (!item.TryGetProperty("token", out var tokProp)) continue;
                                var token = tokProp.GetString() ?? "";
                                // Il logprob del token scelto: exp(logprob) -> probabilità 0..1.
                                double prob = 0;
                                if (item.TryGetProperty("logprob", out var lpProp) && lpProp.ValueKind == JsonValueKind.Number)
                                    prob = Math.Clamp(Math.Exp(lpProp.GetDouble()), 0, 1);

                                var alternatives = new List<TokenAlternative>();
                                if (item.TryGetProperty("top_logprobs", out var topLp) && topLp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var alt in topLp.EnumerateArray())
                                    {
                                        if (!alt.TryGetProperty("token", out var altTok)) continue;
                                        double altProb = 0;
                                        if (alt.TryGetProperty("logprob", out var altLp) && altLp.ValueKind == JsonValueKind.Number)
                                            altProb = Math.Clamp(Math.Exp(altLp.GetDouble()), 0, 1);
                                        alternatives.Add(new TokenAlternative(altTok.GetString() ?? "", altProb));
                                    }
                                }

                                await sink.EmitAsync(new LabEvent(LabEventType.TokenProb, Data: new TokenProbData(token, prob, alternatives)), ct);
                            }
                        }
                    }
                }

                // Caso 2: tool call in costruzione (arriva a frammenti).
                if (delta.TryGetProperty("tool_calls", out var toolCallsDelta) && toolCallsDelta.GetArrayLength() > 0)
                {
                    if (_pendingResult.ToolCalls == null)
                        _pendingResult.ToolCalls = new List<ToolCall>();

                    foreach (var tc in toolCallsDelta.EnumerateArray())
                    {
                        var index = tc.GetProperty("index").GetInt32();
                        if (index >= _pendingResult.ToolCalls.Count)
                            _pendingResult.ToolCalls.Add(new ToolCall("", "", ""));

                        var call = _pendingResult.ToolCalls[index];
                        string? idDelta = null;
                        string? nameDelta = null;
                        var argsDelta = "";

                        if (tc.TryGetProperty("id", out var idProp))
                        {
                            idDelta = idProp.GetString();
                            call = new ToolCall(call.FunctionName, call.ArgumentsJson, idDelta ?? call.Id);
                        }
                        if (tc.TryGetProperty("function", out var func))
                        {
                            if (func.TryGetProperty("name", out var nameProp))
                            {
                                nameDelta = nameProp.GetString();
                                call = new ToolCall(nameDelta ?? call.FunctionName, call.ArgumentsJson, call.Id);
                            }
                            if (func.TryGetProperty("arguments", out var argsProp))
                            {
                                argsDelta = argsProp.GetString() ?? "";
                            }
                            _pendingResult.ToolCalls[index] = new ToolCall(call.FunctionName, call.ArgumentsJson + argsDelta, call.Id);
                        }

                        await sink.EmitAsync(new LabEvent(LabEventType.ToolCallDelta, Data: new ToolCallDeltaData(index, idDelta, nameDelta, argsDelta)), ct);
                    }
                }
            }
        }
    }

    public Task<StreamResult> CollectStreamResultAsync(CancellationToken ct)
    {
        if (_pendingResult == null)
            throw new InvalidOperationException("Nessuno streaming in corso.");
        var result = _pendingResult;
        _pendingResult = null;
        return Task.FromResult(result);
    }
}
