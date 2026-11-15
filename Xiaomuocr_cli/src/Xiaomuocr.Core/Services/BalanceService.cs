using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public class PaymentSyncResult
{
    [System.Text.Json.Serialization.JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public interface IBalanceService
{
    Task<BalanceInfo> GetBalanceAsync();
    Task<PaginatedResponse<ConsumptionRecord>> GetRecordsAsync(int page = 1, int pageSize = 20);
    Task<RechargeResult> RechargeAsync(decimal amount, string paymentMethod = "alipay");
    Task<PaymentSyncResult> SyncPaymentStatusAsync(string outTradeNo);
}

public class BalanceService : IBalanceService
{
    private readonly IApiService _api;

    public BalanceService(IApiService api)
    {
        _api = api;
    }

    public async Task<BalanceInfo> GetBalanceAsync()
    {
        return await _api.GetAsync<BalanceInfo>(Constants.Endpoints.Balance);
    }

    public async Task<PaginatedResponse<ConsumptionRecord>> GetRecordsAsync(int page = 1, int pageSize = 20)
    {
        // 优先用计费流水（含套餐扣减）；失败再回退旧 balance/records
        try
        {
            var billing = await _api.GetAsync<PaginatedResponse<ConsumptionRecord>>(
                $"{Constants.Endpoints.PricingTransactions}?page={page}&page_size={pageSize}");
            foreach (var item in billing.Items)
            {
                // pricing/transactions 用 txn_type + total_amount
                if (string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.TxnType))
                    item.Type = item.TxnType!;
                if (item.TotalAmount.HasValue)
                    item.Amount = item.TotalAmount.Value;
            }
            return billing;
        }
        catch
        {
            return await _api.GetAsync<PaginatedResponse<ConsumptionRecord>>(
                $"{Constants.Endpoints.BalanceRecords}?page={page}&page_size={pageSize}");
        }
    }

    public async Task<RechargeResult> RechargeAsync(decimal amount, string paymentMethod = "alipay")
    {
        return await _api.PostAsync<RechargeResult>(Constants.Endpoints.Recharge,
            new RechargeRequest { Amount = amount, PaymentMethod = paymentMethod });
    }

    public async Task<PaymentSyncResult> SyncPaymentStatusAsync(string outTradeNo)
    {
        return await _api.PostAsync<PaymentSyncResult>(
            $"{Constants.Endpoints.PaymentSync}/{outTradeNo}");
    }
}
