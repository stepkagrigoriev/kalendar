using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kalendar.WebApi.Tests;

public class MongoConfigurationTests
{
    [Theory]
    [InlineData(null, "kalendar", "MongoDB connection string is required.")]
    [InlineData("not a connection string", "kalendar", "MongoDB connection string is invalid.")]
    [InlineData("mongodb://localhost", null, "MongoDB database name is required.")]
    public void InvalidConfigurationFailsWithoutDisclosingValues(string? connectionString, string? name, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = connectionString,
            ["Mongo:Database"] = name
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoInfrastructure.AddMongoDatabase(new ServiceCollection(), configuration));

        Assert.Equal(expected, exception.Message);
        Assert.DoesNotContain("not a connection string", exception.Message);
    }
}
