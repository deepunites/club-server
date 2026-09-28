Проверка резерваций и PXE-флага на настоящей Kea без root: `unshare -r -n` даёт своё сетевое пространство,
в нём пара veth; Kea слушает `v0`, `dhcp_probe.py` шлёт DHCPDISCOVER (MAC, option 93, option 77) с `v1` и
принимает ответ через AF_PACKET (Kea отвечает raw-сокетом на MAC клиента). Используется в
`NetworkTests.Kea_gives_boot_files_only_to_armed_machines`, если задан `KEA_DHCP4`.
