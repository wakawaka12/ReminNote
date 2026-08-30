using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Transport;

/// <summary>
/// Creates endpoint descriptors with an explicit DACL. The business pipe is
/// user-only; SYSTEM is deliberately added only to the separate control pipe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NamedPipeAcl
{
    public static PipeSecurity CreateBusinessSecurity(string userSid)
    {
        EnsureWindows();
        var securityIdentifier = ParseSid(userSid);
        var security = CreateProtectedSecurity();
        security.ResetAccessRule(new PipeAccessRule(
            securityIdentifier,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    public static PipeSecurity CreateControlSecurity(string userSid)
    {
        EnsureWindows();
        var securityIdentifier = ParseSid(userSid);
        var security = CreateProtectedSecurity();
        security.ResetAccessRule(new PipeAccessRule(
            securityIdentifier,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    public static void EnsureClientMatchesProfile(
        NamedPipeServerStream server,
        ResolvedTransportProfile profile,
        ITransportProfileContractAdapter contractAdapter)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(contractAdapter);
        EnsureWindows();

        string? clientSid = null;
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            clientSid = identity.User?.Value;
        });

        if (clientSid is null)
        {
            throw new TransportFailureException(
                TransportFailureKind.Closed,
                "The named pipe client identity is unavailable.",
                connectionMustClose: true);
        }

        var normalizedClientSid = WindowsUserIdentity.NormalizeSid(clientSid);
        if (!normalizedClientSid.Equals(profile.UserSid, StringComparison.Ordinal) ||
            !string.Equals(
                contractAdapter.GetProfileScope(normalizedClientSid, profile.DatabasePath),
                profile.ProfileScope,
                StringComparison.Ordinal))
        {
            throw new TransportFailureException(
                TransportFailureKind.Closed,
                "The named pipe client is outside the endpoint profile.",
                connectionMustClose: true);
        }
    }

    public static string GetAccessSddl(PipeSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        EnsureWindows();
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    private static PipeSecurity CreateProtectedSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return security;
    }

    private static SecurityIdentifier ParseSid(string value)
    {
        try
        {
            return new SecurityIdentifier(WindowsUserIdentity.NormalizeSid(value));
        }
        catch (TransportProfileResolutionException exception)
        {
            throw new ArgumentException("The Windows user SID is invalid.", nameof(value), exception);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named Pipe ACLs require Windows.");
        }
    }
}
