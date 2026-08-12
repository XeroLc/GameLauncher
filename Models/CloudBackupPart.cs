using System;

namespace GameLauncher.Models
{
    /// <summary>云备份的单个分卷信息（123 云盘文件）</summary>
    public class CloudBackupPart
    {
        /// <summary>123 云盘文件 ID</summary>
        public long FileId { get; set; }

        /// <summary>云端文件名（如 GID123456789.vault / GID123456789.part2.vault）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        public long Size { get; set; }

        /// <summary>文件 MD5（十六进制小写）</summary>
        public string Md5 { get; set; } = string.Empty;

        /// <summary>卷序号（从 1 开始）</summary>
        public int PartIndex { get; set; }

        public CloudBackupPart() { }

        public CloudBackupPart(long fileId, string fileName, long size, string md5, int partIndex)
        {
            FileId = fileId;
            FileName = fileName;
            Size = size;
            Md5 = md5;
            PartIndex = partIndex;
        }
    }
}
