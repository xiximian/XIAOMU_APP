namespace Xiaomuocr.Core.Services;

/// <summary>笔记持久化服务：读写 notes.md</summary>
public class NoteService : INoteService
{
    public async Task<string> LoadAsync(string jsonDir)
    {
        var path = Path.Combine(jsonDir, "notes.md");
        if (!File.Exists(path))
            return "";
        return await File.ReadAllTextAsync(path);
    }

    public async Task SaveAsync(string jsonDir, string content)
    {
        var path = Path.Combine(jsonDir, "notes.md");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content);
    }
}
