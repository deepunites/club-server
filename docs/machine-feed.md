# Список ПК для шелла (MachineFeed)

Сервер клуба сам отправляет **полный список одобренных ПК** на адрес внешней системы — кассы шелла или любой другой.
Про шелл сервер ничего не знает: это исходящий подписанный POST на заданный адрес. Работает через NAT, поэтому
получатель может быть в облаке (Railway), а сервер клуба — в сети клуба без проброса портов.

Зачем: реестры ПК два (у сервера клуба — места бездиска, у шелла — коммерческие), связь по MAC. Сервер клуба главный
для **номера места и имени**: от номера зависят IP и имя ПК в DHCP (`docs/network.md`). Шелл зеркалирует их у себя.

Схема: `docs/diskless-api.yaml` → `webhooks.machinesSnapshot`, схемы `MachinesSnapshot`, `MachineFeedEntry`.

## Настройка на сервере клуба

`/etc/club-server/club-server.env`:

```ini
MachineFeed__Url=https://<касса шелла>/<путь, который назначит шелл для этого клуба>
MachineFeed__Secret=<openssl rand -hex 32; тот же секрет — у получателя>
# MachineFeed__CaCertificatePath=/etc/club-server/tls/club-ca.crt   # только если у получателя сертификат своего CA
```

Пустой `Url` — отправка выключена. Секрет короче 32 символов, неверный адрес или отсутствующий файл CA — сервер
не запускается (ошибка в `journalctl -u club-server`).

В панели на «Рабочих станциях» — плашка «список ПК → хост»: когда отправлен, или «ошибка отправки» с текстом ошибки
во всплывающей подсказке.

## Запрос

```http
POST /<путь> HTTP/1.1
Content-Type: application/json; charset=utf-8
User-Agent: club-server/1.0.0
X-Club-Event: machines.snapshot
X-Club-Timestamp: 1790000000
X-Club-Signature: v1=5d41c0…(64 hex)

{
  "schema": "club-server.machines/1",
  "generatedAt": "2026-09-29T12:00:00.123Z",
  "contentHash": "9f86d081…(64 hex)",
  "machines": [
    { "id": "3f1c…", "number": 1, "name": "PC-01", "hostname": "DESKTOP-7Q2", "macAddresses": ["aa:bb:cc:00:00:01"] },
    { "id": "8a02…", "number": 7, "name": "VIP-7", "hostname": "DESKTOP-K1M", "macAddresses": ["aa:bb:cc:00:00:07", "aa:bb:cc:00:00:08"] }
  ]
}
```

| Поле | |
|---|---|
| `machines` | Одобренные ПК по возрастанию номера. Ожидающие одобрения не входят. ПК, которого нет в снимке, удалён или ещё не одобрен |
| `id` | Идентификатор машины в сервере клуба; не меняется, пока машина в реестре (при замене железа — новая машина) |
| `number`, `name` | Номер места и имя из панели бездиска — главные |
| `hostname` | Имя Windows, которое сообщил помощник бездиска |
| `macAddresses` | Нижний регистр, двоеточия, по возрастанию — ключ связи с реестром шелла |
| `contentHash` | sha256 списка машин: одинаковый — список не изменился |
| `generatedAt` | Когда снят список |

Зоны не передаются (тарифные зоны шелла ≠ сетевые зоны клуба).

## Когда отправляется

- При изменении списка (одобрение, номер, имя, MAC, новая регистрация одобренной машины) — не позже чем через
  `MachineFeed:IntervalSec` (10 с).
- Неизменный список — повторно раз в `MachineFeed:ResendMinutes` (10 мин): получатель, потерявший данные,
  восстановится сам. После перезапуска сервера клуба — сразу.
- Ответ **2xx** — принято (тело ответа не читается). Любой другой код, таймаут 15 с, обрыв, ошибка TLS — повтор через
  10 с, 20 с, 40 с … не реже раза в 5 минут, пока не примут. Редиректы не выполняются (3xx — ошибка).

## Что делает получатель

1. **Проверить подпись** по сырым байтам тела, до разбора JSON:
   `v1=` + hex(HMAC-SHA256(секрет, `"{X-Club-Timestamp}.{тело}"`)), сравнение за постоянное время.
2. **Отклонить старое**: `X-Club-Timestamp` дальше 5 минут от текущего времени — 401 (часы сервера клуба
   синхронизируются по NTP, этап 1 `docs/install/stand.md`).
3. **Не откатываться назад**: снимок с `generatedAt` не новее последнего применённого для этого клуба — ответить 2xx
   и ничего не менять. Одинаковый `contentHash` — тоже ничего не менять.
4. **Применить**: связать ПК по MAC, для связанных взять номер и имя из снимка. Отвечать быстро (до 15 с), тяжёлую
   работу — в фоне.
5. **Неизвестные поля пропускать**: в `club-server.machines/1` могут добавляться поля. Несовместимые изменения — только
   с новым значением `schema`.

Секрет и путь — свои для каждого клуба: по ним получатель понимает, чей это список.

Пример проверки на ASP.NET Core:

```csharp
app.MapPost("/integrations/diskless/{clubId}/machines", async (string clubId, HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();
    var timestamp = request.Headers["X-Club-Timestamp"].ToString();
    if (!long.TryParse(timestamp, out var unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300)
    {
        return Results.Unauthorized();
    }

    var secret = SecretFor(clubId); // байты UTF-8 секрета этого клуба
    var message = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
    var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, message));
    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(request.Headers["X-Club-Signature"].ToString())))
    {
        return Results.Unauthorized();
    }

    var snapshot = JsonSerializer.Deserialize<MachinesSnapshot>(body, JsonSerializerOptions.Web)!;
    // generatedAt не новее применённого → return Results.NoContent(); иначе связать по MAC, записать номер и имя.
    return Results.NoContent();
});
```

Контрольный пример подписи: секрет `s3cret`, метка `1790000000`, тело `{"schema":"club-server.machines/1"}` →
`v1=d77bd0633fede6b09679736f029b6cd328da9e829584fb6101e6fd462e52d70d` (тест `MachineFeedTests.Signature_matches_the_documented_example`).

## Проверено и не проверено

- Проверено тестами (`MachineFeedTests`, настоящий HTTP/HTTPS-получатель на Kestrel): подпись по сырым байтам и
  схема снимка; в списке только одобренные; изменения уходят, неизменный список — раз в 10 минут; отказ получателя
  → повторы с паузой и ошибка в панели, потом восстановление; редирект не выполняется; HTTPS со своим CA; неверные
  настройки не дают серверу запуститься.
- Не проверено: доставка на настоящую кассу шелла (Railway) из сети клуба.
