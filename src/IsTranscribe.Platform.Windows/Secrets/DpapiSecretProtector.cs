using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace IsTranscribe.Host.Secrets;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] payload) =>
        ProtectedData.Protect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] payload) =>
        ProtectedData.Unprotect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);
}
