namespace ReminNote.Core.Protocol;

public sealed record ProtocolNegotiationResult(
    ProtocolVersion SelectedProtocolVersion,
    IReadOnlyList<string> AcceptedFeatures,
    IReadOnlyList<string> DowngradedFeatures,
    bool Ready);

/// <summary>
/// Pure version/feature negotiation used by a future business-pipe session.
/// It performs no I/O and does not grant readiness to an unhealthy Agent.
/// </summary>
public static class ProtocolVersionNegotiator
{
    public static ProtocolNegotiationResult Negotiate(
        IReadOnlyCollection<ProtocolVersion> clientSupportedVersions,
        IReadOnlyCollection<ProtocolVersion> serverSupportedVersions,
        IReadOnlyCollection<string> requestedFeatures,
        IReadOnlyCollection<string> requiredFeatures,
        IReadOnlyCollection<string> serverCapabilities)
    {
        ArgumentNullException.ThrowIfNull(clientSupportedVersions);
        ArgumentNullException.ThrowIfNull(serverSupportedVersions);
        ArgumentNullException.ThrowIfNull(requestedFeatures);
        ArgumentNullException.ThrowIfNull(requiredFeatures);
        ArgumentNullException.ThrowIfNull(serverCapabilities);

        ValidateVersions(clientSupportedVersions, nameof(clientSupportedVersions));
        ValidateVersions(serverSupportedVersions, nameof(serverSupportedVersions));
        var commonVersions = clientSupportedVersions
            .Where(client => serverSupportedVersions.Contains(client))
            .OrderByDescending(version => version.Major)
            .ThenByDescending(version => version.Minor)
            .ToArray();
        if (commonVersions.Length == 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                "Client and server have no compatible protocol version.");
        }

        ValidateFeatureNames(requestedFeatures, nameof(requestedFeatures));
        ValidateFeatureNames(requiredFeatures, nameof(requiredFeatures));
        ValidateFeatureNames(serverCapabilities, nameof(serverCapabilities));

        var serverFeatureSet = new HashSet<string>(serverCapabilities, StringComparer.Ordinal);
        var requestedFeatureSet = new HashSet<string>(requestedFeatures, StringComparer.Ordinal);
        requestedFeatureSet.UnionWith(requiredFeatures);
        var requiredFeatureSet = new HashSet<string>(requiredFeatures, StringComparer.Ordinal);
        var accepted = requestedFeatureSet
            .Where(serverFeatureSet.Contains)
            .OrderBy(feature => feature, StringComparer.Ordinal)
            .ToArray();
        var downgraded = requestedFeatureSet
            .Where(feature => !serverFeatureSet.Contains(feature))
            .OrderBy(feature => feature, StringComparer.Ordinal)
            .ToArray();

        if (requiredFeatureSet.Any(feature => !serverFeatureSet.Contains(feature)))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.FeatureRequired,
                "A client-required protocol feature is not supported by the server.");
        }

        return new ProtocolNegotiationResult(
            commonVersions[0],
            Array.AsReadOnly(accepted),
            Array.AsReadOnly(downgraded),
            Ready: true);
    }

    private static void ValidateVersions(
        IReadOnlyCollection<ProtocolVersion> versions,
        string fieldName)
    {
        if (versions.Count is < 1 or > ProtocolLimits.MaxSupportedProtocolVersions)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "A protocol version list must contain between 1 and 8 values.",
                fieldName);
        }

        foreach (var version in versions)
        {
            version.ValidateShape();
        }

        if (versions.Distinct().Count() != versions.Count)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Protocol version lists must not contain duplicates.",
                fieldName);
        }
    }

    private static void ValidateFeatureNames(
        IReadOnlyCollection<string> features,
        string fieldName)
    {
        if (features.Count > 64 || features.Any(string.IsNullOrWhiteSpace) ||
            features.Distinct(StringComparer.Ordinal).Count() != features.Count)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Feature names must be non-empty and unique within their list.",
                fieldName);
        }

        foreach (var feature in features)
        {
            ProtocolValidation.RequireUtf8ByteLength(feature, 96, fieldName);
        }
    }
}
