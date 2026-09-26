using System.Security.Cryptography;
using System.Text;

namespace IsTranscribe.Host.Bootstrap;

public sealed record AppIdentity(string ApplicationName, string UserScope, string InstanceKey)
{
    public string MutexName => $@"Local\{InstanceKey}.primary";

    public string ActivationPipeName => $"{InstanceKey}.activation";
}

public sealed class AppIdentityFactory
{
    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#bootstrap.sequence
    public AppIdentity Create(string applicationName)
    {
        var userScope = $"{GetUserDomain()}\\{Environment.UserName}".Trim('\\');
        var hashSource = $"{applicationName}:{userScope}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashSource))).ToLowerInvariant();
        return new AppIdentity(applicationName, userScope, $"istranscribe.{hash[..24]}");
    }

    private static string GetUserDomain()
    {
        try
        {
            return Environment.UserDomainName;
        }
        catch
        {
            return "local";
        }
    }
}
