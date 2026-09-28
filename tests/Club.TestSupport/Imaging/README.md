`two-images.wim` — настоящий WIM из двух образов (24 КБ), собран wimlib 1.14.5 (пакет Ubuntu `wimtools`):

```bash
mkdir -p src/Windows/System32 && echo hello > src/Windows/System32/a.txt && head -c 20000 /dev/urandom > src/Windows/big.bin
wimcapture src two-images.wim "Windows 11 Pro" "Клуб: золотой образ" --compress=LZX
wiminfo two-images.wim 1 --image-property WINDOWS/EDITIONID=Professional --image-property WINDOWS/ARCH=9 \
  --image-property WINDOWS/VERSION/MAJOR=10 --image-property WINDOWS/VERSION/BUILD=26100 \
  --image-property WINDOWS/VERSION/SPBUILD=4652 --image-property "WINDOWS/PRODUCTNAME=Microsoft® Windows® Operating System" \
  --image-property WINDOWS/LANGUAGES/DEFAULT=ru-RU
wimappend src two-images.wim "Windows 11 Home"
```

Используется в тестах разбора заголовка и XML-описания WIM.
