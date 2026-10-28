import { EvalCompareCaseDiff, EvalCompareVariation, EvalSuiteResult } from './models';

// Calcola il diff per caso tra due esecuzioni della stessa suite (A e B).
// Le suite devono avere gli stessi casi nello stesso ordine (stesso input cases);
// se le lunghezze differiscono si confrontano solo i casi comuni.
export function computeEvalCompareDiff(a: EvalSuiteResult, b: EvalSuiteResult): EvalCompareCaseDiff[] {
  const len = Math.min(a.cases.length, b.cases.length);
  const out: EvalCompareCaseDiff[] = [];
  for (let i = 0; i < len; i++) {
    const ca = a.cases[i];
    const cb = b.cases[i];
    let variation: EvalCompareVariation;
    if (cb.passed && !ca.passed) {
      variation = 'migliorato';
    } else if (ca.passed && !cb.passed) {
      variation = 'peggiorato';
    } else {
      variation = 'uguale';
    }
    out.push({ name: ca.name, passedA: ca.passed, passedB: cb.passed, variation });
  }
  return out;
}
