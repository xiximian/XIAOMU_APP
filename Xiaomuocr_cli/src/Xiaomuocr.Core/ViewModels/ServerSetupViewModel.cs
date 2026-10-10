using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class ServerSetupViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IApiService _api;

    private string _serverAddress = "";
    public string ServerAddress
    {
        get => _serverAddress;
        set => this.RaiseAndSetIfChanged(ref _serverAddress, value);
    }

    /// <summary>继承自 ViewModelBase: StatusMessage, ErrorMessage, HasStatus, HasError</summary>
    public Action? OnSetupComplete { get; set; }
    public Action? OnOfflineReading { get; set; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> TestAndSaveCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OfflineReadingCommand { get; }

    public ServerSetupViewModel(ISettingsService settings, IApiService api)
    {
        _settings = settings;
        _api = api;

        var canTest = this.WhenAnyValue(
            x => x.ServerAddress, x => x.IsBusy,
            (url, busy) => !busy && !string.IsNullOrWhiteSpace(url));

        TestAndSaveCommand = ReactiveCommand.CreateFromTask(TestAndSaveAsync, canTest);
        OfflineReadingCommand = ReactiveCommand.Create(() => OnOfflineReading?.Invoke());
    }

    private async Task TestAndSaveAsync()
    {
        IsBusy = true;
        ClearError();
        StatusMessage = "正在测试连接...";

        var url = ServerAddress.TrimEnd('/');
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var response = await http.GetAsync($"{url}/health");
            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "连接成功！正在保存...";
                await _settings.SetServerUrlAsync(url);
                _api.BaseUrl = url;
                OnSetupComplete?.Invoke();
            }
            else
            {
                ErrorMessage = $"服务器返回 HTTP {(int)response.StatusCode}，请检查地址是否正确";
                ClearStatus();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法连接: {ex.Message}";
            ClearStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
