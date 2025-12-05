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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Net;
using System.Net;

namespace Fanart
{
    public class FanartAlbumProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;

        public FanartAlbumProvider(IHttpClient httpClient, IJsonSerializer jsonSerializer)
        {
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
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
            return item is MusicAlbum;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary,
                ImageType.Disc
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            var artistMusicBrainzId = item.GetProviderId(MetadataProviders.MusicBrainzAlbumArtist);

            if (!string.IsNullOrEmpty(artistMusicBrainzId))
            {
                try
                {
                    var root = await FanartArtistProvider.Current.EnsureArtistJson(artistMusicBrainzId, cancellationToken).ConfigureAwait(false);

                    if (root != null)
                    {
                        var musicBrainzReleaseGroupId = item.GetProviderId(MetadataProviders.MusicBrainzReleaseGroup);

                        AddImages(list, root, musicBrainzReleaseGroupId, cancellationToken);
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

        /// <summary>
        /// Adds the images.
        /// </summary>
        private void AddImages(List<RemoteImageInfo> list, FanartArtistProvider.FanartArtistResponse obj, string releaseGroupId, CancellationToken cancellationToken)
        {
            if (obj != null)
            {
                if (obj.albums != null)
                {
                    var album = obj.albums.FirstOrDefault(i => string.Equals(i.release_group_id, releaseGroupId, StringComparison.OrdinalIgnoreCase));

                    if (album != null)
                    {
                        PopulateImages(list, album.albumcover, ImageType.Primary, 1000, 1000);
                        PopulateImages(list, album.cdart, ImageType.Disc, 1000, 1000);
                    }
                }
            }
        }

        private void PopulateImages(List<RemoteImageInfo> list,
            List<FanartArtistProvider.FanartArtistImage> images,
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
                        Url = FanartMovieImageProvider.NormalizeImageUrl(url, "music"),
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
            get
            {
                // After embedded provider
                return 1;
            }
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
