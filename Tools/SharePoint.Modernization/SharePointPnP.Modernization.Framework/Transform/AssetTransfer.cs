﻿using Microsoft.SharePoint.Client;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Extensions;
using SharePointPnP.Modernization.Framework.Telemetry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharePointPnP.Modernization.Framework.Transform
{
    /// <summary>
    /// Class for operations for transferring the assets over to the target site collection
    /// </summary>
    public class AssetTransfer : BaseTransform
    {
        //Plan:
        //  Detect for referenced assets within the web parts
        //  Referenced assets should only be files e.g. not aspx pages and located in the pages, site pages libraries
        //  Ensure the referenced assets exist within the same site collection/web according to the level of transformation
        //  With the modern destination, locate assets in the site assets library with in a folder using the same naming convention as SharePoint Comm Sites
        //  Add copy assets method to transfer the files to target site collection
        //  Store a dictionary of copied assets to update the URLs of the transferred web parts
        //  Phased approach for this: 
        //      Image Web Parts
        //      Text Web Parts with inline images (need to determine how they are handled)
        //      TBC - expanded as testing progresses

        private ClientContext _sourceClientContext;
        private ClientContext _targetClientContext;

        /// <summary>
        /// Constructor for the asset transfer class
        /// </summary>
        /// <param name="source">Source connection to SharePoint</param>
        /// <param name="target">Target connection to SharePoint</param>
        public AssetTransfer(ClientContext source, ClientContext target, IList<ILogObserver> logObservers = null)
        {
            if (logObservers != null)
            {
                foreach (var observer in logObservers)
                {
                    base.RegisterObserver(observer);
                }
            }

            _sourceClientContext = source;
            _targetClientContext = target;

            Validate(); // Perform validation
        }

        /// <summary>
        /// Perform validation
        /// </summary>
        public void Validate()
        {
            if (_sourceClientContext == null || _targetClientContext == null)
            {
                LogError(LogStrings.Error_AssetTransferClientContextNull, LogStrings.Heading_AssetTransfer);
                throw new ArgumentNullException(LogStrings.Error_AssetTransferClientContextNull);
            }
        }

        /// <summary>
        /// Main entry point to perform the series of operations to transfer related assets
        /// </summary>
        public string TransferAsset(string sourceAssetRelativeUrl, string pageFileName)
        {

            // Deep validation of urls
            var isValid = ValidateAssetInSupportedLocation(sourceAssetRelativeUrl) && !string.IsNullOrEmpty(pageFileName);

            // Check the string is not null
            if (!string.IsNullOrEmpty(sourceAssetRelativeUrl) && isValid)
            {

                // Check the target library exists
                string targetFolderServerRelativeUrl = EnsureDestination(pageFileName);
                // Read in a preferred location

                // Check that the operation to transfer an asset hasnt already been performed for the file on different web parts.
                var assetDetails = GetAssetTransferredIfExists(
                    new AssetTransferredEntity() { SourceAssetUrl = sourceAssetRelativeUrl, TargetAssetFolderUrl = targetFolderServerRelativeUrl });

                if (string.IsNullOrEmpty(assetDetails.TargetAssetTransferredUrl))
                {
                    // Ensures the source context is set to the location of the asset file
                    EnsureAssetContextIfRequired(sourceAssetRelativeUrl);

                    // Copy the asset file
                    string newLocationUrl = CopyAssetToTargetLocation(sourceAssetRelativeUrl, targetFolderServerRelativeUrl);
                    assetDetails.TargetAssetTransferredUrl = newLocationUrl;

                    // Store a reference in the cache manager - ensure a test exists with multiple identical web parts
                    StoreAssetTransferred(assetDetails);

                }

                var finalPath = assetDetails.TargetAssetTransferredUrl;
                LogInfo($"{LogStrings.AssetTransferredToUrl}: {finalPath}", LogStrings.Heading_Summary);
                return finalPath;

            }

            // Fall back to send back the same link
            LogWarning(LogStrings.AssetTransferFailedFallback, LogStrings.Heading_AssetTransfer);
            return sourceAssetRelativeUrl;
        }


        /// <summary>
        /// Checks if the URL is located in a supported location
        /// </summary>
        public bool ValidateAssetInSupportedLocation(string sourceUrl)
        {
            //  Referenced assets should only be files e.g. 
            //      not aspx pages 
            //      located in the pages, site pages libraries

            var fileExtension = Path.GetExtension(sourceUrl).ToLower();

            // Check block list
            var containsBlockedExtension = Constants.BlockedAssetFileExtensions.Any(o => o == fileExtension.Replace(".", ""));
            if (containsBlockedExtension)
            {
                return false;
            }

            // Check allow list
            var containsAllowedExtension = Constants.AllowedAssetFileExtensions.Any(o => o == fileExtension.Replace(".", ""));
            if (!containsAllowedExtension)
            {
                return false;
            }

            //  Ensure the referenced assets exist within the source site collection
            var sourceSiteContextUrl = _sourceClientContext.Site.EnsureProperty(w => w.ServerRelativeUrl);

            //TODO: Bug, doesnt take into account casing and can fail
            if (!sourceUrl.ContainsIgnoringCasing(sourceSiteContextUrl))
            {
                return false;
            }

            //  Ensure the contexts are not e.g. cross-site the same site collection/web according to the level of transformation
            var targetSiteContextUrl = _targetClientContext.Site.EnsureProperty(w => w.ServerRelativeUrl);
            if (sourceSiteContextUrl == targetSiteContextUrl)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Ensure the site assets and page sub-folder exists in the target location
        /// </summary>
        public string EnsureDestination(string pageFileName)
        {
            // In this method we need to calculate the target location from the following factors
            //  Target Site Context + Site Assets Library + Folder (if located in or calculate based on SP method)
            //  Check the libary and folder exists in the target site collection
            //  Currently this method ignores anything from the source, will probabily need an override or params for target location

            // Ensure the Site Assets library exists
            var siteAssetsLibrary = this.EnsureSiteAssetsLibrary();
            var sitePagesFolder = siteAssetsLibrary.RootFolder.EnsureFolder("SitePages");

            var friendlyFolder = ConvertFileToFolderFriendlyName(pageFileName);
            var pageFolder = sitePagesFolder.EnsureFolder(friendlyFolder);

            return pageFolder.EnsureProperty(o => o.ServerRelativeUrl);

        }

        /// <summary>
        /// Create a site assets library
        /// </summary>
        public List EnsureSiteAssetsLibrary()
        {
            // Use a PnP Provisioning template to create a site assets library
            // We cannot assume the SiteAssets library exists, in the case of vanilla communication sites - provision a new library if none exists
            // If a site assets library exist, add a folder, into the library using the same format as SharePoint uses for creating sub folders for pages

            //Ensure that the Site Assets library is created using the out of the box creation mechanism
            //Site Assets that are created using the EnsureSiteAssetsLibrary method slightly differ from
            //default Document Libraries. See issue 512 (https://github.com/SharePoint/PnP-Sites-Core/issues/512)
            //for details about the issue fixed by this approach.
            var createdList = this._targetClientContext.Web.Lists.EnsureSiteAssetsLibrary();
            //Check that Title and Description have the correct values
            this._targetClientContext.Web.Context.Load(createdList, l => l.Title, l => l.RootFolder);
            this._targetClientContext.Web.Context.ExecuteQueryRetry();

            return createdList;
        }

        /// <summary>
        /// Copy the file from the source to the target location
        /// </summary>
        /// <param name="sourceFileUrl"></param>
        /// <param name="targetLocationUrl"></param>
        /// <remarks>
        ///     Based on the documentation: https://docs.microsoft.com/en-us/sharepoint/dev/solution-guidance/upload-large-files-sample-app-for-sharepoint
        /// </remarks>
        public string CopyAssetToTargetLocation(string sourceFileUrl, string targetLocationUrl, int fileChunkSizeInMB = 3)
        {
            // This copies the latest version of the asset to the target site collection
            // Going to need to add a bunch of checks to ensure the target file exists

            // Each sliced upload requires a unique ID.
            Guid uploadId = Guid.NewGuid();
            // Calculate block size in bytes.
            int blockSize = fileChunkSizeInMB * 1024 * 1024;
            bool fileOverwrite = true;

            // Get the file from SharePoint
            var sourceAssetFile = _sourceClientContext.Web.GetFileByServerRelativeUrl(sourceFileUrl);
            ClientResult<System.IO.Stream> sourceAssetFileData = sourceAssetFile.OpenBinaryStream();

            _sourceClientContext.Load(sourceAssetFile);
            _sourceClientContext.ExecuteQueryRetry();

            using (Stream sourceFileStream = sourceAssetFileData.Value)
            {

                string fileName = sourceAssetFile.Name;

                // New File object.
                Microsoft.SharePoint.Client.File uploadFile;

                // Get the information about the folder that will hold the file.
                // Add the file to the target site
                Folder targetFolder = _targetClientContext.Web.GetFolderByServerRelativeUrl(targetLocationUrl);
                _targetClientContext.Load(targetFolder);
                _targetClientContext.ExecuteQueryRetry();

                // Get the file size
                long fileSize = sourceFileStream.Length;

                // Process with two approaches
                if (fileSize <= blockSize)
                {

                    // Use regular approach.

                    FileCreationInformation fileInfo = new FileCreationInformation();
                    fileInfo.ContentStream = sourceFileStream;
                    fileInfo.Url = fileName;
                    fileInfo.Overwrite = fileOverwrite;

                    uploadFile = targetFolder.Files.Add(fileInfo);
                    _targetClientContext.Load(uploadFile);
                    _targetClientContext.ExecuteQuery();

                    // Return the file object for the uploaded file.
                    return uploadFile.EnsureProperty(o => o.ServerRelativeUrl);

                }
                else
                {
                    // Use large file upload approach.
                    ClientResult<long> bytesUploaded = null;

                    using (BinaryReader br = new BinaryReader(sourceFileStream))
                    {
                        byte[] buffer = new byte[blockSize];
                        Byte[] lastBuffer = null;
                        long fileoffset = 0;
                        long totalBytesRead = 0;
                        int bytesRead;
                        bool first = true;
                        bool last = false;

                        // Read data from file system in blocks. 
                        while ((bytesRead = br.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead = totalBytesRead + bytesRead;

                            // You've reached the end of the file.
                            if (totalBytesRead == fileSize)
                            {
                                last = true;
                                // Copy to a new buffer that has the correct size.
                                lastBuffer = new byte[bytesRead];
                                Array.Copy(buffer, 0, lastBuffer, 0, bytesRead);
                            }

                            if (first)
                            {
                                using (MemoryStream contentStream = new MemoryStream())
                                {
                                    // Add an empty file.
                                    FileCreationInformation fileInfo = new FileCreationInformation();
                                    fileInfo.ContentStream = contentStream;
                                    fileInfo.Url = fileName;
                                    fileInfo.Overwrite = fileOverwrite;
                                    uploadFile = targetFolder.Files.Add(fileInfo);

                                    // Start upload by uploading the first slice. 
                                    using (MemoryStream s = new MemoryStream(buffer))
                                    {
                                        // Call the start upload method on the first slice.
                                        bytesUploaded = uploadFile.StartUpload(uploadId, s);
                                        _targetClientContext.ExecuteQueryRetry();
                                        // fileoffset is the pointer where the next slice will be added.
                                        fileoffset = bytesUploaded.Value;
                                    }

                                    // You can only start the upload once.
                                    first = false;
                                }
                            }
                            else
                            {
                                // Get a reference to your file.
                                var fileUrl = targetFolder.ServerRelativeUrl + System.IO.Path.AltDirectorySeparatorChar + fileName;
                                uploadFile = _targetClientContext.Web.GetFileByServerRelativeUrl(fileUrl);

                                if (last)
                                {
                                    // Is this the last slice of data?
                                    using (MemoryStream s = new MemoryStream(lastBuffer))
                                    {
                                        // End sliced upload by calling FinishUpload.
                                        uploadFile = uploadFile.FinishUpload(uploadId, fileoffset, s);
                                        _targetClientContext.ExecuteQuery();

                                        // Return the file object for the uploaded file.
                                        return fileUrl;
                                    }
                                }
                                else
                                {
                                    using (MemoryStream s = new MemoryStream(buffer))
                                    {
                                        // Continue sliced upload.
                                        bytesUploaded = uploadFile.ContinueUpload(uploadId, fileoffset, s);
                                        _targetClientContext.ExecuteQuery();
                                        // Update fileoffset for the next slice.
                                        fileoffset = bytesUploaded.Value;
                                    }
                                }
                            }
                        }
                    }

                }

            }

            return null;
        }

        /// <summary>
        /// Stores an asset transfer reference
        /// </summary>
        /// <param name="assetTransferReferenceEntity"></param>
        /// <param name="update"></param>
        public void StoreAssetTransferred(AssetTransferredEntity assetTransferredEntity)
        {
            // Using the Cache Manager store the asset transfer references
            // If update - treat the source URL as unique, if multiple web parts reference to this, then it will still refer to the single resource
            var cache = Cache.CacheManager.Instance;
            if (!cache.AssetsTransfered.Any(asset =>
                 string.Equals(asset.TargetAssetTransferredUrl, assetTransferredEntity.TargetAssetFolderUrl, StringComparison.InvariantCultureIgnoreCase)))
            {
                cache.AssetsTransfered.Add(assetTransferredEntity);
            }

        }

        /// <summary>
        /// Get asset transfer details if they already exist
        /// </summary>
        public AssetTransferredEntity GetAssetTransferredIfExists(AssetTransferredEntity assetTransferredEntity)
        {
            try
            {
                // Using the Cache Manager retrieve asset transfer references (all)
                var cache = Cache.CacheManager.Instance;

                var result = cache.AssetsTransfered.SingleOrDefault(
                    asset => string.Equals(asset.TargetAssetFolderUrl, assetTransferredEntity.TargetAssetFolderUrl, StringComparison.InvariantCultureIgnoreCase) &&
                    string.Equals(asset.SourceAssetUrl, assetTransferredEntity.SourceAssetUrl, StringComparison.InvariantCultureIgnoreCase));

                // Return the cached details if found, if not return original search 
                return result != default(AssetTransferredEntity) ? result : assetTransferredEntity;
            }
            catch (Exception ex)
            {
                LogError(LogStrings.Error_AssetTransferCheckingIfAssetExists, LogStrings.Heading_AssetTransfer, ex);
            }

            // Fallback in case of error - this will trigger a transfer of the asset
            return assetTransferredEntity;

        }

        /// <summary>
        /// Converts the file name into a friendly format
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public string ConvertFileToFolderFriendlyName(string fileName)
        {
            // This is going to need some heavy testing
            var justFileName = Path.GetFileNameWithoutExtension(fileName);
            var friendlyName = justFileName.Replace(" ", "-");
            return friendlyName;
        }


        /// <summary>
        /// Ensures that we have context of the source site collection
        /// </summary>
        internal void EnsureAssetContextIfRequired(string sourceUrl)
        {
            EnsureAssetContextIfRequired(_sourceClientContext, sourceUrl);
        }


        /// <summary>
        /// Ensures that we have context of the source site collection
        /// </summary>
        /// <param name="context">Source site context</param>
        internal void EnsureAssetContextIfRequired(ClientContext context, string sourceUrl)
        {
            // There is two scenarios to check
            //  - If the asset resides on the root site collection
            //  - If the asset resides on another subsite
            //  - If the asset resides on a subsite below this context
            // Check - if the error is to check teh siterelativeurl, what if we just start at rootweb then get the file?
            
            try
            {
                context.Site.EnsureProperties(o => o.ServerRelativeUrl, o => o.Url);
                context.Web.EnsureProperties(o => o.ServerRelativeUrl, o => o.Url);
                var subWebUrls = context.Site.GetAllSubSites(); // This could be an expensive call

                var fullSiteCollectionUrl = context.Site.Url;

                string match = string.Empty;
                foreach (var subWebUrl in subWebUrls.OrderByDescending(o => o.Length))
                {

                    var hostUri = new Uri(subWebUrl);
                    string host = $"{hostUri.Scheme}://{hostUri.DnsSafeHost}";
                    var relativeSubWebUrl = subWebUrl.Replace(host, "");

                    if (sourceUrl.ContainsIgnoringCasing(relativeSubWebUrl))
                    {
                        match = subWebUrl;
                        break;
                    }
                }

                if (match != string.Empty && match != context.Web.Url)
                {
                    _sourceClientContext = context.Clone(match);
                }

            }
            catch (Exception ex)
            {
                LogError(LogStrings.Error_CannotGetSiteCollContext, LogStrings.Heading_AssetTransfer, ex);
            }
        }
    }
}
