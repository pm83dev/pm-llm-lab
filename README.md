# ConsoleAppLlm — Agent LLM in C#

Questo progetto è un **laboratorio didattico** per capire come funziona la comunicazione tra codice e un LLM (Large Language Model). Non è un'applicazione: è uno strumento per imparare.

## Cosa impara lo sviluppatore

### 1. Un LLM non esegue mai codice
Il modello **genera solo testo**. Se deve leggere un file, eseguire un comando o calcolare qualcosa, **emette una richiesta strutturata** (tool call) e aspetta che il nostro codice la esegua. Questo è il concetto fondamentale di tutto il function calling.

### 2. Il ciclo agente (agent loop)
L'Orchestrator implementa il pattern base:

```
Tu → [messaggio]
LLM → [testo] oppure [tool call]
Se tool call → eseguo il tool, mando risultato al LLM
Ripeto fino a risposta testuale
```

Questo è il meccanismo su cui si basano **tutti** gli agent: LangChain, AutoGen, CrewAI, OpenAI Assistants API.

### 3. Il formato della richiesta OpenAI
La comunicazione avviene tramite JSON nel formato `chat/completions` di OpenAI:

```json
{
  "model": "local",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "che ore sono?" }
  ],
  "stream": true,
  "tools": [...]
}
```

- `messages`: è la **storia completa** della conversazione. Il LLM non ha memoria propria, gli rimandiamo tutto ogni volta.
- `stream: true`: ricevo token per token invece di aspettare la risposta intera.
- `tools`: lo schema dei tool disponibili. Il modello decide se usarli.

### 4. Lo streaming e i tool call
Lo streaming non è solo testo che arriva lento. I `tool_calls` arrivano **a pezzi**:

```
delta: { content: "Devo controllare" }
delta: { tool_calls[0].id: "call_abc" }
delta: { tool_calls[0].function.name: "get_system_time" }
delta: { tool_calls[0].function.arguments: "{}" }
```

Il codice deve **accumulare e ricostruire** i pezzi. Questo è un dettaglio tecnico importante: il modello non invia mai tutto in una volta.

### 5. Il ruolo dei messaggi `tool`
Dopo che abbiamo eseguito un tool, dobbiamo rimandare il risultato al modello con un messaggio speciale:

```json
{ "role": "tool", "content": "Monday, November 2, 2026 3:22:28 PM", "tool_call_id": "call_abc" }
```

Il campo `tool_call_id` è il **collante** che collega la richiesta del modello al nostro risultato. Senza quello, il modello non sa a quale chiamata si riferisce il dato.

## Esecuzione

```bash
cd ConsoleAppLlm
dotnet run
```

Server LLM: `http://100.121.40.27:9005` (Ollama, Qwen3.5-9B)

## Esempio: senza tool

```
User > ciao
🤖 LLM [round 1]: Ciao! Come posso aiutarti oggi?
[AGENT DONE] Total rounds: 1
```

Il modello ha risposto direttamente in linguaggio naturale. Nessun tool chiamato, un solo round.

## Esempio: con tool

```
User > che ore sono?
🤖 LLM [round 1]: 
🔧 Tool: get_system_time({})
✅ Monday, November 2, 2026 3:22:28 PM
🤖 LLM [round 2]: Le ore attuali sono le 15:22:28 di lunedì 2 novembre 2026.
[AGENT DONE] Total rounds: 2
```

Due round:
1. Il modello ha capito che serve un tool → ha generato una `tool_call`
2. Il nostro codice ha eseguito `get_system_time()`, ha mandato il risultato al modello
3. Il modello ha ricevuto i dati e ha scritto la risposta finale in italiano

## Esempio: più tool in sequenza

```
User > leggi C:\DevAgentPM\pm-llm-lab\ConsoleAppLlm\Program.cs e conta le righe
🤖 LLM [round 1]: 
🔧 Tool: read_file(C:\DevAgentPM\pm-llm-lab\ConsoleAppLlm\Program.cs)
✅ [contenuto del file...]
🤖 LLM [round 2]: Il file ha 132 righe.
[AGENT DONE] Total rounds: 2
```

## Struttura del codice

| File | Cosa fa |
|------|---------|
| `LlmClient.cs` | Invia la richiesta HTTP, legge lo streaming, ricostruisce testo e tool_calls |
| `Orchestrator.cs` | Ciclo principale: chiama LLM, esegue tool, aggiorna la storia |
| `ITools.cs` | Interfaccia `ILocalTool` e implementazioni (read_file, write_file, ecc.) |
| `Model.cs` | Record per messaggi, tool call e risultati |
| `StreamResult.cs` | Accumula i dati dello streaming mentre arrivano |

## Limiti noti

- Il parsing tool_calls assume formato OpenAI API
- `_pendingResult` non è thread-safe (unico stream alla volta)
- Richiede server LLM con API OpenAI compatibile e supporto tool calling
