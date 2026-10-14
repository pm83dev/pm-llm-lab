import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { LabService } from './lab.service';
import { TimelineStep } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent implements OnInit, OnDestroy {
  prompt = '';
  steps: TimelineStep[] = [];
  running = false;
  error: string | null = null;

  private subs: Subscription[] = [];

  constructor(private lab: LabService) {}

  ngOnInit(): void {
    this.subs.push(
      this.lab.steps$.subscribe((s) => (this.steps = s)),
      this.lab.running$.subscribe((r) => (this.running = r)),
      this.lab.error$.subscribe((e) => (this.error = e)),
    );
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  run(): void {
    if (!this.prompt.trim() || this.running) return;
    this.lab.run(this.prompt.trim());
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

  // Icona per ogni tipo di step nella timeline.
  markerIcon(kind: string): string {
    switch (kind) {
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
