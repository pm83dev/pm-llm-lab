import { CommonModule } from '@angular/common';
import { Component, Input } from '@angular/core';
import { TokenProbData } from './models';
import {
  computeTokenStats,
  displayToken,
  formatPercent,
  probBackground,
} from './token-probability';

// ============================================================================
// Visualizzatore didattico delle probabilità token: ogni token è uno span
// colorato (verde/giallo/rosso) con tooltip top-5 alternative e una riga di
// statistiche riassuntive.
// ============================================================================
@Component({
  selector: 'app-token-probability-viewer',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="prob-stats">
      <span>Token analizzati: {{ stats.count }}</span>
      <span>Probabilità media: {{ formatPercent(stats.avgProb) }}</span>
      <span>Sotto il 50%: {{ stats.under50Count }}</span>
      @if (stats.leastConfident) {
        <span>
          Più incerto: "{{ displayToken(stats.leastConfident.token) }}"
          ({{ formatPercent(stats.leastConfident.prob) }})
        </span>
      }
    </div>

    <div class="prob-tokens">
      @for (t of tokens; track $index) {
        <span
          class="prob-token"
          [style.background]="probBackground(t.prob)"
          [title]="tooltipFor(t)"
        >{{ displayToken(t.token) }}</span>
      }
    </div>
  `,
  styles: [
    `
      .prob-stats {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        font-size: 0.85rem;
        color: #555;
        margin-bottom: 8px;
      }

      .prob-tokens {
        line-height: 2;
        word-break: break-word;
      }

      .prob-token {
        padding: 1px 2px;
        border-radius: 3px;
        cursor: help;
        white-space: pre-wrap;
      }
    `,
  ],
})
export class TokenProbabilityViewerComponent {
  @Input({ required: true }) tokens: TokenProbData[] = [];

  get stats() {
    return computeTokenStats(this.tokens);
  }

  // Tooltip: probabilità del token + top alternative ordinate per prob.
  tooltipFor(t: TokenProbData): string {
    const lines = [`${displayToken(t.token)} — ${formatPercent(t.prob)}`];
    for (const alt of t.alternatives) {
      lines.push(`${displayToken(alt.token)} — ${formatPercent(alt.prob)}`);
    }
    return lines.join('\n');
  }

  // Esposti al template.
  formatPercent = formatPercent;
  displayToken = displayToken;
  probBackground = probBackground;
}
