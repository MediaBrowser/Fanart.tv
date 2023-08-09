﻿using System.Net;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.LiveTv;

namespace Fanart
{
    public class FanartMovieImageProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _json;

        private const string FanArtBaseUrl = "https://webservice.fanart.tv/v3/movies/{1}?api_key={0}";
        // &client_key=52c813aa7b8c8b3bb87f4797532a2f8c

        internal static FanartMovieImageProvider Current;

        public FanartMovieImageProvider(IServerConfigurationManager config, IHttpClient httpClient, IFileSystem fileSystem, IJsonSerializer json)
        {
            _config = config;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _json = json;

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
            return item is Movie || item is BoxSet || item is MusicVideo;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary, 
                ImageType.Thumb,
                ImageType.Art,
                ImageType.Logo,
                ImageType.Disc,
                ImageType.Banner,
                ImageType.Backdrop
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var baseItem = item;
            var list = new List<RemoteImageInfo>();

            var movieId = baseItem.GetProviderId(MetadataProviders.Tmdb);

            if (!string.IsNullOrEmpty(movieId))
            {
                // Bad id entered
                try
                {
                    await EnsureMovieJson(movieId, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpException ex)
                {
                    if (!ex.StatusCode.HasValue || ex.StatusCode.Value != HttpStatusCode.NotFound)
                    {
                        throw;
                    }
                }

                var path = GetFanartJsonPath(movieId);

                try
                {
                    await AddImages(list, path, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // No biggie. Don't blow up
                }
                catch (IOException)
                {
                    // No biggie. Don't blow up
                }
            }

            return list;
        }

        private async Task AddImages(List<RemoteImageInfo> list, string path, CancellationToken cancellationToken)
        {
            var root = await _json.DeserializeFromFileAsync<RootObject>(path).ConfigureAwait(false);

            if (root != null)
            {
                AddImages(list, root, cancellationToken);
            }
        }

        private void AddImages(List<RemoteImageInfo> list, RootObject obj, CancellationToken cancellationToken)
        {
            PopulateImages(list, obj.hdmovieclearart, ImageType.Art, 1000, 562);
            PopulateImages(list, obj.hdmovielogo, ImageType.Logo, 800, 310);
            PopulateImages(list, obj.moviedisc, ImageType.Disc, 1000, 1000);
            PopulateImages(list, obj.movieposter, ImageType.Primary, 1000, 1426);
            PopulateImages(list, obj.movielogo, ImageType.Logo, 400, 155);
            PopulateImages(list, obj.movieart, ImageType.Art, 500, 281);
            PopulateImages(list, obj.moviethumb, ImageType.Thumb, 1000, 562);
            PopulateImages(list, obj.moviebanner, ImageType.Banner, 1000, 185);
            PopulateImages(list, obj.moviebackground, ImageType.Backdrop, 1920, 1080);
        }

        private void PopulateImages(List<RemoteImageInfo> list, List<Image> images, ImageType type, int width, int height)
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
                        Url = url,
                        Language = NormalizeLanguage(i.lang)
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

        public static string NormalizeLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang))
            {
                return null;
            }

            if (string.Equals(lang, "00", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return lang;
        }

        public int Order
        {
            get { return 1; }
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url
            });
        }

        /// <summary>
        /// Gets the movie data path.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>System.String.</returns>
        internal static string GetMovieDataPath(IApplicationPaths appPaths, string id)
        {
            var dataPath = Path.Combine(GetMoviesDataPath(appPaths), id);

            return dataPath;
        }

        /// <summary>
        /// Gets the movie data path.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <returns>System.String.</returns>
        internal static string GetMoviesDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "fanart-movies");

            return dataPath;
        }

        public string GetFanartJsonPath(string id)
        {
            var movieDataPath = GetMovieDataPath(_config.ApplicationPaths, id);
            return Path.Combine(movieDataPath, "fanart.json");
        }

        /// <summary>
        /// Downloads the movie json.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        internal async Task DownloadMovieJson(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(FanArtBaseUrl, FanartArtistProvider.ApiKey, id);

            var clientKey = FanartSeriesProvider.Current.GetFanartOptions().UserApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            var path = GetFanartJsonPath(id);

			_fileSystem.CreateDirectory(_fileSystem.GetDirectoryName(path));

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
                        using (var fileStream = _fileSystem.GetFileStream(path, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, true))
                        {
                            await response.CopyToAsync(fileStream).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (HttpException exception)
            {
                if (exception.StatusCode.HasValue && exception.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    // If the user has automatic updates enabled, save a dummy object to prevent repeated download attempts
                    _json.SerializeToFile(new RootObject(), path);

                    return;
                }

                throw;
            }
        }

        internal Task EnsureMovieJson(string id, CancellationToken cancellationToken)
        {
            var path = GetFanartJsonPath(id);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                if ((DateTimeOffset.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                {
                    return Task.CompletedTask;
                }
            }

            return DownloadMovieJson(id, cancellationToken);
        }

        public class Image
        {
            public string id { get; set; }
            public string url { get; set; }
            public string lang { get; set; }
            public string likes { get; set; }
        }

        public class RootObject
        {
            public string name { get; set; }
            public string tmdb_id { get; set; }
            public string imdb_id { get; set; }
            public List<Image> hdmovielogo { get; set; }
            public List<Image> moviedisc { get; set; }
            public List<Image> movielogo { get; set; }
            public List<Image> movieposter { get; set; }
            public List<Image> hdmovieclearart { get; set; }
            public List<Image> movieart { get; set; }
            public List<Image> moviebackground { get; set; }
            public List<Image> moviebanner { get; set; }
            public List<Image> moviethumb { get; set; }
        }
    }
}
