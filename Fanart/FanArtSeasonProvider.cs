using System.Net;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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

namespace Fanart
{
    public class FanArtSeasonProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IHttpClient _httpClient;

        public FanArtSeasonProvider(IHttpClient httpClient)
        {
            _httpClient = httpClient;
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
            return item is Season;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Backdrop,
                ImageType.Thumb,
                ImageType.Banner,
                ImageType.Primary
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            var season = (Season)item;
            var series = season.Series;

            if (series != null)
            {
                var id = series.GetProviderId(MetadataProviders.Tvdb);

                if (!string.IsNullOrEmpty(id) && season.IndexNumber.HasValue)
                {
                    // Bad id entered
                    try
                    {
                        var seriesImages = await FanartSeriesProvider.Current.EnsureSeriesJson(id, cancellationToken).ConfigureAwait(false);

                        if (seriesImages != null)
                        {
                            AddImages(list, seriesImages, season.IndexNumber.Value, cancellationToken);
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
            }

            return list;
        }

        private void AddImages(List<RemoteImageInfo> list, FanartSeriesProvider.RootObject obj, int seasonNumber, CancellationToken cancellationToken)
        {
            PopulateImages(list, obj.seasonposter, ImageType.Primary, 1000, 1426, seasonNumber);
            PopulateImages(list, obj.seasonbanner, ImageType.Banner, 1000, 185, seasonNumber);
            PopulateImages(list, obj.seasonthumb, ImageType.Thumb, 500, 281, seasonNumber);
            PopulateImages(list, obj.showbackground, ImageType.Backdrop, 1920, 1080, seasonNumber);
        }

        private void PopulateImages(List<RemoteImageInfo> list,
            List<FanartSeriesProvider.Image> images,
            ImageType type,
            int width,
            int height,
            int seasonNumber)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;
                var season = i.season;

                int imageSeasonNumber;

                if (!string.IsNullOrEmpty(url) &&
                    !string.IsNullOrEmpty(season) &&
                    int.TryParse(season, NumberStyles.Integer, CultureInfo.InvariantCulture, out imageSeasonNumber) &&
                    seasonNumber == imageSeasonNumber)
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
    }
}
