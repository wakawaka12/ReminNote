namespace ReminNote.Windows.ViewModels;

public enum ShellPage
{
    Today,
    Anime
}

public sealed record NavigationItemViewModel(
    ShellPage Page,
    string Title,
    string Subtitle);
