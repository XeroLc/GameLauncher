using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// 同步后端类型
    /// </summary>
    public enum SyncBackendType
    {
        Folder,
        CloudflareR2
    }

    /// <summary>
    /// 同步方向
    /// </summary>
    public enum SyncDirection
    {
        /// <summary>双向同步（默认）</summary>
        TwoWay,
        /// <summary>仅上传本地变更，远程多余文件不动</summary>
        UploadOnly,
        /// <summary>仅下载远程变更，本地多余文件不动</summary>
        DownloadOnly
    }

    public enum SyncActionKind
    {
        Upload,
        Download,
        DeleteLocal,
        DeleteRemote,
        Skip,
        ConflictKeepLocal
    }

    /// <summary>
    /// 远端/本地单个文件快照条目
    /// </summary>
    public sealed class SyncFileEntry
    {
        public string RelativePath { get; init; } = string.Empty;
        public long Size { get; init; }
        /// <summary>用于快速判定的时间戳（文件夹=文件修改时间，R2=对象 LastModified）</summary>
        public DateTime TimestampUtc { get; init; }
        public string? Sha256 { get; init; }
        public string? Md5 { get; init; }
        public string? Etag { get; init; }
        /// <summary>R2 上保存的原始文件修改时间（来自自定义元数据）</summary>
        public DateTime? FileLastWriteUtc { get; init; }
    }

    /// <summary>
    /// 上传结果：ETag 与远端对象时间（用于清单身份比对）
    /// </summary>
    public sealed record SyncUploadResult(string? Etag, DateTime? LastModifiedUtc);

    /// <summary>
    /// 本地扫描到的文件
    /// </summary>
    public sealed class LocalFileInfo
    {
        public string RelativePath { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTime LastWriteUtc { get; init; }
    }

    /// <summary>
    /// 同步清单条目：记录上一次同步时两侧的状态，用于增量判定
    /// </summary>
    public sealed class SyncManifestEntry
    {
        public string LocalSha256 { get; set; } = string.Empty;
        public long LocalSize { get; set; }
        public DateTime LocalLastWriteUtc { get; set; }
        public string? RemoteSha256 { get; set; }
        public string? RemoteEtag { get; set; }
        public long RemoteSize { get; set; }
        public DateTime RemoteIdentityUtc { get; set; }
        public DateTime LastSyncedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class SyncManifest
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public Dictionary<string, SyncManifestEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一次同步计划中的单个动作
    /// </summary>
    public sealed class SyncAction
    {
        public string RelativePath { get; init; } = string.Empty;
        public SyncActionKind Kind { get; init; }
        public long Size { get; init; }
        public string? Reason { get; init; }
        [JsonIgnore]
        public string? Error { get; set; }
    }

    public sealed class SyncProgress
    {
        public string CurrentFile { get; init; } = string.Empty;
        public int CompletedActions { get; init; }
        public int TotalActions { get; init; }
        public long TransferredBytes { get; init; }
    }

    public sealed class SyncResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;
        public int UploadedCount;
        public int DownloadedCount;
        public int DeletedLocalCount;
        public int DeletedRemoteCount;
        public int SkippedCount;
        public int ConflictCount;
        public int FailedCount;
        public long UploadedBytes;
        public long DownloadedBytes;
        public int TotalActions;
        public int ProcessedActions;
        public List<SyncAction> Actions { get; } = new();

        public string Summary
        {
            get
            {
                if (!Success) return $"同步失败: {ErrorMessage}";
                return $"上传 {UploadedCount} · 下载 {DownloadedCount} · 删除 {DeletedLocalCount + DeletedRemoteCount} · 跳过 {SkippedCount}" +
                       (ConflictCount > 0 ? $" · 冲突 {ConflictCount}" : string.Empty) +
                       (FailedCount > 0 ? $" · 失败 {FailedCount}" : string.Empty);
            }
        }
    }

    public sealed class SyncHistoryEntry
    {
        public DateTime TimeUtc { get; set; }
        public bool Success { get; set; }
        public string Backend { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public int Uploaded { get; set; }
        public int Downloaded { get; set; }
        public int Deleted { get; set; }
        public int Skipped { get; set; }
        public int Conflicts { get; set; }
        public int Failed { get; set; }
        public string? Error { get; set; }

        public string Summary
        {
            get
            {
                if (!Success) return $"同步失败: {Error}";
                return $"上传 {Uploaded} · 下载 {Downloaded} · 删除 {Deleted} · 跳过 {Skipped}" +
                       (Conflicts > 0 ? $" · 冲突 {Conflicts}" : string.Empty) +
                       (Failed > 0 ? $" · 失败 {Failed}" : string.Empty);
            }
        }
    }

    public sealed class SyncHistoryFile
    {
        public List<SyncHistoryEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// 文件哈希辅助方法
    /// </summary>
    public static class SyncFileHash
    {
        public static async Task<(string Sha256, string Md5)> ComputeSha256AndMd5Async(string filePath, CancellationToken ct = default)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
            return (ToHex(sha.GetHashAndReset()), ToHex(md5.GetHashAndReset()));
        }

        public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
            return ToHex(sha.GetHashAndReset());
        }

        private static string ToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    /// <summary>
    /// 相对路径规范化与安全校验
    /// </summary>
    public static class SyncPath
    {
        public static string Normalize(string relativePath)
        {
            var p = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(seg => seg == ".." || seg == "."))
                throw new ArgumentException($"非法相对路径: {relativePath}");
            return string.Join("/", parts);
        }

        public static bool IsInside(string path, string root)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
    }
}
