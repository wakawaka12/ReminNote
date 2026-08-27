using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ReminNote.Agent;

internal static class Program
{
    private static int Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        using var host = builder.Build();

        host.Start();
        Console.WriteLine("ReminNote Agent host composed. Runtime services are deferred to later Slices.");
        host.StopAsync().GetAwaiter().GetResult();
        return 0;
    }
}
