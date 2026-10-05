#if __EMBY__

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System.Reflection;

namespace Jellyfin_Plugin_AdultsSubtitle.ScheduledTasks
{
    public class ScanSubtitlesTask : IScheduledTask
    {
        public string Name => "Scan Subtitles";

        public string Key => $"{AdultsSubtitlePlugin.Instance.Name}ScanSubtitles";

        public string Description => "Scan subtitles and download missing subtitles";

        public string Category => AdultsSubtitlePlugin.Instance.Name;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ISubtitleManager _subtitleManager;
      
        public ScanSubtitlesTask(ILibraryManager libraryManager, ISubtitleManager subtitleManager, ILogger logger)
        {
            _subtitleManager = subtitleManager;
            _libraryManager = libraryManager;
            _logger = logger;
        }
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery()
            {
                IsVirtualItem = false,
                MediaTypes = new[] { MediaType.Video },
                IncludeItemTypes = new[] { nameof(Movie) },
            });
            progress.Report(0);
            double index = 1.0;
            foreach (var item in items)
            {
                if (item is Movie movie)
                {
                    var language = "chi";
                    var option = _libraryManager.GetLibraryOptions(item);
                    var dirInfo = new DirectoryInfo(movie.ContainingFolderPath);
                   
                    if (option != null

                        && !option.DisabledSubtitleFetchers.Contains(AdultsSubtitlePlugin.Instance.Name)
                        && option.SubtitleFetcherOrder.Contains(AdultsSubtitlePlugin.Instance.Name)
                        && Api.LanguagesMaps.TryGetValue(language, out var subCatLanguage)
                        && !movie.FileNameWithoutExtension.ToLower().EndsWith("-c")
                        && !dirInfo.GetFiles().Any(p => p.Name.Contains(movie.FileNameWithoutExtension) && p.Extension == ".srt"))
                    {

                        _logger.Info($"{movie.FileNameWithoutExtension} has no subtitle");
                      
                        if (option.SubtitleDownloadLanguages != null && option.SubtitleDownloadLanguages.Length > 0)
                        {
                            language = option.SubtitleDownloadLanguages[0];
                        }

                        using var client = new HttpClient();
                        try
                        {
                            var searchResult = await Api.SearchAsync(client, movie.FileNameWithoutExtension, cancellationToken);
                            _logger.Info($"search {movie.FileNameWithoutExtension} {language} subtitle  result --->{searchResult} ");
                            if (!string.IsNullOrWhiteSpace(searchResult))
                            {
                                var downloadUrl = await Api.SearchDownloadUrlAsync(client, subCatLanguage, searchResult, cancellationToken);
                                if (!string.IsNullOrWhiteSpace(downloadUrl))
                                {
                                    var subName = movie.FileNameWithoutExtension + ".srt";
                                    var subPath = Path.Combine(movie.ContainingFolderPath, subName);
                                    var response = await client.GetAsync(downloadUrl, cancellationToken);
                                    using var fs = File.OpenWrite(subPath);
                                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                                    await stream.CopyToAsync(fs, cancellationToken);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex.ToString());
                        }
                    }
                    progress.Report(index / items.Length);
                }
                index += 1;
            }
        }
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            };
        }
    }
}
#else

using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin_Plugin_AdultsSubtitle.ScheduledTasks
{
    public class ScanSubtitlesTask : IScheduledTask
    {
        public string Name => "Scan Subtitles";

        public string Key => $"{AdultsSubtitlePlugin.Instance!.Name}ScanSubtitles";

        public string Description => "Scan subtitles and download missing subtitles";

        public string Category => AdultsSubtitlePlugin.Instance!.Name;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<ScanSubtitlesTask> _logger;
        private readonly ISubtitleManager _subtitleManager;
        private readonly IHttpClientFactory _httpClientFactory;
        public ScanSubtitlesTask(ILibraryManager libraryManager, ISubtitleManager subtitleManager, IHttpClientFactory httpClientFactory, ILogger<ScanSubtitlesTask> logger)
        {
            _subtitleManager = subtitleManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery()
            {
                IsVirtualItem = false,
                MediaTypes = [MediaType.Video],
                SourceTypes = [SourceType.Library],
            });
            progress.Report(0);

            // 风险1优化：开始前一次性预扫描全部字幕文件，建内存索引。
            // 按目录去重后每个目录只枚举一次，避免对每个视频都做目录 IO
            // （3W 视频规模下可省下数万次磁盘枚举）。
            var subtitleIndex = BuildSubtitleIndex(items, cancellationToken);

            double index = 1.0;
            foreach (var item in items)
            {
                if (item is Movie movie)
                {
                    var language = "zho";
                    var option = _libraryManager.GetLibraryOptions(item);
                   
                    if (option != null
                        && !movie.HasSubtitles
                        && !option.DisabledSubtitleFetchers.Contains(AdultsSubtitlePlugin.Instance!.Name)
                        && option.SubtitleFetcherOrder.Contains(AdultsSubtitlePlugin.Instance.Name)
                        && Api.LanguagesMaps.TryGetValue(language, out var subCatLanguage)
                        && !movie.FileNameWithoutExtension.ToLower().EndsWith("-c")
                        && !HasAnySrtSubtitle(subtitleIndex, movie))
                    {

                        _logger.LogInformation($"{movie.FileNameWithoutExtension} has no subtitle");
                      
                        if (option.SubtitleDownloadLanguages != null && option.SubtitleDownloadLanguages.Length > 0)
                        {
                            language = option.SubtitleDownloadLanguages[0];
                        }

                        using var client = _httpClientFactory.CreateClient();
                        try
                        {
                            
                            var downloadUrl = await Api.SearchDownloadUrlAsyncWithTest(client, subCatLanguage, movie.FileNameWithoutExtension, cancellationToken, str => { _logger.LogInformation(str);});
                            if (!string.IsNullOrWhiteSpace(downloadUrl))
                            {
                                _logger.LogInformation($"start download subtitle {downloadUrl}");

                                var response = await client.GetAsync(downloadUrl, cancellationToken);
                                var ms = new MemoryStream();
                                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                                await stream.CopyToAsync(ms, cancellationToken);
                                ms.Position = 0;
                                _logger.LogInformation($"subtitle {downloadUrl} download comlete");

                                // 内容校验：跳过 404 错误页 / HTML 页面，避免把网页当字幕保存。
                                if (!Api.IsValidSubtitle(ms.ToArray()))
                                {
                                    _logger.LogWarning($"subtitle {downloadUrl} 内容不是有效字幕（可能是 404/HTML），已跳过");
                                }
                                else
                                {
                                    ms.Position = 0;
                                    await _subtitleManager.UploadSubtitle(movie, new SubtitleResponse()
                                    {
                                        Format = "srt",
                                        Language = Api.NormalizeLanguage(language),
                                        Stream = ms,
                                    });
                                }
                            }
                            
                            // var searchResult = await Api.SearchAsync(client, movie.FileNameWithoutExtension, cancellationToken);
                            // _logger.LogInformation($"search {movie.FileNameWithoutExtension} {language} subtitle  result --->{searchResult} ");
                            // if (!string.IsNullOrWhiteSpace(searchResult))
                            // {
                            //     var downloadUrl = await Api.SearchDownloadUrlAsync(client, subCatLanguage, searchResult, cancellationToken);
                            //     _logger.LogInformation($"search{movie.FileNameWithoutExtension} {language} subtitle  download url --->{downloadUrl} ");
                            //     if (!string.IsNullOrWhiteSpace(downloadUrl))
                            //     {
                            //         _logger.LogInformation($"start download subtitle {downloadUrl}");
                            //
                            //         var response = await client.GetAsync(downloadUrl, cancellationToken);
                            //         var ms = new MemoryStream();
                            //         var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                            //         await stream.CopyToAsync(ms, cancellationToken);
                            //         ms.Position = 0;
                            //         _logger.LogInformation($"subtitle {downloadUrl} download comlete");
                            //
                            //
                            //         await _subtitleManager.UploadSubtitle(movie, new SubtitleResponse()
                            //         {
                            //             Format = "srt",
                            //             Language = language,
                            //             Stream = ms,
                            //         });
                            //     }
                            // }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex.ToString());
                        }
                    }
                    progress.Report(index / items.Count);
                }
                index += 1;
            }
        }
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            };
        }

        /// <summary>
        /// 预扫描索引：目录(小写、规范化) -> 该目录下所有 .srt 文件名(小写)。
        /// 只收录视频可能所在的目录与视频的元数据目录，避免遍历无关目录。
        /// </summary>
        private Dictionary<string, HashSet<string>> BuildSubtitleIndex(
            IEnumerable<BaseItem> items, CancellationToken cancellationToken)
        {
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void IndexFolder(string? folder)
            {
                if (string.IsNullOrEmpty(folder))
                {
                    return;
                }

                var key = NormalizePath(folder);
                if (index.ContainsKey(key))
                {
                    return;
                }

                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(folder))
                    {
                        foreach (var file in Directory.EnumerateFiles(folder))
                        {
                            if (string.Equals(Path.GetExtension(file), ".srt", StringComparison.OrdinalIgnoreCase))
                            {
                                files.Add(Path.GetFileName(file));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"扫描字幕目录失败: {folder}");
                }

                index[key] = files;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexFolder(item.ContainingFolderPath);
                IndexFolder(item.GetInternalMetadataPath());
            }

            _logger.LogInformation($"字幕索引构建完成，共 {index.Count} 个目录");

            return index;
        }

        /// <summary>
        /// 基于预扫描索引判断视频目录或元数据目录中是否已存在该视频的字幕文件。
        /// 纯内存查询，不做目录 IO。
        /// 名称匹配与原逻辑一致（文件名包含视频基础名），可覆盖 .zho.srt / .zho.0.srt 等。
        /// </summary>
        private bool HasAnySrtSubtitle(Dictionary<string, HashSet<string>> index, Movie movie)
        {
            var baseName = movie.FileNameWithoutExtension;
            if (string.IsNullOrEmpty(baseName))
            {
                return false;
            }

            static bool HasMatch(Dictionary<string, HashSet<string>> idx, string? folder, string name)
            {
                if (string.IsNullOrEmpty(folder))
                {
                    return false;
                }

                return idx.TryGetValue(NormalizePath(folder), out var files)
                    && files.Any(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));
            }

            try
            {
                return HasMatch(index, movie.ContainingFolderPath, baseName)
                    || HasMatch(index, movie.GetInternalMetadataPath(), baseName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"检查 {baseName} 字幕索引时出错");
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
#endif
