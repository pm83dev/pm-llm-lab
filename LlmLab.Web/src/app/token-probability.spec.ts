import { TokenProbData } from './models';
import {
  computeTokenStats,
  displayToken,
  formatPercent,
  probBackground,
  probColor,
  probLevel,
} from './token-probability';

function tok(token: string, prob: number): TokenProbData {
  return { token, prob, alternatives: [] };
}

describe('token-probability', () => {
  describe('probLevel', () => {
    it('classifica >= 0.9 come high', () => {
      expect(probLevel(0.9)).toBe('high');
      expect(probLevel(1)).toBe('high');
    });

    it("classifica [0.5, 0.9) come medium", () => {
      expect(probLevel(0.5)).toBe('medium');
      expect(probLevel(0.89)).toBe('medium');
    });

    it('classifica < 0.5 come low', () => {
      expect(probLevel(0.49)).toBe('low');
      expect(probLevel(0)).toBe('low');
    });
  });

  describe('probColor / probBackground', () => {
    it('mappa i livelli su verde/giallo/rosso', () => {
      expect(probColor('high')).toBe('#c8e6c9');
      expect(probColor('medium')).toBe('#fff9c4');
      expect(probColor('low')).toBe('#ffcdd2');
    });

    it('probBackground compone livello e colore', () => {
      expect(probBackground(0.95)).toBe('#c8e6c9');
      expect(probBackground(0.7)).toBe('#fff9c4');
      expect(probBackground(0.3)).toBe('#ffcdd2');
    });
  });

  describe('formatPercent', () => {
    it('converte in percentuale con la precisione richiesta', () => {
      expect(formatPercent(1)).toBe('100.0%');
      expect(formatPercent(0.5)).toBe('50.0%');
      expect(formatPercent(0.1234, 2)).toBe('12.34%');
    });

    it('clamp fuori intervallo', () => {
      expect(formatPercent(-0.1)).toBe('0.0%');
      expect(formatPercent(1.5)).toBe('100.0%');
    });
  });

  describe('computeTokenStats', () => {
    it('gestisce la lista vuota', () => {
      const s = computeTokenStats([]);
      expect(s.count).toBe(0);
      expect(s.avgProb).toBe(0);
      expect(s.under50Count).toBe(0);
      expect(s.leastConfident).toBeUndefined();
    });

    it('calcola media, token sotto il 50% e token più incerto', () => {
      const tokens = [tok('a', 0.9), tok('b', 0.4), tok('c', 0.6)];
      const s = computeTokenStats(tokens);
      expect(s.count).toBe(3);
      expect(s.avgProb).toBeCloseTo((0.9 + 0.4 + 0.6) / 3, 10);
      expect(s.under50Count).toBe(1);
      expect(s.leastConfident?.token).toBe('b');
    });

    it('soglia "sotto il 50%" è stretta (< 0.5)', () => {
      const s = computeTokenStats([tok('a', 0.5)]);
      expect(s.under50Count).toBe(0);
    });
  });

  describe('displayToken', () => {
    it('rende visibile lo spazio iniziale dei token BPE', () => {
      expect(displayToken(' ciao')).toBe('␣ciao');
      expect(displayToken('ciao')).toBe('ciao');
    });

    it('gestisce il token vuoto', () => {
      expect(displayToken('')).toBe('∅');
    });
  });
});
