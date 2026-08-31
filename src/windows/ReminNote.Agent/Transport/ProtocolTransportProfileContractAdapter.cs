using ReminNote.Core.Protocol;

namespace ReminNote.Agent.Transport;

/// <summary>
/// The sole adapter from transport profile resolution to the shared protocol
/// identity contract. Both Agent and UI clients use these exact names.
/// </summary>
internal sealed class ProtocolTransportProfileContractAdapter : ITransportProfileContractAdapter
{
    public string GetProfileScope(string actualUserSid, string canonicalDatabasePath) =>
        ProtocolProfileScope.Derive(actualUserSid, canonicalDatabasePath);

    public string GetBusinessPipeName(string profileScope) =>
        ProtocolPipeNames.Business(profileScope);

    public string GetControlPipeName(string profileScope) =>
        ProtocolPipeNames.Control(profileScope);
}
