using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServerControl.Agent.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class TokenRegistryPerformanceTests
{
    [Fact]
    public void Managed_token_miss_allocations_do_not_scale_with_registry_size()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-token-registry-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var oneToken = CreateRegistry(Path.Combine(root, "one"), 1, loggerFactory);
            var manyTokens = CreateRegistry(Path.Combine(root, "many"), 128, loggerFactory);
            const string missingToken = "perf-n02-missing-managed-token";

            WarmUp(oneToken, missingToken);
            WarmUp(manyTokens, missingToken);

            var oneTokenBytes = MeasureLookupAllocations(oneToken, missingToken, 200);
            var manyTokenBytes = MeasureLookupAllocations(manyTokens, missingToken, 200);

            Assert.True(
                manyTokenBytes <= oneTokenBytes + 32 * 1024,
                $"Managed-token miss allocations scaled with token count: one={oneTokenBytes:N0} bytes, many={manyTokenBytes:N0} bytes.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TokenRegistry CreateRegistry(string dataDir, int tokenCount, ILoggerFactory loggerFactory)
    {
        Directory.CreateDirectory(dataDir);
        var records = Enumerable.Range(0, tokenCount)
            .Select(i => new
            {
                Id = "token-" + i,
                Name = "Token " + i,
                TokenHash = HashToken("stored-token-" + i),
                TokenPrefix = "stored-t...",
                Role = "ReadOnly",
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i)
            })
            .ToArray();

        File.WriteAllText(
            Path.Combine(dataDir, "tokens.json"),
            JsonSerializer.Serialize(records));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:DataDir"] = dataDir
            })
            .Build();

        return new TokenRegistry(config, loggerFactory.CreateLogger<TokenRegistry>());
    }

    private static void WarmUp(TokenRegistry registry, string missingToken)
    {
        for (var i = 0; i < 20; i++)
            Assert.Null(registry.Lookup(missingToken));
    }

    private static long MeasureLookupAllocations(TokenRegistry registry, string missingToken, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            Assert.Null(registry.Lookup(missingToken));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
