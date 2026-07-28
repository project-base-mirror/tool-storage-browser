using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed record AwsCredentialResolution(
    AWSCredentials Credentials,
    CredentialSourceKind ActualSource,
    string DisplayName);

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

    public AwsCredentialResolver()
        : this(
            LoadSharedProfile,
            static () => new EnvironmentVariablesAWSCredentials(),
            static () => new GenericContainerCredentials(),
            static () => new InstanceProfileAWSCredentials(),
            static () => FallbackCredentialsFactory.GetCredentials(),
            Environment.GetEnvironmentVariable)
    {
    }

    internal AwsCredentialResolver(
        Func<string, AWSCredentials?> sharedProfileResolver,
        Func<AWSCredentials> environmentFactory,
        Func<AWSCredentials> containerFactory,
        Func<AWSCredentials> instanceFactory,
        Func<AWSCredentials> defaultChainFactory,
        Func<string, string?> getEnvironmentVariable)
    {
        _sharedProfileResolver = sharedProfileResolver;
        _environmentFactory = environmentFactory;
        _containerFactory = containerFactory;
        _instanceFactory = instanceFactory;
        _defaultChainFactory = defaultChainFactory;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public AwsCredentialResolution Resolve(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        return profile.CredentialSource switch
        {
            CredentialSourceKind.StoredKeys => ResolveStoredKeys(profile),
            CredentialSourceKind.AwsSharedProfile => ResolveSharedProfile(profile.AwsProfileName),
            CredentialSourceKind.AwsEnvironmentVariables => ResolveEnvironment(),
            CredentialSourceKind.AwsContainerRole => ResolveContainerRole(),
            CredentialSourceKind.AwsInstanceRole => ResolveInstanceRole(),
            CredentialSourceKind.AwsDefaultChain => ResolveDefaultChain(),
            _ => throw new AwsCredentialResolutionException($"不支持的凭据来源：{profile.CredentialSource}")
        };
    }

    private static AwsCredentialResolution ResolveStoredKeys(ConnectionProfile profile)
    {
        AWSCredentials credentials = profile.UsesTemporarySessionCredentials
            ? new SessionAWSCredentials(profile.AccessKey, profile.SecretKey, profile.SessionToken)
            : new BasicAWSCredentials(profile.AccessKey, profile.SecretKey);
        return new(credentials, CredentialSourceKind.StoredKeys, profile.CredentialSourceDisplayName);
    }

    private AwsCredentialResolution ResolveSharedProfile(string profileName)
    {
        var normalizedName = profileName.Trim();
        try
        {
            var credentials = _sharedProfileResolver(normalizedName);
            if (credentials is null)
                throw new AwsCredentialResolutionException(
                    $"未找到 AWS shared profile“{normalizedName}”。请检查 ~/.aws/credentials、~/.aws/config 或 AWS_PROFILE 设置。");
            EnsureSupportedCredentialType(credentials, $"AWS shared profile“{normalizedName}”");
            return new(credentials, CredentialSourceKind.AwsSharedProfile, $"AWS shared profile：{normalizedName}");
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException(
                $"无法加载 AWS shared profile“{normalizedName}”：{exception.Message}", exception);
        }
    }

    private AwsCredentialResolution ResolveEnvironment()
    {
        var accessKey = FirstEnvironmentValue("AWS_ACCESS_KEY_ID", "AWS_ACCESS_KEY");
        var secretKey = FirstEnvironmentValue("AWS_SECRET_ACCESS_KEY", "AWS_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new AwsCredentialResolutionException(
                "AWS 环境变量凭据不完整。需要 AWS_ACCESS_KEY_ID 和 AWS_SECRET_ACCESS_KEY；临时凭据还需要 AWS_SESSION_TOKEN。");

        return CreateLocked(
            _environmentFactory,
            CredentialSourceKind.AwsEnvironmentVariables,
            "AWS 环境变量");
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

    private AwsCredentialResolution ResolveDefaultChain()
    {
        try
        {
            var credentials = _defaultChainFactory();
            EnsureSupportedCredentialType(credentials, "AWS 默认凭据链");
            var actualSource = ClassifyCredentialType(credentials);
            return new(credentials, actualSource, $"AWS 默认凭据链 → {SourceName(actualSource)}");
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException(
                $"AWS 默认凭据链未找到可用凭据：{exception.Message}", exception);
        }
    }

    private AwsCredentialResolution CreateLocked(
        Func<AWSCredentials> factory,
        CredentialSourceKind source,
        string displayName)
    {
        try
        {
            var credentials = factory();
            EnsureSupportedCredentialType(credentials, displayName);
            return new(credentials, source, displayName);
        }
        catch (AwsCredentialResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AwsCredentialResolutionException($"无法加载{displayName}凭据：{exception.Message}", exception);
        }
    }

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

    private static void EnsureSupportedCredentialType(AWSCredentials credentials, string source)
    {
        var typeName = credentials.GetType().Name;
        if (typeName.Contains("SSO", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("AssumeRole", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("SAML", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Federated", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("WebIdentity", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            throw new AwsCredentialResolutionException(
                $"{source}解析为 {typeName}。AWS SSO、AssumeRole 和 Web Identity 将在高级身份阶段接入，本版本不会缓存或静默使用这些令牌。");
        }
    }

    private static CredentialSourceKind ClassifyCredentialType(AWSCredentials credentials)
    {
        var typeName = credentials.GetType().Name;
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
        return CredentialSourceKind.AwsDefaultChain;
    }

    private static string SourceName(CredentialSourceKind source) => source switch
    {
        CredentialSourceKind.AwsEnvironmentVariables => "环境变量",
        CredentialSourceKind.AwsSharedProfile => "shared profile/config",
        CredentialSourceKind.AwsContainerRole => "容器角色",
        CredentialSourceKind.AwsInstanceRole => "EC2 实例角色",
        _ => "SDK 解析来源"
    };
}
