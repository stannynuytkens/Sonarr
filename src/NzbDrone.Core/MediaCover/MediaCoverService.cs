﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.MediaCover
{
    public interface IMapCoversToLocal
    {
        void ConvertToLocalUrls(int seriesId, IEnumerable<MediaCover> covers);
        string GetCoverPath(int seriesId, MediaCoverTypes mediaCoverTypes, int? height = null);
    }

    public class MediaCoverService :
        IHandleAsync<SeriesUpdatedEvent>,
        IHandleAsync<SeriesDeletedEvent>,
        IMapCoversToLocal
    {
        private readonly IImageResizer _resizer;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly ICoverExistsSpecification _coverExistsSpecification;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        private readonly string _coverRootFolder;
        private readonly SemaphoreSlim _semaphore;

        public MediaCoverService(IImageResizer resizer,
                                 IHttpClient httpClient,
                                 IDiskProvider diskProvider,
                                 IAppFolderInfo appFolderInfo,
                                 ICoverExistsSpecification coverExistsSpecification,
                                 IConfigFileProvider configFileProvider,
                                 IEventAggregator eventAggregator,
                                 Logger logger)
        {
            _resizer = resizer;
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _coverExistsSpecification = coverExistsSpecification;
            _configFileProvider = configFileProvider;
            _eventAggregator = eventAggregator;
            _logger = logger;

            _coverRootFolder = appFolderInfo.GetMediaCoverPath();

            // ImageSharp is slow on ARM (no hardware acceleration on mono yet)
            // So limit the number of concurrent resizing tasks
            _semaphore = new SemaphoreSlim((int)Math.Ceiling(Environment.ProcessorCount / 2.0));
        }

        public string GetCoverPath(int seriesId, MediaCoverTypes coverTypes, int? height = null)
        {
            var heightSuffix = height.HasValue ? "-" + height.ToString() : "";

            return Path.Combine(GetSeriesCoverPath(seriesId), coverTypes.ToString().ToLower() + heightSuffix + ".jpg");
        }

        public void ConvertToLocalUrls(int seriesId, IEnumerable<MediaCover> covers)
        {
            foreach (var mediaCover in covers)
            {
                var filePath = GetCoverPath(seriesId, mediaCover.CoverType);

                mediaCover.Url = _configFileProvider.UrlBase + @"/MediaCover/" + seriesId + "/" + mediaCover.CoverType.ToString().ToLower() + ".jpg";

                if (_diskProvider.FileExists(filePath))
                {
                    var lastWrite = _diskProvider.FileGetLastWrite(filePath);
                    mediaCover.Url += "?lastWrite=" + lastWrite.Ticks;
                }
            }
        }

        private string GetSeriesCoverPath(int seriesId)
        {
            return Path.Combine(_coverRootFolder, seriesId.ToString());
        }

        private void EnsureCovers(Series series)
        {
            var toResize = new List<Tuple<MediaCover, bool>>();

            foreach (var cover in series.Images)
            {
                var fileName = GetCoverPath(series.Id, cover.CoverType);
                var alreadyExists = false;
                try
                {
                    alreadyExists = _coverExistsSpecification.AlreadyExists(cover.Url, fileName);
                    if (!alreadyExists)
                    {
                        DownloadCover(series, cover);
                    }
                }
                catch (WebException e)
                {
                    _logger.Warn("Couldn't download media cover for {0}. {1}", series, e.Message);
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't download media cover for {0}", series);
                }

                toResize.Add(Tuple.Create(cover, alreadyExists));
            }

            try
            {
                _semaphore.Wait();

                foreach (var tuple in toResize)
                {
                    EnsureResizedCovers(series, tuple.Item1, !tuple.Item2);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void DownloadCover(Series series, MediaCover cover)
        {
            var fileName = GetCoverPath(series.Id, cover.CoverType);

            _logger.Info("Downloading {0} for {1} {2}", cover.CoverType, series, cover.Url);
            _httpClient.DownloadFile(cover.Url, fileName);
        }

        private void EnsureResizedCovers(Series series, MediaCover cover, bool forceResize)
        {
            int[] heights;

            switch (cover.CoverType)
            {
                default:
                    return;

                case MediaCoverTypes.Poster:
                case MediaCoverTypes.Headshot:
                    heights = new[] { 500, 250 };
                    break;

                case MediaCoverTypes.Banner:
                    heights = new[] { 70, 35 };
                    break;

                case MediaCoverTypes.Fanart:
                case MediaCoverTypes.Screenshot:
                    heights = new[] { 360, 180 };
                    break;
            }

            foreach (var height in heights)
            {
                var mainFileName = GetCoverPath(series.Id, cover.CoverType);
                var resizeFileName = GetCoverPath(series.Id, cover.CoverType, height);

                if (forceResize || !_diskProvider.FileExists(resizeFileName) || _diskProvider.GetFileSize(resizeFileName) == 0)
                {
                    _logger.Debug("Resizing {0}-{1} for {2}", cover.CoverType, height, series);

                    try
                    {
                        _resizer.Resize(mainFileName, resizeFileName, height);
                    }
                    catch
                    {
                        _logger.Debug("Couldn't resize media cover {0}-{1} for {2}, using full size image instead.", cover.CoverType, height, series);
                    }
                }
            }
        }

        public void HandleAsync(SeriesUpdatedEvent message)
        {
            EnsureCovers(message.Series);
            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(message.Series));
        }

        public void HandleAsync(SeriesDeletedEvent message)
        {
            var path = GetSeriesCoverPath(message.Series.Id);
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }
    }
}
