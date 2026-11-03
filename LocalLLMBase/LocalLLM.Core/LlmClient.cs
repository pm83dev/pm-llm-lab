namespace LocalLLM.Core;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public interface ILLMClient
{
    // IAsyncEnumerable<string>: il chiamante può fare "await foreach" e stampare i token
    // man mano che arrivano (streaming), invece di aspettare la risposta intera.
    // I tool_calls, però, non passano di qui: vengono accumulati internamente e vanno
    // letti dopo, con CollectStreamResultAsync.
    IAsyncEnumerable<string> StreamChatAsync(List<ChatMessage> messages, CancellationToken ct, object? tools = null);
    Task<StreamResult> CollectStreamResultAsync(CancellationToken ct);
}

// Client per un server compatibile con l'API "chat/completions" di OpenAI (LM Studio,
// Ollama in modalità OpenAI-compatible, vLLM, ecc. — vedi l'URL configurato in Program.cs).
public class OpenAILocalClient : ILLMClient
{
    private readonly HttpClient _httpClient;

    // Stato "in sospeso" del round corrente: lo streaming (StreamChatAsync) lo popola
    // token per token / delta per delta, e solo a fine round CollectStreamResultAsync
    // lo consegna al chiamante. È un dettaglio implementativo un po' fragile (un solo
    // round alla volta, nessuna concorrenza) ma sufficiente per un client single-turn come questo.
    private StreamResult? _pendingResult;

    public OpenAILocalClient(IHttpClientFactory factory)
    {
        _httpClient = factory.CreateClient("llm");
    }

    public async IAsyncEnumerable<string> StreamChatAsync(List<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, object? tools = null)
    {
        _pendingResult = new StreamResult();

        // Qui ricostruiamo il JSON "messages" nel formato che l'API OpenAI-compatible si
        // aspetta. Non possiamo serializzare ChatMessage direttamente: role/content vanno
        // sempre, ma tool_calls compare SOLO sui messaggi assistant che hanno chiesto un
        // tool, e tool_call_id SOLO sui messaggi di risposta di un tool. Se li mandassimo
        // sempre (anche null) o mai, molti backend rifiutano la richiesta o perdono il
        // collegamento domanda/risposta del tool.
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
                    // Questo è il campo che permette al modello di sapere "questo risultato
                    // risponde a QUALE delle mie richieste di tool", specialmente quando ne
                    // ha chieste più di una nello stesso turno.
                    tool_call_id = m.ToolCallId
                },
                _ => new { role = m.Role, content = m.Content }
            }
        )));
        // Se tools è null (nessun tool disponibile) omettiamo del tutto il campo "tools"
        // dalla richiesta, invece di mandare un array vuoto: alcuni backend si comportano
        // diversamente nei due casi.
        var toolsJson = tools != null ? JsonSerializer.Serialize(tools) : null;

        JsonElement messagesElem = JsonDocument.Parse(messagesJson).RootElement;
        JsonElement? toolsElem = toolsJson != null ? JsonDocument.Parse(toolsJson).RootElement : null;

        string toolsFragment = "";
        if (toolsElem.HasValue)
        {
            toolsFragment = $",\"tools\": {toolsElem.Value.GetRawText()}";
        }

        using var requestBodyDoc = JsonDocument.Parse($$"""
        {
            "model": "local",
            "messages": {{messagesElem.GetRawText()}},
            "stream": true{{toolsFragment}}
        }
        """);

        var bodyText = requestBodyDoc.RootElement.GetRawText();

        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };

        // HttpCompletionOption.ResponseHeadersRead: non aspettiamo che la risposta sia
        // tutta arrivata, iniziamo a leggere lo stream appena arrivano gli header —
        // altrimenti perderemmo l'effetto "streaming" e aspetteremmo comunque tutto il testo.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LLM server returned HTTP {response.StatusCode}: {errorBody}");
        }
        using var reader = new System.IO.StreamReader(await response.Content.ReadAsStreamAsync(ct));

        // Il protocollo è Server-Sent Events: ogni riga utile inizia con "data: " e contiene
        // un frammento (delta) di JSON. "data: [DONE]" segnala la fine dello stream.
        // Ollama aggiunge anche un campo "usage" in ogni chunk (o solo nell'ultimo) con
        // prompt_tokens, completion_tokens, total_tokens e timing vari.
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line) || line == "data: [DONE]") continue;

            if (line.StartsWith("data: "))
            {
                var json = line["data: ".Length..];
                using var doc = JsonDocument.Parse(json);

                // --- Estrai usage/timing se presente (Ollama) ---
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt))
                        _pendingResult!.PromptTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct2))
                        _pendingResult!.CompletionTokens = ct2.GetInt32();
                    if (usage.TryGetProperty("total_tokens", out var tt))
                        _pendingResult!.TotalTokens = tt.GetInt32();
                    // total_duration è in nanosecondi (long); convertiamo in ms.
                    if (usage.TryGetProperty("total_duration", out var td))
                        _pendingResult!.TotalTimeMs = td.GetInt64() / 1_000_000;
                }

                // --- Estrai timings se presente (llama.cpp / llama-server) ---
                // llama.cpp non usa "usage" ma "timings" nell'ultimo chunk SSE.
                if (doc.RootElement.TryGetProperty("timings", out var timings))
                {
                    if (timings.TryGetProperty("prompt_n", out var pn))
                        _pendingResult!.PromptTokens = pn.GetInt32();
                    if (timings.TryGetProperty("predicted_n", out var pnd))
                        _pendingResult!.CompletionTokens = pnd.GetInt32();
                    // Calcola total_tokens come somma (llama.cpp non lo espone direttamente)
                    if (_pendingResult.PromptTokens.HasValue && _pendingResult.CompletionTokens.HasValue)
                        _pendingResult.TotalTokens = _pendingResult.PromptTokens.Value + _pendingResult.CompletionTokens.Value;
                    // prompt_ms + predicted_ms = tempo totale in millisecondi (double)
                    double totalMs = 0;
                    if (timings.TryGetProperty("prompt_ms", out var pm))
                        totalMs += pm.GetDouble();
                    if (timings.TryGetProperty("predicted_ms", out var pmd))
                        totalMs += pmd.GetDouble();
                    if (totalMs > 0)
                        _pendingResult.TotalTimeMs = (long)totalMs;
                }

                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");

                // Caso 1: il modello sta scrivendo testo normale -> lo mandiamo subito
                // al chiamante via yield, così appare in console lettera per lettera.
                if (delta.TryGetProperty("content", out var content))
                {
                    var text = content.GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                    {
                        _pendingResult!.FullContent += text;
                        yield return text;
                    }
                }

                // Caso 2: il modello sta costruendo una tool call. Anche questa arriva a
                // pezzi (un delta può portare solo l'id, un altro solo un frammento di
                // "arguments"): per questo accumuliamo in ToolCalls invece di sovrascrivere,
                // e usiamo "index" per sapere a quale tool call (se ce n'è più di una in
                // parallelo) appartiene ogni frammento.
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
                        if (tc.TryGetProperty("id", out var idProp))
                            call = new ToolCall(call.FunctionName, call.ArgumentsJson, idProp.GetString() ?? call.Id);

                        if (tc.TryGetProperty("function", out var func))
                        {
                            // name e arguments possono arrivare in delta separati e non
                            // essere presenti entrambi nello stesso frammento: per questo
                            // usiamo TryGetProperty (mai GetProperty, che lancerebbe) e
                            // concateniamo gli argomenti invece di sostituirli.
                            var name = func.TryGetProperty("name", out var nameProp)
                                ? nameProp.GetString() ?? call.FunctionName
                                : call.FunctionName;
                            var argsDelta = func.TryGetProperty("arguments", out var argsProp)
                                ? argsProp.GetString() ?? ""
                                : "";
                            _pendingResult.ToolCalls[index] = new ToolCall(name, call.ArgumentsJson + argsDelta, call.Id);
                        }
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
