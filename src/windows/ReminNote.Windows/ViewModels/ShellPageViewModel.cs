using CommunityToolkit.Mvvm.ComponentModel;

namespace ReminNote.Windows.ViewModels;

public abstract class ShellPageViewModel : ObservableObject
{
    protected ShellPageViewModel(string description)
    {
        Description = description;
    }

    public string Description { get; }
}
