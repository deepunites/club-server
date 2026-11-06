using System.Diagnostics;
using Club.Helper.Core.Updates;
using Microsoft.Win32;

namespace Club.Helper;

/// <summary>
/// Служба Windows <c>ClubDisklessHelper</c> для самообновления (spec 2026-11-06, п. 4.1, 4.3). Действия при сбое
/// проверяются по значению <c>FailureActions</c> раздела службы в реестре (<see cref="ServiceRecovery.Matches"/>: вывод
/// <c>sc.exe qfailure</c> переведён на язык Windows), ставятся <c>sc.exe failure</c> (аргументы — по одному в
/// <see cref="ProcessStartInfo.ArgumentList"/>). Завершение процесса — <see cref="Process.Kill()"/>: Windows не получает
/// <c>SERVICE_STOPPED</c>, считает это сбоем и запускает службу снова (шаг 0 на PC-22: через 5–7 с).
/// </summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public const string ServiceName = "ClubDisklessHelper";

    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    private static readonly TimeSpan ScTimeout = TimeSpan.FromSeconds(30);

    public bool RecoveryConfigured()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        return ServiceRecovery.Matches(key?.GetValue("FailureActions") as byte[]);
    }

    public void EnsureRecovery()
    {
        if (RecoveryConfigured())
        {
            return;
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in ServiceRecovery.FailureArguments(ServiceName))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ScTimeout))
        {
            process.Kill();
            throw new TimeoutException($"sc.exe failure did not finish in {ScTimeout.TotalSeconds:0} s");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failure exited with {process.ExitCode}: {(output.Result + " " + error.Result).Trim()}");
        }

        if (!RecoveryConfigured())
        {
            throw new InvalidOperationException("sc.exe failure succeeded, but the FailureActions value of the service is still different");
        }
    }

    public void TerminateSelf() => Process.GetCurrentProcess().Kill();
}
