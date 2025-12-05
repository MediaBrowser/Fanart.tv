using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
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
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.IO;
using MediaBrowser.Model.IO;
using static Fanart.FanartArtistProvider;

namespace Fanart
{
    public class FanartSeriesProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _json;

        private const string FanArtBaseUrl = "https://webservice.fanart.tv/v3/tv/{1}?api_key={0}";
        // &client_key=52c813aa7b8c8b3bb87f4797532a2f8c

        internal static FanartSeriesProvider Current { get; private set; }

        public static TimeSpan CacheLength = TimeSpan.FromHours(12);

        public FanartSeriesProvider(IServerConfigurationManager config, IHttpClient httpClient, IFileSystem fileSystem, IJsonSerializer json)
        {
            _config = config;
            _httpClient = httpClient;
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
            return item is Series;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary,
                ImageType.Thumb,
                ImageType.Art,
                ImageType.Logo,
                ImageType.Backdrop,
                ImageType.Banner
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            var series = (Series)item;

            var id = series.GetProviderId(MetadataProviders.Tvdb);

            if (!string.IsNullOrEmpty(id))
            {
                // Bad id entered
                try
                {
                    var root = await EnsureSeriesJson(id, cancellationToken).ConfigureAwait(false);

                    if (root != null)
                    {
                        AddImages(list, root, cancellationToken);
                    }
                }
                catch (HttpException ex)
                {
                    if (!ex.StatusCode.HasValue || ex.StatusCode.Value != HttpStatusCode.NotFound)
                    {
                        throw;
                    }
                }
            }

            return list;
        }

        private void AddImages(List<RemoteImageInfo> list, RootObject obj, CancellationToken cancellationToken)
        {
            PopulateImages(list, obj.hdtvlogo, ImageType.Logo, 800, 310);
            PopulateImages(list, obj.hdclearart, ImageType.Art, 1000, 562);
            PopulateImages(list, obj.clearlogo, ImageType.Logo, 400, 155);
            PopulateImages(list, obj.clearart, ImageType.Art, 500, 281);
            PopulateImages(list, obj.showbackground, ImageType.Backdrop, 1920, 1080, true);
            PopulateImages(list, obj.seasonthumb, ImageType.Thumb, 500, 281);
            PopulateImages(list, obj.tvthumb, ImageType.Thumb, 500, 281);
            PopulateImages(list, obj.tvbanner, ImageType.Banner, 1000, 185);
            PopulateImages(list, obj.tvposter, ImageType.Primary, 1000, 1426);
        }

        private void PopulateImages(List<RemoteImageInfo> list,
            List<Image> images,
            ImageType type,
            int width,
            int height,
            bool allowSeasonAll = false)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;
                var season = i.season;

                var isSeasonValid = string.IsNullOrEmpty(season) ||
                    (allowSeasonAll && string.Equals(season, "all", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(url) && isSeasonValid)
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
                        Url = FanartMovieImageProvider.NormalizeImageUrl(url, "tv"),
                        Language = FanartMovieImageProvider.NormalizeLanguage(i.lang)
                    };

                    if (!string.IsNullOrEmpty(likesString) && int.TryParse(likesString, NumberStyles.Integer, CultureInfo.InvariantCulture, out likes))
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

        private readonly SemaphoreSlim _ensureSemaphore = new SemaphoreSlim(1, 1);
        internal async Task<FanartSeriesProvider.RootObject> EnsureSeriesJson(string tvdbId, CancellationToken cancellationToken)
        {
            // Only allow one thread in here at a time since every season will be calling this method, possibly concurrently
            await _ensureSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await DownloadSeriesJson(tvdbId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ensureSemaphore.Release();
            }
        }

        public FanartOptions GetFanartOptions()
        {
            return _config.GetConfiguration<FanartOptions>("fanart");
        }

        /// <summary>
        /// Downloads the series json.
        /// </summary>
        /// <param name="tvdbId">The TVDB identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        internal async Task<FanartSeriesProvider.RootObject> DownloadSeriesJson(string tvdbId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(FanArtBaseUrl, FanartArtistProvider.ApiKey, tvdbId);

            var clientKey = GetFanartOptions().UserApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            using (var httpResponse = await _httpClient.SendAsync(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = true,
                CacheLength = CacheLength,
                CacheMode = CacheMode.Unconditional

            }, "GET").ConfigureAwait(false))
            {
                using (var response = httpResponse.Content)
                {
                    return await _json.DeserializeFromStreamAsync<FanartSeriesProvider.RootObject>(response).ConfigureAwait(false);
                }
            }
        }

        public class Image
        {
            public string id { get; set; }
            public string url { get; set; }
            public string lang { get; set; }
            public string likes { get; set; }
            public string season { get; set; }
        }

        public class RootObject
        {
            public string name { get; set; }
            public string thetvdb_id { get; set; }
            public List<Image> clearlogo { get; set; }
            public List<Image> hdtvlogo { get; set; }
            public List<Image> clearart { get; set; }
            public List<Image> showbackground { get; set; }
            public List<Image> tvthumb { get; set; }
            public List<Image> seasonposter { get; set; }
            public List<Image> seasonthumb { get; set; }
            public List<Image> hdclearart { get; set; }
            public List<Image> tvbanner { get; set; }
            public List<Image> characterart { get; set; }
            public List<Image> tvposter { get; set; }
            public List<Image> seasonbanner { get; set; }
        }
    }

    public class FanartConfigStore : IConfigurationFactory
    {
        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new ConfigurationStore[]
            {
                new ConfigurationStore
                {
                     Key = "fanart",
                     ConfigurationType = typeof(FanartOptions)
                }
            };
        }
    }
}
