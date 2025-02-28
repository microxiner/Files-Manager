using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Extensions;
using Files.App.Filesystem.FilesystemHistory;
using Files.App.Filesystem.StorageItems;
using Files.App.Helpers;
using Files.App.Shell;
using Files.Backend.Services;
using Files.Backend.ViewModels.Dialogs;
using Files.Backend.ViewModels.Dialogs.FileSystemDialog;
using Files.Shared;
using Files.Shared.Enums;
using Files.Shared.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Files.App.Filesystem
{
    public class ShellFilesystemOperations : IFilesystemOperations
    {
        #region Private Members

        private IShellPage associatedInstance;

        private FilesystemOperations filesystemOperations;

        private RecycleBinHelpers recycleBinHelpers;

        private IDialogService DialogService { get; } = Ioc.Default.GetRequiredService<IDialogService>();

        private readonly JsonElement defaultJson = JsonSerializer.SerializeToElement("{}");

        #endregion Private Members

        #region Constructor

        public ShellFilesystemOperations(IShellPage associatedInstance)
        {
            this.associatedInstance = associatedInstance;
            filesystemOperations = new FilesystemOperations(associatedInstance);
            recycleBinHelpers = new RecycleBinHelpers();
        }

        #endregion Constructor

        public async Task<IStorageHistory> CopyAsync(IStorageItem source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> CopyAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyItemsAsync(source.CreateList(), destination.CreateList(), collision.ConvertBack().CreateList(), progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> CopyItemsAsync(IList<IStorageItem> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyItemsAsync(await source.Select((item) => item.FromStorageItem()).ToListAsync(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> CopyItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\", StringComparison.Ordinal)) || destination.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(x) || ZipStorageFolder.IsZipPath(x, false)))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.CopyItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
            }

            var sourceNoSkip = source.Zip(collisions, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.Skip).Select(item => item.src);
            var destinationNoSkip = destination.Zip(collisions, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.Skip).Select(item => item.src);
            var collisionsNoSkip = collisions.Where(c => c != FileNameConflictResolveOptionType.Skip);

            var operationID = Guid.NewGuid().ToString();
            using var r = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, JsonElement>> handler = (s, e) => OnProgressUpdated(s, e, operationID, progress);
            connection.RequestReceived += handler;

            var sourceReplace = sourceNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll == FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var destinationReplace = destinationNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll == FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var sourceRename = sourceNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var destinationRename = destinationNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);

            var result = (FilesystemResult)true;
            var copyResult = new ShellOperationResult();
            if (sourceRename.Any())
            {
                var resultItem = await FileOperationsHelpers.CopyItemAsync(sourceRename.Select(s => s.Path).ToArray(), destinationRename.ToArray(), false, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);
                
                result &= (FilesystemResult)resultItem.Item1;

                copyResult.Items.AddRange(resultItem.Item2?.Final ?? Enumerable.Empty<ShellOperationItemResult>());
            }
            if (sourceReplace.Any())
            {
                var resultItem = await FileOperationsHelpers.CopyItemAsync(sourceReplace.Select(s => s.Path).ToArray(), destinationReplace.ToArray(), true, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);

                result &= (FilesystemResult)resultItem.Item1;

                copyResult.Items.AddRange(resultItem.Item2?.Final ?? Enumerable.Empty<ShellOperationItemResult>());
            }

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            result &= (FilesystemResult)copyResult.Items.All(x => x.Succeeded);

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                var copiedSources = copyResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination);
                if (copiedSources.Any())
                {
                    var sourceMatch = await copiedSources.Select(x => sourceRename
                        .SingleOrDefault(s => s.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return new StorageHistory(FileOperationType.Copy,
                        sourceMatch,
                        await copiedSources.Zip(sourceMatch, (rSrc, oSrc) => new { rSrc, oSrc })
                            .Select(item => StorageHelpers.FromPathAndType(item.rSrc.Destination, item.oSrc.ItemType)).ToListAsync());
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                if (copyResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await CopyItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
                    }
                }
                else if (copyResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse))
                {
                    var failedSources = copyResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse);
                    var filePath = failedSources.Select(x => x.HResult == CopyEngineResult.COPYENGINE_E_SHARING_VIOLATION_SRC ? x.Source : x.Destination);
                    var lockingProcess = WhoIsLocking(filePath);
                    switch (await GetFileInUseDialog(filePath, lockingProcess))
                    {
                        case DialogResult.Primary:
                            var copyZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                            var sourceMatch = await failedSources.Select(x => copyZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                            return await CopyItemsAsync(
                                await sourceMatch.Select(x => x.src).ToListAsync(),
                                await sourceMatch.Select(x => x.dest).ToListAsync(),
                                await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                    }
                }
                else if (copyResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Retry with StorageFile API
                    var failedSources = copyResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong);
                    var copyZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                    var sourceMatch = await failedSources.Select(x => copyZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.CopyItemsAsync(
                        await sourceMatch.Select(x => x.src).ToListAsync(),
                        await sourceMatch.Select(x => x.dest).ToListAsync(),
                        await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                }
                else if (copyResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalizedResource(), "FileNotFoundDialog/Text".GetLocalizedResource());
                }
                else if (copyResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalizedResource(), "ItemAlreadyExistsDialogContent".GetLocalizedResource());
                }
                else if (copyResult.Items.All(x => x.HResult == -1)) // ADS
                {
                    // Retry with StorageFile API
                    var failedSources = copyResult.Items.Where(x => !x.Succeeded);
                    var copyZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                    var sourceMatch = await failedSources.Select(x => copyZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.CopyItemsAsync(
                        await sourceMatch.Select(x => x.src).ToListAsync(),
                        await sourceMatch.Select(x => x.dest).ToListAsync(),
                        await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                }
                errorCode?.Report(CopyEngineResult.Convert(copyResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return null;
            }
        }

        public async Task<(IStorageHistory, IStorageItem)> CreateAsync(IStorageItemWithPath source, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || string.IsNullOrWhiteSpace(source.Path) || source.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(source.Path) || ZipStorageFolder.IsZipPath(source.Path, false))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.CreateAsync(source, errorCode, cancellationToken);
            }

            var createResult = new ShellOperationResult();
            (var success, var response) = (false, new ShellOperationResult());

            switch (source.ItemType)
            {
                case FilesystemItemType.File:
                    {
                        var newEntryInfo = await ShellNewEntryExtensions.GetNewContextMenuEntryForType(Path.GetExtension(source.Path));
                        if (newEntryInfo?.Command != null)
                        {
                            var args = CommandLine.CommandLineParser.SplitArguments(newEntryInfo.Command);
                            if (args.Any())
                            {
                                if (await LaunchHelper.LaunchAppAsync(args[0].Replace("\"", "", StringComparison.Ordinal),
                                        string.Join(" ", args.Skip(1)).Replace("%1", source.Path),
                                        PathNormalization.GetParentDir(source.Path)))
                                {
                                    success = true;
                                }
                            }
                        }
                        else
                            (success, response) = await FileOperationsHelpers.CreateItemAsync(source.Path, "CreateFile", newEntryInfo?.Template, newEntryInfo?.Data);
                        break;
                    }
                case FilesystemItemType.Directory:
                    {
                        (success, response) = await FileOperationsHelpers.CreateItemAsync(source.Path, "CreateFolder");
                        break;
                    }
            }

            var result = (FilesystemResult)success;
            var shellOpResult = response;
            createResult.Items.AddRange(shellOpResult?.Final ?? Enumerable.Empty<ShellOperationItemResult>());

            result &= (FilesystemResult)createResult.Items.All(x => x.Succeeded);

            if (result)
            {
                errorCode?.Report(FileSystemStatusCode.Success);
                var createdSources = createResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination);
                if (createdSources.Any())
                {
                    var item = StorageHelpers.FromPathAndType(createdSources.Single().Destination, source.ItemType);
                    var storageItem = await item.ToStorageItem();
                    return (new StorageHistory(FileOperationType.CreateNew, item.CreateList(), null), storageItem);
                }
                return (null, null);
            }
            else
            {
                if (createResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await CreateAsync(source, errorCode, cancellationToken);
                    }
                }
                else if (createResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Retry with StorageFile API
                    return await filesystemOperations.CreateAsync(source, errorCode, cancellationToken);
                }
                else if (createResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalizedResource(), "FileNotFoundDialog/Text".GetLocalizedResource());
                }
                else if (createResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalizedResource(), "ItemAlreadyExistsDialogContent".GetLocalizedResource());
                }
                errorCode?.Report(CopyEngineResult.Convert(createResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return (null, null);
            }
        }

        public async Task<IStorageHistory> CreateShortcutItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var createdSources = new List<IStorageItemWithPath>();
            var createdDestination = new List<IStorageItemWithPath>();

            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var items = source.Zip(destination, (src, dest, index) => new { src, dest, index }).Where(x => !string.IsNullOrEmpty(x.src.Path) && !string.IsNullOrEmpty(x.dest));
                foreach (var item in items)
                {
                    if (await FileOperationsHelpers.CreateOrUpdateLinkAsync(item.dest, item.src.Path))
                    {
                        createdSources.Add(item.src);
                        createdDestination.Add(StorageHelpers.FromPathAndType(item.dest, FilesystemItemType.File));
                    }
                    progress?.Report(item.index / (float)source.Count * 100.0f);
                }
            }

            errorCode?.Report(createdSources.Count == source.Count ? FileSystemStatusCode.Success : FileSystemStatusCode.Generic);
            return new StorageHistory(FileOperationType.CreateLink, createdSources, createdDestination);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItem source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteAsync(source.FromStorageItem(),
                                                      progress,
                                                      errorCode,
                                                      permanently,
                                                      cancellationToken);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItemWithPath source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteItemsAsync(source.CreateList(), progress, errorCode, permanently, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IList<IStorageItem> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteItemsAsync(await source.Select((item) => item.FromStorageItem()).ToListAsync(), progress, errorCode, permanently, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IList<IStorageItemWithPath> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(x.Path)))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.DeleteItemsAsync(source, progress, errorCode, permanently, cancellationToken);
            }

            var deleleFilePaths = source.Select(s => s.Path).Distinct();
            var deleteFromRecycleBin = source.Any() ? recycleBinHelpers.IsPathUnderRecycleBin(source.ElementAt(0).Path) : false;
            permanently |= deleteFromRecycleBin;

            if (deleteFromRecycleBin)
            {
                // Recycle bin also stores a file starting with $I for each item
                deleleFilePaths = deleleFilePaths.Concat(source.Select(x => Path.Combine(Path.GetDirectoryName(x.Path), Path.GetFileName(x.Path).Replace("$R", "$I", StringComparison.Ordinal)))).Distinct();
            }

            var operationID = Guid.NewGuid().ToString();
            using var r = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, JsonElement>> handler = (s, e) => OnProgressUpdated(s, e, operationID, progress);
            connection.RequestReceived += handler;

            var (success, deleteResult) = await FileOperationsHelpers.DeleteItemAsync(deleleFilePaths.ToArray(), permanently, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);
            
            var result = (FilesystemResult)success;
            var shellOpResult = deleteResult;
            deleteResult.Items.AddRange(shellOpResult?.Final ?? Enumerable.Empty<ShellOperationItemResult>());

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            result &= (FilesystemResult)deleteResult.Items.All(x => x.Succeeded);

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                foreach (var item in deleteResult.Items)
                {
                    await associatedInstance.FilesystemViewModel.RemoveFileOrFolderAsync(item.Source);
                }
                var recycledSources = deleteResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination);
                if (recycledSources.Any())
                {
                    var sourceMatch = await recycledSources.Select(x => source.DistinctBy(x => x.Path)
                        .SingleOrDefault(s => s.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return new StorageHistory(FileOperationType.Recycle,
                        sourceMatch,
                        await recycledSources.Zip(sourceMatch, (rSrc, oSrc) => new { rSrc, oSrc })
                            .Select(item => StorageHelpers.FromPathAndType(item.rSrc.Destination, item.oSrc.ItemType)).ToListAsync());
                }
                return new StorageHistory(FileOperationType.Delete, source, null);
            }
            else
            {
                if (deleteResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await DeleteItemsAsync(source, progress, errorCode, permanently, cancellationToken);
                    }
                }
                else if (deleteResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse))
                {
                    var failedSources = deleteResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse);
                    var filePath = failedSources.Select(x => x.Source); // When deleting only source can be in use but shell returns COPYENGINE_E_SHARING_VIOLATION_DEST for folders
                    var lockingProcess = WhoIsLocking(filePath);
                    switch (await GetFileInUseDialog(filePath, lockingProcess))
                    {
                        case DialogResult.Primary:
                            return await DeleteItemsAsync(await failedSources.Select(x => source.DistinctBy(x => x.Path).SingleOrDefault(s => s.Path == x.Source)).Where(x => x != null).ToListAsync(), progress, errorCode, permanently, cancellationToken);
                    }
                }
                else if (deleteResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Abort, path is too long for recycle bin
                }
                else if (deleteResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalizedResource(), "FileNotFoundDialog/Text".GetLocalizedResource());
                }
                else if (deleteResult.Items.All(x => x.HResult == -1) && permanently) // ADS
                {
                    // Retry with StorageFile API
                    var failedSources = deleteResult.Items.Where(x => !x.Succeeded);
                    var sourceMatch = await failedSources.Select(x => source.DistinctBy(x => x.Path).SingleOrDefault(s => s.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.DeleteItemsAsync(sourceMatch, progress, errorCode, permanently, cancellationToken);
                }
                errorCode?.Report(CopyEngineResult.Convert(deleteResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return null;
            }
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItem source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveItemsAsync(source.CreateList(), destination.CreateList(), collision.ConvertBack().CreateList(), progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> MoveItemsAsync(IList<IStorageItem> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveItemsAsync(await source.Select((item) => item.FromStorageItem()).ToListAsync(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> MoveItemsAsync(IList<IStorageItemWithPath> source, IList<string> destination, IList<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\", StringComparison.Ordinal)) || destination.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(x) || ZipStorageFolder.IsZipPath(x, false)))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.MoveItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
            }

            var sourceNoSkip = source.Zip(collisions, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.Skip).Select(item => item.src);
            var destinationNoSkip = destination.Zip(collisions, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.Skip).Select(item => item.src);
            var collisionsNoSkip = collisions.Where(c => c != FileNameConflictResolveOptionType.Skip);

            var operationID = Guid.NewGuid().ToString();
            using var r = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, JsonElement>> handler = (s, e) => OnProgressUpdated(s, e, operationID, progress);
            connection.RequestReceived += handler;

            var sourceReplace = sourceNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll == FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var destinationReplace = destinationNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll == FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var sourceRename = sourceNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);
            var destinationRename = destinationNoSkip.Zip(collisionsNoSkip, (src, coll) => new { src, coll }).Where(item => item.coll != FileNameConflictResolveOptionType.ReplaceExisting).Select(item => item.src);

            var result = (FilesystemResult)true;
            var moveResult = new ShellOperationResult();
            if (sourceRename.Any())
            {
                var (status, response) = await FileOperationsHelpers.MoveItemAsync(sourceRename.Select(s => s.Path).ToArray(), destinationRename.ToArray(), false, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);

                result &= (FilesystemResult)status;
                moveResult.Items.AddRange(response?.Final ?? Enumerable.Empty<ShellOperationItemResult>());
            }
            if (sourceReplace.Any())
            {
                var (status, response) = await FileOperationsHelpers.MoveItemAsync(sourceReplace.Select(s => s.Path).ToArray(), destinationReplace.ToArray(), true, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);

                result &= (FilesystemResult)status;
                moveResult.Items.AddRange(response?.Final ?? Enumerable.Empty<ShellOperationItemResult>());
            }

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            result &= (FilesystemResult)moveResult.Items.All(x => x.Succeeded);

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                var movedSources = moveResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination);
                if (movedSources.Any())
                {
                    var sourceMatch = await movedSources.Select(x => sourceRename
                        .SingleOrDefault(s => s.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return new StorageHistory(FileOperationType.Move,
                        sourceMatch,
                        await movedSources.Zip(sourceMatch, (rSrc, oSrc) => new { rSrc, oSrc })
                            .Select(item => StorageHelpers.FromPathAndType(item.rSrc.Destination, item.oSrc.ItemType)).ToListAsync());
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await MoveItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
                    }
                }
                else if (source.Zip(destination, (src, dest) => (src, dest)).FirstOrDefault(x => x.src.ItemType == FilesystemItemType.Directory && PathNormalization.GetParentDir(x.dest).IsSubPathOf(x.src.Path)) is (IStorageItemWithPath, string) subtree)
                {
                    var destName = subtree.dest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    var srcName = subtree.src.Path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    await DialogDisplayHelper.ShowDialogAsync("ErrorDialogThisActionCannotBeDone".GetLocalizedResource(), $"{"ErrorDialogTheDestinationFolder".GetLocalizedResource()} ({destName}) {"ErrorDialogIsASubfolder".GetLocalizedResource()} ({srcName})");
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse))
                {
                    var failedSources = moveResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse);
                    var filePath = failedSources.Select(x => x.HResult == CopyEngineResult.COPYENGINE_E_SHARING_VIOLATION_SRC ? x.Source : x.Destination);
                    var lockingProcess = WhoIsLocking(filePath);
                    switch (await GetFileInUseDialog(filePath, lockingProcess))
                    {
                        case DialogResult.Primary:
                            var moveZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                            var sourceMatch = await failedSources.Select(x => moveZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                            return await MoveItemsAsync(
                                await sourceMatch.Select(x => x.src).ToListAsync(),
                                await sourceMatch.Select(x => x.dest).ToListAsync(),
                                await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                    }
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Retry with StorageFile API
                    var failedSources = moveResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong);
                    var moveZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                    var sourceMatch = await failedSources.Select(x => moveZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.MoveItemsAsync(
                        await sourceMatch.Select(x => x.src).ToListAsync(),
                        await sourceMatch.Select(x => x.dest).ToListAsync(),
                        await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalizedResource(), "FileNotFoundDialog/Text".GetLocalizedResource());
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalizedResource(), "ItemAlreadyExistsDialogContent".GetLocalizedResource());
                }
                else if (moveResult.Items.All(x => x.HResult == -1)) // ADS
                {
                    // Retry with StorageFile API
                    var failedSources = moveResult.Items.Where(x => !x.Succeeded);
                    var moveZip = sourceNoSkip.Zip(destinationNoSkip, (src, dest) => new { src, dest }).Zip(collisionsNoSkip, (z1, coll) => new { z1.src, z1.dest, coll });
                    var sourceMatch = await failedSources.Select(x => moveZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.MoveItemsAsync(
                        await sourceMatch.Select(x => x.src).ToListAsync(),
                        await sourceMatch.Select(x => x.dest).ToListAsync(),
                        await sourceMatch.Select(x => x.coll).ToListAsync(), progress, errorCode, cancellationToken);
                }
                errorCode?.Report(CopyEngineResult.Convert(moveResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return null;
            }
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItem source, string newName, NameCollisionOption collision, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await RenameAsync(StorageHelpers.FromStorageItem(source), newName, collision, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItemWithPath source, string newName, NameCollisionOption collision, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || string.IsNullOrWhiteSpace(source.Path) || source.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(source.Path) || ZipStorageFolder.IsZipPath(source.Path, false))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
            }

            var renameResult = new ShellOperationResult();

            var (status, response) = await FileOperationsHelpers.RenameItemAsync(source.Path, newName, collision == NameCollisionOption.ReplaceExisting);

            var result = (FilesystemResult)status;
            renameResult.Items.AddRange(response?.Final ?? Enumerable.Empty<ShellOperationItemResult>());

            result &= (FilesystemResult)renameResult.Items.All(x => x.Succeeded);

            if (result)
            {
                errorCode?.Report(FileSystemStatusCode.Success);
                var renamedSources = renameResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination)
                    .Where(x => new[] { source }.Select(s => s.Path).Contains(x.Source));
                if (renamedSources.Any())
                {
                    return new StorageHistory(FileOperationType.Rename, source,
                        StorageHelpers.FromPathAndType(renamedSources.Single().Destination, source.ItemType));
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                if (renameResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await RenameAsync(source, newName, collision, errorCode, cancellationToken);
                    }
                }
                else if (renameResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse))
                {
                    var failedSources = renameResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse);
                    var filePath = failedSources.Select(x => x.HResult == CopyEngineResult.COPYENGINE_E_SHARING_VIOLATION_SRC ? x.Source : x.Destination);
                    var lockingProcess = WhoIsLocking(filePath);
                    switch (await GetFileInUseDialog(filePath, lockingProcess))
                    {
                        case DialogResult.Primary:
                            return await RenameAsync(source, newName, collision, errorCode, cancellationToken);
                    }
                }
                else if (renameResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Retry with StorageFile API
                    return await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                }
                else if (renameResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("RenameError/ItemDeleted/Title".GetLocalizedResource(), "RenameError/ItemDeleted/Text".GetLocalizedResource());
                }
                else if (renameResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalizedResource(), "ItemAlreadyExistsDialogContent".GetLocalizedResource());
                }
                else if (renameResult.Items.All(x => x.HResult == -1)) // ADS
                {
                    // Retry with StorageFile API
                    return await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                }
                errorCode?.Report(CopyEngineResult.Convert(renameResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return null;
            }
        }

        public async Task<IStorageHistory> RestoreFromTrashAsync(IStorageItem source, string destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await RestoreFromTrashAsync(source.FromStorageItem(), destination, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RestoreFromTrashAsync(IStorageItemWithPath source, string destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await RestoreItemsFromTrashAsync(source.CreateList(), destination.CreateList(), progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RestoreItemsFromTrashAsync(IList<IStorageItem> source, IList<string> destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await RestoreItemsFromTrashAsync(await source.Select((item) => item.FromStorageItem()).ToListAsync(), destination, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RestoreItemsFromTrashAsync(IList<IStorageItemWithPath> source, IList<string> destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\", StringComparison.Ordinal)) || destination.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith(@"\\?\", StringComparison.Ordinal) || FtpHelpers.IsFtpPath(x) || ZipStorageFolder.IsZipPath(x, false)))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.RestoreItemsFromTrashAsync(source, destination, progress, errorCode, cancellationToken);
            }

            var operationID = Guid.NewGuid().ToString();
            using var r = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, JsonElement>> handler = (s, e) => OnProgressUpdated(s, e, operationID, progress);
            connection.RequestReceived += handler;

            var moveResult = new ShellOperationResult();
            var (status, response) = await FileOperationsHelpers.MoveItemAsync(source.Select(s => s.Path).ToArray(), destination.ToArray(), false, NativeWinApiHelper.CoreWindowHandle.ToInt64(), operationID);

            var result = (FilesystemResult)status;
            moveResult.Items.AddRange(response?.Final ?? Enumerable.Empty<ShellOperationItemResult>());

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            result &= (FilesystemResult)moveResult.Items.All(x => x.Succeeded);

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                var movedSources = moveResult.Items.Where(x => x.Succeeded && x.Destination != null && x.Source != x.Destination);
                if (movedSources.Any())
                {
                    var sourceMatch = await movedSources.Select(x => source
                        .SingleOrDefault(s => s.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    // Recycle bin also stores a file starting with $I for each item
                    await DeleteItemsAsync(await movedSources.Zip(sourceMatch, (rSrc, oSrc) => new { rSrc, oSrc })
                        .Select(src => StorageHelpers.FromPathAndType(
                            Path.Combine(Path.GetDirectoryName(src.rSrc.Source), Path.GetFileName(src.rSrc.Source).Replace("$R", "$I", StringComparison.Ordinal)),
                            src.oSrc.ItemType)).ToListAsync(), null, null, true, cancellationToken);
                    return new StorageHistory(FileOperationType.Restore,
                        sourceMatch,
                        await movedSources.Zip(sourceMatch, (rSrc, oSrc) => new { rSrc, oSrc })
                            .Select(item => StorageHelpers.FromPathAndType(item.rSrc.Destination, item.oSrc.ItemType)).ToListAsync());
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.Unauthorized))
                {
                    if (await RequestAdminOperation())
                    {
                        return await RestoreItemsFromTrashAsync(source, destination, progress, errorCode, cancellationToken);
                    }
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse))
                {
                    var failedSources = moveResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.InUse);
                    var filePath = failedSources.Select(x => x.HResult == CopyEngineResult.COPYENGINE_E_SHARING_VIOLATION_SRC ? x.Source : x.Destination);
                    var lockingProcess = WhoIsLocking(filePath);
                    switch (await GetFileInUseDialog(filePath, lockingProcess))
                    {
                        case DialogResult.Primary:
                            var moveZip = source.Zip(destination, (src, dest) => new { src, dest });
                            var sourceMatch = await failedSources.Select(x => moveZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                            return await RestoreItemsFromTrashAsync(
                                await sourceMatch.Select(x => x.src).ToListAsync(),
                                await sourceMatch.Select(x => x.dest).ToListAsync(), progress, errorCode, cancellationToken);
                    }
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong))
                {
                    // Retry with StorageFile API
                    var failedSources = moveResult.Items.Where(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NameTooLong);
                    var moveZip = source.Zip(destination, (src, dest) => new { src, dest });
                    var sourceMatch = await failedSources.Select(x => moveZip.SingleOrDefault(s => s.src.Path.Equals(x.Source, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToListAsync();
                    return await filesystemOperations.RestoreItemsFromTrashAsync(
                        await sourceMatch.Select(x => x.src).ToListAsync(),
                        await sourceMatch.Select(x => x.dest).ToListAsync(), progress, errorCode, cancellationToken);
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.NotFound))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalizedResource(), "FileNotFoundDialog/Text".GetLocalizedResource());
                }
                else if (moveResult.Items.Any(x => CopyEngineResult.Convert(x.HResult) == FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalizedResource(), "ItemAlreadyExistsDialogContent".GetLocalizedResource());
                }
                errorCode?.Report(CopyEngineResult.Convert(moveResult.Items.FirstOrDefault(x => !x.Succeeded)?.HResult));
                return null;
            }
        }

        private void OnProgressUpdated(object sender, Dictionary<string, JsonElement> message, string currentOperation, IProgress<float> progress)
        {
            if (message.ContainsKey("OperationID"))
            {
                var operationID = message["OperationID"].GetString();
                if (operationID == currentOperation)
                {
                    var value = message["Progress"].GetInt64();
                    progress?.Report(value);
                }
            }
        }

        private async void CancelOperation(object operationID)
            => FileOperationsHelpers.TryCancelOperation((string)operationID);

        private async Task<bool> RequestAdminOperation()
        {
            if (!App.AppModel.IsAppElevated)
            {
                if (await DialogService.ShowDialogAsync(new ElevateConfirmDialogViewModel()) == DialogResult.Primary)
                {
                    var connection = await AppServiceConnectionHelper.Instance;
                    if (connection != null && await connection.Elevate())
                    {
                        connection = await AppServiceConnectionHelper.Instance;
                        return connection != null;
                    }
                }
            }
            return false;
        }

        private Task<DialogResult> GetFileInUseDialog(IEnumerable<string> source, List<Win32Process> lockingProcess = null)
        {
            var titleText = "FileInUseDialog/Title".GetLocalizedResource();
            var subtitleText = lockingProcess.IsEmpty() ? "FileInUseDialog/Text".GetLocalizedResource() :
                string.Format("FileInUseByDialog/Text".GetLocalizedResource(), string.Join(", ", lockingProcess.Select(x => $"{x.AppName ?? x.Name} (PID: {x.Pid})")));
            return GetFileListDialog(source, titleText, subtitleText, "Retry".GetLocalizedResource(), "Cancel".GetLocalizedResource());
        }

        private async Task<DialogResult> GetFileListDialog(IEnumerable<string> source, string titleText, string descriptionText = null, string primaryButtonText = null, string secondaryButtonText = null)
        {
            var incomingItems = new List<BaseFileSystemDialogItemViewModel>();
            List<ShellFileItem> binItems = null;
            foreach (var src in source)
            {
                if (recycleBinHelpers.IsPathUnderRecycleBin(src))
                {
                    binItems ??= await recycleBinHelpers.EnumerateRecycleBin();
                    if (!binItems.IsEmpty()) // Might still be null because we're deserializing the list from Json
                    {
                        var matchingItem = binItems.FirstOrDefault(x => x.RecyclePath == src); // Get original file name
                        incomingItems.Add(new FileSystemDialogDefaultItemViewModel() { SourcePath = src, DisplayName = matchingItem?.FileName });
                    }
                }
                else
                {
                    incomingItems.Add(new FileSystemDialogDefaultItemViewModel() { SourcePath = src });
                }
            }

            var dialogViewModel = FileSystemDialogViewModel.GetDialogViewModel(
                incomingItems, titleText, descriptionText, primaryButtonText, secondaryButtonText);

            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();

            return await dialogService.ShowDialogAsync(dialogViewModel);
        }

        private List<Win32Process> WhoIsLocking(IEnumerable<string> filesToCheck)
            => FileOperationsHelpers.CheckFileInUse(filesToCheck.ToArray()).ToList();

        #region IDisposable

        public void Dispose()
        {
            filesystemOperations?.Dispose();

            filesystemOperations = null;
            recycleBinHelpers = null;
            associatedInstance = null;
        }

        #endregion IDisposable
    }
}