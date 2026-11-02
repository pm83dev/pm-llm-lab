import { computeEvalCompareDiff } from './eval-compare-diff';
import { EvalSuiteResult } from './models';

function suite(cases: { name: string; passed: boolean }[]): EvalSuiteResult {
  return {
    cases: cases.map(c => ({
      name: c.name,
      passed: c.passed,
      checkResults: [],
      finalAnswer: null,
      toolsCalled: [],
      rounds: 1,
      durationMs: 10,
    })),
    passedCount: cases.filter(c => c.passed).length,
    failedCount: cases.filter(c => !c.passed).length,
    totalDurationMs: 100,
  };
}

describe('computeEvalCompareDiff', () => {
  it('segna migliorato quando B passa e A fallisce', () => {
    const a = suite([{ name: 'x', passed: false }]);
    const b = suite([{ name: 'x', passed: true }]);
    const d = computeEvalCompareDiff(a, b);
    expect(d[0].variation).toBe('migliorato');
  });

  it('segna peggiorato quando A passa e B fallisce', () => {
    const a = suite([{ name: 'x', passed: true }]);
    const b = suite([{ name: 'x', passed: false }]);
    const d = computeEvalCompareDiff(a, b);
    expect(d[0].variation).toBe('peggiorato');
  });

  it('segna uguale quando entrambi passano o entrambi falliscono', () => {
    const a = suite([{ name: 'p', passed: true }, { name: 'f', passed: false }]);
    const b = suite([{ name: 'p', passed: true }, { name: 'f', passed: false }]);
    const d = computeEvalCompareDiff(a, b);
    expect(d[0].variation).toBe('uguale');
    expect(d[1].variation).toBe('uguale');
  });

  it('usa il nome del caso A e gli esiti corretti', () => {
    const a = suite([{ name: 'c1', passed: false }]);
    const b = suite([{ name: 'c1', passed: true }]);
    const d = computeEvalCompareDiff(a, b);
    expect(d[0].name).toBe('c1');
    expect(d[0].passedA).toBeFalse();
    expect(d[0].passedB).toBeTrue();
  });

  it('confronta solo i casi comuni se le lunghezze differiscono', () => {
    const a = suite([{ name: 'c1', passed: true }, { name: 'c2', passed: false }]);
    const b = suite([{ name: 'c1', passed: false }]);
    const d = computeEvalCompareDiff(a, b);
    expect(d.length).toBe(1);
    expect(d[0].name).toBe('c1');
    expect(d[0].variation).toBe('peggiorato');
  });
});
