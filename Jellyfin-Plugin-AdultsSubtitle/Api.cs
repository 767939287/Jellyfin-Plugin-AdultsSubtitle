using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using System.Collections.Concurrent;

namespace Jellyfin_Plugin_AdultsSubtitle
{
    public class Api
    {
        public static readonly ConcurrentDictionary<string, (string, string)> DownloadUrls = new();
        public static readonly Dictionary<string, string> LanguagesMaps = new()
        {
            {"chi","zh-CN"},
            {"eng","en"},
            {"zh-CN","zh-CN"},
        };
        private static readonly HtmlParser _parser = new();

        private static readonly List<string> OrderSuffix = [
            "zh-CN",
            ".zh",
            "-zh",
            "-c",
        ];
        
        public static async Task<string?> SearchDownloadUrlAsyncWithTest(HttpClient client, string language, string name, CancellationToken cancellationToken, Action<string> logger)
        {
            var response = await client.GetAsync($"https://www.subtitlecat.com/index.php?search={name}", cancellationToken);
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
                    return anchor.Href.Contains(name, StringComparison.CurrentCultureIgnoreCase);
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
            logger.Invoke($"search {name} {language} subtitle 排序前url={string.Join(',', originTemp)}, 排序后url = {string.Join(',', urls)}");
            foreach (var url in urls)
            {
                var downloadUrl = await SearchDownloadUrlAsync(client, language, url, cancellationToken);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }
                if (await TestContext(client, downloadUrl, logger))
                {
                    logger.Invoke($"search 有效链接 {name} {language} subtitle  download url --->{downloadUrl} ");
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

    }
}
