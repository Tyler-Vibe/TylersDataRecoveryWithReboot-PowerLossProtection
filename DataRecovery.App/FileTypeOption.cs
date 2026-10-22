using System.ComponentModel;
using System.Runtime.CompilerServices;
using DataRecovery.Core.Models;

namespace DataRecovery.App;

public sealed class FileTypeOption(FileTypeDefinition definition) : INotifyPropertyChanged
{
    public FileTypeDefinition Definition { get; } = definition;

    public string DisplayName => Definition.DisplayName;

    private bool _isSelected = definition.SelectedByDefault;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
