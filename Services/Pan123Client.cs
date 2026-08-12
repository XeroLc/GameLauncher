using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// 123 云盘开放平台 API 客户端。
    /// 接口域名 https://open-api.123pan.com
    /// 认证方式：POST /api/v1/access_token 用 clientID + clientSecret 直接换取 access_token
    /// </summary>
    public class Pan123Client
    {
        public const string ApiBaseUrl = "https://open-api.123pan.com";

        private const string PlatformHeader = "open_platform";
        private const int MaxRetries = 3;
        private static readonly TimeSpan TokenEarlyRefresh = TimeSpan.FromMinutes(5);

        private static readonly HttpClient _http = CreateHttpClient();
        private readonly UserSettings _settings;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        public Pan123Client(UserSettings settings)
        {
            _settings = settings;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(120)
            };
            client.DefaultRequestHeaders.Add("Platform", PlatformHeader);
            return client;
        }

        // ---------- 认证 ----------

        /// <summary>用 clientID + clientSecret 获取 access_token，并保存到设置</summary>
        public async Task<(bool success, string? error)> FetchAccessTokenAsync()
        {
            try
            {
                var json = await PostRawJsonAsync("/api/v1/access_token",
                    new Dictionary<string, object>
                    {
                        ["clientID"] = _settings.Pan123ClientId,
                        ["clientSecret"] = SecretProtector.Decrypt(_settings.Pan123ClientSecret)
                    });

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code) && code != 0)
                    return (false, $"获取令牌失败：{GetMessage(root)} (code={code})");

                if (root.TryGetProperty("data", out var dataEl))
                {
                    var accessToken = dataEl.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
                    var expiredAtStr = dataEl.TryGetProperty("expiredAt", out var exp) ? exp.GetString() : null;

                    if (string.IsNullOrEmpty(accessToken))
                        return (false, "获取令牌失败：响应缺少 accessToken");

                    _settings.Pan123AccessToken = SecretProtector.Encrypt(accessToken);
                    _settings.Pan123TokenExpiry = TryParseExpiredAt(expiredAtStr);
                    _settings.Save();
                    return (true, null);
                }
                return (false, $"获取令牌失败：{json}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pan123] 获取 token 失败: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private static string? GetMessage(JsonElement root)
        {
            return root.TryGetProperty("message", out var m) ? m.GetString() : null;
        }

        private static DateTime? TryParseExpiredAt(string? expiredAtStr)
        {
            if (DateTimeOffset.TryParse(expiredAtStr, out var dto))
                return dto.UtcDateTime;
            return null;
        }

        /// <summary>测试连接：调用用户信息接口</summary>
        public async Task<(bool success, string? error)> TestConnectionAsync()
        {
            try
            {
                var token = await GetValidAccessTokenAsync();
                if (string.IsNullOrEmpty(token))
                    return (false, "未获取到有效的访问令牌，请先点击「获取访问令牌」");

                using var request = WithAuth(HttpMethod.Get, "/api/v1/user/info", token);
                var response = await _http.SendAsync(request);
                var result = await ParseResponseAsync(response);
                if (result.code == 0)
                    return (true, null);

                return (false, $"连接失败：{result.message} (code={result.code})");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>返回有效 access_token（自动解密），过期前自动用 clientID/clientSecret 重新获取</summary>
        public async Task<string> GetValidAccessTokenAsync()
        {
            var stored = SecretProtector.Decrypt(_settings.Pan123AccessToken);
            if (string.IsNullOrEmpty(stored))
                return string.Empty;

            bool needsRefresh = !_settings.Pan123TokenExpiry.HasValue ||
                                _settings.Pan123TokenExpiry.Value - DateTime.UtcNow < TokenEarlyRefresh;
            if (!needsRefresh)
                return stored;

            await _tokenLock.WaitAsync();
            try
            {
                // 双重检查：可能在等待锁期间已被其他请求刷新
                if (_settings.Pan123TokenExpiry.HasValue &&
                    _settings.Pan123TokenExpiry.Value - DateTime.UtcNow >= TokenEarlyRefresh)
                    return SecretProtector.Decrypt(_settings.Pan123AccessToken);

                var (success, error) = await FetchAccessTokenAsync();
                if (success)
                    return SecretProtector.Decrypt(_settings.Pan123AccessToken);
                Debug.WriteLine($"[Pan123] 自动续期失败: {error}");
                return stored; // 刷新失败时尝试用旧 token
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private async Task<string> PostRawJsonAsync(string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // HttpRequestMessage 不可复用，每次重试都新建
                    using var request = new HttpRequestMessage(HttpMethod.Post, path)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    using var response = await _http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                        return body;

                    // 限流等待后重试
                    if ((int)response.StatusCode == 429 && attempt < MaxRetries)
                    {
                        await Task.Delay(attempt * 1000);
                        continue;
                    }
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
                }
                catch (HttpRequestException)
                {
                    if (attempt >= MaxRetries)
                        throw;
                    await Task.Delay(attempt * 500);
                }
            }
            throw new InvalidOperationException("请求失败");
        }

        // ---------- 文件操作 ----------

        /// <summary>创建文件（预上传）。reuse=true 表示秒传成功</summary>
        public async Task<Pan123CreateFileResult> CreateFileAsync(string filename, string md5, long size, long parentFileID, int duplicate = 2)
        {
            var payload = new Dictionary<string, object>
            {
                ["parentFileID"] = parentFileID,
                ["filename"] = filename,
                ["etag"] = md5,
                ["size"] = size,
                ["duplicate"] = duplicate
            };
            var result = await PostAsync<Pan123CreateFileResult>("/upload/v2/file/create", payload);
            return result ?? throw new InvalidOperationException("创建文件响应为空");
        }

        /// <summary>获取可用的上传域名（多个任选其一）</summary>
        public async Task<List<string>> GetUploadDomainsAsync()
        {
            var result = await GetAsync<List<string>>("/upload/v2/file/domain");
            return result ?? new List<string>();
        }

        /// <summary>上传一个分片（multipart/form-data，走上传域名服务器）</summary>
        public async Task UploadSliceAsync(string uploadServer, string preuploadID, int sliceNo, string sliceMD5, byte[] sliceData)
        {
            var url = $"{uploadServer.TrimEnd('/')}/upload/v2/file/slice";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(preuploadID), "preuploadID");
            form.Add(new StringContent(sliceNo.ToString()), "sliceNo");
            form.Add(new StringContent(sliceMD5), "sliceMD5");
            var byteContent = new ByteArrayContent(sliceData);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(byteContent, "slice", "slice.bin");

            var token = await GetValidAccessTokenAsync();

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // HttpRequestMessage 不可复用，每次重试都新建（form 内容可重复使用）
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = form
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await _http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonSerializer.Deserialize<Pan123ApiEnvelope>(body);
                        if (result != null && result.code == 0)
                            return;
                        throw new HttpRequestException($"分片上传失败: {result?.message} (code={result?.code})");
                    }
                    if ((int)response.StatusCode == 429 && attempt < MaxRetries)
                    {
                        await Task.Delay(attempt * 1000);
                        continue;
                    }
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
                }
                catch (HttpRequestException)
                {
                    if (attempt >= MaxRetries)
                        throw;
                    await Task.Delay(attempt * 500);
                }
            }
        }

        /// <summary>
        /// 上传完成确认。返回 null 表示仍在校验中（code=20103），调用方应隔 1 秒重试。
        /// </summary>
        public async Task<Pan123UploadCompleteResult?> UploadCompleteAsync(string preuploadID)
        {
            var token = await GetValidAccessTokenAsync();
            var payload = new Dictionary<string, object> { ["preuploadID"] = preuploadID };
            var json = JsonSerializer.Serialize(payload);

            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/upload/v2/file/upload_complete")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            });
            var result = JsonSerializer.Deserialize<Pan123ApiEnvelope>(response);
            if (result == null)
                return null;

            // code=20103 文件正在校验中：异步未完成，返回 null 让调用方轮询
            if (result.code == 20103)
                return null;

            if (result.code != 0)
                throw new InvalidOperationException($"API 错误: {result.message} (code={result.code})");

            if (result.data == null)
                return null;
            return result.data.Value.Deserialize<Pan123UploadCompleteResult>();
        }

        /// <summary>获取下载直链信息</summary>
        public async Task<Pan123DownloadInfo> GetDownloadInfoAsync(long fileId)
        {
            var result = await GetAsync<Pan123DownloadInfo>($"/api/v1/file/download_info?fileId={fileId}");
            return result ?? throw new InvalidOperationException("获取下载地址响应为空");
        }

        /// <summary>创建目录，返回目录 ID</summary>
        public async Task<long> CreateFolderAsync(string name, long parentId)
        {
            var result = await PostAsync<Pan123CreateFolderResult>("/upload/v1/file/mkdir",
                new Dictionary<string, object> { ["name"] = name, ["parentID"] = parentId });
            if (result == null)
                return 0;
            return result.DirID != 0 ? result.DirID : result.FileIDAlt;
        }

        /// <summary>列出父目录下的文件/文件夹（limit 最大 100，按 lastFileId 游标分页）</summary>
        public async Task<List<Pan123FileItem>> ListFilesAsync(long parentFileID)
        {
            var items = new List<Pan123FileItem>();
            long lastFileId = 0;
            while (true)
            {
                var result = await GetAsync<Pan123FileListResult>(
                    $"/api/v2/file/list?parentFileID={parentFileID}&limit=100&lastFileId={lastFileId}");
                var page = result?.GetFileList() ?? new List<Pan123FileItem>();
                items.AddRange(page);
                var newLast = result?.GetLastFileId() ?? -1;
                if (newLast <= lastFileId || page.Count < 100)
                    break;
                lastFileId = newLast;
            }
            return items;
        }

        /// <summary>删除云端文件</summary>
        public async Task DeleteFilesAsync(IEnumerable<long> fileIds)
        {
            var idList = fileIds as long[] ?? System.Linq.Enumerable.ToArray(fileIds);
            if (idList.Length == 0) return;
            await PostAsync<Pan123ApiEnvelope>("/api/v1/file/delete",
                new Dictionary<string, object> { ["fileIDs"] = idList });
        }

        // ---------- 内部 ----------

        private static HttpRequestMessage WithAuth(HttpMethod method, string path, string token)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        private async Task<T?> PostAsync<T>(string path, object payload) where T : class
        {
            var token = await GetValidAccessTokenAsync();
            var json = JsonSerializer.Serialize(payload);

            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            });
            return DeserializeData<T>(response);
        }

        private async Task<T?> GetAsync<T>(string path) where T : class
        {
            var token = await GetValidAccessTokenAsync();

            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            });
            return DeserializeData<T>(response);
        }

        private async Task<string> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory)
        {
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // HttpRequestMessage 不可复用，每次重试都新建
                    using var request = requestFactory();
                    using var response = await _http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                        return body;
                    if ((int)response.StatusCode == 429 && attempt < MaxRetries)
                    {
                        await Task.Delay(attempt * 1000);
                        continue;
                    }
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
                }
                catch (HttpRequestException)
                {
                    if (attempt >= MaxRetries)
                        throw;
                    await Task.Delay(attempt * 500);
                }
            }
            throw new InvalidOperationException("请求失败");
        }

        private static T? DeserializeData<T>(string json) where T : class
        {
            var result = JsonSerializer.Deserialize<Pan123ApiEnvelope>(json);
            if (result == null || result.code != 0)
                throw new InvalidOperationException($"API 错误: {result?.message} (code={result?.code})");
            if (result.data == null)
                return null;
            var data = result.data.Value;
            if (data.ValueKind == JsonValueKind.Null)
                return null;
            return data.Deserialize<T>();
        }

        private static async Task<(int code, string message)> ParseResponseAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<Pan123ApiEnvelope>(body);
                return (result?.code ?? -1, result?.message ?? string.Empty);
            }
            return ((int)response.StatusCode, body);
        }
    }

    public class Pan123ApiEnvelope
    {
        public int code { get; set; }
        public string? message { get; set; }
        public JsonElement? data { get; set; }
    }

    public class Pan123CreateFileResult
    {
        public long fileID { get; set; }
        public bool reuse { get; set; }
        public string? preuploadID { get; set; }
        public long sliceSize { get; set; }
        public List<string>? servers { get; set; }
    }

    public class Pan123UploadCompleteResult
    {
        public bool completed { get; set; }
        public long fileID { get; set; }
        public bool async { get; set; }
    }

    public class Pan123DownloadInfo
    {
        public string? downloadUrl { get; set; }
    }

    public class Pan123CreateFolderResult
    {
        /// <summary>123 云盘 mkdir 接口返回的目录 ID 字段名是 dirID</summary>
        [System.Text.Json.Serialization.JsonPropertyName("dirID")]
        public long DirID { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fileID")]
        public long FileIDAlt { get; set; }
    }

    public class Pan123FileListResult
    {
        public long LastFileId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("lastFileId")]
        public long LastFileIdAlt { get; set; }
        public List<Pan123FileItem>? FileList { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fileList")]
        public List<Pan123FileItem>? FileListAlt { get; set; }

        public long GetLastFileId() => LastFileId != 0 ? LastFileId : LastFileIdAlt;
        public List<Pan123FileItem>? GetFileList() => FileList ?? FileListAlt;
    }

    public class Pan123FileItem
    {
        public long FileId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fileId")]
        public long FileIdAlt { get; set; }
        public string? FileName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string? FileNameAlt { get; set; }
        /// <summary>0=文件, 1=文件夹</summary>
        public int Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public int TypeAlt { get; set; }

        public long GetFileId() => FileId != 0 ? FileId : FileIdAlt;
        public string GetFileName() => FileName ?? FileNameAlt ?? string.Empty;
        public int GetType() => Type != 0 ? Type : TypeAlt;
    }
}
