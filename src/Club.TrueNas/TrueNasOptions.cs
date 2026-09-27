namespace Club.TrueNas;

public sealed class TrueNasOptions
{
    /// <summary>Хост TrueNAS (имя из сертификата, которое проверяется при TLS).</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 443;

    /// <summary>Сервисный пользователь TrueNAS, к которому привязан API-ключ (минимальные роли, без пароля и web_shell).</summary>
    public string Username { get; set; } = "";

    /// <summary>API-ключ вида <c>&lt;id&gt;-&lt;64 символа&gt;</c>. Передаётся только по wss:// — по незащищённому
    /// транспорту TrueNAS отзывает ключ сам.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>PEM внутреннего CA клуба, которым подписан сертификат TrueNAS. Самоподписанный <c>truenas_default</c> не пинить:
    /// TrueNAS перевыпускает его с новым ключом.</summary>
    public string CaCertificatePath { get; set; } = "";

    /// <summary>
    /// Версии API, проверенные на стенде, в порядке предпочтения не важен — берётся максимальная доступная.
    /// Номер версии API не совпадает с номером релиза (25.10.7 → v25.10.5), поэтому белый список ведётся по /api/versions.
    /// </summary>
    public List<string> AllowedApiVersions { get; set; } = ["v25.10.0", "v25.10.1", "v25.10.2", "v25.10.3", "v25.10.4", "v25.10.5"];

    /// <summary>Не больше 10 параллельных вызовов на соединение: у сервера SoftHardSemaphore(10, 20), 21-й получает -32000.</summary>
    public int MaxConcurrentCalls { get; set; } = 10;

    public int CallTimeoutSec { get; set; } = 60;

    /// <summary>core.ping после логина: nginx в 25.x закрывает /api после 60 с тишины со стороны сервера.</summary>
    public int PingIntervalSec { get; set; } = 25;
}
