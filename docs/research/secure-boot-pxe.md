# Secure Boot и загрузка по сети для перезаливки

Дата: 2026-09-28. Статусы: **проверено** — по файлам или официальной документации; **ГИПОТЕЗА** — не проверено.

## Цепочка

```
прошивка UEFI (Secure Boot: db/dbx)
  └─ ipxe-shim.efi   TFTP   подпись Microsoft Corporation UEFI CA 2011 (сторонний CA)
      └─ ipxe.efi    TFTP   подпись iPXE Secure Boot CA (ему доверяет shim — сертификат зашит в shim)
          └─ wimboot HTTP   подпись Microsoft: UEFI CA 2011 и UEFI CA 2023 (двойная)
              └─ bootmgfw.efi из boot.wim (Windows Production PCA 2011)
                 или boot/bootx64.efi (Windows UEFI CA 2023) — для ПК с отозванным PCA 2011
                  └─ WinPE (подписан Microsoft)
```

Одна цепочка для всех UEFI-ПК: с выключенным Secure Boot shim и подписанный iPXE работают так же.

## Проверено по файлам

Официальный релиз iPXE **v2.0.0** (`ipxeboot.tar.gz`, sha256 `01a526d4…17eee1`) и **wimboot v2.9.0** (sha256
`5f067ccd…fae8e3`). Подписи разобраны сервером (`Imaging/Authenticode.cs`, тест `AuthenticodeTests` на этих файлах):

| Файл | Подпись | SBAT |
|---|---|---|
| `x86_64-sb/ipxe-shim.efi` (= `shimx64.efi`) | Microsoft Windows UEFI Driver Publisher ← **Microsoft Corporation UEFI CA 2011** ← Third Party Marketplace Root. Подписи CA 2023 нет | `shim,4`, `shim.ipxe,1` |
| `x86_64-sb/ipxe.efi` | iPXE Secure Boot Automatic Code Signing G1A ← Intermediate G1A ← **iPXE Secure Boot CA** | `ipxe.efi 2.0.0 (g12798)` |
| `x86_64/ipxe.efi` | **не подписан** | — |
| `wimboot` | **Microsoft UEFI CA 2011** и вложенная подпись **Microsoft UEFI CA 2023** | `wimboot v2.9.0` |
| `ipxe.efi` из пакета Ubuntu `ipxe` 1.21.1 | **не подписан** | — |

Вывод: `ipxe.efi` из Ubuntu (и `x86_64/ipxe.efi` без `-sb`) с включённым Secure Boot не загрузится.
Ставить только `x86_64-sb` из релиза iPXE — это делает `scripts/pxe/install-boot-files.sh` (версии и sha256
закреплены, подменённый файл не ставится — проверено).

Shim загружает iPXE с тем же именем без `-shim` из того же каталога TFTP (`ipxe-shim.efi` → `ipxe.efi`).
Собрать свой iPXE с другими функциями и подписью нельзя — доверяется только сборкам проекта iPXE; нам хватает
стандартной (DHCP, HTTP, скрипт с сервера).

Источники: [ipxe.org/secboot](https://ipxe.org/secboot) — «With UEFI Secure Boot enabled, you can use iPXE to boot
into Microsoft Windows using wimboot in the usual way»; [ipxe.org/wimboot](https://ipxe.org/wimboot) — подмена
загрузчика через `initrd … bootx64.efi`, внедрение файлов в `X:\Windows\System32`.

## Условия на ПК

1. **Сторонний Microsoft UEFI CA 2011 в db.** Им подписан shim. На большинстве плат для самосборных ПК он есть;
   на части ноутбуков и «Secured-core» ПК отключён опцией вроде «Allow Microsoft 3rd-party UEFI CA».
   [ГИПОТЕЗА: распространённость среди клубных плат — проверить на стенде.]
2. **Отзыв PCA 2011 (CVE-2023-24932, BlackLotus).** Если в dbx есть «Microsoft Windows Production PCA 2011»,
   загрузчики Windows со старой подписью не запустятся — WinPE нужен загрузчик с подписью Windows UEFI CA 2023.
   Microsoft: [управление отзывом загрузчиков](https://support.microsoft.com/en-us/topic/how-to-manage-the-windows-boot-manager-revocations-for-secure-boot-changes-associated-with-cve-2023-24932-41a975df-beb2-40c1-99a3-b3ff139f832d),
   [руководство для предприятий](https://support.microsoft.com/en-us/topic/enterprise-deployment-guidance-for-cve-2023-24932-88b8f034-20b7-4a45-80cb-c6049b0f9967).
   Проверки на ПК из этих статей (их выполняет помощник):
   `[Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes) -match 'Windows UEFI CA 2023'`,
   `… (Get-SecureBootUEFI dbx).bytes) -match 'Microsoft Windows Production PCA 2011'`.
3. Источник загрузчика CA 2023 для WinPE: `bcdboot … /bootex` / `MakeWinPEMedia /bootex` (Microsoft).
   `build-winpe.cmd` берёт `Windows\Boot\EFI_EX\bootmgfw_EX.efi` из WinPE. [ГИПОТЕЗА: путь в текущих сборках WinPE —
   проверить; подпись файла сервер проверяет сам и покажет «не та подпись», если это не CA 2023.]

## Что делает сервер

- Проверяет файлы цепочки (`BootFiles`): наличие, подпись по-настоящему (хэш Authenticode сверяется с подписанным),
  кем подписан, версию из SBAT. Панель: «Образы Windows» → «Файлы загрузки по сети».
- Перед постановкой ПК на перезаливку отказывает, если загрузка по сети всё равно не пройдёт:
  нет файлов (`bootFilesMissing`); Secure Boot без CA 2011 (`secureBootThirdPartyCa`); Secure Boot и файлы без нужной
  подписи (`bootFilesNotSigned`); отозван PCA 2011, а загрузчика CA 2023 нет (`needsCa2023BootManager`).
- ПК с отозванным PCA 2011 получают в скрипте iPXE `initrd …/boot/bootx64.efi bootx64.efi`; остальные — загрузчик
  из boot.wim (он подходит и ПК без CA 2023 в db).

## Не проверено

- Реальная загрузка shim → iPXE → wimboot → WinPE на железе с Secure Boot (стенд).
- Отзывы SBAT: shim iPXE имеет `shim,4` — текущий уровень [ГИПОТЕЗА: на ПК с более новой политикой SbatLevel
  проверить].
- Срок действия сертификатов Microsoft 2011 истёк в 2026 году; на загрузку уже подписанных файлов это не влияет
  (прошивка не проверяет срок), но новые версии shim будут подписаны CA 2023 — тогда понадобится CA 2023 в db.
  [ГИПОТЕЗА: следить за релизами iPXE.]
