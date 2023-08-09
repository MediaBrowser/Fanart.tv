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

namespace Fanart
{
    public class FanartAlbumProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;

        public FanartAlbumProvider(IServerConfigurationManager config, IHttpClient httpClient, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            _config = config;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
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
                await FanartArtistProvider.Current.EnsureArtistJson(artistMusicBrainzId, cancellationToken).ConfigureAwait(false);

                var artistJsonPath = FanartArtistProvider.GetArtistJsonPath(_config.CommonApplicationPaths, artistMusicBrainzId);

                var musicBrainzReleaseGroupId = item.GetProviderId(MetadataProviders.MusicBrainzReleaseGroup);

                var musicBrainzId = item.GetProviderId(MetadataProviders.MusicBrainzAlbum);

                try
                {
                    await AddImages(list, artistJsonPath, musicBrainzId, musicBrainzReleaseGroupId, cancellationToken).ConfigureAwait(false);
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
        /// <param name="releaseId">The release identifier.</param>
        /// <param name="releaseGroupId">The release group identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task AddImages(List<RemoteImageInfo> list, string path, string releaseId, string releaseGroupId, CancellationToken cancellationToken)
        {
            var obj = await _jsonSerializer.DeserializeFromFileAsync<FanartArtistProvider.FanartArtistResponse>(path).ConfigureAwait(false);

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
