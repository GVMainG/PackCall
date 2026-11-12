namespace PackCall.Worker.Background;

public sealed record WorkerIdentity(string Id)
{
    internal static string CreateId()
    {
        // GUID различает процессы на одном хосте, а усечение имени укладывает ID в ограничение БД.
        var host = Environment.MachineName;
        return $"{host[..Math.Min(host.Length, 200)]}-{Guid.NewGuid():N}";
    }
}
