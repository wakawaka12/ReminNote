namespace ReminNote.Windows.Features.Anime;

public interface IAnimeMockCatalog
{
    IReadOnlyList<AnimeMockEntry> LoadCatalog();
}

public sealed class AnimeMockCatalogService : IAnimeMockCatalog
{
    public IReadOnlyList<AnimeMockEntry> LoadCatalog() => AnimeMockEntry.CreateCatalog();
}
