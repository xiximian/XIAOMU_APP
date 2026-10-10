using ReactiveUI;
using System.Reactive.Disposables;

namespace Xiaomuocr.Core.ViewModels;

public class ViewModelBase : ReactiveObject, IActivatableViewModel
{
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public void ClearError() => ErrorMessage = null;

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            this.RaiseAndSetIfChanged(ref _statusMessage, value);
            this.RaisePropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public void ClearStatus() => StatusMessage = null;

    public ViewModelBase() { }

    // IActivatableViewModel — required for Avalonia view activation
    public ViewModelActivator Activator { get; } = new ViewModelActivator();
}
