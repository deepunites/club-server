using System.Text.RegularExpressions;

namespace Club.Helper.Tests;

/// <summary>
/// Запросы помощника, как они ушли на сервер: метод, путь с запросом, код ответа и тело запроса. Изменчивое замаскировано:
/// id машины, время загрузки, HWID. Один журнал на несколько клиентов (помощник после перезагрузки — новый клиент):
/// у каждого свой <see cref="Handler"/>.
/// </summary>
public sealed partial class WireRecorder
{
    private readonly List<(string Path, string Line)> _requests = [];

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex MachineIdPattern();

    [GeneratedRegex("\"(bootTime|hwid)\":\"[^\"]*\"")]
    private static partial Regex VolatilePattern();

    /// <summary>Строки запросов, путь которых подходит под <paramref name="path"/>: <c>METHOD путь → код</c>, затем тело с отступом.</summary>
    public IReadOnlyList<string> Lines(Func<string, bool> path)
    {
        lock (_requests)
        {
            return _requests.Where(r => path(r.Path)).SelectMany(r => r.Line.Split('\n')).ToList();
        }
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_requests)
            {
                return _requests.Select(r => r.Path).ToList();
            }
        }
    }

    public DelegatingHandler Handler() => new Recording(this);

    private async Task<HttpResponseMessage> RecordAsync(HttpRequestMessage request, Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await send();
        var path = MachineIdPattern().Replace(request.RequestUri!.PathAndQuery, "{id}");
        var line = $"{request.Method} {path} -> {(int)response.StatusCode}";
        if (body is not null)
        {
            line += "\n  " + VolatilePattern().Replace(MachineIdPattern().Replace(body, "{id}"), m => $"\"{m.Groups[1].Value}\":\"*\"");
        }

        lock (_requests)
        {
            _requests.Add((path, line));
        }

        return response;
    }

    private sealed class Recording(WireRecorder recorder) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            recorder.RecordAsync(request, () => base.SendAsync(request, cancellationToken), cancellationToken);
    }
}
