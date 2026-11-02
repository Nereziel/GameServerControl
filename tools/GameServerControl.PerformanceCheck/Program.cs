using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static InstallProgress Job(string id, DateTimeOffset updatedAt, bool finished = true) =>
    new(id, finished ? "done" : "steamcmd", "test", finished ? 100 : 50, finished, finished,
        Array.Empty<string>(), updatedAt.AddMinutes(-1), updatedAt, id, "test", "/tmp/test");

var now = new DateTimeOffset(2026, 11, 2, 22, 30, 0, TimeSpan.Zero);

var ttlInput = new[]
{
    Job("expired", now.AddHours(-24).AddSeconds(-1)),
    Job("boundary", now.AddHours(-24)),
    Job("active-old", now.AddDays(-7), finished: false),
    Job("recent", now.AddHours(-1))
};
var ttlEvictions = InstallJobRetention.SelectEvictions(ttlInput, now).ToHashSet(StringComparer.Ordinal);
Require(ttlEvictions.Contains("expired"), "Completed job older than 24 hours was retained.");
Require(ttlEvictions.Contains("boundary"), "Completed job at the 24-hour retention boundary was retained.");
Require(!ttlEvictions.Contains("active-old"), "Active job was selected for eviction.");
Require(!ttlEvictions.Contains("recent"), "Recent completed job was selected for eviction.");

var capped = Enumerable.Range(0, 105)
    .Select(i => Job($"cap-{i:000}", now.AddMinutes(-i)))
    .ToArray();
var capEvictions = InstallJobRetention.SelectEvictions(capped, now).ToHashSet(StringComparer.Ordinal);
Require(capEvictions.Count == 5, $"Expected 5 cap evictions, got {capEvictions.Count}.");
for (var i = 100; i < 105; i++)
    Require(capEvictions.Contains($"cap-{i:000}"), $"Old completed job cap-{i:000} survived the 100-job cap.");
for (var i = 0; i < 100; i++)
    Require(!capEvictions.Contains($"cap-{i:000}"), $"Newer completed job cap-{i:000} was evicted by the cap.");

var equalTimestamp = Enumerable.Range(0, 102)
    .Select(i => Job($"eq-{i:000}", now.AddMinutes(-1)))
    .ToArray();
var equalFirst = InstallJobRetention.SelectEvictions(equalTimestamp, now).OrderBy(x => x, StringComparer.Ordinal).ToArray();
var equalSecond = InstallJobRetention.SelectEvictions(equalTimestamp.Reverse(), now).OrderBy(x => x, StringComparer.Ordinal).ToArray();
Require(equalFirst.SequenceEqual(equalSecond), "Equal-timestamp retention decisions depend on input enumeration order.");
Require(equalFirst.SequenceEqual(new[] { "eq-100", "eq-101" }),
    "Equal-timestamp retention tie-breaker is not deterministic by job ID.");

Console.WriteLine("PERF-02 completed-install-job retention behavior OK.");
