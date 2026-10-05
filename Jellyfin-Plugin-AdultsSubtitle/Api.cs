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

        /// <summary>
        /// 将各种语言写法（ISO-639-1/2、站点语言等）统一归一为 Jellyfin 使用的
        /// 三字母 ISO-639-2 语言代码，避免字幕文件名出现 .zho / .chi / .zh 混用。
        /// 无法识别时默认返回中文的 "zho"。
        /// </summary>
        public static string NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "zho";
            }

            return language.Trim().ToLowerInvariant() switch
            {
                "zh" or "zho" or "chi" or "zh-cn" or "zh-hans" or "zh-sg" => "zho",
                "zh-tw" or "zh-hk" or "zh-hant" or "zh-mo" => "zho",
                "en" or "eng" => "eng",
                _ => language.Trim().ToLowerInvariant(),
            };
        }

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

        /// <summary>
        /// 校验下载到的内容是否为真正的字幕，而不是 404 错误页 / HTML / nginx 报错页。
        /// 判定规则：
        /// 1) 不能包含 HTML 页面特征（&lt;html&gt;、&lt;head&gt;、&lt;body&gt;、404 Not Found、nginx 等）；
        /// 2) 必须包含 SRT/VTT 的时间轴标记 "--&gt;"。
        /// </summary>
        public static bool IsValidSubtitle(byte[] content)
        {
            if (content is null || content.Length == 0)
            {
                return false;
            }

            string text;
            try
            {
                // 字幕多为 UTF-8，也可能带 BOM；这里用宽松解码，忽略非法字节。
                text = System.Text.Encoding.UTF8.GetString(content);
            }
            catch
            {
                return false;
            }

            // 归一化后做不区分大小写的特征匹配
            var lower = text.ToLowerInvariant();

            // 1) HTML / 错误页特征：命中任意一个即视为非法
            string[] invalidMarkers =
            [
                "<html",
                "<head",
                "<body",
                "<title",
                "<!doctype",
                "404 not found",
                "404找不到",
                "nginx/",
                "<center",
                "</html",
            ];
            foreach (var marker in invalidMarkers)
            {
                if (lower.Contains(marker))
                {
                    return false;
                }
            }

            // 2) 必须包含字幕时间轴标记
            return text.Contains("-->");
        }
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
