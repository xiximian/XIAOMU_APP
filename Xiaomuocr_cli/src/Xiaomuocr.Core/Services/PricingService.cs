using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IPricingService
{
    Task<PricingConfig> GetConfigAsync();
    Task<UserAssets> GetAssetsAsync();
    Task<PurchasePackageResult> PurchaseAsync(int planId, string paymentMethod = "mock");
}

public class PricingService : IPricingService
{
    private readonly IApiService _api;

    public PricingService(IApiService api)
    {
        _api = api;
    }

    public async Task<PricingConfig> GetConfigAsync()
    {
        return await _api.GetAsync<PricingConfig>(Constants.Endpoints.PricingConfig);
    }

    public async Task<UserAssets> GetAssetsAsync()
    {
        return await _api.GetAsync<UserAssets>(Constants.Endpoints.PricingAssets);
    }

    public async Task<PurchasePackageResult> PurchaseAsync(int planId, string paymentMethod = "mock")
    {
        return await _api.PostAsync<PurchasePackageResult>(
            Constants.Endpoints.PricingPurchase,
            new PurchasePackageRequest { PlanId = planId, PaymentMethod = paymentMethod });
    }
}
