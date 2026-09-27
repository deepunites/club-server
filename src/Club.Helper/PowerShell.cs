using System.Diagnostics;
using System.Text;

namespace Club.Helper;

/// <summary>
/// Запуск Windows PowerShell 5.1 (есть в любой Windows 10/11) со скриптом. Данные с сервера (IQN, портал, буква)
/// передаются только через переменные окружения <c>CLUB_*</c>, в текст скрипта не подставляются — внедрение команд
/// через них невозможно.
/// </summary>
public static class PowerShell
{
    private static readonly string Exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<string> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(Prelude + script) })
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = (await stdout).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell failed ({process.ExitCode}): {(await stderr).Trim()}");
        }

        return output;
    }

    /// <summary>Ошибки командлетов — исключения (exit 1), вывод — UTF-8.</summary>
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }

        """;

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
