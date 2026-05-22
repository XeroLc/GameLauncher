using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GameLauncher.Services
{
    public class UpdateCheckerConfig
    {
        public string CurrentVersion { get; set; } = "3.2";
        public int MaxConsecutiveFailures { get; set; } = 3;
        public string GitHubApiUrl { get; set; } = "https://api.github.com/repos/XeroLc/GameLauncher/releases/latest";
        public string GitHubAtomUrl { get; set; } = "https://github.com/XeroLc/GameLauncher/releases.atom";
        public string ReleasesPageUrl { get; set; } = "https://github.com/XeroLc/GameLauncher/releases";
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    }

    public class UpdateCheckerService
    {
        private readonly HttpClient _httpClient;
        private readonly UpdateCheckerConfig _config;
        private int _consecutiveFailures;
        private bool _hasCheckedAutomatically;
        private readonly object _stateLock = new object();

        public string CurrentVersion => _config.CurrentVersion;

        public UpdateCheckerService(UpdateCheckerConfig? config = null)
        {
            _config = config ?? new UpdateCheckerConfig();
            _consecutiveFailures = 0;
            _hasCheckedAutomatically = false;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GameLauncher");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            _httpClient.Timeout = _config.RequestTimeout;
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync(bool forceCheck = false)
        {
            lock (_stateLock)
            {
                if (!forceCheck && _hasCheckedAutomatically)
                {
                    System.Diagnostics.Debug.WriteLine("[UpdateCheck] 自动检查已在本次启动时执行过，跳过");
                    return null;
                }

                if (!forceCheck && _consecutiveFailures >= _config.MaxConsecutiveFailures)
                {
                    System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 连续失败次数已达上限 ({_config.MaxConsecutiveFailures})，跳过检查");
                    return null;
                }
            }

            UpdateInfo? result = await TryApiCheckAsync();
            if (result != null)
            {
                lock (_stateLock)
                {
                    _consecutiveFailures = 0;
                    if (!forceCheck) _hasCheckedAutomatically = true;
                }
                return result;
            }

            System.Diagnostics.Debug.WriteLine("[UpdateCheck] API 失败，尝试 Atom Feed...");
            result = await TryAtomFeedAsync();
            if (result != null)
            {
                lock (_stateLock)
                {
                    _consecutiveFailures = 0;
                    if (!forceCheck) _hasCheckedAutomatically = true;
                }
                return result;
            }

            System.Diagnostics.Debug.WriteLine("[UpdateCheck] Atom Feed 失败，尝试 HTML 页面...");
            result = await TryPageScrapeAsync();
            if (result != null)
            {
                lock (_stateLock)
                {
                    _consecutiveFailures = 0;
                    if (!forceCheck) _hasCheckedAutomatically = true;
                }
                return result;
            }

            lock (_stateLock)
            {
                _consecutiveFailures++;
                if (!forceCheck) _hasCheckedAutomatically = true;
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 所有检查方式均失败，连续失败次数: {_consecutiveFailures}/{_config.MaxConsecutiveFailures}");
            }
            return null;
        }

        public void ResetCheckState()
        {
            lock (_stateLock)
            {
                _consecutiveFailures = 0;
                _hasCheckedAutomatically = false;
            }
        }

        private async Task<UpdateInfo?> TryApiCheckAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 尝试 API: {_config.GitHubApiUrl}");

                var response = await _httpClient.GetAsync(_config.GitHubApiUrl);
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] API HTTP 状态码: {(int)response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubRelease>(json);

                if (release == null)
                    return null;

                var latestVersion = ExtractVersionFromReleaseName(release.Name) ?? ExtractVersionFromReleaseName(release.TagName);
                if (latestVersion == null)
                    return null;

                if (!IsNewerVersion(latestVersion, _config.CurrentVersion))
                    return null;

                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] API 发现新版本: {latestVersion}");
                return new UpdateInfo
                {
                    LatestVersion = latestVersion,
                    CurrentVersion = _config.CurrentVersion,
                    ReleaseNotes = release.Body ?? "暂无更新说明",
                    DownloadUrl = release.HtmlUrl ?? _config.ReleasesPageUrl,
                    PublishedAt = release.PublishedAt
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] API 异常: {ex.Message}");
                return null;
            }
        }

        private async Task<UpdateInfo?> TryAtomFeedAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 尝试 Atom Feed: {_config.GitHubAtomUrl}");

                var response = await _httpClient.GetAsync(_config.GitHubAtomUrl);
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] Atom HTTP 状态码: {(int)response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                    return null;

                var xml = await response.Content.ReadAsStringAsync();
                var doc = XDocument.Parse(xml);
                XNamespace ns = "http://www.w3.org/2005/Atom";

                var firstEntry = doc.Descendants(ns + "entry").FirstOrDefault();
                if (firstEntry == null)
                    return null;

                var title = firstEntry.Element(ns + "title")?.Value ?? "";
                var link = firstEntry.Elements(ns + "link")
                    .FirstOrDefault(e => e.Attribute("rel")?.Value == "alternate")
                    ?.Attribute("href")?.Value ?? _config.ReleasesPageUrl;
                var published = firstEntry.Element(ns + "published")?.Value;
                var content = firstEntry.Element(ns + "content")?.Value ?? "";

                var latestVersion = ExtractVersionFromReleaseName(title);
                if (latestVersion == null)
                    return null;

                if (!IsNewerVersion(latestVersion, _config.CurrentVersion))
                    return null;

                DateTime? pubDate = null;
                if (DateTime.TryParse(published, out var dt))
                    pubDate = dt;

                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] Atom 发现新版本: {latestVersion}");
                return new UpdateInfo
                {
                    LatestVersion = latestVersion,
                    CurrentVersion = _config.CurrentVersion,
                    ReleaseNotes = StripHtml(content),
                    DownloadUrl = link,
                    PublishedAt = pubDate
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] Atom 异常: {ex.Message}");
                return null;
            }
        }

        private async Task<UpdateInfo?> TryPageScrapeAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 尝试页面抓取: {_config.ReleasesPageUrl}");

                var response = await _httpClient.GetAsync(_config.ReleasesPageUrl);
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 页面 HTTP 状态码: {(int)response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                    return null;

                var html = await response.Content.ReadAsStringAsync();

                var latestVersion = ExtractVersionFromHtml(html);
                if (latestVersion == null)
                    return null;

                if (!IsNewerVersion(latestVersion, _config.CurrentVersion))
                    return null;

                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 页面抓取发现新版本: {latestVersion}");
                return new UpdateInfo
                {
                    LatestVersion = latestVersion,
                    CurrentVersion = _config.CurrentVersion,
                    ReleaseNotes = "请访问 GitHub Releases 查看更新详情",
                    DownloadUrl = _config.ReleasesPageUrl,
                    PublishedAt = null
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateCheck] 页面抓取异常: {ex.Message}");
                return null;
            }
        }

        private string? ExtractVersionFromReleaseName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var trimmed = name.TrimStart('v', 'V').Trim();
            var match = Regex.Match(trimmed, @"^(\d+\.\d+(?:\.\d+)?)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        private string? ExtractVersionFromHtml(string html)
        {
            var match = Regex.Match(html, @"<span\s+class=""[^""]*text-bold[^""]*"">\s*v?([\d.]+)\s*</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value;

            match = Regex.Match(html, @"<a\s+[^>]*href=""/XeroLc/GameLauncher/releases/tag/[^""]*"">\s*v?([\d.]+)\s*</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value;

            match = Regex.Match(html, @"release-header[^>]*>.*?v?(\d+\.\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return "暂无更新说明";
            var stripped = Regex.Replace(html, "<[^>]+>", "");
            stripped = System.Net.WebUtility.HtmlDecode(stripped);
            stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
            return stripped.Trim();
        }

        private bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                var latestParts = latestVersion.Split('.');
                var currentParts = currentVersion.Split('.');

                int maxLength = Math.Max(latestParts.Length, currentParts.Length);

                for (int i = 0; i < maxLength; i++)
                {
                    int latest = i < latestParts.Length ? int.Parse(latestParts[i]) : 0;
                    int current = i < currentParts.Length ? int.Parse(currentParts[i]) : 0;

                    if (latest > current) return true;
                    if (latest < current) return false;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public class UpdateInfo
    {
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
    }

    public class GitHubRelease
    {
        public string TagName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
    }
}
