using System.Text.Json;
using Xiaomuocr.Core.Models;

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

var json = "{\"items\":[{\"id\":1,\"task_uuid\":\"b919de38\",\"image_url\":\"http://127.0.0.1:8000/static/test.jpg\",\"page_count\":1,\"status\":\"failed\",\"cost\":1.0,\"error_message\":\"test\",\"created_at\":\"2026-07-18T14:17:59\",\"completed_at\":\"2026-07-18T14:18:17\"}],\"total\":1,\"page\":1,\"page_size\":20,\"total_pages\":1}";

Console.WriteLine("=== OcrTaskListResult ===");
try {
    var r = JsonSerializer.Deserialize<OcrTaskListResult>(json, jsonOpts);
    Console.WriteLine("Total=" + r?.Total + ", Items=" + r?.Items?.Count);
    if (r?.Items?.Count > 0)
        Console.WriteLine("  First: " + r.Items[0].TaskUuid + " | " + r.Items[0].Status);
    else
        Console.WriteLine("  ITEMS IS EMPTY!");
} catch (Exception ex) {
    Console.WriteLine("EX: " + ex.GetType().Name + ": " + ex.Message);
    if (ex.InnerException != null)
        Console.WriteLine("  INNER: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
}

// Without naming policy
var r2 = JsonSerializer.Deserialize<OcrTaskListResult>(json);
Console.WriteLine("Without policy: Total=" + r2?.Total + ", Items=" + r2?.Items?.Count);
