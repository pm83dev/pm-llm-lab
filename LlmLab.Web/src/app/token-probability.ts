import { TokenProbData } from './models';

// ============================================================================
// Logica PURA del visualizzatore probabilità token (nessun Angular): colore,
// formattazione percentuali e statistiche. Testabile con ng test.
// ============================================================================

export type ProbLevel = 'high' | 'medium' | 'low';

// Soglie didattiche: >= 0.9 "sicuro", >= 0.5 "medio", altrimenti "incerto".
export const HIGH_THRESHOLD = 0.9;
export const MEDIUM_THRESHOLD = 0.5;

// Classifica una probabilità (0..1) in un livello di colore.
export function probLevel(prob: number): ProbLevel {
  if (prob >= HIGH_THRESHOLD) return 'high';
  if (prob >= MEDIUM_THRESHOLD) return 'medium';
  return 'low';
}

// Colore di sfondo per il livello (verde/giallo/rosso, toni chiari per leggibilità).
export function probColor(level: ProbLevel): string {
  switch (level) {
    case 'high':
      return '#c8e6c9'; // verde chiaro
    case 'medium':
      return '#fff9c4'; // giallo chiaro
    default:
      return '#ffcdd2'; // rosso chiaro
  }
}

// Colore di sfondo diretto da probabilità.
export function probBackground(prob: number): string {
  return probColor(probLevel(prob));
}

// Formatta una probabilità (0..1) come percentuale con la precisione data.
export function formatPercent(prob: number, digits = 1): string {
  const clamped = Math.min(1, Math.max(0, prob));
  return `${(clamped * 100).toFixed(digits)}%`;
}

// Statistiche riassuntive della risposta (riga di statistiche del visualizzatore).
export interface TokenProbStats {
  count: number; // numero totale di token analizzati
  avgProb: number; // probabilità media (0..1)
  under50Count: number; // token con probabilità < 50%
  leastConfident?: TokenProbData; // token più incerto (probabilità minima)
}

export function computeTokenStats(tokens: TokenProbData[]): TokenProbStats {
  if (tokens.length === 0) {
    return { count: 0, avgProb: 0, under50Count: 0 };
  }

  let sum = 0;
  let under50 = 0;
  let least: TokenProbData | undefined;

  for (const t of tokens) {
    sum += t.prob;
    if (t.prob < MEDIUM_THRESHOLD) under50++;
    if (least === undefined || t.prob < least.prob) least = t;
  }

  return {
    count: tokens.length,
    avgProb: sum / tokens.length,
    under50Count: under50,
    leastConfident: least,
  };
}

// Testo leggibile del token per il tooltip (gli spazi iniziali dei token BPE
// sono significativi: li rendiamo visibili con un marcatore).
export function displayToken(token: string): string {
  if (token === '') return '∅';
  return token.replace(/^ /, '␣');
}
