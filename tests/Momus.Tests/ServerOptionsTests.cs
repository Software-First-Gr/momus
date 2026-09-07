using Momus.Server;

namespace Momus.Tests;

/// <summary>
/// Configuration is the first thing a new user touches, so its edge cases are worth pinning down:
/// an interval typed as "90s", a target given on the command line, a localhost connection string
/// inside a container.
/// </summary>
public class ServerOptionsTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("45", 45)]
    [InlineData("1.5m", 90)]
    public void Intervals_are_read_the_way_people_write_them(string text, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ServerOptions.ParseInterval(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("-5m")]
    [InlineData("0s")]
    public void An_interval_that_makes_no_sense_is_rejected_rather_than_guessed(string text)
    {
        Assert.Null(ServerOptions.ParseInterval(text));
    }

    [Fact]
    public void A_target_flag_carries_provider_and_connection_string()
    {
        var target = ServerOptions.ParseTargetFlag("postgres:Host=localhost;Database=shop", 0);

        Assert.NotNull(target);
        Assert.Equal("postgres", target.Provider);
        Assert.Equal("Host=localhost;Database=shop", target.ConnectionString);
        Assert.Equal("target-1", target.Name);
        Assert.Equal("cli", target.Source);
    }

    [Fact]
    public void A_target_flag_can_be_named()
    {
        var target = ServerOptions.ParseTargetFlag("shop=postgres:Host=localhost;Database=shop", 0);

        Assert.NotNull(target);
        Assert.Equal("shop", target.Name);
        Assert.Equal("postgres", target.Provider);
        Assert.Equal("Host=localhost;Database=shop", target.ConnectionString);
    }

    [Fact]
    public void A_connection_string_with_no_provider_is_rejected()
    {
        // Guessing the provider from the connection string would be clever and wrong.
        Assert.Null(ServerOptions.ParseTargetFlag("Host=localhost;Database=shop", 0));
        Assert.Null(ServerOptions.ParseTargetFlag("postgres:", 0));
    }

    [Theory]
    [InlineData("shop", "shop")]
    [InlineData("Shop API", "shop-api")]
    [InlineData("  staging / eu  ", "staging-eu")]
    [InlineData("!!!", "target")]
    public void Names_become_stable_ids_so_a_restart_does_not_duplicate_targets(string name, string id)
    {
        Assert.Equal(id, TargetSpec.Slug(name));
    }

    [Theory]
    [InlineData("Host=localhost;Database=shop", "Host=host.docker.internal;Database=shop")]
    [InlineData("Host=127.0.0.1;Port=5432", "Host=host.docker.internal;Port=5432")]
    [InlineData("Server=localhost,1433;Database=shop", "Server=host.docker.internal,1433;Database=shop")]
    [InlineData("Data Source=localhost;Database=shop", "Data Source=host.docker.internal;Database=shop")]
    public void Localhost_is_rewritten_to_the_docker_host(string connectionString, string expected)
    {
        Assert.Equal(expected, DockerHostFallback.Alternative(connectionString));
    }

    [Theory]
    [InlineData("Host=db;Database=shop")]
    [InlineData("Host=10.0.0.4;Database=shop")]
    [InlineData("Server=prod.example.com;Database=shop")]
    public void A_connection_string_that_already_names_a_host_is_left_alone(string connectionString)
    {
        // Returning null is how the scheduler knows the retry would be pointless.
        Assert.Null(DockerHostFallback.Alternative(connectionString));
    }
}
