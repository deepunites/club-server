<#
.SYNOPSIS
    Ставит на ПК клуба ClubMaxRefresh: при входе любого пользователя каждому монитору — максимальная частота обновления
    при текущем разрешении.
.DESCRIPTION
    Частоту Windows хранит отдельно для каждого монитора. После заливки образа на гибрид, а на бездисковом ПК — после
    каждой загрузки (C: сбрасывается) монитор для Windows новый, и она ставит 60 Гц.
    Что делает (PowerShell от администратора; на эталоне бездиска — в режиме мастера, до seal-golden.ps1):
      1. собирает ClubMaxRefresh.exe компилятором C# из .NET Framework 4.x, который есть в каждом Windows 10/11
         (оконное приложение без консоли: в сеансе игрока ClubShell запрещает powershell.exe, cmd.exe и wscript.exe);
      2. кладёт его в %ProgramFiles%\ClubDiskless (туда же, где помощник; писать туда может только администратор);
      3. регистрирует задачу планировщика ClubMaxRefresh: при входе любого пользователя (группа Users, без повышения
         прав), задержка 10 с, лимит 2 мин;
      4. сразу запускает её для текущего сеанса.
    Программа меняет только частоту и только вверх, разрешение и глубину цвета не трогает; журнал —
    %TEMP%\club-max-refresh.log пользователя. Повторный запуск установщика безопасен (пересобирает exe, заменяет задачу).
    По сети одной строкой: irm http://192.168.1.201:5080/pxe/v1/files/tools/install-max-refresh.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
    Удалить: Unregister-ScheduledTask ClubMaxRefresh -Confirm:$false; Remove-Item "$env:ProgramFiles\ClubDiskless\ClubMaxRefresh.exe"
#>
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$source = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

// ClubMaxRefresh: every attached display gets its highest refresh rate at the current resolution and colour depth.
static class ClubMaxRefresh
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    const int DM_DISPLAYFREQUENCY = 0x400000;
    const int CDS_UPDATEREGISTRY = 0x1;
    const int DISP_CHANGE_SUCCESSFUL = 0;
    const int DISP_CHANGE_RESTART = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    static string logPath = Path.Combine(Path.GetTempPath(), "club-max-refresh.log");

    static void Log(string text)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine); }
        catch (Exception) { }
    }

    static DEVMODE NewMode()
    {
        DEVMODE m = new DEVMODE();
        m.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return m;
    }

    static int Main(string[] args)
    {
        bool dryRun = args.Length > 0 && args[0] == "/whatif";
        int changed = 0;
        for (int i = 0; i < 16; i++)
        {
            DISPLAY_DEVICE dev = new DISPLAY_DEVICE();
            dev.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            DEVMODE cur = NewMode();
            if (!EnumDisplaySettings(dev.DeviceName, ENUM_CURRENT_SETTINGS, ref cur))
            {
                Log(dev.DeviceName + ": current mode not readable");
                continue;
            }

            int best = cur.dmDisplayFrequency;
            for (int m = 0; m < 4096; m++)
            {
                DEVMODE mode = NewMode();
                if (!EnumDisplaySettings(dev.DeviceName, m, ref mode)) break;
                if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight &&
                    mode.dmBitsPerPel == cur.dmBitsPerPel && mode.dmDisplayFrequency > best)
                {
                    best = mode.dmDisplayFrequency;
                }
            }

            string what = string.Format("{0} ({1}): {2}x{3} {4} Hz", dev.DeviceName, dev.DeviceString,
                cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency);
            if (best <= cur.dmDisplayFrequency) { Log(what + ", already the maximum"); continue; }
            if (dryRun) { Log(what + ", could be " + best + " Hz"); continue; }

            DEVMODE want = NewMode();
            want.dmPelsWidth = cur.dmPelsWidth;
            want.dmPelsHeight = cur.dmPelsHeight;
            want.dmBitsPerPel = cur.dmBitsPerPel;
            want.dmDisplayFrequency = best;
            want.dmFields = DM_DISPLAYFREQUENCY;
            int rc = ChangeDisplaySettingsEx(dev.DeviceName, ref want, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (rc == DISP_CHANGE_SUCCESSFUL || rc == DISP_CHANGE_RESTART)
            {
                changed++;
                Log(what + " -> " + best + " Hz");
            }
            else
            {
                Log(what + " -> " + best + " Hz failed, code " + rc);
            }
        }
        if (changed == 0) Log("nothing changed");
        return 0;
    }
}
'@

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }
$dir = Join-Path $env:ProgramFiles 'ClubDiskless'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$exe = Join-Path $dir 'ClubMaxRefresh.exe'
$work = Join-Path $env:TEMP 'club-max-refresh-build'
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
$src = Join-Path $work 'ClubMaxRefresh.cs'
[System.IO.File]::WriteAllText($src, $source, (New-Object System.Text.UTF8Encoding $false))

Write-Host 'Building ClubMaxRefresh.exe'
$out = & $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$work\ClubMaxRefresh.exe" $src 2>&1
if ($LASTEXITCODE -ne 0) { $out | ForEach-Object { Write-Host $_ }; throw "csc failed with code $LASTEXITCODE" }
Copy-Item -LiteralPath "$work\ClubMaxRefresh.exe" -Destination $exe -Force
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Registering scheduled task ClubMaxRefresh (at logon of any user)'
$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$trigger.Delay = 'PT10S'
# Группа «Пользователи» по SID: имя зависит от языка Windows (BUILTIN\Users / BUILTIN\Пользователи).
$users = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([System.Security.Principal.NTAccount]).Value
$principal = New-ScheduledTaskPrincipal -GroupId $users -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'ClubMaxRefresh' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Club: highest refresh rate for every monitor at logon' -Force | Out-Null

Write-Host 'Running it now for this session'
Start-Process -FilePath $exe -Wait
$log = Join-Path $env:TEMP 'club-max-refresh.log'
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 4 | ForEach-Object { Write-Host "  $_" } }
Write-Host 'Done: ClubMaxRefresh installed' -ForegroundColor Green
