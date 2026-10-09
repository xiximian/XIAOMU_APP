using System.Text.Json;
using Xiaomuocr.Core.Models;

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
};

// Simulate what the API returns
var json = """
{
    "items": [{
        "id": 1,
        "task_uuid": "b919de3830d14f5489ec729f1bff77b7",
        "image_url": "http://127.0.0.1:8000/static/test.jpg",
        "page_count": 1,
        "status": "failed",
        "cost": 1.0,
        "result_json": null,
        "error_message": "test error",
        "created_at": "2026-07-18T14:17:59",
        "completed_at": "2026-07-18T14:18:17.588534"
    }],
    "total": 1,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
}
""";

Console.WriteLine("=== Test 1: OcrTaskListResult ===");
try
{
    var result = JsonSerializer.Deserialize<OcrTaskListResult>(json, jsonOpts);
    Console.WriteLine($"Total: {result?.Total}");
    Console.WriteLine($"Items count: {result?.Items?.Count}");
    if (result?.Items?.Count > 0)
    {
        var t = result.Items[0];
        Console.WriteLine($"  TaskUuid: {t.TaskUuid}");
        Console.WriteLine($"  Status: {t.Status}");
        Console.WriteLine($"  ImageUrl: {t.ImageUrl}");
        Console.WriteLine($"  Cost: {t.Cost}");
        Console.WriteLine($"  CreatedAt: {t.CreatedAt}");
        Console.WriteLine($"  ErrorMessage: {t.ErrorMessage}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Test 2: Consumption records ===");
var json2 = """
{
    "items": [{
        "id": 1,
        "type": "ocr_deduct",
        "amount": -1.0,
        "balance_after": 9.0,
        "description": "OCR 1 page",
        "reference_id": "abc123",
        "created_at": "2026-07-18T14:17:59"
    }],
    "total": 1,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
}
""";

try
{
    var result = JsonSerializer.Deserialize<PaginatedResponse<ConsumptionRecord>>(json2, jsonOpts);
    Console.WriteLine($"Total: {result?.Total}");
    Console.WriteLine($"Items count: {result?.Items?.Count}");
    if (result?.Items?.Count > 0)
    {
        var r = result.Items[0];
        Console.WriteLine($"  Type: {r.Type}");
        Console.WriteLine($"  Amount: {r.Amount}");
        Console.WriteLine($"  BalanceAfter: {r.BalanceAfter}");
        Console.WriteLine($"  Description: {r.Description}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Test 3: No naming policy (raw) ===");
var result3 = JsonSerializer.Deserialize<OcrTaskListResult>(json);
Console.WriteLine($"No-policy Total: {result3?.Total}, Items: {result3?.Items?.Count}");
