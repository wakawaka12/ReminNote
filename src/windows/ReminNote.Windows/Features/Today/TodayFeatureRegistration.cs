using Microsoft.Extensions.DependencyInjection;

namespace ReminNote.Windows.Features.Today;

public static class TodayFeatureRegistration
{
    public static IServiceCollection AddTodayFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TodayPageViewModel>();
        return services;
    }
}
