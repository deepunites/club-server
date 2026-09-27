using YamlDotNet.Serialization;

namespace Club.Server.Api;

public sealed record ContractOperation(string Method, string Path, string OperationId, string ServerStatus);

/// <summary>
/// Операции из собранного контракта (<c>club-contracts/openapi/openapi.yaml</c>). Всё, что сервер не реализует явно,
/// отвечает 501 — никогда 404: так агент видит «операция есть, но не реализована».
/// </summary>
public static class ContractStatus
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];

    public static IReadOnlyList<ContractOperation> Load(string openApiPath)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(openApiPath));
        var paths = (Dictionary<object, object>)root["paths"];
        var operations = new List<ContractOperation>();
        foreach (var (pathKey, item) in paths)
        {
            if (item is not Dictionary<object, object> pathItem)
            {
                continue;
            }

            foreach (var method in Methods)
            {
                if (pathItem.TryGetValue(method, out var op) && op is Dictionary<object, object> operation)
                {
                    operations.Add(new ContractOperation(
                        method.ToUpperInvariant(),
                        (string)pathKey,
                        operation.TryGetValue("operationId", out var id) ? (string)id : "",
                        operation.TryGetValue("x-server-status", out var status) ? (string)status : "notImplemented"));
                }
            }
        }

        return operations;
    }

    /// <summary>Регистрирует 501 для всех операций контракта, кроме реализованных.</summary>
    public static IReadOnlyList<ContractOperation> MapNotImplemented(this IEndpointRouteBuilder app, IReadOnlyList<ContractOperation> operations, IEnumerable<string> implemented)
    {
        var done = implemented.ToHashSet(StringComparer.Ordinal);
        var pending = operations.Where(o => !done.Contains($"{o.Method} {o.Path}")).ToList();
        foreach (var group in pending.GroupBy(o => o.Path))
        {
            app.MapMethods("/api/v1" + group.Key, group.Select(o => o.Method).ToArray(), (HttpContext context) =>
                ApiErrorWriter.WriteAsync(
                    context,
                    StatusCodes.Status501NotImplemented,
                    ErrorCodes.ServerUnavailable,
                    "Not implemented in server v1",
                    new { reason = "notImplemented" }));
        }

        return pending;
    }
}
