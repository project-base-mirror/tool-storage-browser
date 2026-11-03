using System.Text.Json;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

[Collection("Profile signing console")]
public sealed class ProfileSigningCommandTests
{
    [Theory]
    [InlineData("auto", UploadSigningMode.Auto)]
    [InlineData("streaming", UploadSigningMode.Streaming)]
    [InlineData("unsigned-payload", UploadSigningMode.UnsignedPayload)]
    [InlineData(" UNSIGNED-PAYLOAD ", UploadSigningMode.UnsignedPayload)]
    public void ParsesOnlyNamedUploadModes(string value, UploadSigningMode expected) =>
        Assert.Equal(expected, Program.ParseUploadSigningMode(value));

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("unsigned")]
    [InlineData("")]
    public void RejectsUnknownUploadModes(string value) =>
        Assert.Throws<CliUsageException>(() => Program.ParseUploadSigningMode(value));

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    public void UploadSigningOptionIsAcceptedOnlyOnProfileEdits(string verb)
    {
        var args = CliArguments.Parse(["profile", verb, "--upload-signing", "unsigned-payload"]);
        Program.ValidateCommandOptions("profile", verb, args);
        Assert.Throws<CliUsageException>(() => Program.ValidateCommandOptions("profile", "show", args));
    }

    [Theory]
    [InlineData(null, UploadSigningMode.Auto)]
    [InlineData("unsigned-payload", UploadSigningMode.UnsignedPayload)]
    public async Task AddSavesSelectedModeAndDefaultsToAuto(string? mode, UploadSigningMode expected)
    {
        var store = new ProfileStore([]);
        var values = new List<string> { "profile", "add", "--name", "custom", "--type", "custom", "--credential", "fake" };
        if (mode is not null) values.AddRange(["--upload-signing", mode]);
        var result = await Program.RunProfileAsync("add", CliArguments.Parse(values.ToArray()), store,
            new CredentialStore(), true, TestContext.Current.CancellationToken);
        Assert.Equal(0, result);
        Assert.Equal(expected, Assert.Single(store.Profiles).UploadSigningMode);
    }

    [Fact]
    public async Task UpdateRetainsIdentityCredentialAndOtherSettings()
    {
        var original = Profile() with { DefaultBucket = "bucket", CustomHostHeader = "proxy.example.test" };
        var store = new ProfileStore([original]);
        await Program.UpdateProfileSigningAsync(CliArguments.Parse(["profile", "update", original.Id.ToString(),
            "--upload-signing", "unsigned-payload"]), store, true, TestContext.Current.CancellationToken);
        var updated = Assert.Single(store.Profiles);
        Assert.Equal(original with { UploadSigningMode = UploadSigningMode.UnsignedPayload }, updated);
        await Program.UpdateProfileSigningAsync(CliArguments.Parse(["profile", "update", original.Name,
            "--upload-signing", "auto"]), store, true, TestContext.Current.CancellationToken);
        Assert.Equal(original, Assert.Single(store.Profiles));
    }

    [Theory]
    [InlineData("invalid", "https://storage.example.test")]
    [InlineData("unsigned-payload", "http://storage.example.test")]
    public async Task InvalidUpdateDoesNotSave(string mode, string endpoint)
    {
        var original = Profile() with { Endpoint = endpoint };
        var store = new ProfileStore([original]);
        await Assert.ThrowsAnyAsync<Exception>(() => Program.UpdateProfileSigningAsync(
            CliArguments.Parse(["profile", "update", original.Name, "--upload-signing", mode]), store,
            true, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(original, Assert.Single(store.Profiles));
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData("show", true)]
    [InlineData("list", false)]
    [InlineData("show", false)]
    public async Task ListAndShowExposeModeInJsonAndText(string verb, bool json)
    {
        var profile = Profile() with { UploadSigningMode = UploadSigningMode.UnsignedPayload };
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await Program.RunProfileAsync(verb, CliArguments.Parse(["profile", verb, profile.Name]),
                new ProfileStore([profile]), new CredentialStore(), json, TestContext.Current.CancellationToken);
        }
        finally { Console.SetOut(original); }
        var text = output.ToString();
        Assert.Contains("unsigned-payload", text, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.SecretKey, text, StringComparison.Ordinal);
        if (json)
        {
            var data = JsonDocument.Parse(text).RootElement.GetProperty("data");
            if (verb == "list") data = data[0];
            Assert.Equal("unsigned-payload", data.GetProperty("uploadSigningMode").GetString());
        }
    }

    [Fact]
    public async Task UpdateDoesNotRequireResolvedCredentials()
    {
        var profile = Profile() with { AccessKey = string.Empty, SecretKey = string.Empty };
        var store = new ProfileStore([profile]);
        await Program.UpdateProfileSigningAsync(CliArguments.Parse(["profile", "update", profile.Name,
            "--upload-signing", "unsigned-payload"]), store, true, TestContext.Current.CancellationToken);
        Assert.Equal(UploadSigningMode.UnsignedPayload, Assert.Single(store.Profiles).UploadSigningMode);
    }

    [Fact]
    public async Task UpdateUsesLatestStateAndRetainsConcurrentEditsAndAddedProfiles()
    {
        var original = Profile();
        var concurrentEdit = original with { Endpoint = "https://new-endpoint.example.test", Name = "renamed" };
        var added = Profile() with { Id = Guid.NewGuid(), Name = "concurrently-added" };
        var store = new ProfileStore([original])
        {
            BeforeAtomicUpdate = current => current.Profiles = [concurrentEdit, added]
        };
        await Program.UpdateProfileSigningAsync(CliArguments.Parse(["profile", "update", original.Id.ToString(),
            "--upload-signing", "unsigned-payload"]), store, true, TestContext.Current.CancellationToken);
        Assert.Equal(2, store.Profiles.Count);
        Assert.Equal(concurrentEdit with { UploadSigningMode = UploadSigningMode.UnsignedPayload }, store.Profiles[0]);
        Assert.Same(added, store.Profiles[1]);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task UpdateDoesNotRecreateAConcurrentlyDeletedProfile()
    {
        var original = Profile();
        var replacement = Profile() with { Id = Guid.NewGuid() };
        var store = new ProfileStore([original])
        {
            BeforeAtomicUpdate = current => current.Profiles = [replacement]
        };
        await Assert.ThrowsAsync<CliNotFoundException>(() => Program.UpdateProfileSigningAsync(
            CliArguments.Parse(["profile", "update", original.Id.ToString(), "--upload-signing", "unsigned-payload"]),
            store, true, TestContext.Current.CancellationToken));
        Assert.Same(replacement, Assert.Single(store.Profiles));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task UpdateValidatesLatestEndpointBeforeSaving()
    {
        var original = Profile();
        var concurrentEdit = original with { Endpoint = "http://localhost:9000" };
        var store = new ProfileStore([original])
        {
            BeforeAtomicUpdate = current => current.Profiles = [concurrentEdit]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Program.UpdateProfileSigningAsync(
            CliArguments.Parse(["profile", "update", original.Id.ToString(), "--upload-signing", "unsigned-payload"]),
            store, true, TestContext.Current.CancellationToken));
        Assert.Same(concurrentEdit, Assert.Single(store.Profiles));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void ProfileViewIncludesModeWithoutCredentials()
    {
        var method = typeof(Program).GetMethod("ProfileView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var profile = Profile() with { UploadSigningMode = UploadSigningMode.UnsignedPayload };
        var json = JsonSerializer.Serialize(method.Invoke(null, [profile, null]));
        var document = JsonDocument.Parse(json);
        Assert.Equal("unsigned-payload", document.RootElement.GetProperty("uploadSigningMode").GetString());
        Assert.DoesNotContain(profile.SecretKey, json, StringComparison.Ordinal);
    }

    private static ConnectionProfile Profile() => new()
    {
        Name = "custom", ServiceType = S3ServiceType.Custom, Endpoint = "https://storage.example.test",
        AccessKey = "fake-access", SecretKey = "fake-secret"
    };

    private sealed class ProfileStore(IReadOnlyList<ConnectionProfile> profiles) : IProfileStore, IExplorerConfigurationStore
    {
        public IReadOnlyList<ConnectionProfile> Profiles { get; set; } = profiles;
        public int SaveCount { get; private set; }
        public Action<ProfileStore>? BeforeAtomicUpdate { get; init; }
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Profiles);
        public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> value, CancellationToken cancellationToken = default)
        {
            Profiles = value.ToArray();
            SaveCount++;
            return Task.CompletedTask;
        }
        Task<ExplorerConfiguration> IExplorerConfigurationStore.LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerConfiguration(new ConnectionProfileConfiguration(Profiles, []), CdnConfiguration.Empty, []));
        Task IExplorerConfigurationStore.SaveAsync(ExplorerConfiguration value, CancellationToken cancellationToken) =>
            SaveAsync(value.Storage.Profiles, cancellationToken);
        public Task<ExplorerConfiguration> UpdateAsync(Func<ExplorerConfiguration, ExplorerConfiguration> update,
            CancellationToken cancellationToken = default)
        {
            // Inject a different writer's committed state immediately before the locked read/update.
            BeforeAtomicUpdate?.Invoke(this);
            var current = new ExplorerConfiguration(new ConnectionProfileConfiguration(Profiles, []), CdnConfiguration.Empty, []);
            var next = update(current);
            Profiles = next.Storage.Profiles;
            SaveCount++;
            return Task.FromResult(next);
        }
    }

    private sealed class CredentialStore : ICredentialStore
    {
        public Task<IReadOnlyList<CredentialProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CredentialProfile>>([new CredentialProfile
            {
                Name = "fake", Provider = CredentialProviderKind.S3Compatible, Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "fake-access", Secret = "fake-secret"
            }]);
        public Task SaveAsync(IReadOnlyCollection<CredentialProfile> credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

[CollectionDefinition("Profile signing console", DisableParallelization = true)]
public sealed class ProfileSigningConsoleCollection { }
