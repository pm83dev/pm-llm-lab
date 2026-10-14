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
    // emessi come eventi (Token / ToolCallDelta). A fine streaming, CollectStreamResultAsync
    // restituisce il risultato completo del round.
    Task StreamChatAsync(List<ChatMessage> messages, object? tools, IEventSink sink, CancellationToken ct);
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

    public async Task StreamChatAsync(List<ChatMessage> messages, object? tools, IEventSink sink, CancellationToken ct)
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

        var toolsJson = tools != null ? JsonSerializer.Serialize(tools) : null;
        var requestBody = toolsJson != null
            ? $"{{\"model\":\"local\",\"messages\":{messagesJson},\"stream\":true,\"tools\":{toolsJson}}}"
            : $"{{\"model\":\"local\",\"messages\":{messagesJson},\"stream\":true}}";

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
                if (delta.TryGetProperty("content", out var content))
                {
                    var text = content.GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        _pendingResult!.FullContent += text;
                        await sink.EmitAsync(new LabEvent(LabEventType.Token, Data: new TokenData(text)), ct);
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
