using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IInvoiceService
{
    Task<string> GetNoticeAsync();
    Task<List<InvoiceListItem>> ListAsync();
    Task<List<InvoiceOrderBrief>> GetEligibleOrdersAsync();
    Task<InvoiceDetail> GetAsync(int id);
    Task<InvoiceDetail> CreateAsync(InvoiceCreateRequest body);
    Task<InvoiceDetail> UpdateAsync(int id, InvoiceCreateRequest body);
    Task<InvoiceDetail> PostMessageAsync(int id, string content);
}

public class InvoiceService : IInvoiceService
{
    private readonly IApiService _api;

    public InvoiceService(IApiService api) => _api = api;

    public async Task<string> GetNoticeAsync()
    {
        var r = await _api.GetAsync<InvoiceNoticeResponse>(Constants.Endpoints.InvoiceNotice);
        return r.Notice ?? "";
    }

    public async Task<List<InvoiceListItem>> ListAsync()
    {
        var r = await _api.GetAsync<InvoiceListResponse>(Constants.Endpoints.Invoices);
        return r.Items ?? new List<InvoiceListItem>();
    }

    public async Task<List<InvoiceOrderBrief>> GetEligibleOrdersAsync()
    {
        var r = await _api.GetAsync<InvoiceEligibleOrdersResponse>(
            Constants.Endpoints.InvoiceEligibleOrders);
        return r.Items ?? new List<InvoiceOrderBrief>();
    }

    public Task<InvoiceDetail> GetAsync(int id)
        => _api.GetAsync<InvoiceDetail>(Constants.Endpoints.InvoiceDetail(id));

    public Task<InvoiceDetail> CreateAsync(InvoiceCreateRequest body)
        => _api.PostAsync<InvoiceDetail>(Constants.Endpoints.Invoices, body);

    public Task<InvoiceDetail> UpdateAsync(int id, InvoiceCreateRequest body)
        => _api.PutAsync<InvoiceDetail>(Constants.Endpoints.InvoiceDetail(id), body);

    public Task<InvoiceDetail> PostMessageAsync(int id, string content)
        => _api.PostAsync<InvoiceDetail>(
            Constants.Endpoints.InvoiceMessages(id),
            new InvoiceMessageCreateRequest { Content = content });
}
