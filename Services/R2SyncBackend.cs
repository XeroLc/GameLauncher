using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime.Internal.Util;
using Amazon.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// Cloudflare R2 后端（S3 兼容 API）。
    /// 上传时附带 sha256 / md5 / 原始修改时间元数据，用于增量比对；
    /// 删除对象前会先复制到 _trash/ 前缀下，避免误删后无法恢复。
    /// </summary>
    public sealed class R2SyncBackend : ISyncBackend
    {
        private const string TrashPrefix = "_trash/";
        private const string MetaSha256 = "x-amz-meta-sha256";
        private const string MetaMd5 = "x-amz-meta-md5";
        private const string MetaLastWrite = "x-amz-meta-last-write-utc";

        private readonly AmazonS3Client _client;
        private readonly string _bucket;
        private readonly string _endpoint;

        public R2SyncBackend(string endpoint, string bucket, string accessKeyId, string secretAccessKey)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("R2 Endpoint 不能为空", nameof(endpoint));
            if (string.IsNullOrWhiteSpace(bucket))
                throw new ArgumentException("R2 Bucket 不能为空", nameof(bucket));
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
                throw new ArgumentException("R2 访问密钥不能为空", nameof(accessKeyId));

            _endpoint = NormalizeEndpoint(endpoint);
            _bucket = bucket.Trim().Trim('/');
            var config = new AmazonS3Config
            {
                ServiceURL = _endpoint,
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
                // R2 不支持 AWS SDK 默认的流式尾部校验和签名
                // (STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER)，需要关闭分块编码与自动校验和
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                MaxConnectionsPerServer = 128,
                Timeout = TimeSpan.FromSeconds(30),
                MaxErrorRetry = 2
            };
            _client = new AmazonS3Client(accessKeyId, secretAccessKey, config);
        }

        public string DisplayName => $"Cloudflare R2 ({_bucket} @ {_endpoint})";
        public bool PreservesLocalTimestamp => false;

        public async Task<IReadOnlyList<SyncFileEntry>> ListAsync(CancellationToken ct)
        {
            var entries = new List<SyncFileEntry>();
            string? token = null;
            do
            {
                var request = new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    MaxKeys = 1000,
                    ContinuationToken = token
                };
                var response = await _client.ListObjectsV2Async(request, ct);
                foreach (var obj in response.S3Objects)
                {
                    if (obj.Key.StartsWith(TrashPrefix, StringComparison.Ordinal))
                        continue;
                    entries.Add(new SyncFileEntry
                    {
                        RelativePath = SyncPath.Normalize(obj.Key),
                        Size = obj.Size,
                        TimestampUtc = obj.LastModified.ToUniversalTime(),
                        Etag = obj.ETag?.Trim('"')
                    });
                }
                token = response.IsTruncated ? response.NextContinuationToken : null;
            } while (!string.IsNullOrEmpty(token));
            return entries;
        }

        public async Task<SyncFileEntry?> GetEntryAsync(string relativePath, CancellationToken ct)
        {
            try
            {
                var request = new GetObjectMetadataRequest { BucketName = _bucket, Key = relativePath };
                var response = await _client.GetObjectMetadataAsync(request, ct);

                DateTime? fileLastWrite = null;
                if (TryParseUtc(GetMetadataValue(response.Metadata, MetaLastWrite), out var parsed))
                {
                    fileLastWrite = parsed;
                }

                return new SyncFileEntry
                {
                    RelativePath = relativePath,
                    Size = response.ContentLength,
                    TimestampUtc = response.LastModified.ToUniversalTime(),
                    FileLastWriteUtc = fileLastWrite,
                    Sha256 = GetMetadataValue(response.Metadata, MetaSha256),
                    Md5 = GetMetadataValue(response.Metadata, MetaMd5),
                    Etag = response.ETag?.Trim('"')
                };
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<SyncUploadResult> UploadAsync(string relativePath, string localFilePath, string sha256, string? md5, CancellationToken ct)
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = relativePath,
                FilePath = localFilePath,
                ContentType = GetContentType(relativePath),
                // R2 不支持 aws-chunked 流式签名，改为普通 PUT（UNSIGNED-PAYLOAD）
                UseChunkEncoding = false,
                DisablePayloadSigning = true
            };
            request.Metadata[MetaSha256] = sha256;
            request.Metadata[MetaMd5] = md5 ?? string.Empty;
            request.Metadata[MetaLastWrite] = File.GetLastWriteTimeUtc(localFilePath)
                .ToString("o", CultureInfo.InvariantCulture);

            // ETag 由上传响应直接返回（单次 PUT 的 ETag 与 ListObjectsV2 一致），
            // 不再额外 HEAD，减少一半请求数
            var response = await _client.PutObjectAsync(request, ct);
            return new SyncUploadResult(response.ETag?.Trim('"'), null);
        }

        public async Task DownloadAsync(string relativePath, string destinationFilePath, CancellationToken ct)
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = relativePath
            }, ct);

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = destinationFilePath + $".glsync-tmp-{Guid.NewGuid():N}";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.ResponseStream.CopyToAsync(fs, ct);
                }

                if (TryParseUtc(GetMetadataValue(response.Metadata, MetaLastWrite), out var parsed))
                {
                    File.SetLastWriteTimeUtc(tmp, parsed);
                }
                File.Move(tmp, destinationFilePath, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        public async Task DeleteAsync(string relativePath, CancellationToken ct)
        {
            var trashKey = TrashPrefix + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "/" + relativePath;
            try
            {
                var copyRequest = new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = relativePath,
                    DestinationBucket = _bucket,
                    DestinationKey = trashKey
                };
                await _client.CopyObjectAsync(copyRequest, ct);
            }
            catch (Exception ex)
            {
                // 复制到回收站失败时保留原对象，避免数据丢失
                throw new InvalidOperationException($"无法将对象移入回收站 {relativePath}: {ex.Message}", ex);
            }

            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = relativePath
            }, ct);
        }

        public async Task<string?> TestAsync(CancellationToken ct)
        {
            try
            {
                var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    MaxKeys = 1
                }, ct);
                _ = response.S3Objects;
                return null;
            }
            catch (AmazonS3Exception ex)
            {
                return $"R2 连接失败: {ex.Message} (StatusCode={ex.StatusCode})";
            }
            catch (Exception ex)
            {
                return $"R2 连接失败: {ex.Message}";
            }
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            var e = endpoint.Trim().TrimEnd('/');
            if (!e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !e.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e = "https://" + e;
            }
            return e;
        }

        private static string GetContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".json" => "application/json",
                ".db" => "application/octet-stream",
                ".txt" => "text/plain",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };
        }

        private static string? GetMetadataValue(MetadataCollection metadata, string key)
        {
            foreach (var k in metadata.Keys)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return metadata[k];
            }
            return null;
        }

        private static bool TryParseUtc(string? value, out DateTime result)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                // RoundtripKind 与 AdjustToUniversal 互斥；元数据使用 "o" 格式（含 Z），
                // 用 RoundtripKind 保留 UTC 类型即可
                DateTimeStyles.RoundtripKind,
                out result);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
