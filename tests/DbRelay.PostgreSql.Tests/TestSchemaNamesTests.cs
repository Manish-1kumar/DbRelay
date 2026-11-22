using System.Globalization;
using DbRelay.Testing;

namespace DbRelay.PostgreSql.Tests;

/// <summary>K13: the stale-schema sweep must only ever select abandoned test schemas.</summary>
public sealed class TestSchemaNamesTests
{
    [Fact]
    public void New_names_match_the_documented_pattern_and_are_not_stale()
    {
        var name = TestSchemaNames.New("t");
        Assert.Matches("^t_[0-9a-f]{16}$", name);
        Assert.False(TestSchemaNames.IsStale(name, DateTimeOffset.UtcNow));
        Assert.False(TestSchemaNames.IsStale(name, DateTimeOffset.UtcNow + TestSchemaNames.StaleAfter - TimeSpan.FromMinutes(1)));
        Assert.True(TestSchemaNames.IsStale(name, DateTimeOffset.UtcNow + TestSchemaNames.StaleAfter + TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("t")]
    [InlineData("it")]
    [InlineData("p")]
    public void Names_older_than_the_threshold_are_stale(string prefix)
    {
        var created = DateTimeOffset.UtcNow - TestSchemaNames.StaleAfter - TimeSpan.FromMinutes(5);
        var name = $"{prefix}_{((uint)created.ToUnixTimeSeconds()).ToString("x8", CultureInfo.InvariantCulture)}0123abcd";
        Assert.True(TestSchemaNames.IsStale(name, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("dbrelay")]
    [InlineData("public")]
    [InlineData("soak_0123456789ab")]
    [InlineData("t_0000000000000000x")]
    [InlineData("t_000000000000000")]
    [InlineData("x_0000000000000000")]
    [InlineData("T_0000000000000000")]
    [InlineData("t_000000000000000G")]
    public void Other_schemas_are_never_stale(string name) => Assert.False(TestSchemaNames.IsStale(name, DateTimeOffset.UtcNow));
}
