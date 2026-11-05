using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Club.Helper.Core;

namespace Club.Helper;

/// <summary>
/// Канал шелла <c>\\.\pipe\club-diskless-helper</c> (spec 2026-11-05, п. 4.1): транспорт для
/// <see cref="SavesCommandHandler"/>. Доступ — только SYSTEM и BUILTIN\Administrators (FullControl, без наследования и
/// без остальных: Everyone, Users и т. п. не подключатся); агент шелла — служба LocalSystem, тестовая команда
/// <c>saves-test.ps1</c> — от администратора. До <see cref="MaxInstances"/> подключений одновременно (ping отвечает и во
/// время долгой выдачи; attach и detach выполняются по одной — это делает обработчик).
/// На подключение: одна строка запроса до первого <c>\n</c>, не больше <see cref="MaxRequestBytes"/> байт (длиннее —
/// <c>badRequest</c> без чтения остального; не пришла за <see cref="RequestTimeout"/> — подключение закрывается);
/// команда — с токеном остановки службы, а не подключения: обрыв клиента начатую выдачу или сохранение не прерывает
/// (сохранение доходит до сервера, иначе прогресс игрока потерялся бы); ответ и <c>\n</c>, ожидание, пока клиент его
/// прочитает (не дольше <see cref="AnswerTimeout"/>), и канал закрывается. Сбой одного подключения — в журнал, канал
/// принимает следующие. Ответы <c>internal</c> и <c>badRequest</c> — в журнал (остальные команды пишет
/// <see cref="SavesService"/>).
/// Первый экземпляр канала создаётся с <see cref="PipeOptions.FirstPipeInstance"/>: имя занял другой процесс (до старта
/// службы) — ошибка в журнал и повтор через <see cref="RetryDelay"/>, к чужому каналу с чужими правами помощник не
/// присоединяется. Сбои канала службу не останавливают: циклы библиотек идут как шли.
/// </summary>
public sealed class SavesPipeServer(SavesCommandHandler handler, ILogger<SavesPipeServer> logger) : BackgroundService
{
    public const string PipeName = "club-diskless-helper";

    /// <summary>Сколько клиентов канал обслуживает одновременно.</summary>
    public const int MaxInstances = 4;

    /// <summary>Наибольшая длина строки запроса в байтах UTF-8 (без <c>\n</c>), как <see cref="SavesCommandHandler.MaxRequestLength"/>.</summary>
    public const int MaxRequestBytes = SavesCommandHandler.MaxRequestLength;

    /// <summary>Сколько ждать строку запроса после подключения: молчащий клиент не держит экземпляр канала.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждать, пока клиент прочитает ответ.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Пауза перед новой попыткой создать канал после сбоя.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Ответ на строку длиннее <see cref="MaxRequestBytes"/> (до обработчика она не доходит).</summary>
    private static readonly string TooLongAnswer =
        $$"""{"ok":false,"error":"{{SavesErrors.BadRequest}}","message":"Request line is longer than {{MaxRequestBytes}} bytes"}""";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = Security();
        NamedPipeServerStream? first = null;
        while (first is null)
        {
            try
            {
                first = Create(security, PipeOptions.FirstPipeInstance);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Исключение из ExecuteAsync остановило бы всю службу (и тома библиотек) — только журнал и повтор.
                logger.LogError("Saves pipe {Pipe} not created (is the name taken by another process?): {Reason}; retrying in {Delay}",
                    PipeName, ex.Message, RetryDelay);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        logger.LogInformation("Saves pipe {Pipe} is listening (SYSTEM and Administrators only, {Instances} instances)", PipeName, MaxInstances);
        var listeners = new Task[MaxInstances];
        listeners[0] = ListenAsync(security, first, stoppingToken);
        for (var i = 1; i < MaxInstances; i++)
        {
            listeners[i] = ListenAsync(security, null, stoppingToken);
        }

        await Task.WhenAll(listeners);
    }

    /// <summary>Права канала: FullControl для SYSTEM и BUILTIN\Administrators, наследование отключено, больше никого.</summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static NamedPipeServerStream Create(PipeSecurity security, PipeOptions extra = PipeOptions.None) =>
        NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | extra, 0, 0,
            security);

    /// <summary>
    /// Один экземпляр канала: ждёт клиента, обслуживает, закрывается и создаётся снова — до остановки службы.
    /// <paramref name="first"/> — уже созданный первый экземпляр (или <c>null</c>).
    /// </summary>
    private async Task ListenAsync(PipeSecurity security, NamedPipeServerStream? first, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = first ?? Create(security);
                first = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Saves pipe instance not created: {Reason}; retrying in {Delay}", ex.Message, RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Клиент подключился и сразу ушёл и т. п.: экземпляр создаётся заново.
                    logger.LogDebug("Saves pipe connection failed: {Reason}", ex.Message);
                    continue;
                }

                try
                {
                    await ServeAsync(pipe, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Saves pipe client not served");
                }
            }
        }
    }

    /// <summary>Строка запроса → команда (с токеном службы) → ответ и <c>\n</c>.</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var line = await ReadLineAsync(pipe, stoppingToken);
        if (line is null)
        {
            return;
        }

        var answer = line.TooLong ? TooLongAnswer : await handler.HandleAsync(line.Text, stoppingToken);
        LogFailure(answer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(AnswerTimeout);
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            // Шелл ушёл, не дождавшись ответа: команда уже выполнена, итог остаётся только в журнале.
            logger.LogWarning("Saves pipe client gone before the answer ({Reason}): {Answer}", ex.Message, answer);
            return;
        }

        await WaitForReadAsync(pipe, answer, stoppingToken);
    }

    /// <summary>
    /// Ждёт, пока клиент прочитает ответ (<see cref="NamedPipeServerStream.WaitForPipeDrain"/>, не дольше
    /// <see cref="AnswerTimeout"/>). Ответ уже в канале: клиент, который прочитал его и сразу закрыл канал (обычный случай),
    /// обрывает ожидание ошибкой «канал закрыт» — это не сбой и не «клиент ушёл без ответа».
    /// </summary>
    private async Task WaitForReadAsync(NamedPipeServerStream pipe, string answer, CancellationToken stoppingToken)
    {
        var drained = Task.Run(pipe.WaitForPipeDrain, CancellationToken.None);
        if (await Task.WhenAny(drained, Task.Delay(AnswerTimeout, stoppingToken)) != drained)
        {
            // Ожидание отпустит само, когда клиент прочитает или уйдёт (до тех пор Windows держит этот экземпляр канала);
            // его ошибка не нужна.
            _ = drained.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Saves pipe client did not read the answer within {Timeout}: {Answer}", AnswerTimeout, answer);
            }

            return;
        }

        try
        {
            await drained;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogDebug("Saves pipe client closed the pipe right after the answer: {Reason}", ex.Message);
        }
    }

    /// <summary>Строка запроса (текст без <c>\n</c>) или признак «длиннее предела».</summary>
    private sealed record RequestLine(string Text, bool TooLong);

    /// <summary>
    /// Читает до первого <c>\n</c>, не больше <see cref="MaxRequestBytes"/> байт текста; <c>null</c> — клиент ушёл или
    /// молчал дольше <see cref="RequestTimeout"/>. Байты после <c>\n</c> не читаются.
    /// </summary>
    private async Task<RequestLine?> ReadLineAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var buffer = new byte[MaxRequestBytes + 1];
        var count = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            while (count < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(count, 1), timeout.Token);
                if (read == 0)
                {
                    logger.LogDebug("Saves pipe client left without a full request line ({Bytes} bytes)", count);
                    return null;
                }

                if (buffer[count] == (byte)'\n')
                {
                    return new RequestLine(Encoding.UTF8.GetString(buffer, 0, count), TooLong: false);
                }

                count++;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Saves pipe client sent no request line within {Timeout}", RequestTimeout);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogDebug("Saves pipe client left while sending the request: {Reason}", ex.Message);
            return null;
        }

        return new RequestLine("", TooLong: true);
    }

    /// <summary>Ответ <c>internal</c> — ошибка в журнал, <c>badRequest</c> — предупреждение (шелл шлёт не то).</summary>
    private void LogFailure(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False && root.TryGetProperty("error", out var error))
            {
                var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
                switch (error.GetString())
                {
                    case SavesErrors.Internal:
                        logger.LogError("Saves pipe command failed: {Message}", message);
                        break;
                    case SavesErrors.BadRequest:
                        logger.LogWarning("Saves pipe request rejected: {Message}", message);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogError("Saves pipe answer is not JSON ({Reason}): {Answer}", ex.Message, answer);
        }
    }
}
