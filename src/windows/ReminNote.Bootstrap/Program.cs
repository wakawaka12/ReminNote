using Microsoft.Extensions.Hosting;

namespace ReminNote.Bootstrap;

internal static class Program
{
    private static int Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        using var host = builder.Build();

        host.Start();
        Console.WriteLine("ReminNote Bootstrap host composed. Process activation is deferred to later Slices.");
        host.StopAsync().GetAwaiter().GetResult();
        return 0;
    }
}
