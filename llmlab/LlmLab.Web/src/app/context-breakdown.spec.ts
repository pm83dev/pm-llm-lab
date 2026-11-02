import { contextBreakdown, estimateMessageTokens } from './context-breakdown';
import { PromptMessageView } from './models';

describe('contextBreakdown', () => {
  it('solo system: restituisce un solo segmento con i token del messaggio system', () => {
    const messages: PromptMessageView[] = [
      { role: 'system', content: 'x'.repeat(40) }, // 40 caratteri → 10 token
    ];

    const result = contextBreakdown(messages);

    expect(result.byRole).toEqual([{ role: 'system', tokens: 10 }]);
    expect(result.totalTokens).toBe(10);
  });

  it('con tool call: conta anche gli argomenti delle tool call nel ruolo assistant', () => {
    const messages: PromptMessageView[] = [
      { role: 'system', content: 'sys' }, // 3 caratteri → ceil(3/4) = 1 token
      {
        role: 'assistant',
        content: '',
        toolCalls: [
          // "get_time" (8) + '{"city":"Roma"}' (15) = 23 caratteri di tool call
          { functionName: 'get_time', argumentsJson: '{"city":"Roma"}', id: 'call_1' },
        ],
      },
    ];

    const result = contextBreakdown(messages);

    expect(result.byRole).toEqual([
      { role: 'system', tokens: 1 },
      { role: 'assistant', tokens: Math.ceil(23 / 4) }, // 6 token
    ]);
    expect(result.totalTokens).toBe(1 + Math.ceil(23 / 4));
  });

  it('history vuota: nessun segmento e zero token totali', () => {
    const result = contextBreakdown([]);

    expect(result.byRole).toEqual([]);
    expect(result.totalTokens).toBe(0);
  });

  it('ruoli multipli: raggruppa per ruolo mantenendo l\'ordine system, user, assistant, tool', () => {
    const messages: PromptMessageView[] = [
      { role: 'user', content: 'a'.repeat(8) }, // 2 token
      { role: 'tool', content: 'b'.repeat(16) }, // 4 token
      { role: 'assistant', content: 'c'.repeat(4) }, // 1 token
      { role: 'user', content: 'd'.repeat(8) }, // 2 token (somma al primo user)
    ];

    const result = contextBreakdown(messages);

    expect(result.byRole).toEqual([
      { role: 'user', tokens: 4 },
      { role: 'assistant', tokens: 1 },
      { role: 'tool', tokens: 4 },
    ]);
    expect(result.totalTokens).toBe(9);
  });

  it('estimateMessageTokens: content undefined e toolCalls assente → 0 token', () => {
    const msg: PromptMessageView = { role: 'user', content: '' };
    expect(estimateMessageTokens(msg)).toBe(0);
  });
});
