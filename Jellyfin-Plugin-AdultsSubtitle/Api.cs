using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Jellyfin_Plugin_AdultsSubtitle
{
    public class Api
    {
        public static readonly ConcurrentDictionary<string, (string, string)> DownloadUrls = new();
        public static readonly Dictionary<string, string> LanguagesMaps = new()
        {
            {"eng","en"},            
            {"zho","zh-CN"},
            {"chi","zh-CN"},
            {"zh-CN","zh-CN"},
            {"zh-TW","zh-TW"},
        };
        private static readonly HtmlParser _parser = new();

        private static readonly List<string> OrderSuffix = [
            "zh-CN",
            ".zh",
            "-zh",
            "-C",
            "-c",
        ];

        public static async Task<string?> SearchDownloadUrlAsyncWithTest(HttpClient client, string language, string name, CancellationToken cancellationToken, Action<string> logger)
        {
            var designations = GetDesignations(name, logger);
            if (designations.Count == 0)
            {
                return null;
            }
            
            foreach (var designation in designations)
            {
                var downloadUrl = await SearchDownloadUrlAsyncWithTestByKey(client, language, designation, cancellationToken, logger);
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    return downloadUrl;
                }
            }
            return null;
        }

        public static async Task<string?> SearchDownloadUrlAsync(HttpClient client, string language, string url, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync($"https://www.subtitlecat.com{url}", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = _parser.ParseDocument(content);
            var element = document.All.FirstOrDefault(p => p.Id == $"download_{language}");
            if (element is IHtmlAnchorElement anchorElement)
            {
                return $"https://www.subtitlecat.com{anchorElement.Href.Replace("about://", "")}";
            }
            return null;
        }

        public static async Task<string?> SearchAsync(HttpClient client, string name, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync($"https://www.subtitlecat.com/index.php?search={name}", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = _parser.ParseDocument(content);
            var element = document.All.FirstOrDefault(p => p is IHtmlAnchorElement anchorElement && anchorElement.Href.ToLower().Contains(name.ToLower()));
            if (element is IHtmlAnchorElement anchorElement)
            {
                return anchorElement.Href.Replace("about://", "");
            }
            return null;
        }
        
        // 字幕文件检测必须>该值
        private const long MinFileSize = 1 * 1024;
        private static async Task<bool> TestContext(HttpClient client, string url, Action<string> logger)
        {
            try
            {
                // 发送HEAD请求（仅获取头信息，不下载正文，效率更高）
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                // 确保请求成功
                response.EnsureSuccessStatusCode();
                return (response.Content.Headers.ContentLength??0) > MinFileSize;
            }
            catch (Exception ex)
            {
                logger.Invoke($"检测内容大小错误 ：{ex.Message}");
            }

            return true;
        }

        private static int GetPriority(string name)
        {
            if (string.IsNullOrEmpty(name))
                return 0;

            var orderSuffixCount = OrderSuffix.Count;
            for (var i = 0; i < orderSuffixCount; i++)
            {
                if (name.Contains(OrderSuffix[i]))
                {
                    return orderSuffixCount - i;
                }
            }
            return 0;
        }

        private static List<string> GetDesignations(string name, Action<string> logger)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(name))
                return results;

            // 正则表达式模式：
            // [A-Za-z]+  匹配1个或多个字母（大小写不限）
            // -          匹配连字符“-”
            // \d+        匹配1个或多个数字
            var pattern = @"[A-Za-z]+-\d+";
            // 执行匹配（忽略大小写，但模式本身已包含大小写字母）
            var matches = Regex.Matches(name, pattern);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    results.Add(match.Value);
                }
            }
            results.Add(name);
            logger($"原始名称={name}, 提取后规则={string.Join(',', results)}");
            return results;
        }
        
        private static async Task<string?> SearchDownloadUrlAsyncWithTestByKey(HttpClient client, string language, string key,
            CancellationToken cancellationToken, Action<string> logger)
        {
            var requestUri = $"https://www.subtitlecat.com/index.php?search={key}";
            var response = await client.GetAsync(requestUri, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = _parser.ParseDocument(content);
            // 删选出全部匹配的数据
            var urls = document
                .All
                .Where(p =>
                {
                    if (p is not IHtmlAnchorElement anchor)
                    {
                        return false;
                    }
                    
                    // logger.Invoke($"请求name={name}, url={anchor.Href}");
                    return anchor.Href.Contains(key, StringComparison.CurrentCultureIgnoreCase);
                })
                .Select(p => ((IHtmlAnchorElement)p).Href.Replace("about://", ""))
                .ToList();
            var originTemp = new List<string>(urls);
            // 优先使用
            urls.Sort((a, b) =>
            {
                if (a == b)
                {
                    return 0;
                }
                
                var aPriority = GetPriority(a);
                var bPriority = GetPriority(b);
                if (aPriority != bPriority)
                {
                    return bPriority.CompareTo(aPriority);
                }
                return string.Compare(a, b, StringComparison.Ordinal);
            });
            logger.Invoke($"search subtitle {key} {language} 请求url={requestUri}, 返回待处理链接: 排序前url={string.Join(',', originTemp)}, 排序后url = {string.Join(',', urls)}");
            foreach (var url in urls)
            {
                var downloadUrl = await SearchDownloadUrlAsync(client, language, url, cancellationToken);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }
                if (await TestContext(client, downloadUrl, logger))
                {
                    logger.Invoke($"search 有效链接 {key} {language} subtitle  download url --->{downloadUrl} ");
                    return downloadUrl;
                }
            }
            return null;
        }
    }
}
