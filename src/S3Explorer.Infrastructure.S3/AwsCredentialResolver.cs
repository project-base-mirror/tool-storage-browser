using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed record AwsCredentialResolution(
    AWSCredentials Credentials,
    CredentialSourceKind ActualSource,
    string DisplayName,
    AwsIdentitySummary Identity)
{
    public AwsIdentitySummary GetCurrentIdentity() =>
        Identity with { SessionExpiresAtUtc = AwsCredentialResolver.TryGetSessionExpiration(Credentials) };
}

public sealed class AwsCredentialResolutionException : InvalidOperationException
{
    public AwsCredentialResolutionException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class AwsCredentialResolver
{
    private readonly Func<string, AWSCredentials?> _sharedProfileResolver;
    private readonly Func<AWSCredentials> _environmentFactory;
    private readonly Func<AWSCredentials> _containerFactory;
    private readonly Func<AWSCredentials> _instanceFactory;
    private readonly Func<AWSCredentials> _defaultChainFactory;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Action<Uri> _openSsoVerificationUri;
    private readonly ConcurrentDictionary<string, AWSCredentials> _sessionCredentials = new(StringComparer.Ordinal);

    public AwsCredentialResolver()
        : this(
            LoadSharedProfile,
            static () => new EnvironmentVariablesAWSCredentials(),
            static () => new GenericContainerCredentials(),
            static () => new InstanceProfileAWSCredentials(),
            static () =>
            {
                using var resolver = new DefaultAWSCredentialsIdentityResolver();
                return resolver.ResolveIdentity(new AmazonS3Config());
            },
            Environment.GetEnvironmentVariable,
            OpenSsoVerificationUri)
    {
    }

    internal AwsCredentialResolver(
        Func<string, AWSCredentials?> sharedProfileResolver,
        Func<AWSCredentials> environmentFactory,
        Func<AWSCredentials> containerFactory,
        Func<AWSCredentials> instanceFactory,
        Func<AWSCredentials> defaultChainFactory,
        Func<string, string?> getEnvironmentVariable,
        Action<Uri>? openSsoVerificationUri = null)
    {
        _sharedProfileResolver = sharedProfileResolver;
        _environmentFactory = environmentFactory;
        _containerFactory = containerFactory;
        _instanceFactory = instanceFactory;
        _defaultChainFactory = defaultChainFactory;
        _getEnvironmentVariable = getEnvironmentVariable;
        _openSsoVerificationUri = openSsoVerificationUri ?? (_ => { });
    }

    public AwsCredentialResolution Resolve(ConnectionProfile profile, bool allowInteractiveSso = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        return profile.CredentialSource switch
        {
            CredentialSourceKind.StoredKeys => ResolveStoredKeys(profile),
            CredentialSourceKind.AwsSharedProfile => ResolveSharedProfile(profile.AwsProfileName, allowInteractiveSso),
            CredentialSourceKind.AwsEnvironmentVariables => ResolveEnvironment(),
            CredentialSourceKind.AwsContainerRole => ResolveContainerRole(),
            CredentialSourceKind.AwsInstanceRole => ResolveInstanceRole(),
            CredentialSourceKind.AwsDefaultChain => ResolveDefaultChain(allowInteractiveSso),
            CredentialSourceKind.AwsSso => ResolveSso(profile, allowInteractiveSso),
            CredentialSourceKind.AwsAssumeRole => ResolveAssumeRole(profile, allowInteractiveSso),
            CredentialSourceKind.AwsWebIdentity => ResolveWebIdentity(profile),
            _ => throw new AwsCredentialResolutionException($"不支持的凭据来源：{profile.CredentialSource}")
        };
    }

    private static AwsCredentialResolution ResolveStoredKeys(ConnectionProfile profile)
    {
        AWSCredentials credentials = profile.UsesTemporarySessionCredentials
            ? new SessionAWSCredentials(profile.AccessKey, profile.SecretKey, profile.SessionToken)
            : new BasicAWSCredentials(profile.AccessKey, profile.SecretKey);
        return CreateResolution(credentials, CredentialSourceKind.StoredKeys, profile.CredentialSourceDisplayName,
            profile.UsesTemporarySessionCredentials ? "已保存的临时会话密钥" : "已保存的长期密钥");
    }

    private AwsCredentialResolution ResolveSharedProfile(string profileName, bool allowInteractiveSso)
    {
        var normalizedName = profileName.Trim();
        try
        {
            var credentials = _sharedProfileResolver(normalizedName);
            if (credentials is null)
                throw new AwsCredentialResolutionException(
                    $"未找到 AWS shared profile“{normalizedName}”。请检查 ~/.aws/credentials、~/.aws/config 或 AWS_PROFILE 设置。");
            if (credentials is RefreshingAWSCredentials)
                credentials = _sessionCredentials.GetOrAdd($"shared|{normalizedName}|{allowInteractiveSso}", credentials);
            EnsureSupportedCredentialType(credentials, $"AWS shared profile“{normalizedName}”");
            ConfigureInteractiveSso(credentials, allowInteractiveSso);
            var actualSource = ClassifyCredentialType(credentials, CredentialSourceKind.AwsSharedProfile);
            return CreateResolution(credentials, actualSource, $"AWS shared profile：{normalizedName}",
                DescribeSourceIdentity(credentials, $"shared profile {normalizedName}"),
                userLoginMayBeRequired: actualSource == CredentialSourceKind.AwsSso && !allowInteractiveSso);
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException(
                $"无法加载 AWS shared profile“{normalizedName}”：{SafeMessage(exception)}", exception);
        }
    }

    private AwsCredentialResolution ResolveSso(ConnectionProfile profile, bool allowInteractiveSso)
    {
        var resolution = ResolveSharedProfile(profile.AwsProfileName, allowInteractiveSso);
        if (resolution.Credentials is not SSOAWSCredentials)
            throw new AwsCredentialResolutionException(
                $"AWS profile“{profile.AwsProfileName.Trim()}”不是 SSO profile。请检查 ~/.aws/config 中的 sso_session、sso_start_url、sso_account_id 与 sso_role_name。");
        return resolution with
        {
            ActualSource = CredentialSourceKind.AwsSso,
            DisplayName = profile.CredentialSourceDisplayName,
            Identity = resolution.Identity with
            {
                Source = CredentialSourceKind.AwsSso,
                UserLoginMayBeRequired = !allowInteractiveSso
            }
        };
    }

    private AwsCredentialResolution ResolveAssumeRole(ConnectionProfile profile, bool allowInteractiveSso)
    {
        try
        {
            var credentials = GetOrCreateSessionCredentials(profile, allowInteractiveSso, () =>
            {
                var source = ResolveSharedProfile(profile.AwsSourceProfileName, allowInteractiveSso);
                return new AssumeRoleAWSCredentials(
                    source.Credentials,
                    profile.AwsRoleArn.Trim(),
                    profile.AwsRoleSessionName.Trim(),
                    new AssumeRoleAWSCredentialsOptions
                    {
                        DurationSeconds = profile.AwsSessionDurationSeconds,
                        ExternalId = EmptyToNull(profile.AwsExternalId),
                        SourceIdentity = EmptyToNull(profile.AwsRoleSourceIdentity)
                    });
            });
            var sourceIdentity = string.IsNullOrWhiteSpace(profile.AwsRoleSourceIdentity)
                ? $"shared profile {profile.AwsSourceProfileName.Trim()}"
                : $"shared profile {profile.AwsSourceProfileName.Trim()} / SourceIdentity {profile.AwsRoleSourceIdentity.Trim()}";
            return CreateResolution(credentials, CredentialSourceKind.AwsAssumeRole, profile.CredentialSourceDisplayName,
                sourceIdentity, profile.AwsRoleArn.Trim(), !string.IsNullOrWhiteSpace(profile.AwsExternalId),
                userLoginMayBeRequired: ContainsSso(credentials) && !allowInteractiveSso);
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException(
                $"无法创建 AssumeRole 会话（{profile.AwsRoleArn.Trim()}）：{SafeMessage(exception)}", exception);
        }
    }

    private AwsCredentialResolution ResolveWebIdentity(ConnectionProfile profile)
    {
        try
        {
            var credentials = GetOrCreateSessionCredentials(profile, false, () =>
                new AssumeRoleWithWebIdentityCredentials(
                    profile.AwsWebIdentityTokenFile.Trim(),
                    profile.AwsRoleArn.Trim(),
                    profile.AwsRoleSessionName.Trim(),
                    new AssumeRoleWithWebIdentityCredentialsOptions
                    {
                        DurationSeconds = profile.AwsSessionDurationSeconds
                    }));
            return CreateResolution(credentials, CredentialSourceKind.AwsWebIdentity, profile.CredentialSourceDisplayName,
                "Web Identity token 文件", profile.AwsRoleArn.Trim());
        }
        catch (Exception exception) when (exception is not AwsCredentialResolutionException)
        {
            throw new AwsCredentialResolutionException(
                $"无法创建 Web Identity 角色会话（{profile.AwsRoleArn.Trim()}）：{SafeMessage(exception)}", exception);
        }
    }

    private AwsCredentialResolution ResolveEnvironment()
    {
        var accessKey = FirstEnvironmentValue("AWS_ACCESS_KEY_ID", "AWS_ACCESS_KEY");
        var secretKey = FirstEnvironmentValue("AWS_SECRET_ACCESS_KEY", "AWS_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new AwsCredentialResolutionException(
                "AWS 环境变量凭据不完整。需要 AWS_ACCESS_KEY_ID 和 AWS_SECRET_ACCESS_KEY；临时凭据还需要 AWS_SESSION_TOKEN。");

        return CreateLocked(_environmentFactory, CredentialSourceKind.AwsEnvironmentVariables, "AWS 环境变量");
    }

    private AwsCredentialResolution ResolveContainerRole()
    {
        var relativeUri = _getEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI");
        var fullUri = _getEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI");
        if (string.IsNullOrWhiteSpace(relativeUri) && string.IsNullOrWhiteSpace(fullUri))
            throw new AwsCredentialResolutionException(
                "未检测到 AWS 容器角色凭据端点。需要 AWS_CONTAINER_CREDENTIALS_RELATIVE_URI 或 AWS_CONTAINER_CREDENTIALS_FULL_URI。");

        return CreateLocked(_containerFactory, CredentialSourceKind.AwsContainerRole, "AWS 容器角色");
    }

    private AwsCredentialResolution ResolveInstanceRole()
    {
        if (string.Equals(_getEnvironmentVariable("AWS_EC2_METADATA_DISABLED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new AwsCredentialResolutionException(
                "EC2 Instance Metadata 已被 AWS_EC2_METADATA_DISABLED 禁用，无法读取实例角色凭据。");

        return CreateLocked(_instanceFactory, CredentialSourceKind.AwsInstanceRole, "AWS EC2 实例角色");
    }

    private AwsCredentialResolution ResolveDefaultChain(bool allowInteractiveSso)
    {
        try
        {
            var credentials = _defaultChainFactory();
            if (credentials is RefreshingAWSCredentials)
                credentials = _sessionCredentials.GetOrAdd(
                    $"default|{credentials.GetType().FullName}|{allowInteractiveSso}", credentials);
            EnsureSupportedCredentialType(credentials, "AWS 默认凭据链");
            ConfigureInteractiveSso(credentials, allowInteractiveSso);
            var actualSource = ClassifyCredentialType(credentials, CredentialSourceKind.AwsDefaultChain);
            return CreateResolution(credentials, actualSource, $"AWS 默认凭据链 → {SourceName(actualSource)}",
                DescribeSourceIdentity(credentials, SourceName(actualSource)),
                userLoginMayBeRequired: actualSource == CredentialSourceKind.AwsSso && !allowInteractiveSso);
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException(
                $"AWS 默认凭据链未找到可用凭据：{SafeMessage(exception)}", exception);
        }
    }

    private AwsCredentialResolution CreateLocked(Func<AWSCredentials> factory, CredentialSourceKind source, string displayName)
    {
        try
        {
            var credentials = factory();
            EnsureSupportedCredentialType(credentials, displayName);
            return CreateResolution(credentials, source, displayName, displayName);
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException($"无法加载{displayName}凭据：{SafeMessage(exception)}", exception);
        }
    }

    private AWSCredentials GetOrCreateSessionCredentials(
        ConnectionProfile profile,
        bool allowInteractiveSso,
        Func<AWSCredentials> factory)
    {
        var externalIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.AwsExternalId ?? string.Empty)));
        var key = string.Join('|', profile.Id, profile.CredentialSource, profile.AwsSourceProfileName,
            profile.AwsRoleArn, profile.AwsRoleSessionName, profile.AwsRoleSourceIdentity, externalIdHash,
            profile.AwsSessionDurationSeconds, profile.AwsWebIdentityTokenFile, allowInteractiveSso);
        return _sessionCredentials.GetOrAdd(key, _ => factory());
    }

    private void ConfigureInteractiveSso(AWSCredentials credentials, bool allowInteractiveSso)
    {
        switch (credentials)
        {
            case SSOAWSCredentials sso:
                sso.Options.SupportsGettingNewToken = allowInteractiveSso;
                sso.Options.SsoVerificationCallback = allowInteractiveSso
                    ? arguments =>
                    {
                        var target = arguments.VerificationUriComplete ?? arguments.VerificationUri;
                        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) _openSsoVerificationUri(uri);
                    }
                    : null;
                break;
            case AssumeRoleAWSCredentials role:
                ConfigureInteractiveSso(role.SourceCredentials, allowInteractiveSso);
                break;
        }
    }

    private static bool ContainsSso(AWSCredentials credentials) => credentials switch
    {
        SSOAWSCredentials => true,
        AssumeRoleAWSCredentials role => ContainsSso(role.SourceCredentials),
        _ => credentials.GetType().Name.Contains("SSO", StringComparison.OrdinalIgnoreCase)
    };

    private string? FirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = _getEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static AWSCredentials? LoadSharedProfile(string profileName)
    {
        var chain = new CredentialProfileStoreChain();
        return chain.TryGetAWSCredentials(profileName, out var credentials) ? credentials : null;
    }

    private static void OpenSsoVerificationUri(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void EnsureSupportedCredentialType(AWSCredentials credentials, string source)
    {
        var typeName = credentials.GetType().Name;
        if (typeName.Contains("SAML", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Federated", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            throw new AwsCredentialResolutionException(
                $"{source}解析为尚未支持的凭据类型 {typeName}。");
        }
    }

    private static CredentialSourceKind ClassifyCredentialType(
        AWSCredentials credentials,
        CredentialSourceKind fallback)
    {
        var typeName = credentials.GetType().Name;
        if (typeName.Contains("SSO", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsSso;
        if (typeName.Contains("WebIdentity", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsWebIdentity;
        if (typeName.Contains("AssumeRole", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsAssumeRole;
        if (typeName.Contains("Environment", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsEnvironmentVariables;
        if (typeName.Contains("ECS", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Container", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsContainerRole;
        if (typeName.Contains("InstanceProfile", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsInstanceRole;
        if (typeName.Contains("Profile", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Stored", StringComparison.OrdinalIgnoreCase))
            return CredentialSourceKind.AwsSharedProfile;
        return fallback;
    }

    private static string DescribeSourceIdentity(AWSCredentials credentials, string fallback) => credentials switch
    {
        SSOAWSCredentials sso => $"AWS SSO account {sso.AccountId} / role {sso.RoleName}",
        AssumeRoleAWSCredentials role => $"AssumeRole source {role.SourceCredentials.GetType().Name}",
        AssumeRoleWithWebIdentityCredentials => "Web Identity token 文件",
        _ => fallback
    };

    private static AwsCredentialResolution CreateResolution(
        AWSCredentials credentials,
        CredentialSourceKind source,
        string displayName,
        string sourceIdentity,
        string? targetRoleArn = null,
        bool externalIdConfigured = false,
        bool userLoginMayBeRequired = false) =>
        new(credentials, source, displayName,
            new AwsIdentitySummary(source, sourceIdentity, targetRoleArn, externalIdConfigured,
                TryGetSessionExpiration(credentials), userLoginMayBeRequired));

    internal static DateTimeOffset? TryGetSessionExpiration(AWSCredentials credentials)
    {
        for (var type = credentials.GetType(); type is not null; type = type.BaseType)
        {
            var stateField = type.GetField("currentState", BindingFlags.Instance | BindingFlags.NonPublic);
            var state = stateField?.GetValue(credentials);
            if (state is null) continue;
            var expiration = state.GetType().GetProperty("Expiration", BindingFlags.Instance | BindingFlags.Public)?.GetValue(state);
            if (expiration is not DateTime value || value == default) return null;
            return value.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : new DateTimeOffset(value).ToUniversalTime();
        }
        return null;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SafeMessage(Exception exception) =>
        SensitiveDataRedactor.Redact(exception.Message);

    private static string SourceName(CredentialSourceKind source) => source switch
    {
        CredentialSourceKind.AwsEnvironmentVariables => "环境变量",
        CredentialSourceKind.AwsSharedProfile => "shared profile/config",
        CredentialSourceKind.AwsContainerRole => "容器角色",
        CredentialSourceKind.AwsInstanceRole => "EC2 实例角色",
        CredentialSourceKind.AwsSso => "AWS SSO",
        CredentialSourceKind.AwsAssumeRole => "AssumeRole",
        CredentialSourceKind.AwsWebIdentity => "Web Identity",
        _ => "SDK 解析来源"
    };
}
