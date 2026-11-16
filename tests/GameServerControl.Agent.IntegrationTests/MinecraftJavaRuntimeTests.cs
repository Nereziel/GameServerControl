using GameServerControl.Agent.Servers;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class MinecraftJavaRuntimeTests
{
    [Theory]
    [InlineData("openjdk version \"21.0.5\" 2025-01-21", 21)]
    [InlineData("java version \"1.8.0_421\"", 8)]
    [InlineData("openjdk version \"17.0.12\" 2024-07-16", 17)]
    [InlineData("not a JVM", null)]
    public void Parses_java_version_output(string output, int? expected)
        => Assert.Equal(expected, MinecraftJavaRuntime.ParseMajorVersion(output));

    [Theory]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.11", 21)]
    [InlineData("1.16.5", null)]
    public void Requires_appropriate_runtime_for_known_minecraft_releases(string release, int? expected)
        => Assert.Equal(expected, MinecraftJavaRuntime.MinimumJavaMajor(release));
}
