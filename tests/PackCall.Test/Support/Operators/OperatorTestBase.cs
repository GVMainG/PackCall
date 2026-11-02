using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Sending;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Journaling;
using PackCall.Core.Services;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using PackCall.Infrastructure.Sending;

namespace PackCall.Core.Test;

public abstract class OperatorTestBase
{
    private protected static IConfiguration Config(string? environment = "test", string? mode = "inprocess", string? simulate = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["PACKCALL_ENVIRONMENT"] = environment, ["TEST_SENDER_MODE"] = mode,
            ["TEST_SIMULATE_ERRORS"] = simulate, ["TEST_OPERATOR_URL"] = "http://localhost:8000" }).Build();

    private protected sealed class SequenceRandom(params double[] values) : IOperatorRandom
    {
        public int Calls;
        public double NextDouble() => values[Calls++];
    }
}
