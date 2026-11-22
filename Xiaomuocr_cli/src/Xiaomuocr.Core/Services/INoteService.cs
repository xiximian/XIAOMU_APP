namespace Xiaomuocr.Core.Services;

/// <summary>笔记持久化服务：读写文档目录下的 notes.md</summary>
public interface INoteService
{
    /// <summary>从 {jsonDir}/notes.md 加载笔记。文件不存在返回空字符串。</summary>
    Task<string> LoadAsync(string jsonDir);

    /// <summary>将笔记内容写入 {jsonDir}/notes.md。</summary>
    Task SaveAsync(string jsonDir, string content);
}
