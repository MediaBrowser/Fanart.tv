﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Extensions;

namespace Fanart
{
    public class FanartArtistProvider : IRemoteImageProvider, IHasOrder
    {
        internal const string ApiKey = "5c6b04c68e904cfed1e6cbc9a9e683d4";
        private const string FanArtBaseUrl = "https://webservice.fanart.tv/v3.1/music/{1}?api_key={0}";

        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;

        internal static FanartArtistProvider Current;

        public FanartArtistProvider(IServerConfigurationManager config, IHttpClient httpClient, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            _config = config;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;

            Current = this;
        }

        public string Name
        {
            get { return ProviderName; }
        }

        public static string ProviderName
        {
            get { return "FanArt"; }
        }

        public bool Supports(BaseItem item)
        {
            return item is MusicArtist;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary, 
                ImageType.Logo,
                ImageType.Art,
                ImageType.Banner,
                ImageType.Backdrop
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var artist = (MusicArtist)item;

            var list = new List<RemoteImageInfo>();

            var artistMusicBrainzId = artist.GetProviderId(MetadataProviders.MusicBrainzArtist);

            if (!String.IsNullOrEmpty(artistMusicBrainzId))
            {
                await EnsureArtistJson(artistMusicBrainzId, cancellationToken).ConfigureAwait(false);

                var artistJsonPath = GetArtistJsonPath(_config.CommonApplicationPaths, artistMusicBrainzId);

                try
                {
                    AddImages(list, artistJsonPath, cancellationToken);
                }
                catch (FileNotFoundException)
                {

                }
                catch (IOException)
                {

                }
            }

            return list;
        }

        /// <summary>
        /// Adds the images.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private void AddImages(List<RemoteImageInfo> list, string path, CancellationToken cancellationToken)
        {
            var obj = _jsonSerializer.DeserializeFromFile<FanartArtistResponse>(path);

            if (obj == null)
            {
                return;
            }

            PopulateImages(list, obj.artistbackground, ImageType.Backdrop, 1920, 1080);
            PopulateImages(list, obj.artistthumb, ImageType.Primary, 500, 281);
            PopulateImages(list, obj.hdmusiclogo, ImageType.Logo, 800, 310);
            PopulateImages(list, obj.musicbanner, ImageType.Banner, 1000, 185);
            PopulateImages(list, obj.musiclogo, ImageType.Logo, 400, 155);
            PopulateImages(list, obj.hdmusicarts, ImageType.Art, 1000, 562);
            PopulateImages(list, obj.musicarts, ImageType.Art, 500, 281);
        }

        private void PopulateImages(List<RemoteImageInfo> list,
            List<FanartArtistImage> images,
            ImageType type,
            int width,
            int height)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;

                if (!string.IsNullOrEmpty(url))
                {
                    var likesString = i.likes;
                    int likes;

                    var info = new RemoteImageInfo
                    {
                        RatingType = RatingType.Likes,
                        Type = type,
                        Width = width,
                        Height = height,
                        ProviderName = Name,
                        Url = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase),
                        Language = FanartMovieImageProvider.NormalizeLanguage(i.lang)
                    };

                    if (!string.IsNullOrEmpty(likesString) && int.TryParse(likesString, NumberStyles.Integer, _usCulture, out likes))
                    {
                        info.CommunityRating = likes;
                    }

                    return info;
                }

                return null;
            }).Where(i => i != null));
        }

        public int Order
        {
            get { return 0; }
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url
            });
        }

        internal Task EnsureArtistJson(string musicBrainzId, CancellationToken cancellationToken)
        {
            var jsonPath = GetArtistJsonPath(_config.ApplicationPaths, musicBrainzId);

            var fileInfo = _fileSystem.GetFileSystemInfo(jsonPath);

            if (fileInfo.Exists)
            {
                if ((DateTimeOffset.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                {
                    return Task.CompletedTask;
                }
            }

            return DownloadArtistJson(musicBrainzId, cancellationToken);
        }

        /// <summary>
        /// Downloads the artist data.
        /// </summary>
        /// <param name="musicBrainzId">The music brainz id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.Boolean}.</returns>
        internal async Task DownloadArtistJson(string musicBrainzId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(FanArtBaseUrl, ApiKey, musicBrainzId);

            var clientKey = FanartSeriesProvider.Current.GetFanartOptions().UserApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            var jsonPath = GetArtistJsonPath(_config.ApplicationPaths, musicBrainzId);

            _fileSystem.CreateDirectory(_fileSystem.GetDirectoryName(jsonPath));

            try
            {
                using (var httpResponse = await _httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    BufferContent = true

                }, "GET").ConfigureAwait(false))
                {
                    using (var response = httpResponse.Content)
                    {
                        using (var saveFileStream = _fileSystem.GetFileStream(jsonPath, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, true))
                        {
                            await response.CopyToAsync(saveFileStream).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (HttpException ex)
            {
                if (ex.StatusCode.HasValue && ex.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    _jsonSerializer.SerializeToFile(new FanartArtistResponse(), jsonPath);
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the artist data path.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <param name="musicBrainzArtistId">The music brainz artist identifier.</param>
        /// <returns>System.String.</returns>
        private static string GetArtistDataPath(IApplicationPaths appPaths, string musicBrainzArtistId)
        {
            var dataPath = Path.Combine(GetArtistDataPath(appPaths), musicBrainzArtistId);

            return dataPath;
        }

        /// <summary>
        /// Gets the artist data path.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>System.String.</returns>
        internal static string GetArtistDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "fanart-music");

            return dataPath;
        }

        internal static string GetArtistJsonPath(IApplicationPaths appPaths, string musicBrainzArtistId)
        {
            var dataPath = GetArtistDataPath(appPaths, musicBrainzArtistId);

            return Path.Combine(dataPath, "fanart.json");
        }


        public class FanartArtistImage
        {
            public string id { get; set; }
            public string url { get; set; }
            public string likes { get; set; }
            public string disc { get; set; }
            public string size { get; set; }
            public string lang { get; set; }
        }

        public class Album
        {
            public string release_group_id { get; set; }
            public List<FanartArtistImage> cdart { get; set; }
            public List<FanartArtistImage> albumcover { get; set; }
        }

        public class FanartArtistResponse
        {
            public string name { get; set; }
            public string mbid_id { get; set; }
            public List<FanartArtistImage> artistthumb { get; set; }
            public List<FanartArtistImage> artistbackground { get; set; }
            public List<FanartArtistImage> hdmusiclogo { get; set; }
            public List<FanartArtistImage> musicbanner { get; set; }
            public List<FanartArtistImage> musiclogo { get; set; }
            public List<FanartArtistImage> musicarts { get; set; }
            public List<FanartArtistImage> hdmusicarts { get; set; }
            public List<Album> albums { get; set; }
        }
    }
}
