using ReminNote.Agent.Runtime;
using System.Runtime.Versioning;

namespace ReminNote.Agent;

internal static class Program
{
    [SupportedOSPlatform("windows")]
    private static Task<int> Main(string[] args) => AgentRuntime.RunAsync(args);
}
