using System.Net;
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
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _json;

        private const string FanArtBaseUrl = "https://webservice.fanart.tv/v3/movies/{1}?api_key={0}";
        // &client_key=52c813aa7b8c8b3bb87f4797532a2f8c

        internal static FanartMovieImageProvider Current;

        public FanartMovieImageProvider(IHttpClient httpClient, IJsonSerializer json)
        {
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
                    var root = await EnsureMovieJson(movieId, cancellationToken).ConfigureAwait(false); 
                    
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

        public static string NormalizeImageUrl(string url, string fanartType)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            url = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);

            return url;
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
                        Url = NormalizeImageUrl(url, "movies"),
                        Language = NormalizeLanguage(i.lang)
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
        /// Downloads the movie json.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task<RootObject> DownloadMovieJson(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(FanArtBaseUrl, FanartArtistProvider.ApiKey, id);

            var clientKey = FanartSeriesProvider.Current.GetFanartOptions().UserApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            using (var httpResponse = await _httpClient.SendAsync(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = true,
                CacheLength = FanartSeriesProvider.CacheLength,
                CacheMode = CacheMode.Unconditional

            }, "GET").ConfigureAwait(false))
            {
                using (var response = httpResponse.Content)
                {
                    return await _json.DeserializeFromStreamAsync<RootObject>(response).ConfigureAwait(false);
                }
            }
        }

        internal Task<RootObject> EnsureMovieJson(string id, CancellationToken cancellationToken)
        {
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
