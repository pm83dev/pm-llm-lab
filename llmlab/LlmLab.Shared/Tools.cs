namespace LlmLab.Shared.Tools;

using System.Diagnostics;
using System.Text.Json;

// ============================================================================
// ILocalTool: contratto che ogni "capacità" concreta dell'agente deve rispettare.
// Il modello non conosce questa interfaccia: conosce solo Name/Description e i
// parametri tradotti in JSON Schema. ExecuteAsync è dove il codice C# fa il
// lavoro vero che il modello da solo non può fare.
// ============================================================================

public interface ILocalTool
{
    string Name { get; }
    string Description { get; }
    Task<string> ExecuteAsync(string argumentsJson);
}

// Tool più semplice possibile: nessun argomento. "Hello world" del function calling.
public class GetSystemTimeTool : ILocalTool
{
    public string Name => "get_system_time";
    public string Description => "Restituisce l'ora corrente del sistema.";
    public Task<string> ExecuteAsync(string argumentsJson)
        => Task.FromResult(DateTime.Now.ToString("F"));
}

// Accesso in lettura al filesystem locale.
public class ReadFileTool : ILocalTool
{
    public string Name => "read_file";
    public string Description => "Legge e restituisce il contenuto testuale di un file dato il suo percorso.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString()
            ?? throw new ArgumentException("Il parametro 'path' è obbligatorio.");

        if (!File.Exists(path))
            return $"Errore: il file '{path}' non esiste.";

        return await File.ReadAllTextAsync(path);
    }
}

// Scrittura su filesystem locale.
public class WriteFileTool : ILocalTool
{
    public string Name => "write_file";
    public string Description => "Scrive del contenuto testuale in un file al percorso specificato, sovrascrivendolo se già esiste.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString()
            ?? throw new ArgumentException("Il parametro 'path' è obbligatorio.");
        var content = doc.RootElement.TryGetProperty("content", out var contentProp)
            ? contentProp.GetString() ?? ""
            : "";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content);
        return $"File '{path}' scritto correttamente ({content.Length} caratteri).";
    }
}

// Esecuzione di comandi PowerShell con timeout e cattura stdout/stderr.
public class ShellCommandTool : ILocalTool
{
    public string Name => "shell_command";
    public string Description => "Esegue un comando PowerShell e restituisce stdout, stderr e il codice di uscita.";

    public async Task<string> ExecuteAsync(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var command = doc.RootElement.GetProperty("command").GetString()
            ?? throw new ArgumentException("Il parametro 'command' è obbligatorio.");

        if (string.IsNullOrWhiteSpace(command))
            return "Errore: il parametro 'command' non può essere vuoto.";

        const int timeoutSeconds = 60;

        string? FindPowerShell()
        {
            var pwshUser = Environment.GetEnvironmentVariable("ProgramFiles") + @"\PowerShell\7\pwsh.exe";
            if (File.Exists(pwshUser)) return pwshUser;
            var pwshUserX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\PowerShell\7\pwsh.exe";
            if (File.Exists(pwshUserX86)) return pwshUserX86;
            var winPS = Environment.GetEnvironmentVariable("SystemRoot") + @"\System32\WindowsPowerShell\v1.0\powershell.exe";
            if (File.Exists(winPS)) return winPS;
            var winPSX86 = Environment.GetEnvironmentVariable("SystemRoot") + @"\Sysnative\WindowsPowerShell\v1.0\powershell.exe";
            if (File.Exists(winPSX86)) return winPSX86;
            var pwshEnv = Environment.GetEnvironmentVariable("PATH")?.Split(';')
                .Select(p => p.TrimEnd('\\') + @"\pwsh.exe").FirstOrDefault(File.Exists);
            if (pwshEnv != null) return pwshEnv;
            var pshEnv = Environment.GetEnvironmentVariable("PATH")?.Split(';')
                .Select(p => p.TrimEnd('\\') + @"\powershell.exe").FirstOrDefault(File.Exists);
            return pshEnv;
        }

        var shellExe = FindPowerShell()
            ?? throw new InvalidOperationException("Impossibile trovare PowerShell.");

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
                return $"Errore: timeout di {timeoutSeconds}s.\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
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
