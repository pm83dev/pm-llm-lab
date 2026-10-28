import { PromptMessageView } from './models';

// Ruoli noti della barra "Anatomia del contesto". Gli altri ruoli (es. 'developer')
// vengono raggruppati in 'other'.
export const CONTEXT_ROLES = ['system', 'user', 'assistant', 'tool'] as const;

export type ContextRole = (typeof CONTEXT_ROLES)[number] | 'other';

// Stima token di un singolo messaggio: caratteri/4, contando anche gli
// argomenti delle tool call.
export function estimateMessageTokens(msg: PromptMessageView): number {
  let chars = (msg.content ?? '').length;
  if (msg.toolCalls) {
    for (const tc of msg.toolCalls) {
      // Nome della funzione + argomenti JSON: sono parte del contesto inviato al modello.
      chars += (tc.functionName ?? '').length + (tc.argumentsJson ?? '').length;
    }
  }
  return Math.ceil(chars / 4);
}

export interface ContextBreakdown {
  // Token stimati per ruolo, in ordine di CONTEXT_ROLES (+ 'other' se presente).
  byRole: { role: ContextRole; tokens: number }[];
  totalTokens: number;
}

// Funzione pura: dato l'elenco dei messaggi inviati al modello (PromptSentData.messages),
// restituisce la ripartizione dei token stimati per ruolo. Non ha effetti collaterali.
export function contextBreakdown(messages: PromptMessageView[]): ContextBreakdown {
  const totals = new Map<ContextRole, number>();

  for (const msg of messages ?? []) {
    const role: ContextRole = (CONTEXT_ROLES as readonly string[]).includes(msg.role)
      ? (msg.role as ContextRole)
      : 'other';
    totals.set(role, (totals.get(role) ?? 0) + estimateMessageTokens(msg));
  }

  const byRole: { role: ContextRole; tokens: number }[] = [];
  let totalTokens = 0;
  for (const role of [...CONTEXT_ROLES, 'other'] as ContextRole[]) {
    const tokens = totals.get(role) ?? 0;
    if (tokens > 0) byRole.push({ role, tokens });
    totalTokens += tokens;
  }

  return { byRole, totalTokens };
}
