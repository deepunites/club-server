#Requires -Version 5.1
<#
.SYNOPSIS
    Диагностика: какие приложения Store (кодеки, Store, фото и т. д.) есть в системе — в файл appx.log рядом со скриптом.
    Ничего не меняет.
#>
$ErrorActionPreference = 'Continue'
$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'appx.log'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Компьютер: $env:COMPUTERNAME  время: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  ОС: $((Get-CimInstance Win32_OperatingSystem).Version)")
$want = 'VP9|WebMedia|AV1|HEVC|HEIF|WebpImage|RawImage|MPEG2|WindowsStore|StorePurchase|DesktopAppInstaller|Photos|VCLibs|UI\.Xaml|WindowsAppRuntime|NET\.Native'
$lines.Add('')
$lines.Add('== Кодеки и основа Store (установлены):')
$installed = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue)
foreach ($p in $installed | Where-Object Name -match $want | Sort-Object Name) { $lines.Add(('  {0}  {1}  {2}' -f $p.Name, $p.Version, $p.Architecture)) }
$lines.Add('')
$lines.Add('== Отсутствуют из ожидаемых:')
foreach ($n in 'Microsoft.VP9VideoExtensions', 'Microsoft.WebMediaExtensions', 'Microsoft.AV1VideoExtension', 'Microsoft.HEVCVideoExtension',
    'Microsoft.HEIFImageExtension', 'Microsoft.WebpImageExtension', 'Microsoft.WindowsStore', 'Microsoft.DesktopAppInstaller', 'Microsoft.Windows.Photos') {
    if (-not ($installed | Where-Object Name -eq $n)) { $lines.Add("  $n") }
}
$lines.Add('')
$lines.Add("== Все установленные пакеты ($($installed.Count)):")
foreach ($p in $installed | Sort-Object Name -Unique) { $lines.Add(('  {0}  {1}' -f $p.Name, $p.Version)) }
$lines.Add('')
$prov = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue)
$lines.Add("== Пакеты в образе (provisioned, $($prov.Count)):")
foreach ($p in $prov | Sort-Object DisplayName) { $lines.Add(('  {0}  {1}' -f $p.DisplayName, $p.Version)) }
$lines | Set-Content -LiteralPath $out -Encoding UTF8
$lines | Select-Object -First 25 | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Полный список — в $out" -ForegroundColor Green
