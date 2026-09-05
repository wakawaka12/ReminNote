using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using ReminNote.Agent.Transport;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// Bootstrap-only health client. It talks to the scoped control pipe and never
/// opens, migrates or queries the business database.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentControlClient
{
    private readonly ResolvedTransportProfile profile;
    private readonly NamedPipeTransportEndpoint endpoint;
    private readonly TransportLimits limits = AgentTransportDefaults.CreateLimits();
    private readonly LengthPrefixedFrameCodec frameCodec;

    public AgentControlClient(
        string repositoryRoot,
        string? profileName = null,
        string? dataRoot = null)
    {
        profile = TransportProfileResolver.ResolveForCurrentUser(
            new TransportProfileArguments(
                DataRoot: dataRoot,
                RepoRoot: dataRoot is null ? repositoryRoot : null,
                Profile: profileName),
            new ProtocolTransportProfileContractAdapter());
        endpoint = new NamedPipeTransportEndpoint(profile, NamedPipeEndpointKind.Control);
        frameCodec = new LengthPrefixedFrameCodec(limits);
    }

    public string ProfileScope => profile.ProfileScope;

    public async ValueTask<AgentHealthInfo> WaitForHealthyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > limits.AbsoluteRequestDeadline)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Exception? lastFailure = null;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (Stopwatch.GetElapsedTime(startedAt) < timeout)
            {
                try
                {
                    return await ProbeOnceAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or
                    TimeoutException or
                    TransportFailureException or
                    InvalidOperationException)
                {
                    lastFailure = exception;
                }

                var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(100)
                            ? remaining
                            : TimeSpan.FromMilliseconds(100),
                        deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The Agent did not become healthy before the control deadline.", lastFailure);
    }

    private async ValueTask<AgentHealthInfo> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        await using var client = endpoint.CreateClientStream();
        await client.ConnectAsync(
                ToTimeoutMilliseconds(limits.ConnectDeadline),
                cancellationToken)
            .ConfigureAwait(false);

        var request = AgentControlProtocol.CreateHealthRequest(profile.ProfileScope);
        await frameCodec.WriteAsync(
                client,
                request,
                limits.HelloQueryStatusDeadline,
                cancellationToken)
            .ConfigureAwait(false);
        var response = await frameCodec.ReadAsync(
                client,
                limits.HelloQueryStatusDeadline,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            throw new IOException("The Agent closed the control pipe without a health response.");
        }

        return AgentControlProtocol.ParseHealthResponse(response, profile.ProfileScope);
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        checked((int)Math.Ceiling(timeout.TotalMilliseconds));
}
