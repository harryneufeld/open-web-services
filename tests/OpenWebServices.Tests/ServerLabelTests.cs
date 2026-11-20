using OpenWebServices.Application.Servers;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Tests;

public class ServerLabelTests
{
    [Fact]
    public void Valid_labels_are_trimmed_and_blank_rows_dropped()
    {
        var labels = ServerLabels.Normalize(
        [
            KeyValuePair.Create(" env ", " production "),
            KeyValuePair.Create("example.com/team", ""),
            KeyValuePair.Create("", ""),
        ]);

        Assert.Equal(2, labels.Count);
        Assert.Equal("production", labels["env"]);
        Assert.Equal("", labels["example.com/team"]);
    }

    [Theory]
    [InlineData("-env", "x")]
    [InlineData("env", "has space")]
    [InlineData("", "orphan")]
    public void Invalid_labels_are_rejected(string key, string value) =>
        Assert.Throws<DomainException>(() => ServerLabels.Normalize([KeyValuePair.Create(key, value)]));

    [Fact]
    public void Duplicate_keys_are_rejected() =>
        Assert.Throws<DomainException>(() => ServerLabels.Normalize([KeyValuePair.Create("env", "a"), KeyValuePair.Create("env", "b")]));

    [Fact]
    public async Task Saving_replaces_the_labels_at_the_provider()
    {
        var connection = new FakeConnection();
        var servers = new InMemoryServerRepository();
        var server = Server.Provision(TestData.TenantId, Guid.NewGuid(), "web-1", "fsn1", "cx23", "ubuntu-24.04", 4m, "EUR", "key", TestKeys.Ed25519Line(200), DateTime.UtcNow);
        server.AttachProviderResources("1", "2", "203.0.113.10");
        servers.Add(server);
        var labels = new ServerLabels(TestData.Access(), new FakeConnectionFactory(connection), servers);

        await labels.SaveAsync(server.Id, [KeyValuePair.Create("env", "prod")], CancellationToken.None);

        Assert.Equal("prod", connection.LabelProvider!.Labels["1"]["env"]);
        Assert.Equal("prod", (await labels.GetAsync(server.Id, CancellationToken.None)).Labels!["env"]);
    }
}
