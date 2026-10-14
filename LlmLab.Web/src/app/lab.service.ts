import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import {
  FinalAnswerData,
  LabEvent,
  PromptSentData,
  RoundCompletedData,
  RoundStartedData,
  RunCompletedData,
  RunFailedData,
  RunStartedData,
  TimelineStep,
  TokenData,
  ToolCallDetectedData,
  ToolExecutingData,
  ToolResultData,
} from './models';

// ============================================================================
// LabService: gestisce la connessione SSE al backend e trasforma gli eventi
// grezzi in una timeline di step leggibile dal frontend.
// ============================================================================

@Injectable({ providedIn: 'root' })
export class LabService {
  private readonly _steps = new BehaviorSubject<TimelineStep[]>([]);
  private readonly _running = new BehaviorSubject<boolean>(false);
  private readonly _error = new BehaviorSubject<string | null>(null);

  readonly steps$ = this._steps.asObservable();
  readonly running$ = this._running.asObservable();
  readonly error$ = this._error.asObservable();

  private nextId = 1;
  private abortController: AbortController | null = null;

  // URL dell'API. In dev, il proxy di Angular (proxy.conf.json) inoltra /api/*
  // al backend, quindi possiamo usare un percorso relativo.
  private readonly apiUrl = '/api/lab/run';

  // Avvia una nuova esecuzione: invia il prompt e inizia ad ascoltare gli eventi.
  run(prompt: string): void {
    this.reset();
    this._running.next(true);
    this._error.next(null);

    this.abortController = new AbortController();

    fetch(this.apiUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ prompt }),
      signal: this.abortController.signal,
    })
      .then(async (response) => {
        if (!response.ok || !response.body) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }
        const reader = response.body.getReader();
        const decoder = new TextDecoder('utf-8');
        let buffer = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          // Gli eventi SSE sono separati da una riga vuota.
          let sepIndex;
          while ((sepIndex = buffer.indexOf('\n\n')) !== -1) {
            const rawEvent = buffer.slice(0, sepIndex);
            buffer = buffer.slice(sepIndex + 2);
            this.parseSseEvent(rawEvent);
          }
        }
      })
      .catch((err) => {
        if (err.name !== 'AbortError') {
          this._error.next(err.message);
        }
        this._running.next(false);
      });
  }

  // Interrompe l'esecuzione corrente.
  cancel(): void {
    this.abortController?.abort();
    this._running.next(false);
  }

  private reset(): void {
    this._steps.next([]);
    this.nextId = 1;
  }

  // Parsa un singolo evento SSE (righe "event:" e "data:").
  private parseSseEvent(raw: string): void {
    let eventType = 'message';
    let dataStr = '';
    for (const line of raw.split('\n')) {
      if (line.startsWith('event:')) {
        eventType = line.slice(6).trim();
      } else if (line.startsWith('data:')) {
        dataStr += line.slice(5).trim();
      }
    }
    if (!dataStr) return;

    let payload: any;
    try {
      payload = JSON.parse(dataStr);
    } catch {
      return;
    }

    const evt: LabEvent = {
      type: payload.type as any,
      round: payload.round ?? undefined,
      timestamp: payload.timestamp ?? Date.now(),
      data: payload.data,
    };

    this.handleEvent(evt);
  }

  // Trasforma un evento in uno step della timeline.
  private handleEvent(evt: LabEvent): void {
    const steps = [...this._steps.value];
    const last = steps[steps.length - 1];

    switch (evt.type) {
      case 'runstarted': {
        const d = evt.data as RunStartedData;
        steps.push({
          id: this.nextId++,
          kind: 'run-start',
          title: `Avvio esecuzione`,
          detail: `Prompt: "${d.prompt}" · Tool disponibili: ${d.toolNames.join(', ')}`,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'roundstarted': {
        const d = evt.data as RoundStartedData;
        steps.push({
          id: this.nextId++,
          kind: 'round-start',
          round: d.round,
          title: `Round ${d.round} — richiesta al modello`,
          detail: `History: ${d.historyCount} messaggi · Tool: ${d.toolCount}`,
          isPending: true,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'promptsent': {
        const d = evt.data as PromptSentData;
        steps.push({
          id: this.nextId++,
          kind: 'prompt',
          round: d.round,
          title: `Prompt inviato al modello (${d.messageCount} messaggi, ~${d.estimatedTokens} token)`,
          promptData: d,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'token': {
        const d = evt.data as TokenData;
        // Aggiorniamo l'ultimo step di tipo 'streaming' dello stesso round,
        // oppure ne creiamo uno nuovo.
        const streamingIdx = this.findLastIndex(
          steps,
          (s) => s.kind === 'streaming' && s.round === evt.round,
        );
        if (streamingIdx >= 0) {
          const existing = steps[streamingIdx];
          steps[streamingIdx] = {
            ...existing,
            streamingText: (existing.streamingText ?? '') + d.text,
          };
        } else {
          const roundLabel = evt.round != null ? ` (round ${evt.round})` : '';
          steps.push({
            id: this.nextId++,
            kind: 'streaming',
            round: evt.round,
            title: `Streaming risposta${roundLabel}`,
            streamingText: d.text,
            isPending: true,
            timestamp: evt.timestamp,
          });
        }
        break;
      }

      case 'toolcalldetected': {
        const d = evt.data as ToolCallDetectedData;
        // Chiudiamo lo step di streaming (se presente) togliendo il pending.
        this.closePendingStreaming(steps, evt.round);
        steps.push({
          id: this.nextId++,
          kind: 'tool-call',
          round: evt.round,
          title: `Tool call: ${d.functionName}`,
          toolName: d.functionName,
          toolArgs: d.argumentsJson,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'toolexecuting': {
        const d = evt.data as ToolExecutingData;
        steps.push({
          id: this.nextId++,
          kind: 'tool-exec',
          round: evt.round,
          title: `Esecuzione: ${d.functionName}`,
          toolName: d.functionName,
          toolArgs: d.argumentsJson,
          isPending: true,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'toolresult': {
        const d = evt.data as ToolResultData;
        // Chiudiamo lo step di esecuzione (pending) e aggiungiamo il risultato.
        const execIdx = this.findLastIndex(
          steps,
          (s) =>
            s.kind === 'tool-exec' &&
            s.round === evt.round &&
            s.toolName === d.functionName,
        );
        if (execIdx >= 0) {
          steps[execIdx] = { ...steps[execIdx], isPending: false };
        }
        steps.push({
          id: this.nextId++,
          kind: 'tool-result',
          round: evt.round,
          title: `Risultato: ${d.functionName} (${d.durationMs} ms)`,
          toolName: d.functionName,
          toolOutput: d.output,
          toolIsError: d.isError,
          toolDurationMs: d.durationMs,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'finalanswer': {
        const d = evt.data as FinalAnswerData;
        this.closePendingStreaming(steps, evt.round);
        steps.push({
          id: this.nextId++,
          kind: 'final-answer',
          round: evt.round,
          title: `Risposta finale`,
          finalContent: d.content,
          totalRounds: d.totalRounds,
          totalDurationMs: d.totalDurationMs,
          timestamp: evt.timestamp,
        });
        break;
      }

      case 'roundcompleted': {
        const d = evt.data as RoundCompletedData;
        // Chiudiamo lo step di round-start (pending) di questo round.
        const roundIdx = this.findLastIndex(
          steps,
          (s) => s.kind === 'round-start' && s.round === d.round,
        );
        if (roundIdx >= 0) {
          steps[roundIdx] = {
            ...steps[roundIdx],
            isPending: false,
            detail: `Durata: ${d.durationMs} ms · Tool call: ${d.hasToolCalls ? 'sì' : 'no'}`,
          };
        }
        break;
      }

      case 'runcompleted': {
        const d = evt.data as RunCompletedData;
        steps.push({
          id: this.nextId++,
          kind: 'run-complete',
          title: `Esecuzione completata`,
          detail: `Round totali: ${d.totalRounds} · Durata: ${d.totalDurationMs} ms`,
          totalRounds: d.totalRounds,
          totalDurationMs: d.totalDurationMs,
          timestamp: evt.timestamp,
        });
        this._running.next(false);
        break;
      }

      case 'runfailed': {
        const d = evt.data as RunFailedData;
        steps.push({
          id: this.nextId++,
          kind: 'run-failed',
          title: `Errore`,
          error: d.error,
          errorDetails: d.details,
          timestamp: evt.timestamp,
        });
        this._running.next(false);
        break;
      }
    }

    this._steps.next(steps);
  }

  private findLastIndex(
    arr: TimelineStep[],
    pred: (s: TimelineStep) => boolean,
  ): number {
    for (let i = arr.length - 1; i >= 0; i--) {
      if (pred(arr[i])) return i;
    }
    return -1;
  }

  private closePendingStreaming(steps: TimelineStep[], round?: number): void {
    const idx = this.findLastIndex(
      steps,
      (s) =>
        s.kind === 'streaming' && s.round === round && s.isPending === true,
    );
    if (idx >= 0) {
      steps[idx] = { ...steps[idx], isPending: false };
    }
  }
}
