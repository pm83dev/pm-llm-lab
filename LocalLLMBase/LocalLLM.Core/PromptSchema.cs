namespace LocalLLM.Core;

public interface IPromptSchema
{
    public string SystemPrompt { get; }
    public List<object> ToolsDef { get; }
}


public class PromptSchema : IPromptSchema
{
    // Costruttore: si inizializzano i campi di PromptSchema.
    // La "prompt schema" è un JSON che contiene una "system prompt" e una lista di "tools definition".
    public PromptSchema()
    {
        SystemPrompt = "";
        ToolsDef = new List<Object>();
        AddSystemPrompt();
        AddTool();

    }

    // Istruzione di sistema: è l'unico posto dove si può orientare il "comportamento
    // di default" del modello (tono, quando usare i tool, ecc.) senza toccare codice.
    public string SystemPrompt { get; private set; }

    // Schema JSON (formato "function calling" OpenAI) di ogni tool: è la SOLA cosa che
    // il modello vede per decidere se/come chiamarlo — non vede il codice C# in ITools.cs.
    // "properties"/"required" seguono JSON Schema: dicono al modello quali argomenti
    // generare e quali sono obbligatori nella stringa JSON di ArgumentsJson.
    public List<object> ToolsDef { get; private set; }

    public void AddSystemPrompt()
    {
        SystemPrompt = "Sei un assistente esperto di nome superPM. Usa i tool quando necessario per rispondere alle domande dell'utente.";
    }

    public void AddTool()
    {
        ToolsDef.Add(new
        {
            type = "function",
            @function = new
            {
                name = "get_system_time",
                description = "Restituisce l'ora corrente del sistema.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[0]
                }
            }
        });
        ToolsDef.Add(new {
            type = "function",
            @function = new {
                name = "get_weather",
                description = "Ottiene il meteo attuale (temperatura, vento, condizioni meteo) " +
                              "per un luogo usando l'API Open-Meteo gratuita. " +
                              "Parametro obbligatorio: 'location' (stringa). " +
                              "Supporta: nomi di città (es. 'Roma'), coordinate GPS (es. '41.9028,12.4964'), " +
                              "o codice paese (es. 'IT'). " +
                              "L'API restituisce temperatura in °C e vento in km/h.",

                parameters = new {
                    type = "object",

                    properties = new {
                        location = new { type = "string", description = "Città, coordinate GPS, o codice paese per il meteo." }
                    },
                    required = new[] { "location" }
                }
            }

        });
        ToolsDef.Add(
            new {
                type = "function",
                @function = new {
                    name = "read_file",
                    description = "Legge e restituisce il contenuto testuale di un file dato il suo percorso.",
                    parameters = new {
                        type = "object",

                        properties = new {
                            path = new { type = "string", description = "Percorso del file da leggere." }
                        },
                        required = new[] { "path" }
                    }
                }
            }
        );
        ToolsDef.Add(
            new
            {
                type = "function",
                @function = new
                {
                    name = "write_file",
                    description = "Scrive del contenuto testuale in un file al percorso specificato, sovrascrivendolo se già esiste.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Percorso del file in cui scrivere." },
                            content = new { type = "string", description = "Contenuto testuale da scrivere nel file." }
                        },
                        required = new[] { "path", "content" }
                    }
                }
            }
        );
        ToolsDef.Add(

            new
            {
                type = "function",
                @function = new
                {
                    name = "search_file",
                    description = "Ricerca una parola nel contenuto di un file e restituisce le linee in cui è presente o elencare tutti i file che la contengono.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            word = new { type = "string", description = "Parola da cercare." }
                        },
                        required = new[] { "word" }
                    }
                    }
        });

        ToolsDef.Add(new {
            type = "function",
            @function = new {
                name = "shell_command",
                description = "Esegue un comando PowerShell e restituisce stdout, stderr e il codice di uscita. Utile per esplorare il filesystem, installare pacchetti, gestire processi, ecc.",
                parameters = new {
                    type = "object",

                    properties = new {
                        command = new { type = "string", description = "Il comando PowerShell da eseguire." }
                    },
                    required = new[] { "command" }
                }
            }

        });


    }
}
