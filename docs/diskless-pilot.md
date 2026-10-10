# Пилот полного бездиска на 2–3 ПК

Статус: порядок пилота, 2026-10-10. Цель — до перевода зон 1–2 (28 ПК) проверить на 2–3 ПК загрузку Windows 11 25H2
Pro по iSCSI (iPXE `sanboot` + iBFT, Secure Boot выкл.) с одного эталона. Проект — `docs/diskless-full.md`, подготовка
сервера — `docs/install/club.md` (раздел 5). Основание — исследование 2026-10-10 (источники в конце).

Обозначения: **[руки]** — человек у ПК или коммутатора; **[удалённо]** — инженер по SSH на сервере
(`clubadmin@192.168.1.201`) или в панели `https://192.168.1.201:5443/panel/`.

## 0. Сеть клуба и чужой сервер бездиска

| | Адрес |
|---|---|
| Роутер: шлюз, DNS, DHCP (сейчас) | `192.168.1.1` |
| TrueNAS: портал iSCSI `:3260` | `192.168.1.200` |
| Сервер клуба (ВМ): панель `:5443`, iPXE и файлы `:5080` | `192.168.1.201` |
| Сервер бездиска на Windows, продукт неизвестен | `192.168.1.220` |

**Пока не выяснено, что за сервер `.220` и раздаёт ли он DHCP/PXE, наш Kea не включать** (`docs/network.md`): при двух
DHCP в одной сети ПК получают адреса и загрузчики вразнобой — ломается и пилот, и ПК, которые грузятся с `.220`.

1. [руки] Спросить у клуба: что за программа на `.220` (CCBoot, iCafe8, NxD, ggRock…), какие ПК с неё грузятся, можно ли
   выключить её DHCP/PXE.
2. [удалённо] Кто отвечает на загрузку по сети — запустить и включить любой ПК с загрузкой по сети:
   ```sh
   IF=$(ip -o -4 route show default | awk '{print $5}')
   sudo tcpdump -ni "$IF" -vv 'udp port 67 or udp port 68 or udp port 4011'
   ```
   Ответы с `192.168.1.220` (порт 67 — DHCP, 4011 — proxyDHCP, в ответе `PXEClient` и имя загрузчика) — он раздаёт
   PXE. Ответы, адресованные одному ПК (unicast), с ВМ видны не всегда — смотреть широковещательные.
3. Как грузить пилотные ПК, пока Kea выключен:
   - **(а)** клуб выключил DHCP/PXE на `.220` → [удалённо] Kea по `docs/network.md` (резервации, классы PXE), DHCP
     роутера — выключить (иначе снова два DHCP);
   - **(б)** без Kea — iPXE с флешки (на стенде не проверялось): [руки] флешка FAT32, файл `/srv/tftp/ipxe.efi` с сервера
     → `EFI\BOOT\BOOTX64.EFI`, в BIOS флешка первой; на экране iPXE — Ctrl-B и
     ```
     dhcp
     chain http://192.168.1.201:5080/pxe/v1/boot.ipxe
     ```
     Адрес выдаёт роутер, дальше всё как при загрузке по сети: сервер узнаёт ПК по MAC (ПК должен быть добавлен в
     панели). Набирать на каждой загрузке. На время установки Windows (раздел 4) флешку вынуть, когда открылся
     установщик, и вставить обратно до его перезагрузки.

## 1. Инвентаризация: сетевая карта и плата [руки]

На каждый ПК зон 1–2 (сначала — пилотные) записать: место, плата (производитель, модель), видеокарта, MAC, VEN/DEV
сетевой карты.

- ПК сейчас работает (Windows с `.220` или с диска) — PowerShell:
  `Get-NetAdapter -Physical | fl Name,InterfaceDescription,DriverFileName,MacAddress,PnPDeviceID` — в `PnPDeviceID`
  видно `VEN_10EC&DEV_8168&SUBSYS_…`.
- Иначе — в iPXE (Ctrl-B, затем `dhcp`):
  `echo ${net0/mac} ${pci/${net0/busloc}.0.2} ${pci/${net0/busloc}.2.2} ${pci/${net0/busloc}.44.2} ${pci/${net0/busloc}.46.2}`
  — MAC, VEN, DEV, SUBSYS (у Realtek RTL8111H/8118 — `10ec` и `8168`, возможно в виде `10:ec`).
- Всё, что не `10EC:8168` (Intel I219-V: `8086:15BC/15BE/0D4F/0D4D/15FA/15F9/1A1F/1A1D`), — в гибридную зону или
  вводить отдельно (разделы 7, 10): у Intel другой драйвер, без введения — 0x7B.
- Пилот: 2 ПК самой частой платы зоны 1 (H410 одного производителя) + 1 ПК другой платы (H410 другого
  производителя, H510 или A520).

## 2. Коммутаторы [руки / удалённо]

На портах ПК (не на SFP+ между коммутаторами и к серверу):

- **spanning tree: portfast (edge) + BPDU guard.** Без portfast порт после каждого подъёма линка (iPXE, затем драйвер
  Windows) 30–60 с не пропускает трафик: таймауты iPXE и синие экраны. Ruijie, CLI (синтаксис сверить с моделью):
  `interface range gigabitEthernet 0/1-40`, `spanning-tree portfast`, `spanning-tree bpduguard enable`.
- **EEE (Energy Efficient Ethernet, 802.3az) — выкл.** на тех же портах.

Проверка [руки]: ПК обесточить на 10 с и включить — iPXE получает адрес за секунды, без долгого ожидания линка/DHCP.

## 3. Драйвер сетевой карты для установщика Windows [удалённо]

Без драйвера карты в WinPE установщик отказывается от iSCSI-диска («iSCSI deployment is disabled since no NICs
referenced in the iBFT can be resolved…» или «This computer's hardware may not support booting to this disk»), а
`net use` к шаре с ISO повторяется бесконечно.

`extract-winsetup.sh` (`docs/install/club.md`, раздел 5) монтирует ISO, отдаёт его SMB-шарой `winsetup` и пишет
`install.cmd`/`winpeshl.ini`. Новая версия `install.cmd` загружает драйверы (`drvload`), запускает установщик с
`/noreboot` и потом готовит установленную Windows. Если ISO распакован старой версией скрипта — запустить его ещё раз и
проверить: `grep -c drvload /srv/club/pxe/winsetup/install.cmd` — не 0.

### 3.1 Есть ли драйвер в boot.wim

```sh
sudo apt install -y wimtools binutils
B=/srv/club/pxe/winsetup/sources/boot.wim
wiminfo "$B"                                   # индекс 2 — «Microsoft Windows Setup»
rm -rf /tmp/bw && mkdir -p /tmp/bw && wimextract "$B" 2 /Windows/INF --dest-dir=/tmp/bw --no-acls
cd /tmp/bw/INF && for f in *.inf; do { cat "$f"; strings -el "$f"; } \
  | grep -qiE 'VEN_10EC&DEV_8168|VEN_8086&DEV_(15BC|15BE|0D4F|0D4D|15FA|15F9|1A1F|1A1D)' && echo "$f"; done
```

Пусто — драйвера нет, нужен 3.2. Есть — WinPE карту увидит, но **для Realtek папку `drv/` всё равно заполнить**: из неё
же `install.cmd` ставит драйвер в установленную Windows, а эталону нужен NDIS-драйвер `rt640x64`, а не встроенный в
Windows 11 `rtcx21x64` (NetAdapterCx).

### 3.2 Драйвер Realtek в `/srv/club/pxe/winsetup/drv/`

Сервер добавляет **каждый файл** этой папки в загрузку установщика (initrd wimboot → `X:\Windows\System32`),
`install.cmd` загружает каждый `*.inf` (`drvload`) до запуска сети, после первой фазы установки добавляет их в
установленную Windows (`dism /Add-Driver`) и ставит службам сетевых карт загрузку при старте (`Start=0`, `BootFlags=1`).

1. [руки или удалённо, с ноутбука] realtek.com → Downloads → Network Interface Controllers → 10/100/1000M Gigabit
   Ethernet → PCI Express → Software: **«Win10/Win11 Auto Installation Program (NDIS) – Not Support Power Saving»**
   (по исследованию — 10.80.20). Не вариант с энергосбережением (10.80.50) и не NetAdapterCx (11.031.x).
2. [удалённо] Распаковать на сервере в пустую папку и найти файлы:
   ```sh
   scp <архив>.zip clubadmin@192.168.1.201:~      # с ноутбука
   rm -rf ~/rtk && mkdir ~/rtk && cd ~/rtk && unzip -q ~/<архив>.zip
   find . -iname 'rt640x64*'        # .inf, .sys, .cat — из папки для 64-бит Windows 11 (или Win10, 64)
   ```
   Внутри только `setup.exe` и `data*.cab` (InstallShield) — `sudo apt install -y unshield && unshield x data1.cab` и
   снова `find`. Не вышло — поставить пакет на любом Windows-ПК и взять файлы из
   `C:\Windows\System32\DriverStore\FileRepository\rt640x64.inf_amd64_*\`.
3. Какие файлы нужны INF и его версия (INF бывает в UTF-16):
   ```sh
   INF=$(find ~/rtk -iname 'rt640x64.inf' | head -n1); D=$(dirname "$INF")
   { cat "$INF"; iconv -f UTF-16 -t UTF-8 "$INF" 2>/dev/null; } | grep -aiE -A4 '^\[SourceDisksFiles|^DriverVer'
   ```
   Все файлы из `[SourceDisksFiles]` — тоже в `drv/`.
4. Положить и проверить, что сервер их отдаёт:
   ```sh
   sudo install -d -m 755 /srv/club/pxe/winsetup/drv
   find "$D" -maxdepth 1 -iname 'rt640x64.*' -exec sudo install -m 644 {} /srv/club/pxe/winsetup/drv/ \;
   ls -la /srv/club/pxe/winsetup/drv
   curl -sI http://192.168.1.201:5080/pxe/v1/files/winsetup/drv/rt640x64.inf | head -n1     # 200
   ```

Правила папки: только файлы прямо в ней (подпапки не читаются), имена — латиница, цифры, `.`, `-`, `_`, до 64 символов
(остальные сервер пропускает). Только драйверы сетевых карт зон 1–2: каждый файл грузится в память WinPE и ставится в
эталон. Intel I219-V (если такие платы останутся) — «Intel Ethernet Adapter Complete Driver Pack», папка
`PRO1000\Winx64\` для Windows 11 (`e1d*.inf/.sys/.cat`, имена сверить). Повторный `extract-winsetup.sh` папку `drv/`
не трогает, а `boot.wim` заменяет.

### 3.3 Если drvload не хватило [удалённо + руки]

Драйвер в `drv/`, а установщик не видит Диск 0 или пишет «hardware may not support booting to this disk» — вероятно,
вход по iBFT прошёл раньше, чем появился драйвер. Тогда драйвер — прямо в `boot.wim`, на любом Windows-ПК (гибрид):
скопировать туда `/srv/club/pxe/winsetup/sources/boot.wim` и файлы драйвера (`C:\drv`), затем от администратора

```bat
dism /Mount-Image /ImageFile:C:\w\boot.wim /Index:2 /MountDir:C:\m
dism /Image:C:\m /Add-Driver /Driver:C:\drv /Recurse
dism /Unmount-Image /MountDir:C:\m /Commit
```

и то же с `/Index:1`; вернуть файл на сервер (`sudo install -m 644 boot.wim /srv/club/pxe/winsetup/sources/`). После
повторного `extract-winsetup.sh` — повторить.

## 4. Установка эталона [удалённо + руки]

ПК мастера — самой частой платы зоны 1 (H410 + Realtek), **без локальных дисков и флешек** (установщик может положить
загрузчик на них).

1. [удалённо] Панель → «Бездиск» → «Новый бездисковый ПК»: MAC и номер места.
2. [удалённо] «Режим мастера»: этот ПК, галочка «Установка Windows с нуля» → «Включить».
3. [руки] В течение 3 минут перезагрузить ПК (установщик выдаётся один раз; повтор — снова «Режим мастера» с галочкой).
   В чёрном окне `install.cmd`: строки `drvload` без ошибок, затем `iscsicli SessionList` — сессия к таргету
   `…:diskless-master`. Сфотографировать. Нет сессии или в установщике нет Диска 0 — раздел 3.3.
4. [руки] Установщик: Windows 11 Pro, «Выборочная», Диск 0 (эталон, 64 ГБ). После копирования файлов `install.cmd`
   сам готовит установленную Windows (драйверы из `drv/`, загрузка сетевой карты при старте, дампы, шифрование, быстрый
   запуск) и перезагружает ПК; ПК грузится с эталона и продолжает установку.
5. [руки] OOBE 25H2 требует интернет (шлюз `192.168.1.1`) и учётную запись Microsoft; локальная учётная запись — если
   OOBE даёт такой путь, иначе учётная запись Microsoft клуба и затем локальный администратор.
6. [удалённо] После рабочего стола — «Установка завершена»: дальше ПК грузится с эталона в режиме правки.

## 5. Первая загрузка с iSCSI: проверки [руки → вывод инженеру]

В мастере, командная строка от администратора:

```bat
ipconfig /all
route print -4
iscsicli SessionList
powershell -c "(Find-NetRoute -RemoteIPAddress 192.168.1.200).NextHop; Get-NetAdapter -Physical | fl Name,InterfaceDescription,DriverFileName,PnPDeviceID,LinkSpeed"
```

- `iscsicli SessionList` — сессия к `…:diskless-master` (позже ещё диск игр `…:games-…`).
- `ipconfig /all` — адрес `192.168.1.x`, шлюз `192.168.1.1`, DHCP-сервер — роутер (или наш Kea), **не** `.220`.
- **Маршрут к TrueNAS.** `NextHop` = `0.0.0.0` (напрямую) — хорошо. `192.168.1.1` (в `route print` строка
  `192.168.1.200  255.255.255.255  192.168.1.1 …`) — инициатор ведёт диск через роутер. Тогда [удалённо]:
  ```sh
  sudo sed -i '/^Diskless__ClearIbftGateway=/d' /etc/club-server/club-server.env
  echo 'Diskless__ClearIbftGateway=true' | sudo tee -a /etc/club-server/club-server.env
  sudo systemctl restart club-server
  ```
  iPXE перед подключением диска уберёт шлюз из iBFT (`set netX/gateway 0.0.0.0`). [руки] Перезагрузить мастер и
  проверить снова: маршрута через `.1` нет **и интернет есть** (`route print -4` — строка `0.0.0.0  0.0.0.0
  192.168.1.1`, `ping 1.1.1.1`, `nslookup microsoft.com`). Интернета нет — вернуть `false`, перезапустить службу и
  разбираться (запасной путь — маршрут к порталу опцией DHCP 121/249, это уже Kea).
- **Драйвер карты** — `rt640x64.sys`. `rtcx21x64.sys` — драйвер из `drv/` не встал: **драйвер живой загрузочной карты
  не менять** (синий экран), проверить `drv/` и переустановить эталон (раздел 4).
- Синий экран DRIVER_IRQL_NOT_LESS_OR_EQUAL на первой загрузке — известная беда с файлом подкачки на iSCSI. Выключить
  его офлайн: «Режим мастера» с «Установка Windows с нуля» → в установщике Shift+F10 →
  `reg load HKLM\T <буква>:\Windows\System32\config\SYSTEM`,
  `reg add "HKLM\T\ControlSet001\Control\Session Manager\Memory Management" /v PagingFiles /t REG_MULTI_SZ /d "" /f`,
  `reg unload HKLM\T` → закрыть установщик (отказаться от установки): `install.cmd` ещё раз подготовит Windows и
  перезагрузит ПК; затем снова «Установка завершена». Флаг «Установка Windows с нуля» диск эталона не стирает — он
  только выдаёт ПК установщик.

## 6. Эталон в режиме мастера: перед КАЖДОЙ публикацией [руки]

1. Скрипт `scripts/golden/prepare-golden.ps1` (из пакета) — на мастер, в `C:\ProgramData\ClubGolden\` (останется в
   эталоне для следующих сеансов). С ноутбука администратора (раздача пакета — как в `docs/install/club.md`,
   `python3 -m http.server 8000`), PowerShell от администратора:
   ```powershell
   New-Item -ItemType Directory -Force C:\ProgramData\ClubGolden | Out-Null
   Invoke-WebRequest -UseBasicParsing http://<ноутбук>:8000/scripts/golden/prepare-golden.ps1 -OutFile C:\ProgramData\ClubGolden\prepare-golden.ps1
   powershell -ExecutionPolicy Bypass -File C:\ProgramData\ClubGolden\prepare-golden.ps1 -DisableMemoryIntegrity
   ```
   Скрипт выключает гибернацию, быстрый запуск, сон, PCIe ASPM, дампы, восстановление и WinRE, автообновления и драйверы
   из Windows Update (версия 25H2), оптимизацию доставки, контроль памяти, обслуживание, дефрагментацию, индексатор,
   точки восстановления; добавляет исключения Defender для `G:\`/`H:\`, запрещает шифрование устройства; ставит iSCSI
   удержание 120 с и таймаут диска 120 с, MSiSCSI — автоматически; находит загрузочную сетевую карту (по сессии iSCSI),
   ставит её службе и iScsiPrt `Start=0`, `BootFlags=1`, выключает её энергосбережение (`PnPCapabilities=24`, EEE,
   Green Ethernet, Power Saving Mode) **без перезапуска карты**. Ошибка одного шага остальные не останавливает.
   - `-DisableMemoryIntegrity` — целостность памяти (HVCI) выкл.: быстрее, особенно на Ryzen 5 1600; античитов ядра на
     бездиске нет. Без ключа HVCI не трогается.
   - `-StageDrivers <папка>` — драйверы в хранилище Windows без установки (`pnputil /add-driver`), например Intel для
     будущих плат.
   - На обычном месте (не мастер) или не на iSCSI-диске скрипт останавливается (обойти — `-Force`).
2. В конце — строки `PASS`/`WARN`. **Публикуем без WARN** (или с понятным, записанным исключением). WARN о
   BitLocker — `manage-bde -off C:` и дождаться «Fully Decrypted»; о стороннем компоненте на карте — удалить поставившую
   его программу (VPN, Npcap, сетевой фильтр антивируса); о `rtcx21x64` — раздел 5.
3. Порядок сеанса: изменения (драйверы, обновления, программы) → `prepare-golden.ps1` → перезагрузка мастера (Windows
   грузится, G: подключается) → выключить ПК → панель «Бездиск» → «Опубликовать».
4. Перед первой публикацией ещё: помощник (`docs/helper.md`), шелл клуба, видеодрайверы (раздел 7).

В мастере нельзя: обновлять, удалять или менять драйвер загрузочной карты, отключать её, менять её свойства без
`-NoRestart`, `ipconfig /release`. После обновлений Windows — снова скрипт: служба карты должна остаться той же.

## 7. Видеодрайверы и введение плат [руки + удалённо]

- **NVIDIA — ветка R580**: последняя с Maxwell/Pascal (GTX 980/1050), GTX 1650 тоже. Драйвер 590+ не ставить — на
  980/1050 будет «Базовый видеоадаптер». Установка «Выборочная» → только графический драйвер.
- **AMD RX 470 (Polaris)** — отдельный пакет «Polaris/Vega» (основной Adrenalin их не поддерживает с 23.11), вариант
  «Только драйвер», чтобы интерфейс AMD не запускался на ПК с NVIDIA.
- Установщик ставит драйвер, только если карта есть: NVIDIA — в сеансе мастера на ПК с NVIDIA, AMD — на ПК с RX 470.
- **Каждая модель платы и каждая пара (плата × видеокарта)** — один раз загрузить мастер на таком ПК: Windows
  ставит его устройства (сетевая карта, чипсет, видео) в эталон. Иначе на месте с этой парой драйверы ставятся при каждой
  загрузке (чёрный экран 10–60 с, откат всё стирает), а новая сетевая карта может не поднять диск.
  Для каждой пары: [удалённо] «Режим мастера» → этот ПК (прежний ПК мастера выключен) → [руки] загрузка, дождаться
  установки устройств (в «Диспетчере устройств» нет «!»), `prepare-golden.ps1` (флаги и энергосбережение сетевой карты
  этой платы), перезагрузка, выключение → следующая пара. В конце — «Опубликовать».

| Зона | Плата | Видеокарты (по факту) | MAC ПК для введения | Введено |
|---|---|---|---|---|
| 1 | H410 Gigabyte + i3-10100F | | | |
| 1 | H410 Colorful + i3-10100F | | | |
| 1 | H410 MSI + i3-10100F | | | |
| 2 | A520 + Ryzen 5 1600 | | | |
| 2 | H510 + i5-10400F | | | |

Видеокарты зон: GTX 980/1050/1650, RX 470. Драйверов в эталоне — только нужные: каждый лишний замедляет загрузку мест.

## 8. Второй и третий ПК [удалённо + руки]

1. [удалённо] Версия опубликована; второй ПК той же платы — «Новый бездисковый ПК» (обычное место). [руки] Загрузка:
   Windows с личного диска места, G: подключается. На обоих ПК
   `Get-PnpDevice -Class Net | ? InstanceId -like 'PCI\*' | fl InstanceId`: у Realtek одной модели платы хвост ИД
   обычно совпадает (`…\01000000684CE00000`) — для Windows это одно устройство.
2. [руки] ПК другой платы как обычное место — до введения его платы: грузится → хорошо (плату всё равно ввести,
   раздел 7); 0x7B → раздел 10.
3. Записать время загрузки места от включения до рабочего стола: ожидаем 1–2 мин (iPXE медленно читает диск до
   передачи Windows).

## 9. Обрывы сети [руки + удалённо]

На работающем месте (не мастер, без игроков). Журнал смотреть **до перезагрузки** — после неё диск места сброшен.

| Тест | Ожидаем |
|---|---|
| Кабель ПК выдернуть на 5 с, 30 с, 90 с | Windows подвисает и продолжает работу |
| Перезагрузка коммутатора | то же, если укладывается в 120 с |
| [удалённо] Перестройка iSCSI TrueNAS при работающем месте: загрузка другого места или публикация версии | то же |

Обрыв дольше 120 с (удержание запросов) — синий экран ожидаем, это предел. После каждого теста:

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='iScsiPrt'} -MaxEvents 20 | ft TimeCreated,Id,Message -Wrap
```

Событие 20 — связь с таргетом потеряна, 34 — восстановлена. На местах не делать: `ipconfig /release`, отключение карты,
смену её свойств; не менять CHAP-секрет места при работающем ПК (переподключение идёт со старым секретом из iBFT).

## 10. Синий экран 0x7B INACCESSIBLE_BOOT_DEVICE

Почти всегда — сетевая карта: Windows не подняла загрузочную карту раньше, чем понадобился диск. Сначала проверить в
мастере (`prepare-golden.ps1`, раздел 5): у службы драйвера `Start=0`, `BootFlags=1`, нет `StartOverride`; на карте
нет сторонних компонентов; служба не подменена Windows Update; карта той же модели (Intel I219-V — другой драйвер, без
введения 0x7B ожидаем). Дальше по порядку:

1. **Ввести плату в мастере** (раздел 7): если мастер на ней загрузился — опубликовать, места заработают.
2. **Создать службу вручную** (низкая уверенность). В мастере на плате, которая грузится: `.sys` — в
   `C:\Windows\System32\drivers\`, служба — по имени из INF (строка `AddService = <служба>, …`):
   ```bat
   set S=HKLM\SYSTEM\CurrentControlSet\Services\<служба>
   reg add %S% /v Type /t REG_DWORD /d 1 /f
   reg add %S% /v Start /t REG_DWORD /d 0 /f
   reg add %S% /v ErrorControl /t REG_DWORD /d 1 /f
   reg add %S% /v Group /t REG_SZ /d NDIS /f
   reg add %S% /v ImagePath /t REG_EXPAND_SZ /d \SystemRoot\System32\drivers\<файл>.sys /f
   reg add %S% /v BootFlags /t REG_DWORD /d 1 /f
   pnputil /add-driver <папка>\<драйвер>.inf
   ```
   Выключить мастер, загрузить мастером ПК проблемной платы: загрузился — дождаться установки карты,
   `prepare-golden.ps1`, опубликовать.
3. **Временный локальный SSD** в этом ПК: с Linux live скопировать на него эталон (`dd` из таргета мастера по
   iSCSI), один раз загрузить Windows локально (PnP поставит карту), скопировать обратно. Долго — крайний случай.
4. **Отдельный эталон** для этой платформы — сервер пока держит один эталон, поэтому на практике такие ПК —
   **в гибрид**.

## 11. Итог пилота — перед зонами 1–2

- [ ] `.220` выяснен; DHCP/PXE в сети один (Kea по `docs/network.md` или договорённость с клубом).
- [ ] Коммутаторы: portfast + BPDU guard, EEE выкл.; холодный старт ПК без задержек.
- [ ] Эталон: `prepare-golden.ps1` без WARN; маршрут к `192.168.1.200` не через `.1`, интернет есть.
- [ ] 2 ПК одной платы и 1 ПК другой платы грузятся как места; G: подключается; время загрузки записано.
- [ ] Все пары (плата × видеокарта) зон 1–2 введены (таблица раздела 7).
- [ ] Обрывы 5/30/90 с пережиты, события iScsiPrt 20/34 есть.
- [ ] Записано: версия эталона, Windows (сборка), драйверы Realtek, NVIDIA R580, AMD Polaris.

## Источники

- iPXE: [howto/winpe](https://ipxe.org/howto/winpe), [wimboot](https://ipxe.org/wimboot);
  [NiKiZe/wimboot-install](https://github.com/NiKiZe/wimboot-install) — drvload, `/noreboot`, DISM в установленную
  Windows; [ipxe #1544](https://github.com/ipxe/ipxe/discussions/1544) — скорость до передачи Windows.
- Microsoft: [About iSCSI Boot](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/ee619722(v=ws.10)),
  [KB: смена сетевого оборудования → 0x7B (WFP LWF)](https://support.microsoft.com/en-us/topic/windows-may-fail-to-boot-from-an-iscsi-drive-if-networking-hardware-is-changed-5363d4bc-0103-e183-cb1c-8436e1691c13),
  [inbox-драйверы сети](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/inbox-network-drivers?view=windows-11).
- Обрывы: [NetApp — параметры MS iSCSI](https://kb.netapp.com/onprem/ontap/da/SAN-KBs/What_are_the_parameters_that_control_how_MS_iSCSI_survives_lost_TCP_connections_without_causing_applications_harm),
  [AWS — рекомендуемые настройки iSCSI](https://docs.aws.amazon.com/storagegateway/latest/vgw/recommendediSCSISettings.html),
  [ggCircuit — STP edge/portfast](https://helpdesk.ggcircuit.com/en/article/diskless-pcs-fail-to-pxe-boot-after-a-merakicisco-switch-firmware-upgrade-stp-edge-port-portfast-fix-mii859/).
- Новые платы в образе: [CCBoot NIC PnP](https://www.ccboot.com/wikis-nic-pnp.htm),
  [ggRock Add New Hardware](https://ggcircuit.atlassian.net/wiki/spaces/GKB/pages/15860307/Adding+a+New+Machine+Type+to+a+ggRock+Image+using+the+ggRock+Seamless+Boot+Procedure);
  [OSR: BootFlags и iBFT](https://community.osr.com/t/network-boot-and-bootflags-how-does-windows-know/52391).
- Драйверы: [Realtek](https://www.realtek.com/Download/List?cate_id=584); NVIDIA R580 — последняя ветка для
  Maxwell/Pascal ([Phoronix](https://www.phoronix.com/news/NVIDIA-580-Linux-Driver-Last-HW)).
