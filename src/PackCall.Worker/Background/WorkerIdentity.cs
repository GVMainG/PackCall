namespace PackCall.Worker.Background;

public sealed record WorkerIdentity(string Id)
{
    internal static string CreateId()
    {
        var host = Environment.MachineName;
        return $"{host[..Math.Min(host.Length, 200)]}-{Guid.NewGuid():N}";
    }
}
