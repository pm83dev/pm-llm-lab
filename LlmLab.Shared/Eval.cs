namespace LlmLab.Shared.Eval;

// ============================================================================
// Suite di test (eval): modelli e valutazione PURA, senza dipendenze da HTTP,
// DI o LLM. EvalChecker riceve i dati già raccolti dalla run (risposta finale,
// tool chiamati, round) e restituisce l'esito di ogni check con il motivo.
// ============================================================================

// Un singolo controllo sul risultato della run. `value` è il testo atteso per
// contains/notContains/regex; per toolCalled è il nome del tool; per maxRounds
// è N (passato come stringa nel JSON, es. "3").
public record EvalCheck(string Type, string Value);

// Un caso della suite: prompt da eseguire + controlli da applicare al risultato.
public record EvalCase(string Name, string Prompt, List<EvalCheck> Checks);

// Esito di un singolo check. `Reason` è il motivo del fallimento (null se passa).
public record EvalCheckResult(string Type, string Value, bool Passed, string? Reason);

// Esito completo di un caso della suite.
public record EvalCaseResult(
    string Name,
    bool Passed,
    List<EvalCheckResult> CheckResults,
    string? FinalAnswer,
    List<string> ToolsCalled,
    int Rounds,
    long DurationMs);

// Esito dell'intera suite: risultati per caso + contatori + durata totale.
public record EvalSuiteResult(List<EvalCaseResult> Cases, int PassedCount, int FailedCount, long TotalDurationMs);

// Valutazione pura dei check: nessun I/O, nessuna dipendenza da LLM o HTTP.
public static class EvalChecker
{
    public static List<EvalCheckResult> Evaluate(EvalCase evalCase, string? finalAnswer, IReadOnlyList<string> toolsCalled, int rounds)
    {
        var answer = finalAnswer ?? string.Empty;
        var results = new List<EvalCheckResult>();

        foreach (var check in evalCase.Checks)
        {
            switch (check.Type)
            {
                case "contains":
                    results.Add(new EvalCheckResult(check.Type, check.Value,
                        answer.Contains(check.Value, StringComparison.OrdinalIgnoreCase),
                        $"La risposta finale non contiene \"{check.Value}\" (case-insensitive)."));
                    break;

                case "notContains":
                    results.Add(new EvalCheckResult(check.Type, check.Value,
                        !answer.Contains(check.Value, StringComparison.OrdinalIgnoreCase),
                        $"La risposta finale contiene \"{check.Value}\" ma non doveva."));
                    break;

                case "regex":
                    bool matched;
                    string? regexError = null;
                    try { matched = System.Text.RegularExpressions.Regex.IsMatch(answer, check.Value); }
                    catch (System.Text.RegularExpressions.RegexParseException ex) { matched = false; regexError = $"Regex non valida: {ex.Message}"; }
                    results.Add(new EvalCheckResult(check.Type, check.Value, matched,
                        regexError ?? $"La risposta finale non fa match con la regex /{check.Value}/."));
                    break;

                case "toolCalled":
                    var called = toolsCalled.Contains(check.Value);
                    results.Add(new EvalCheckResult(check.Type, check.Value, called,
                        called ? null : $"Il tool \"{check.Value}\" non è stato chiamato (chiamati: {string.Join(", ", toolsCalled) ?? "nessuno"})."));
                    break;

                case "maxRounds":
                    if (int.TryParse(check.Value, out var max))
                    {
                        results.Add(new EvalCheckResult(check.Type, check.Value, rounds <= max,
                            $"La run ha usato {rounds} round, limite massimo {max}."));
                    }
                    else
                    {
                        results.Add(new EvalCheckResult(check.Type, check.Value, false,
                            $"Valore non numerico per maxRounds: \"{check.Value}\"."));
                    }
                    break;

                default:
                    results.Add(new EvalCheckResult(check.Type, check.Value, false,
                        $"Tipo di check non supportato: \"{check.Type}\"."));
                    break;
            }
        }

        return results;
    }

    public static bool AllPassed(List<EvalCheckResult> checkResults) => checkResults.All(r => r.Passed);
}
