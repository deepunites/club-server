using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Таргет версии должен быть виден ПК, а не только записан в конфиг TrueNAS. Поддельный TrueNAS воспроизводит стенд
/// 2026-10-02 (25.10.7): reload, который срывается на read-only устройстве в copy_manager_tgt, при успешном ответе API.
/// </summary>
public sealed partial class LibraryTests
{
    private LibraryOptions Options => _server.Services.GetRequiredService<LibraryOptions>();

    [Fact]
    public async Task Published_target_is_live_on_truenas_25_10()
    {
        foreach (var label in new[] { "v1", "v2", "v3" })
        {
            await PublishAsync(label);
            Assert.True(_nas.IsLive($"games-{label}"), label);
        }

        Assert.True(_nas.FailedReloads > 0); // ошибка copy_manager действительно срабатывала
        Assert.Contains($"{Basename}:games-v3", await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, IqnB, TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("iscsi.target.update", _nas.CallLog); // порядок «таргет → экстент → LUN» обходится без лишних reload
    }

    [Fact]
    public async Task Stale_copy_manager_devices_after_an_iscsi_restart_are_worked_off_before_promote()
    {
        await PublishAsync("v1");
        await PublishAsync("v2");
        _nas.RestartIscsiService(); // перезагрузка TrueNAS: оба read-only устройства снова в copy_manager_tgt
        Assert.Equal(2, _nas.CopyManagerDevices.Count);

        var failedBefore = _nas.FailedReloads;
        await PublishAsync("v3");

        Assert.True(_nas.IsLive("games-v3"));
        Assert.True(_nas.FailedReloads - failedBefore >= 2); // reload-ы от target.create и targetextent.create сорвались
        var log = _nas.CallLog.ToList();
        Assert.True(log.LastIndexOf("iscsi.target.update") > log.LastIndexOf("iscsi.targetextent.create"), string.Join(", ", log));
        Assert.True(_nas.IsLive("games-v2")); // откатная версия не пострадала
    }

    [Fact]
    public async Task Silently_lost_reload_is_reapplied_before_the_version_becomes_current()
    {
        _nas.LoseReloadOf("iscsi.targetextent.create");
        await PublishAsync("v1");
        Assert.True(_nas.IsLive("games-v1"));
        Assert.Contains("iscsi.target.update", _nas.CallLog);
    }

    [Fact]
    public async Task Target_that_stays_hidden_fails_the_publish_until_iscsi_is_restarted()
    {
        _nas.ReloadsFail = true; // scstadmin падает каждый раз — повторный reload не лечит
        using var accepted = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "v1" });
        var operationId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid();
        await PassAsync(reconcile: false);

        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Contains("not visible", version.LastError);
        Assert.Contains("restart the iSCSI service", version.LastError);
        Assert.Null((await _repository.PointersAsync()).Current); // ПК не отправлены на невидимый таргет
        Assert.Equal(3, _nas.CallLog.Count(c => c == "iscsi.target.update")); // один read-only экстент + 2

        // Администратор перезапустил службу iSCSI и повторил публикацию из панели.
        _nas.ReloadsFail = false;
        _nas.RestartIscsiService();
        using var retry = await Panel().PostAsync($"/panel/api/v1/library/operations/{operationId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await PassAsync();
        Assert.Equal("v1", (await _repository.PointersAsync()).Current!.Label);
        Assert.True(_nas.IsLive("games-v1"));
    }

    [Fact]
    public async Task Hidden_target_fails_on_the_first_attempt_without_repeating_the_reloads()
    {
        // Повтор через такт воркера прогнал бы ту же лестницу reload-ов ещё раз (до MaxAttempts × (RO + 2)) и упал бы так же.
        Options.MaxAttempts = 3;
        _nas.ReloadsFail = true;
        var operationId = await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var operation = (await _repository.FindOperationAsync(operationId))!;
        Assert.Equal(("failed", 1), (operation.Status, operation.Attempts));
        Assert.Contains("restart the iSCSI service", operation.LastError);
        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Equal(operation.LastError, version.LastError);
        Assert.Equal(3, _nas.CallLog.Count(c => c == "iscsi.target.update")); // одна лестница: один RO-экстент + 2

        await PassAsync(reconcile: false); // следующий такт воркера
        Assert.Equal(3, _nas.CallLog.Count(c => c == "iscsi.target.update"));
        Assert.Empty(await _repository.OpenOperationsAsync());
        Assert.Null((await _repository.PointersAsync()).Current);
    }

    [Fact]
    public async Task Probe_denied_fails_on_the_first_attempt_without_reloads()
    {
        Options.MaxAttempts = 3;
        var group = await _storage.EnsureInitiatorGroupAsync("club pcs", IqnA);
        Options.InitiatorGroupId = group.Id;
        var operationId = await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var operation = (await _repository.FindOperationAsync(operationId))!;
        Assert.Equal(("failed", 1), (operation.Status, operation.Attempts));
        Assert.Contains("does not include the server's probe name", operation.LastError);
        Assert.Equal("failed", (await _repository.FindVersionAsync("v1"))!.State);

        await PassAsync(reconcile: false);
        Assert.DoesNotContain("iscsi.target.update", _nas.CallLog);
        Assert.Empty(await _repository.OpenOperationsAsync());
    }

    [Theory]
    [InlineData("iqn.1991-05.com.microsoft:*", false)] // группа «все Windows-ПК» имя проверки не пускает
    [InlineData("*", true)]
    [InlineData("iqn.2026-10.local.clubsrv:*", true)]
    [InlineData("IQN.2026-10.LOCAL.CLUBSRV:PROBE", true)] // регистр SCST не различает
    [InlineData("!iqn.1991-05.com.microsoft:pc-07", true)] // «все, кроме pc-07»
    public async Task Initiator_group_patterns_are_evaluated_like_scst(string pattern, bool admitsProbe)
    {
        var client = _server.Services.GetRequiredService<TrueNasClient>();
        var group = await _storage.EnsureInitiatorGroupAsync("club pcs", IqnA);
        await client.CallAsync("iscsi.initiator.update", [group.Id, new { initiators = new[] { pattern } }]);
        Options.InitiatorGroupId = group.Id;
        await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.True(_nas.IsLive("games-v1")); // таргет отдан в любом случае — вопрос только, видно ли его проверке
        if (admitsProbe)
        {
            Assert.Equal("published", version.State);
        }
        else
        {
            Assert.Equal("failed", version.State);
            Assert.Contains($"does not include the server's probe name {Options.ProbeInitiatorIqn}", version.LastError);
            Assert.DoesNotContain("iscsi.target.update", _nas.CallLog); // ошибка настройки: reload-ов не было
        }
    }

    [Fact]
    public async Task Hidden_target_behind_a_listed_group_hints_at_the_probe_name_first()
    {
        // Таргет открыт только по списку, имя проверки в нём есть, но таргет так и не виден. Перезапуск службы рвёт
        // сессии всех ПК, поэтому первая подсказка — проверить список.
        var client = _server.Services.GetRequiredService<TrueNasClient>();
        var group = await _storage.EnsureInitiatorGroupAsync("club pcs", IqnA);
        await client.CallAsync("iscsi.initiator.update", [group.Id, new { initiators = new[] { IqnA, Options.ProbeInitiatorIqn } }]);
        Options.InitiatorGroupId = group.Id;
        _nas.ReloadsFail = true;
        await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Contains($"initiator group {group.Id} admits only listed initiators", version.LastError);
        Assert.Contains($"includes the server's probe name {Options.ProbeInitiatorIqn}", version.LastError);
        Assert.Contains("restart the iSCSI service", version.LastError);

        // Та же подсказка у сверки, если таргет текущей версии потом пропал из discovery.
        _nas.ReloadsFail = false;
        _nas.RestartIscsiService();
        await PublishAsync("v2");
        _nas.DisableInScst("games-v2");
        await PassAsync();
        var warning = Assert.Single(await _repository.ActiveWarningsAsync());
        Assert.Equal(("targetNotDiscoveredListed", "games-v2"), (warning.Kind, warning.Subject)); // в панели — не «перезапустите службу»
        Assert.Contains($"имени проверки сервера {Options.ProbeInitiatorIqn}", warning.Message);
        Assert.Contains($"группа {group.Id}", warning.Message);
        Assert.Contains("Library:ProbeInitiatorIqn", warning.Message);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("!iqn.1991-05.com.microsoft:pc-07")] // «все, кроме pc-07» — тоже не список
    public async Task Hidden_target_behind_a_group_open_by_star_or_negation_gets_the_plain_restart_hint(string pattern)
    {
        // Имя проверки пускает не конкретная строка списка, а «*» или «!…»: проверять список незачем, подсказка — перезапуск.
        var client = _server.Services.GetRequiredService<TrueNasClient>();
        var group = await _storage.EnsureInitiatorGroupAsync("club pcs", IqnA);
        await client.CallAsync("iscsi.initiator.update", [group.Id, new { initiators = new[] { IqnA, pattern } }]);
        Options.InitiatorGroupId = group.Id;
        _nas.ReloadsFail = true;
        await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Contains("restart the iSCSI service", version.LastError);
        Assert.DoesNotContain("admits only listed initiators", version.LastError);
        Assert.DoesNotContain("Library:ProbeInitiatorIqn", version.LastError);

        _nas.ReloadsFail = false;
        _nas.RestartIscsiService();
        await PublishAsync("v2");
        _nas.DisableInScst("games-v2");
        await PassAsync();
        var warning = Assert.Single(await _repository.ActiveWarningsAsync());
        Assert.Equal(("targetNotDiscovered", "games-v2"), (warning.Kind, warning.Subject));
        Assert.DoesNotContain("по списку", warning.Message);
    }

    [Fact]
    public async Task Portal_that_answered_once_and_then_went_silent_fails_the_publish_as_hidden()
    {
        // Первый SendTargets ответил без таргета, потом портал замолчал. Таргет уже показан скрытым — это не
        // «проверить нельзя», и версия не должна стать текущей без проверки.
        _nas.ReloadsFail = true;
        _nas.DiscoveryAnswersLeft = 1;
        var operationId = await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);

        var operation = (await _repository.FindOperationAsync(operationId))!;
        Assert.Equal("failed", operation.Status);
        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Contains("not visible", version.LastError);
        Assert.Null((await _repository.PointersAsync()).Current);
        Assert.Equal(3, _nas.CallLog.Count(c => c == "iscsi.target.update")); // вся лестница: один RO-экстент + 2
        Assert.Equal(0, _nas.DiscoveryAnswersLeft);
    }

    [Fact]
    public async Task Restart_after_promote_completes_the_operation_without_rechecking_the_live_version()
    {
        // Сервер остановился между PromoteAsync и отметкой «done»: операция осталась running/promote, версия уже текущая.
        var operationId = await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);
        await _repository.MarkOperationAsync(operationId, "running", "promote", null, countAttempt: false, DateTimeOffset.UtcNow);

        // Проверка сейчас провалилась бы — и пометила бы failed версию, на которой уже работают ПК.
        _nas.DisableInScst("games-v1");
        _nas.ReloadsFail = true;
        var before = _nas.CallLog.Count;
        var discoveries = _nas.DiscoveryInitiators.Count;
        await PassAsync(reconcile: false);

        var operation = (await _repository.FindOperationAsync(operationId))!;
        Assert.Equal(("done", 0), (operation.Status, operation.Attempts));
        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal(("published", (string?)null), (version.State, version.LastError));
        Assert.Equal("v1", (await _repository.PointersAsync()).Current!.Label);
        Assert.DoesNotContain("iscsi.target.update", _nas.CallLog.Skip(before)); // без проверки и reload-ов
        Assert.Equal(discoveries, _nas.DiscoveryInitiators.Count);
    }

    [Fact]
    public async Task Unverifiable_portal_falls_back_to_reloads_and_is_reported()
    {
        Options.DiscoveryAddress = $"127.0.0.1:{ClosedPort()}";
        await PublishAsync("v1");
        await PublishAsync("v2");
        _nas.RestartIscsiService();
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-07", "games-v1"); // v1 не разбирается — её reload-ы не в счёт

        var before = _nas.CallLog.Count;
        await PublishAsync("v3");
        Assert.True(_nas.IsLive("games-v3"));
        // Reload-ы вслепую: по одному на read-only экстент (v1, v2, v3) и ещё один — не больше и не меньше.
        Assert.Equal(4, _nas.CallLog.Skip(before).Count(c => c == "iscsi.target.update"));
        Assert.Contains(await _repository.ActiveWarningsAsync(), w => w.Kind == "discoveryUnavailable");
    }

    [Fact]
    public async Task Bad_discovery_port_is_reported_instead_of_stalling_the_queue()
    {
        // Порт вне диапазона (настройку поменяли мимо проверки при старте): раньше TcpClient бросал
        // ArgumentOutOfRangeException, операция навсегда оставалась в running/verify без попыток и lastError.
        Options.DiscoveryAddress = "127.0.0.1:70000";
        await PublishAsync("v1");
        Assert.True(_nas.IsLive("games-v1"));
        Assert.Empty(await _repository.OpenOperationsAsync());
        var warning = Assert.Single(await _repository.ActiveWarningsAsync());
        Assert.Equal(("discoveryUnavailable", "127.0.0.1:70000"), (warning.Kind, warning.Subject));
    }

    [Fact]
    public async Task Narrowed_initiator_group_must_include_the_probe_name()
    {
        // Группу версий сузили списком ПК (старая инструкция: «сузить доступ можно позже»). iscsi-scstd применяет
        // список и к SendTargets — имени проверки сервер таргета не увидит, сколько ни перезагружай конфигурацию.
        var client = _server.Services.GetRequiredService<TrueNasClient>();
        var group = await _storage.EnsureInitiatorGroupAsync("club pcs", IqnA);
        Options.InitiatorGroupId = group.Id;
        using var accepted = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "v1" });
        var operationId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid();
        await PassAsync(reconcile: false);

        var version = (await _repository.FindVersionAsync("v1"))!;
        Assert.Equal("failed", version.State);
        Assert.Contains($"does not include the server's probe name {Options.ProbeInitiatorIqn}", version.LastError);
        Assert.DoesNotContain("restart the iSCSI service", version.LastError); // перезапуск порвал бы сессии всех ПК и не помог бы
        Assert.DoesNotContain("iscsi.target.update", _nas.CallLog);
        Assert.Contains($"{Basename}:games-v1", await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, IqnA, TimeSpan.FromSeconds(5)));

        // Администратор добавил в группу имя проверки (своё, из Library:ProbeInitiatorIqn) и повторил публикацию.
        Options.ProbeInitiatorIqn = "iqn.2026-10.local.club:verify";
        await client.CallAsync("iscsi.initiator.update", [group.Id, new { initiators = new[] { IqnA, "iqn.2026-10.local.club:verify" } }]);
        using var retry = await Panel().PostAsync($"/panel/api/v1/library/operations/{operationId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await PassAsync();
        Assert.Equal("v1", (await _repository.PointersAsync()).Current!.Label);
        Assert.Contains("iqn.2026-10.local.clubsrv:probe", _nas.DiscoveryInitiators);
        Assert.Contains("iqn.2026-10.local.club:verify", _nas.DiscoveryInitiators);
        Assert.Empty(await _repository.ActiveWarningsAsync());

        // Имя проверки из группы убрали позже — сверка говорит о группе, а не советует перезапускать службу iSCSI.
        await client.CallAsync("iscsi.initiator.update", [group.Id, new { initiators = new[] { IqnA } }]);
        await PassAsync();
        var warning = Assert.Single(await _repository.ActiveWarningsAsync());
        Assert.Equal(("probeDenied", "games-v1"), (warning.Kind, warning.Subject));
        Assert.True(_nas.IsLive("games-v1"));
    }

    [Fact]
    public async Task Restart_during_verify_resumes_and_still_reapplies_without_duplicates()
    {
        _nas.LoseReloadOf("iscsi.targetextent.create");
        Options.VerifyDelayMs = 60_000;
        await _publisher.RequestPublishAsync("v1", "test");
        using (var restart = new CancellationTokenSource())
        {
            var pass = StorageWorker.RunOnceAsync(_publisher, _repository, false, TimeProvider.System, restart.Token);
            await WaitForStepAsync("verify");
            restart.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        }

        // Сервер остановлен на проверке: всё создано, таргет ещё не виден, версия не текущая.
        var operation = Assert.Single(await _repository.OpenOperationsAsync());
        Assert.Equal(("running", "verify"), (operation.Status, operation.Step));
        Assert.False(_nas.IsLive("games-v1"));
        Assert.Null((await _repository.PointersAsync()).Current);

        // После рестарта ensure-шаги ничего не создают и reload не вызывают — конфигурацию применяет только проверка.
        Options.VerifyDelayMs = 0;
        var resumed = _nas.CallLog.Count;
        await PassAsync();
        Assert.Equal("published", (await _repository.FindVersionAsync("v1"))!.State);
        Assert.True(_nas.IsLive("games-v1"));
        Assert.Contains("iscsi.target.update", _nas.CallLog.Skip(resumed));
        foreach (var create in new[] { "pool.snapshot.create", "pool.snapshot.clone", "iscsi.target.create", "iscsi.extent.create", "iscsi.targetextent.create" })
        {
            Assert.Equal((create, 1), (create, _nas.CallLog.Count(c => c == create)));
        }
    }

    [Fact]
    public async Task Reconcile_reports_a_current_target_that_pcs_cannot_see()
    {
        await PublishAsync("v1");
        Assert.Empty(await _repository.ActiveWarningsAsync());

        _nas.DisableInScst("games-v1"); // рантайм разошёлся с конфигом, как после сорвавшегося reload
        var before = _nas.CallLog.Count;
        await PassAsync();
        var warning = Assert.Single(await _repository.ActiveWarningsAsync());
        Assert.Equal(("targetNotDiscovered", "games-v1"), (warning.Kind, warning.Subject));
        Assert.False(_nas.IsLive("games-v1")); // сверка только сообщает
        Assert.DoesNotContain("iscsi.target.update", _nas.CallLog.Skip(before));

        _nas.RestartIscsiService();
        await PassAsync();
        Assert.Empty(await _repository.ActiveWarningsAsync());
    }

    [Fact]
    public async Task Clone_device_that_appears_late_is_waited_for_within_the_pass()
    {
        _nas.CloneDeviceDelay = 1;
        await PublishAsync("v1");
        Assert.Equal(2, _nas.CallLog.Count(c => c == "iscsi.extent.create"));
        Assert.Equal(1, _nas.Count("extent"));
        Assert.True(_nas.IsLive("games-v1"));
    }

    [Fact]
    public async Task Master_target_is_checked_with_the_superclient_initiator_name()
    {
        await PublishAsync("v1");
        _nas.LoseReloadOf("iscsi.targetextent.create");
        await OpenMasterAsync();

        Assert.True(_nas.IsLive("club-master"));
        Assert.Contains(IqnA, _nas.DiscoveryInitiators);
        Assert.Contains("iscsi.target.update", _nas.CallLog);
        Assert.DoesNotContain($"{Basename}:club-master", await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, IqnB, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Master_opens_with_a_warning_when_its_target_stays_hidden()
    {
        _nas.ReloadsFail = true; // scstadmin падает каждый раз: таргет мастер-тома в SCST так и не включается
        await OpenMasterAsync();

        var master = await MasterAsync();
        Assert.Equal("open", master.GetProperty("state").GetString());
        Assert.Contains("not visible to the superclient PC", master.GetProperty("lastError").GetString());
        Assert.False(_nas.IsLive("club-master"));
        Assert.Equal(2, _nas.CallLog.Count(c => c == "iscsi.target.update")); // read-only экстентов нет: 0 + 2

        // Чистое закрытие снимает оговорку.
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/close", new { }));
        await PassAsync();
        Assert.False((await MasterAsync()).TryGetProperty("lastError", out _));
    }

    [Theory]
    [InlineData("Library:DiscoveryAddress", "192.168.77.3:70000")]
    [InlineData("Library:PortalAddress", "")]
    [InlineData("Library:PortalAddress", "http://192.168.77.3:3260")] // раньше сходило за «IPv6 без скобок»
    [InlineData("Library:DiscoveryAddress", "192.168.77.3:3260:3260")]
    [InlineData("Library:ProbeInitiatorIqn", "club probe")]
    public async Task Bad_library_settings_stop_the_server_at_start(string key, string value)
    {
        var server = new ServerFixture();
        foreach (var (k, v) in _server.Settings)
        {
            server.Settings[k] = v;
        }

        server.Settings[key] = value;
        await server.InitializeAsync();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => server.CreateClient());
            Assert.Contains(key, error.Message);
        }
        finally
        {
            await ((IAsyncLifetime)server).DisposeAsync();
        }
    }

    private async Task WaitForStepAsync(string step)
    {
        for (var i = 0; i < 500; i++)
        {
            if ((await _repository.OpenOperationsAsync()).Any(o => o.Step == step))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"operation did not reach step {step}");
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
