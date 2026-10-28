import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { LabService } from './lab.service';
import { ContextBreakdown, contextBreakdown } from './context-breakdown';
import { TokenProbabilityViewerComponent } from './token-probability-viewer.component';
import { computeEvalCompareDiff } from './eval-compare-diff';
import { EvalCase, EvalCaseResult, EvalCompareResult, EvalRunConfig, EvalSuiteResult, TimelineStep } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule, TokenProbabilityViewerComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent implements OnInit, OnDestroy {
  prompt = '';
  steps: TimelineStep[] = [];
  running = false;
  error: string | null = null;

  // Toggle didattico: se attivo, la run chiede i logprobs al modello e la
  // risposta finale mostra il visualizzatore delle probabilità token.
  showProbabilities = false;

  private subs: Subscription[] = [];

  constructor(private lab: LabService) {}

  ngOnInit(): void {
    this.subs.push(
      this.lab.steps$.subscribe((s) => this.onStepsChanged(s)),
      this.lab.running$.subscribe((r) => (this.running = r)),
      this.lab.error$.subscribe((e) => (this.error = e)),
    );
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  run(): void {
    if (!this.prompt.trim() || this.running) return;
    this.lab.run(this.prompt.trim(), this.showProbabilities);
  }

  // Invia l'esecuzione con Enter (ma non con Shift+Enter, che inserisce un a capo).
  onKeydown(event: Event): void {
    const ke = event as KeyboardEvent;
    if (ke.key === 'Enter' && !ke.shiftKey) {
      event.preventDefault();
      this.run();
    }
  }

  cancel(): void {
    this.lab.cancel();
  }

  // ============================================================================
  // Suite di test (eval): textarea JSON con i casi, esecuzione in sequenza e
  // tabella risultati. Stato separato dalla run singola: non tocca steps/running.
  // ============================================================================

  evalJson = `[
  {
    "name": "Ora corrente (tool)",
    "prompt": "Che ora è adesso? Usa il tool per saperlo con certezza.",
    "checks": [
      { "type": "toolCalled", "value": "get_system_time" },
      { "type": "maxRounds", "value": "3" }
    ]
  },
  {
    "name": "Capitale della Francia (contains)",
    "prompt": "Qual è la capitale della Francia? Rispondi in una sola frase.",
    "checks": [
      { "type": "contains", "value": "parigi" }
    ]
  },
  {
    "name": "Data odierna (regex)",
    "prompt": "Di che giorno siamo oggi? Rispondi solo con la data nel formato gg/mm/aaaa.",
    "checks": [
      { "type": "regex", "value": "\\d{2}/\\d{2}/\\d{4}" }
    ]
  }
]`;

  evalRunning = false;
  evalError: string | null = null;
  evalResult: EvalSuiteResult | null = null;

  // Confronto A/B: stessa suite con due configurazioni opzionali. I campi vuoti
  // vengono inviati come null e il backend usa i valori attuali.
  compareEnabled = false;
  compareRunning = false;
  compareError: string | null = null;
  compareResult: EvalCompareResult | null = null;

  emptyConfig(): EvalRunConfig { return { temperature: null, systemPrompt: '', maxRounds: null }; }
  configA: EvalRunConfig = this.emptyConfig();
  configB: EvalRunConfig = this.emptyConfig();

  private parseEvalCases(): EvalCase[] | null {
    try {
      const parsed = JSON.parse(this.evalJson);
      if (!Array.isArray(parsed)) throw new Error("il JSON deve essere un array di casi");
      return parsed;
    } catch (err) {
      this.evalError = `JSON non valido: ${this.describeJsonError(err)}`;
      return null;
    }
  }

  // Trasforma l'errore di JSON.parse in un messaggio azionabile: estrae la
  // posizione (linea/colonna) dal messaggio e mostra la riga problematica.
  private describeJsonError(err: unknown): string {
    const msg = err instanceof Error ? err.message : String(err);

    let line: number | null = null;
    let col: number | null = null;

    // Chrome/Edge: "Bad escaped character in JSON at position 637 (line 21 column 37)"
    const m1 = msg.match(/position (\d+)(?:\s*\(line (\d+) column (\d+)\))?/);
    if (m1) {
      col = parseInt(m1[1], 10);
      line = m1[2] ? parseInt(m1[2], 10) : null;
      if (m1[3]) col = parseInt(m1[3], 10);
    }

    // Firefox: "JSON.parse: unexpected character at line 21 column 37"
    const m2 = msg.match(/line (\d+) column (\d+)/);
    if (!line && m2) {
      line = parseInt(m2[1], 10);
      col = parseInt(m2[2], 10);
    }

    let detail = msg;
    if (line !== null) {
      const lines = this.evalJson.split('\n');
      const problemLine = lines[line - 1] ?? '';
      detail += ` — riga ${line}${col ? `, colonna ${col}` : ''}: "${problemLine.trim()}"`;
    } else if (col !== null) {
      // Solo posizione assoluta: la mostro come offset di carattere.
      const pos = col - 1;
      const before = this.evalJson.slice(0, pos);
      const after = this.evalJson.slice(pos, pos + 20);
      detail += ` — carattere ${col}: …"${before.slice(-30)}|${after}…`;
    }

    return detail;
  }

  runEval(): void {
    if (this.evalRunning) return;

    const cases = this.parseEvalCases();
    if (!cases) return;

    this.evalRunning = true;
    this.evalError = null;

    this.lab.runEval(cases)
      .then((result) => { this.evalResult = result; })
      .catch((err) => { this.evalError = err instanceof Error ? err.message : String(err); })
      .finally(() => { this.evalRunning = false; });
  }

  // Converte i campi del form in EvalRunConfig: vuoti → null (valori attuali).
  private toConfig(c: EvalRunConfig): EvalRunConfig {
    return {
      temperature: c.temperature ?? null,
      systemPrompt: c.systemPrompt && c.systemPrompt.trim() ? c.systemPrompt : null,
      maxRounds: c.maxRounds ?? null,
    };
  }

  runEvalCompare(): void {
    if (this.compareRunning) return;

    const cases = this.parseEvalCases();
    if (!cases) return;

    this.compareRunning = true;
    this.compareError = null;

    this.lab.runEvalCompare(cases, this.toConfig(this.configA), this.toConfig(this.configB))
      .then((result) => {
        // Il diff è calcolato lato client con la funzione pura testata.
        result.diff = computeEvalCompareDiff(result.a, result.b);
        this.compareResult = result;
      })
      .catch((err) => { this.compareError = err instanceof Error ? err.message : String(err); })
      .finally(() => { this.compareRunning = false; });
  }

  // Check falliti di un caso (per la tabella risultati).
  failedChecks(c: EvalCaseResult): EvalCaseResult['checkResults'] {
    return c.checkResults.filter((r) => !r.passed);
  }

  expandedStepId: number | null = null;
  toggleStep(step: TimelineStep): void {
    this.expandedStepId = this.expandedStepId === step.id ? null : step.id;
  }

  isExpanded(step: TimelineStep): boolean {
    return this.expandedStepId === step.id;
  }

  formatMs(ms?: number): string {
    if (ms == null) return '';
    if (ms < 1000) return `${ms} ms`;
    return `${(ms / 1000).toFixed(2)} s`;
  }

  truncate(text: string, max = 300): string {
    return text.length > max ? text.slice(0, max) + '…' : text;
  }

  // ============================================================================
  // "Anatomia del contesto": per ogni step 'prompt' mostra la ripartizione dei
  // token stimati per ruolo e il delta rispetto al round precedente della stessa
  // esecuzione. I calcoli sono delegati alla funzione pura contextBreakdown().
  // ============================================================================

  private promptStepIndex = new Map<number, number>();

  /** Indice dello step 'prompt' nel vettore steps (per trovare il precedente). */
  private promptIndexOf(step: TimelineStep): number {
    return this.promptStepIndex.get(step.id) ?? -1;
  }

  /** Aggiorna la mappa indice→posizione dopo ogni aggiornamento della timeline. */
  onStepsChanged(steps: TimelineStep[]): void {
    this.steps = steps;
    this.promptStepIndex.clear();
    steps.forEach((s, i) => {
      if (s.kind === 'prompt') this.promptStepIndex.set(s.id, i);
    });
  }

  /** Ripartizione dei token stimati per ruolo del prompt dello step. */
  contextAnatomy(step: TimelineStep): ContextBreakdown | null {
    if (step.kind !== 'prompt' || !step.promptData) return null;
    return contextBreakdown(step.promptData.messages);
  }

  /** Segmenti della barra con larghezza percentuale per il CSS. */
  anatomySegments(step: TimelineStep): { role: string; tokens: number; percent: number }[] {
    const bd = this.contextAnatomy(step);
    if (!bd || bd.totalTokens === 0) return [];
    return bd.byRole.map((r) => ({
      role: r.role,
      tokens: r.tokens,
      percent: (r.tokens / bd.totalTokens) * 100,
    }));
  }

  /** Token aggiunti rispetto al prompt del round precedente della stessa esecuzione. */
  anatomyDelta(step: TimelineStep): number | null {
    const idx = this.promptIndexOf(step);
    if (idx < 0) return null;
    // Cerca lo step 'prompt' immediatamente precedente, fermandosi al separatore di run.
    for (let i = idx - 1; i >= 0; i--) {
      const prev = this.steps[i];
      if (prev.kind === 'run-separator') return null;
      if (prev.kind === 'prompt' && prev.promptData) {
        const prevTotal = contextBreakdown(prev.promptData.messages).totalTokens;
        const curTotal = contextBreakdown(step.promptData!.messages).totalTokens;
        return curTotal - prevTotal;
      }
    }
    return null; // primo round dell'esecuzione: nessun precedente
  }

  /** Colore CSS per ruolo, usato in barra e legenda. */
  roleColor(role: string): string {
    switch (role) {
      case 'system': return '#8b5cf6';
      case 'user': return '#3b82f6';
      case 'assistant': return '#10b981';
      case 'tool': return '#f59e0b';
      default: return '#64748b';
    }
  }

  // Icona per ogni tipo di step nella timeline.
  markerIcon(kind: string): string {
    switch (kind) {
      case 'run-separator':
        return '💬';
      case 'run-start':
        return '🚀';
      case 'round-start':
        return '🔄';
      case 'prompt':
        return '📤';
      case 'streaming':
        return '💬';
      case 'tool-call':
        return '🔧';
      case 'tool-exec':
        return '⚙️';
      case 'tool-result':
        return '📥';
      case 'final-answer':
        return '✅';
      case 'run-complete':
        return '🏁';
      case 'run-failed':
        return '❌';
      default:
        return '•';
    }
  }
}
