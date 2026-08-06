using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// 增量同步后端抽象：本地文件夹与 Cloudflare R2 共用同一套协议
    /// </summary>
    public interface ISyncBackend
    {
        string DisplayName { get; }

        /// <summary>上传/下载时是否保留文件原始修改时间（文件夹=是，R2=否）</summary>
        bool PreservesLocalTimestamp { get; }

        /// <summary>列出远端全部文件（不含回收站内容）</summary>
        Task<IReadOnlyList<SyncFileEntry>> ListAsync(CancellationToken ct);

        /// <summary>获取单个远端文件的完整元数据（含哈希，可能比 List 慢）</summary>
        Task<SyncFileEntry?> GetEntryAsync(string relativePath, CancellationToken ct);

        /// <summary>上传本地文件到远端，返回远端 ETag 与对象时间</summary>
        Task<SyncUploadResult> UploadAsync(string relativePath, string localFilePath, string sha256, string? md5, CancellationToken ct);

        /// <summary>把远端文件下载到本地目标路径（目标文件尚不存在）</summary>
        Task DownloadAsync(string relativePath, string destinationFilePath, CancellationToken ct);

        /// <summary>删除远端文件（实现层负责先移入回收站再删除）</summary>
        Task DeleteAsync(string relativePath, CancellationToken ct);

        /// <summary>测试连接/可写性，成功返回 null，失败返回错误信息</summary>
        Task<string?> TestAsync(CancellationToken ct);
    }
}
