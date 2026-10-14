namespace LocalLLM.Tools;

using System.Diagnostics;
using System.Text.Json;
using LocalLLM.Models;


// Contratto che ogni "capacità" concreta dell'agente deve rispettare. Il modello NON conosce
// questa interfaccia: conosce solo Name/Description/parametri tradotti in JSON Schema
// (vedi toolsDef in Program.cs). ExecuteAsync è dove il codice C# fa il lavoro vero
// (leggere/scrivere file, leggere l'orologio, ecc.) che il modello da solo non può fare.
public interface ILocalTool
{
    // Deve combaciare esattamente con "function.name" nello schema tools e con
    // ToolCall.FunctionName che il modello restituisce: è la chiave con cui
    // AgentOrchestrator cerca il tool nel Dictionary<string, ILocalTool>.
    string Name { get; }

    // Va nello schema JSON mandato al modello: è l'UNICA informazione che il modello
    // ha per decidere quando/se usare questo tool. Una descrizione vaga porta a
    // chiamate sbagliate o mancate.
    string Description { get; }

    // argumentsJson è la stringa JSON grezza generata dal modello (es. {"path":"C:\\..."}).
    // Va parsata e validata qui: il modello può sbagliare formato o omettere campi richiesti.
    Task<string> ExecuteAsync(string argumentsJson);
}

// Tool più semplice possibile: nessun argomento, nessuno stato. Utile come "hello world"
// del function calling prima di introdurre tool con parametri.
public class GetSystemTimeTool : ILocalTool
{
    public string Name => "get_system_time";
    public string Description => "Restituisce l'ora corrente del sistema.";

    public Task<string> ExecuteAsync(string argumentsJson)
        => Task.FromResult(DateTime.Now.ToString("F"));
}

// Dà all'agente accesso in lettura al filesystem locale. Nota di sicurezza: qui non c'è
// nessuna restrizione di percorso (sandboxing) — in un contesto reale andrebbe limitato
// a una cartella consentita, perché il modello sceglie il "path" in autonomia.
public class ReadFileTool : ILocalTool
{
    public string Name => "read_file";
    public string Description => "Legge e restituisce il contenuto testuale di un file dato il suo percorso.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        // Parsing manuale via JsonDocument invece di un DTO tipizzato: per un tool semplice
        // evita di dover definire una classe record solo per un paio di campi opzionali.
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString()
            ?? throw new ArgumentException("Il parametro 'path' è obbligatorio.");

        // Ritorniamo una stringa di errore invece di lanciare: il messaggio finisce come
        // risultato "tool" nella history, così il modello lo legge e può correggersi
        // (es. chiedere un percorso diverso) senza che l'intero round vada in eccezione.
        if (!File.Exists(path))
            return $"Errore: il file '{path}' non esiste.";

        return await File.ReadAllTextAsync(path);
    }
}

// Simmetrico a ReadFileTool ma in scrittura: stesso avviso di sicurezza, qui ancora più
// delicato perché il modello può sovrascrivere qualunque file scrivibile dal processo.
public class WriteFileTool : ILocalTool
{
    public string Name => "write_file";
    public string Description => "Scrive del contenuto testuale in un file al percorso specificato, sovrascrivendolo se già esiste.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString()
            ?? throw new ArgumentException("Il parametro 'path' è obbligatorio.");
        // "content" è dichiarato "required" nello schema tools, ma trattiamo comunque
        // la sua assenza come stringa vuota invece di eccezione: più tollerante se il
        // modello genera un JSON incompleto.
        var content = doc.RootElement.TryGetProperty("content", out var contentProp)
            ? contentProp.GetString() ?? ""
            : "";

        // Se il path include sottocartelle non ancora esistenti, File.WriteAllTextAsync
        // fallirebbe da solo: le creiamo prima.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content);
        // Messaggio di conferma testuale: è ciò che il modello "vede" per sapere che
        // l'operazione è andata a buon fine, non ha altro modo di verificarlo.
        return $"File '{path}' scritto correttamente ({content.Length} caratteri).";
    }
}

public class SearchFileTool : ILocalTool
{
    public string Name => "search_file";
    public string Description => "Ricerca una parola nel contenuto di un file e restituisce le linee in cui è presente o elencare tutti i file che la contengono.";

    public Task<string> ExecuteAsync(string argumentsJson)
    {
        Console.WriteLine("SearchFileTool.ExecuteAsync");
        return Task.FromResult("");
    }
}

// Tool per eseguire comandi PowerShell. Usa pwsh.exe (PowerShell 7) con fallback su
// powershell.exe (Windows PowerShell 5.1). Include timeout di 60 secondi e cattura
// sia stdout che stderr per dare all'agente tutte le informazioni necessarie.
public class ShellCommandTool : ILocalTool
{
    public string Name => "shell_command";
    public string Description => "Esegue un comando PowerShell e restituisce stdout, stderr e il codice di uscita. Utile per esplorare il filesystem, installare pacchetti, gestire processi, ecc.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var command = doc.RootElement.GetProperty("command").GetString()
            ?? throw new ArgumentException("Il parametro 'command' è obbligatorio.");

        if (string.IsNullOrWhiteSpace(command))
            return "Errore: il parametro 'command' non può essere vuoto.";

        const int timeoutSeconds = 60;

        // Cerca il percorso di PowerShell nei percorsi noti di Windows (il PATH del processo
        // .NET a volte non include le cartelle di sistema).
        string? FindPowerShell()
        {
            // 1. pwsh.exe (PowerShell 7+) — percorso user-wide
            var pwshUser = Environment.GetEnvironmentVariable("ProgramFiles")
                + @"\PowerShell\7\pwsh.exe";
            if (File.Exists(pwshUser)) return pwshUser;

            // 2. pwsh.exe — percorso 32-bit user-wide
            var pwshUserX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                + @"\PowerShell\7\pwsh.exe";
            if (File.Exists(pwshUserX86)) return pwshUserX86;

            // 3. powershell.exe (Windows PowerShell 5.1) — percorso di sistema
            var winPS = Environment.GetEnvironmentVariable("SystemRoot")
                + @"\System32\WindowsPowerShell\v1.0\powershell.exe";
            if (File.Exists(winPS)) return winPS;

            // 4. powershell.exe — percorso 32-bit su sistema 64-bit
            var winPSX86 = Environment.GetEnvironmentVariable("SystemRoot")
                + @"\Sysnative\WindowsPowerShell\v1.0\powershell.exe";
            if (File.Exists(winPSX86)) return winPSX86;

            // 5. Fallback: prova il PATH dell'utente
            var pwshEnv = Environment.GetEnvironmentVariable("PATH")?.Split(';')
                .Select(p => p.TrimEnd('\\') + @"\pwsh.exe")
                .FirstOrDefault(File.Exists);
            if (pwshEnv != null) return pwshEnv;

            var pshEnv = Environment.GetEnvironmentVariable("PATH")?.Split(';')
                .Select(p => p.TrimEnd('\\') + @"\powershell.exe")
                .FirstOrDefault(File.Exists);
            if (pshEnv != null) return pshEnv;

            return null;
        }

        var shellExe = FindPowerShell()
            ?? throw new InvalidOperationException(
                "Impossibile trovare PowerShell: né pwsh.exe né powershell.exe nei percorsi noti.");

        // -NoProfile: non carica il profilo dell'utente (più veloce, più prevedibile)
        // -NoLogo: non mostra il banner di PowerShell
        // -EncodedCommand: esegue un comando codificato in base64 (UTF-16LE)
        //                 → evita TUTTI i problemi di escaping/quote della riga di comando
        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo
        {
            FileName = shellExe,
            Arguments = $"-NoProfile -NoLogo -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(timeoutSeconds * 1000);

            if (!exited)
            {
                process.Kill();
                return $"Errore: il comando ha superato il timeout di {timeoutSeconds} secondi.\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
            }

            var exitCode = process.ExitCode;
            var output = $"ExitCode: {exitCode}\n--- STDOUT ---\n{stdout}";
            if (!string.IsNullOrWhiteSpace(stderr))
                output += $"\n--- STDERR ---\n{stderr}";

            return output;
        }
        catch (Exception ex)
        {
            return $"Errore durante l'esecuzione: {ex.Message}";
        }
    }
}

