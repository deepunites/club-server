using System.Security.Principal;
using Club.Helper.Core;
using Microsoft.Win32;

namespace Club.Helper;

/// <summary>
/// Профиль пользователя Windows по SID для сохранений игрока (spec 2026-11-05, п. 2 и 4.2): <c>ProfileImagePath</c> из
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\&lt;SID&gt;</c>, переменные (<c>%SystemDrive%</c>)
/// раскрываются. Не <c>C:\Users\player</c> напрямую: после неудачного удаления профиль бывает <c>player.&lt;ПК&gt;</c>. SID
/// от шелла сначала разбирается (<see cref="SecurityIdentifier"/>), в путь реестра идёт его каноническая форма: строка с
/// <c>\</c> или чем-то ещё, кроме SID, ключ реестра не выберет. Не SID, нет ключа, нет пути или папки профиля — <c>null</c>
/// (<c>profileNotFound</c>); сбой чтения реестра — исключение (<c>internal</c>, в журнал).
/// </summary>
public sealed class WindowsProfileLocator : IProfileLocator
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public string? ProfilePath(string sid)
    {
        SecurityIdentifier identifier;
        try
        {
            identifier = new SecurityIdentifier(sid.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{identifier.Value}");
        if (key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = Environment.ExpandEnvironmentVariables(raw.Trim());
        return Path.IsPathFullyQualified(path) && Directory.Exists(path) ? path : null;
    }
}

/// <summary>Корень буквы библиотеки: <c>G:\</c>, <c>H:\</c>.</summary>
public sealed class WindowsDriveRoots : IDriveRoots
{
    public string RootOf(char letter) => $"{char.ToUpperInvariant(letter)}:\\";
}
