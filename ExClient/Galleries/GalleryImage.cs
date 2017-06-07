﻿using ExClient.Internal;
using HtmlAgilityPack;
using Opportunity.MvvmUniverse;
using Opportunity.MvvmUniverse.AsyncHelpers;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Html;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using static System.Runtime.InteropServices.WindowsRuntime.AsyncInfo;

namespace ExClient.Galleries
{
    [System.Diagnostics.DebuggerDisplay(@"\{PageId = {PageId} State = {State} File = {ImageFile?.Name}\}")]
    public class GalleryImage : ObservableObject
    {
        static GalleryImage()
        {
            DispatcherHelper.BeginInvokeOnUIThread(async () =>
            {
                var info = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                thumbWidth = (uint)(100 * info.RawPixelsPerViewPixel);
                DefaultThumb = new BitmapImage();
                using (var stream = await StorageHelper.GetIconOfExtension("jpg"))
                {
                    await DefaultThumb.SetSourceAsync(stream);
                }
            });
        }

        private static uint thumbWidth = 100;

        protected static BitmapImage DefaultThumb
        {
            get; private set;
        }

        internal static IAsyncOperation<GalleryImage> LoadCachedImageAsync(Gallery owner, Models.ImageModel model)
        {
            return Run(async token =>
            {
                var folder = owner.GalleryFolder ?? await owner.GetFolderAsync();
                var imageFile = await folder.TryGetFileAsync(model.FileName);
                if (imageFile == null)
                    return null;
                var img = new GalleryImage(owner, model.PageId, model.ImageKey, null)
                {
                    ImageFile = imageFile,
                    OriginalLoaded = model.OriginalLoaded,
                    Progress = 100,
                    State = ImageLoadingState.Loaded
                };
                return img;
            });
        }

        internal GalleryImage(Gallery owner, int pageId, ulong imageKey, Uri thumb)
        {
            this.Owner = owner;
            this.PageId = pageId;
            this.imageKey = imageKey;
            this.PageUri = new Uri(Client.Current.Uris.RootUri, $"s/{imageKey.TokenToString()}/{Owner.Id}-{PageId}");
            this.thumbUri = thumb;
        }

        private static readonly Regex failTokenMatcher = new Regex(@"return\s+nl\(\s*'(.+?)'\s*\)", RegexOptions.Compiled);

        private IAsyncAction loadImageUri()
        {
            return Run(async token =>
            {
                var loadPageUri = default(Uri);
                if (this.failToken != null)
                    loadPageUri = new Uri(this.PageUri, $"?nl={failToken}");
                else
                    loadPageUri = this.PageUri;
                var loadPage = Client.Current.HttpClient.GetStringAsync(loadPageUri);
                var pageResult = new HtmlDocument();
                pageResult.LoadHtml(await loadPage);

                this.imageUri = new Uri(HtmlUtilities.ConvertToText(pageResult.GetElementbyId("img").GetAttributeValue("src", "")));
                var originalNode = pageResult.GetElementbyId("i7").Descendants("a").FirstOrDefault();
                if (originalNode == null)
                {
                    this.originalImageUri = null;
                }
                else
                {
                    this.originalImageUri = new Uri(HtmlUtilities.ConvertToText(originalNode.GetAttributeValue("href", "")));
                }
                var loadFail = pageResult.GetElementbyId("loadfail").GetAttributeValue("onclick", "");
                this.failToken = failTokenMatcher.Match(loadFail).Groups[1].Value;
            });
        }

        private ImageLoadingState state;

        public ImageLoadingState State
        {
            get => state;
            protected set => Set(ref state, value);
        }

        private Uri thumbUri;

        private readonly WeakReference<ImageSource> thumb = new WeakReference<ImageSource>(null);

        private static HttpClient thumbClient { get; } = new HttpClient();

        private void loadThumb()
        {
            DispatcherHelper.BeginInvokeOnUIThread(async () =>
            {
                var img = new BitmapImage();
                try
                {
                    if (this.imageFile != null)
                    {
                        using (var stream = await this.imageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 180, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail))
                        {
                            await img.SetSourceAsync(stream);
                        }
                    }
                    else if (this.thumbUri != null)
                    {
                        var buffer = await thumbClient.GetBufferAsync(this.thumbUri);
                        using (var stream = buffer.AsRandomAccessStream())
                        {
                            await img.SetSourceAsync(stream);
                        }
                    }
                    else
                    {
                        img = null;
                    }
                }
                catch (Exception)
                {
                    img = null;
                }
                this.thumb.SetTarget(img);
                if (img != null)
                    RaisePropertyChanged(nameof(Thumb));
            });
        }

        public virtual ImageSource Thumb
        {
            get
            {
                if (this.thumb.TryGetTarget(out var thb))
                    return thb;
                loadThumb();
                return DefaultThumb;
            }
        }

        public Gallery Owner
        {
            get;
        }

        /// <summary>
        /// 1-based Id for image.
        /// </summary>
        public int PageId
        {
            get;
        }

        public Uri PageUri { get; }

        private IAsyncAction loadImageAction;

        public virtual IAsyncAction LoadImageAsync(bool reload, ConnectionStrategy strategy, bool throwIfFailed)
        {
            var previousAction = this.loadImageAction;
            var previousEnded = previousAction == null || previousAction.Status != AsyncStatus.Started;
            switch (this.state)
            {
            case ImageLoadingState.Loading:
            case ImageLoadingState.Loaded:
                if (!reload)
                {
                    if (previousEnded)
                        return AsyncWrapper.CreateCompleted();
                    return PollingAsyncWrapper.Wrap(previousAction, 1500);
                }
                else
                {
                    if (!previousEnded)
                        previousAction?.Cancel();
                }
                break;
            case ImageLoadingState.Preparing:
                if (previousEnded)
                    return AsyncWrapper.CreateCompleted();
                return PollingAsyncWrapper.Wrap(previousAction, 1500);
            }
            return this.loadImageAction = startLoadImageAsync(strategy, throwIfFailed);
        }

        private IAsyncAction startLoadImageAsync(ConnectionStrategy strategy, bool throwIfFailed)
        {
            return Run(async token =>
            {
                try
                {
                    this.State = ImageLoadingState.Preparing;
                    this.Progress = 0;
                    var loadImgUri = this.loadImageUri();
                    IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> loadImg = null;
                    token.Register(() =>
                    {
                        loadImgUri.Cancel();
                        loadImg?.Cancel();
                    });
                    await loadImgUri;
                    if (this.imageUri.LocalPath.EndsWith("/509.gif"))
                        throw new InvalidOperationException(LocalizedStrings.Resources.ExceedLimits);
                    Uri imgUri = null;
                    var loadFull = !ConnectionHelper.IsLofiRequired(strategy);
                    if (loadFull)
                    {
                        imgUri = this.originalImageUri ?? this.imageUri;
                        this.OriginalLoaded = true;
                    }
                    else
                    {
                        imgUri = this.imageUri;
                        this.OriginalLoaded = (this.originalImageUri == null);
                    }
                    this.State = ImageLoadingState.Loading;
                    token.ThrowIfCancellationRequested();
                    loadImg = Client.Current.HttpClient.GetAsync(imgUri);
                    loadImg.Progress = loadImgProgress;
                    var imageLoadResponse = await loadImg;
                    if (imageLoadResponse.Content.Headers.ContentType.MediaType == "text/html")
                    {
                        var error = HtmlUtilities.ConvertToText(imageLoadResponse.Content.ToString());
                        if (error.StartsWith("You have exceeded your image viewing limits."))
                        {
                            throw new InvalidOperationException(LocalizedStrings.Resources.ExceedLimits);
                        }
                        throw new InvalidOperationException(error);
                    }
                    token.ThrowIfCancellationRequested();
                    await this.deleteImageFileAsync();
                    var buffer = await imageLoadResponse.Content.ReadAsBufferAsync();
                    var ext = Path.GetExtension(imageLoadResponse.RequestMessage.RequestUri.LocalPath);
                    var pageId = this.PageId;
                    var folder = this.Owner.GalleryFolder ?? await this.Owner.GetFolderAsync();
                    this.ImageFile = await folder.SaveFileAsync($"{pageId}{ext}", CreationCollisionOption.ReplaceExisting, buffer);
                    using (var db = new Models.GalleryDb())
                    {
                        var gid = this.Owner.Id;
                        var myModel = db.ImageSet.SingleOrDefault(model => model.OwnerId == gid && model.PageId == pageId);
                        if (myModel == null)
                        {
                            db.ImageSet.Add(new Models.ImageModel().Update(this));
                        }
                        else
                        {
                            myModel.Update(this);
                        }
                        db.SaveChanges();
                    }
                    this.State = ImageLoadingState.Loaded;
                }
                catch (TaskCanceledException) { throw; }
                catch (Exception)
                {
                    this.Progress = 100;
                    this.State = ImageLoadingState.Failed;
                    if (throwIfFailed)
                        throw;
                }
            });
        }

        private void loadImgProgress(IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> asyncInfo, HttpProgress progress)
        {
            if (progress.TotalBytesToReceive == null || progress.TotalBytesToReceive == 0)
                this.Progress = 0;
            else
            {
                var pro = (int)(progress.BytesReceived * 100 / ((ulong)progress.TotalBytesToReceive));
                this.Progress = pro;
            }
        }

        private async Task deleteImageFileAsync()
        {
            var file = this.ImageFile;
            if (file != null)
            {
                this.ImageFile = null;
                await file.DeleteAsync();
            }
        }

        private int progress;

        public int Progress
        {
            get => progress;
            private set => Set(ref progress, value);
        }

        private Uri imageUri;
        private Uri originalImageUri;

        private StorageFile imageFile;

        public StorageFile ImageFile
        {
            get => this.imageFile;
            protected set
            {
                Set(ref this.imageFile, value);
                if (value != null)
                {
                    loadThumb();
                }
            }
        }

        private ulong imageKey;

        public ulong ImageKey
        {
            get => this.imageKey;
            protected set => Set(nameof(PageUri), ref this.imageKey, value);
        }

        private string failToken;

        public bool OriginalLoaded
        {
            get => this.originalLoaded;
            private set => Set(ref this.originalLoaded, value);
        }

        private bool originalLoaded;
    }
}
