@echo off
rem Builds WinPE for network reinstall (docs/imaging.md). Run as administrator in the
rem "Deployment and Imaging Tools Environment" (Windows ADK + WinPE add-on matching the Windows image).
rem Output: out\pxe with boot\BCD, boot\boot.sdi, sources\boot.wim. Copy to Imaging:PxeRoot on the server with wimboot.
rem The deploy script and server address are not baked in: wimboot injects them from the server on every boot.
rem HYPOTHESIS: not run yet, verify on a machine with the ADK.
setlocal
if not defined WinPERoot (echo Run from "Deployment and Imaging Tools Environment" & exit /b 1)
set ARCH=amd64
set WORK=%~dp0work
set OUT=%~dp0out\pxe
set OC=%WinPERoot%\%ARCH%\WinPE_OCs
if exist "%WORK%" rmdir /s /q "%WORK%"
if exist "%OUT%" rmdir /s /q "%OUT%"
call copype %ARCH% "%WORK%" || exit /b 1
dism /Mount-Image /ImageFile:"%WORK%\media\sources\boot.wim" /Index:1 /MountDir:"%WORK%\mount" || exit /b 1
rem Package order matters: PowerShell needs WMI, NetFX and Scripting; StorageWMI provides the disk cmdlets.
for %%p in (WinPE-WMI WinPE-NetFX WinPE-Scripting WinPE-PowerShell WinPE-StorageWMI WinPE-DismCmdlets) do (
  dism /Image:"%WORK%\mount" /Add-Package /PackagePath:"%OC%\%%p.cab" || goto fail
  if exist "%OC%\en-us\%%p_en-us.cab" dism /Image:"%WORK%\mount" /Add-Package /PackagePath:"%OC%\en-us\%%p_en-us.cab" || goto fail
)
mkdir "%OUT%\boot" "%OUT%\sources"
rem Boot manager signed with Windows UEFI CA 2023 for PCs that revoked PCA 2011 (dbx). The server checks its
rem signature (panel: Windows images -> network boot files). HYPOTHESIS: EFI_EX exists in current WinPE builds.
if exist "%WORK%\mount\Windows\Boot\EFI_EX\bootmgfw_EX.efi" (
  copy /y "%WORK%\mount\Windows\Boot\EFI_EX\bootmgfw_EX.efi" "%OUT%\boot\bootx64.efi" || goto fail
) else (
  echo NOTE: no Windows\Boot\EFI_EX\bootmgfw_EX.efi in WinPE: PCs with PCA 2011 revoked will not boot it.
)
dism /Unmount-Image /MountDir:"%WORK%\mount" /Commit || exit /b 1
copy /y "%WORK%\media\Boot\BCD" "%OUT%\boot\BCD" || exit /b 1
copy /y "%WORK%\media\Boot\boot.sdi" "%OUT%\boot\boot.sdi" || exit /b 1
copy /y "%WORK%\media\sources\boot.wim" "%OUT%\sources\boot.wim" || exit /b 1
echo Done: %OUT%
echo Next: copy it to Imaging:PxeRoot on the server and add wimboot (https://github.com/ipxe/wimboot/releases).
exit /b 0
:fail
dism /Unmount-Image /MountDir:"%WORK%\mount" /Discard
exit /b 1
