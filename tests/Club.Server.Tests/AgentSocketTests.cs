using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Club.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

public sealed class AgentSocketTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Pending_command_is_delivered_acked_and_keepalive_works()
    {
        var http = server.CreateClient();
        var (agent, _) = await TestAgent.RegisterAsync(http);
        var commands = server.Services.GetRequiredService<CommandRepository>();
        var queued = await commands.EnqueueAsync(agent.PcId, "unlock", null, "test", TimeSpan.FromMinutes(5), null, DateTimeOffset.UtcNow);

        var client = server.Server.CreateWebSocketClient();
        client.SubProtocols.Add("clubshell.v1");
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {agent.AccessToken}";
        using var socket = await client.ConnectAsync(new Uri(server.Server.BaseAddress, "/ws/agent"), CancellationToken.None);
        Assert.Equal("clubshell.v1", socket.SubProtocol);

        var command = await ReceiveAsync(socket);
        Contract.AssertMatches("WsFrame", command);
        Assert.Equal("command", command.GetProperty("type").GetString());
        Assert.Equal(queued.Id, command.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, command.GetProperty("payload").ValueKind);

        await SendAsync(socket, new { type = "ack", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null, ack = new { id = queued.Id, ok = true } });

        // Пинг сервера (в тестах каждые 2 с) — отвечаем pong, соединение живо.
        var ping = await ReceiveAsync(socket);
        Assert.Equal("ping", ping.GetProperty("type").GetString());
        await SendAsync(socket, new { type = "pong", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null });

        // Ack обрабатывается асинхронно в цикле приёма: ждём, пока команда уйдёт из очереди.
        var remaining = -1;
        for (var attempt = 0; attempt < 30 && remaining != 0; attempt++)
        {
            using var pending = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/commands");
            remaining = (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength();
            if (remaining != 0)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal(0, remaining);

        var next = await ReceiveAsync(socket);
        Assert.Equal("ping", next.GetProperty("type").GetString());
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task Missing_pong_closes_the_socket()
    {
        var http = server.CreateClient();
        var (agent, _) = await TestAgent.RegisterAsync(http);
        var client = server.Server.CreateWebSocketClient();
        client.SubProtocols.Add("clubshell.v1");
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {agent.AccessToken}";
        using var socket = await client.ConnectAsync(new Uri(server.Server.BaseAddress, "/ws/agent"), CancellationToken.None);

        Assert.Equal("ping", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
    }

    [Fact]
    public async Task Invalid_token_is_rejected_before_upgrade()
    {
        var client = server.Server.CreateWebSocketClient();
        client.SubProtocols.Add("clubshell.v1");
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer not-a-token";
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri(server.Server.BaseAddress, "/ws/agent"), CancellationToken.None));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static Task SendAsync(WebSocket socket, object frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, Api.ApiJson.Options)), WebSocketMessageType.Text, true, CancellationToken.None);
}
