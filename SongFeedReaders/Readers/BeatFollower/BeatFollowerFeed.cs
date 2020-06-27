using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongFeedReaders.Data;
using SongFeedReaders.Logging;
using WebUtilities;

namespace SongFeedReaders.Readers.BeatFollower
{
    class BeatFollowerFeed : IFeed
    {
        
        private const string PageNumberKey = "{PAGE}";
        private const string PageCountKey = "{COUNT}";

        private static FeedReaderLoggerBase _logger;
        public static FeedReaderLoggerBase Logger
        {
            get { return _logger ?? LoggingController.DefaultLogger; }
            set { _logger = value; }
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public Uri RootUri { get; }

        public string BaseUrl => "http://localhost:3000/feed/queue/{PAGE}/{COUNT}";

        public int SongsPerPage { get; }
        public bool StoreRawData { get; set; }
        public IFeedSettings Settings { get; }
        public bool HasValidSettings { get; }
        public void EnsureValidSettings()
        {
            throw new NotImplementedException();
        }

        public async Task<PageReadResult> GetSongsFromPageAsync(int page, CancellationToken cancellationToken)
        {
            Dictionary<string, ScrapedSong> songs = new Dictionary<string, ScrapedSong>();
            Uri uri;
            try
            {
                uri = GetUriForPage(page);
            }
            catch (InvalidFeedSettingsException)
            {
                throw;
            }

            string pageText = "";

            Logger.Debug($"Getting songs from '{uri}'");
            IWebResponseMessage? response = null;

            try
            {
                response = await WebUtils.WebClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (WebClientException ex)
            {
                string errorText = string.Empty;
                int statusCode = ex?.Response?.StatusCode ?? 0;
                if (statusCode != 0)
                {
                    switch (statusCode)
                    {
                        case 404:
                            errorText = $"{uri.ToString()} was not found.";
                            break;
                        case 408:
                            errorText = $"Timeout getting first page in BeatFollowerReader: {uri}: {ex.Message}";
                            break;
                        default:
                            errorText = $"Site Error getting first page in BeatFollowerReader: {uri}: {ex.Message}";
                            break;
                    }
                }
                Logger?.Debug(errorText);
                // No need for a stacktrace if it's one of these errors.
                if (!(statusCode == 404 || statusCode == 408 || statusCode == 500))
                    Logger?.Debug($"{ex.Message}\n{ex.StackTrace}");
                return PageReadResult.FromWebClientException(ex, uri, page);
            }
            catch (Exception ex)
            {
                string message = $"Uncaught error getting the first page in BeatFollowerReader.GetSongsFromPageAsync(): {ex.Message}";
                return new PageReadResult(uri, new List<ScrapedSong>(), page, new FeedReaderException(message, ex, FeedReaderFailureCode.SourceFailed), PageErrorType.Unknown);
            }
            finally
            {
                response?.Dispose();
                response = null;
            }




            bool isLastPage;
            try
            {
                List<ScrapedSong>? diffs = GetSongsFromPageText(pageText, uri, Settings.StoreRawData || StoreRawData);
                isLastPage = diffs.Count < SongsPerPage;
                foreach (ScrapedSong? diff in diffs)
                {
                    if (!songs.ContainsKey(diff.Hash) && (Settings.Filter == null || Settings.Filter(diff)))
                        songs.Add(diff.Hash, diff);
                    if (Settings.StopWhenAny != null && Settings.StopWhenAny(diff))
                        isLastPage = true;
                }
            }
            catch (JsonReaderException ex)
            {
                string message = "Unable to parse JSON from text";
                Logger?.Debug($"{message}: {ex.Message}\n{ex.StackTrace}");
                return new PageReadResult(uri, null, page, new FeedReaderException(message, ex, FeedReaderFailureCode.PageFailed), PageErrorType.ParsingError);
            }
            catch (Exception ex)
            {
                string message = $"Unhandled exception from GetSongsFromPageText() while parsing {uri}";
                Logger?.Debug($"{message}: {ex.Message}\n{ex.StackTrace}");
                return new PageReadResult(uri, null, page, new FeedReaderException(message, ex, FeedReaderFailureCode.PageFailed), PageErrorType.ParsingError);
            }

            return new PageReadResult(uri, songs.Values.ToList(), page, isLastPage);
        }

        public Uri GetUriForPage(int page)
        {
            
            string url = BaseUrl;
            Dictionary<string, string> urlReplacements = new Dictionary<string, string>() {
                {PageCountKey, SongsPerPage.ToString() },
                {PageNumberKey, page.ToString()},
            };
            foreach (KeyValuePair<string, string> pair in urlReplacements)
            {
                url = url.Replace(pair.Key, pair.Value);
            }
            return new Uri(url);
        }

        public static List<ScrapedSong>? GetSongsFromPageText(string pageText, Uri sourceUri, bool storeRawData)
        {

            JArray result;
            List<ScrapedSong> songs = new List<ScrapedSong>();
            try
            {
                result = JArray.Parse(pageText);
            }
            catch (JsonReaderException)
            {
                throw;
            }

            foreach (JObject recommendation in result.Children<JObject>())
            {
                // Example Response:
                // [{
                //      "recommender":"phaenixvr",
                //      "songName":"Little Swing ",
                //      "levelAuthorName":"ConnorJC",
                //      "hash":"235336F468A6290C87D724616E9B1D952AE3B8F2"
                // }]
                
                var song = recommendation["song"];

                string? hash = song["hash"]?.Value<string>();
                string? songName = song["songName"]?.Value<string>();
                string? mapperName = song["levelAuthorName"]?.Value<string>();

                if (!string.IsNullOrEmpty(hash))
                    songs.Add(new ScrapedSong(hash, songName, mapperName, Utilities.GetDownloadUriByHash(hash), sourceUri, storeRawData ? recommendation : null));
            }
            return songs;
        }


        public FeedAsyncEnumerator GetEnumerator(bool cachePages)
        {
            return new FeedAsyncEnumerator(this, Settings.StartingPage, cachePages);
        }
        public FeedAsyncEnumerator GetEnumerator()
        {
            return GetEnumerator(false);
        }
    }
}
