using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Today;

namespace ReminNote.Windows.Features.Today;

public static class TodayFeatureRegistration
{
    public static IServiceCollection AddTodayFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ITodayMockDataService, TodayMockDataService>();
        services.AddSingleton<TodayPageViewModel>(serviceProvider =>
            new TodayPageViewModel(
                serviceProvider.GetRequiredService<ITodayQueryService>(),
                serviceProvider.GetRequiredService<ITaskApplicationService>(),
                serviceProvider.GetRequiredService<IClock>()));
        return services;
    }
}
