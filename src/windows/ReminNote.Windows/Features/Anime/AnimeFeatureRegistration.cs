using Microsoft.Extensions.DependencyInjection;

namespace ReminNote.Windows.Features.Anime;

public static class AnimeFeatureRegistration
{
    public static IServiceCollection AddAnimeFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<AnimePageViewModel>();
        return services;
    }
}
