using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace ReminNote.Agent.Transport;

internal enum NamedPipeEndpointKind
{
    Business,
    Control
}

internal sealed record NamedPipeEndpointOptions
{
    public int MaxServerInstances { get; init; } = 16;

    public int InputBufferSize { get; init; } = 65_536;

    public int OutputBufferSize { get; init; } = 65_536;

    public void Validate()
    {
        if (MaxServerInstances is < 1 or > 254)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxServerInstances));
        }

        if (InputBufferSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InputBufferSize));
        }

        if (OutputBufferSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputBufferSize));
        }
    }
}

/// <summary>
/// Scoped endpoint factory used by the Agent business/control runtime. The
/// business and control names are resolved from the same profile descriptor,
/// while their ACLs remain separate.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedPipeTransportEndpoint
{
    private readonly NamedPipeEndpointOptions options;

    public NamedPipeTransportEndpoint(
        ResolvedTransportProfile profile,
        NamedPipeEndpointKind kind,
        NamedPipeEndpointOptions? options = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Kind = kind;
        this.options = options ?? new NamedPipeEndpointOptions();
        this.options.Validate();
        PipeName = kind switch
        {
            NamedPipeEndpointKind.Business => profile.BusinessPipeName,
            NamedPipeEndpointKind.Control => profile.ControlPipeName,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public ResolvedTransportProfile Profile { get; }

    public NamedPipeEndpointKind Kind { get; }

    public string PipeName { get; }

    public NamedPipeServerStream CreateServerStream()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named Pipe endpoints require Windows.");
        }

        var security = Kind == NamedPipeEndpointKind.Business
            ? NamedPipeAcl.CreateBusinessSecurity(Profile.UserSid)
            : NamedPipeAcl.CreateControlSecurity(Profile.UserSid);

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            options.MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            options.InputBufferSize,
            options.OutputBufferSize,
            security,
            HandleInheritability.None,
            additionalAccessRights: 0);
    }

    public NamedPipeClientStream CreateClientStream()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named Pipe endpoints require Windows.");
        }

        return new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
    }
}
