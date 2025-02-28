using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Files.App.Extensions;
using Files.App.Filesystem;
using Files.App.Filesystem.StorageItems;
using Files.App.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Services.Maps;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Files.App.ViewModels.Properties
{
	public class FileProperties : BaseProperties
	{
		public ListedItem Item { get; }

		public FileProperties(SelectedItemsPropertiesViewModel viewModel, CancellationTokenSource tokenSource,
			DispatcherQueue coreDispatcher, ListedItem item, IShellPage instance)
		{
			ViewModel = viewModel;
			TokenSource = tokenSource;
			Dispatcher = coreDispatcher;
			Item = item;
			AppInstance = instance;

			GetBaseProperties();

			ViewModel.PropertyChanged += ViewModel_PropertyChanged;
		}

		public override void GetBaseProperties()
		{
			if (Item == null)
				return;

			ViewModel.ItemName = Item.Name;
			ViewModel.OriginalItemName = Item.Name;
			ViewModel.ItemType = Item.ItemType;
			ViewModel.ItemPath = (Item as RecycleBinItem)?.ItemOriginalFolder ??
				(Path.IsPathRooted(Item.ItemPath) ? Path.GetDirectoryName(Item.ItemPath) : Item.ItemPath);
			ViewModel.ItemModifiedTimestamp = Item.ItemDateModified;
			ViewModel.ItemCreatedTimestamp = Item.ItemDateCreated;
			ViewModel.LoadCustomIcon = Item.LoadCustomIcon;
			ViewModel.CustomIconSource = Item.CustomIconSource;
			ViewModel.LoadFileIcon = Item.LoadFileIcon;

			if (!Item.IsShortcut)
				return;

			var shortcutItem = (ShortcutItem)Item;

			var isApplication = !string.IsNullOrWhiteSpace(shortcutItem.TargetPath) &&
				(shortcutItem.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
					|| shortcutItem.TargetPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
					|| shortcutItem.TargetPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

			ViewModel.ShortcutItemType = isApplication ? "Application".GetLocalizedResource() :
				Item.IsLinkItem ? "PropertiesShortcutTypeLink".GetLocalizedResource() : "PropertiesShortcutTypeFile".GetLocalizedResource();
			ViewModel.ShortcutItemPath = shortcutItem.TargetPath;
			ViewModel.IsShortcutItemPathReadOnly = shortcutItem.IsSymLink;
			ViewModel.ShortcutItemWorkingDir = shortcutItem.WorkingDirectory;
			ViewModel.ShortcutItemWorkingDirVisibility = Item.IsLinkItem || shortcutItem.IsSymLink ? false : true;
			ViewModel.ShortcutItemArguments = shortcutItem.Arguments;
			ViewModel.ShortcutItemArgumentsVisibility = Item.IsLinkItem || shortcutItem.IsSymLink ? false : true;
			ViewModel.IsSelectedItemShortcut = ".lnk".Equals(Item.FileExtension, StringComparison.OrdinalIgnoreCase);
			ViewModel.ShortcutItemOpenLinkCommand = new RelayCommand(async () =>
			{
				if (Item.IsLinkItem)
				{
					var tmpItem = (ShortcutItem)Item;
					await Win32Helpers.InvokeWin32ComponentAsync(ViewModel.ShortcutItemPath, AppInstance, ViewModel.ShortcutItemArguments, tmpItem.RunAsAdmin, ViewModel.ShortcutItemWorkingDir);
				}
				else
				{
					await App.Window.DispatcherQueue.EnqueueAsync(
						() => NavigationHelpers.OpenPathInNewTab(Path.GetDirectoryName(ViewModel.ShortcutItemPath)));
				}
			}, () =>
			{
				return !string.IsNullOrWhiteSpace(ViewModel.ShortcutItemPath);
			});
		}

		public override async void GetSpecialProperties()
		{
			ViewModel.IsReadOnly = NativeFileOperationsHelper.HasFileAttribute(
				Item.ItemPath, System.IO.FileAttributes.ReadOnly);
			ViewModel.IsHidden = NativeFileOperationsHelper.HasFileAttribute(
				Item.ItemPath, System.IO.FileAttributes.Hidden);

			ViewModel.ItemSizeVisibility = true;
			ViewModel.ItemSize = Item.FileSizeBytes.ToLongSizeString();

			var fileIconData = await FileThumbnailHelper.LoadIconFromPathAsync(Item.ItemPath, 80, Windows.Storage.FileProperties.ThumbnailMode.DocumentsView, false);
			if (fileIconData != null)
			{
				ViewModel.IconData = fileIconData;
				ViewModel.LoadUnknownTypeGlyph = false;
				ViewModel.LoadFileIcon = true;
			}

			if (Item.IsShortcut)
			{
				ViewModel.ItemCreatedTimestamp = Item.ItemDateCreated;
				ViewModel.ItemAccessedTimestamp = Item.ItemDateAccessed;
				ViewModel.LoadLinkIcon = Item.LoadWebShortcutGlyph;
				if (Item.IsLinkItem || string.IsNullOrWhiteSpace(((ShortcutItem)Item).TargetPath))
				{
					// Can't show any other property
					return;
				}
			}

			string filePath = (Item as ShortcutItem)?.TargetPath ?? Item.ItemPath;
			BaseStorageFile file = await AppInstance.FilesystemViewModel.GetFileFromPathAsync(filePath);

			// Couldn't access the file and can't load any other properties
			if (file == null)
				return;

			// Can't load any other properties
			if (Item.IsShortcut)
				return;

			if (FileExtensionHelpers.IsBrowsableZipFile(Item.FileExtension, out _))
			{
				if (await ZipStorageFolder.FromPathAsync(Item.ItemPath) is ZipStorageFolder zipFolder)
				{
					var uncompressedSize = await zipFolder.GetUncompressedSize();
					ViewModel.UncompressedItemSize = uncompressedSize.ToLongSizeString();
					ViewModel.UncompressedItemSizeBytes = uncompressedSize;
				}
			}

			if (file.Properties != null)
				GetOtherProperties(file.Properties);
		}

		public async void GetSystemFileProperties()
		{
			BaseStorageFile file = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(Item.ItemPath));
			if (file == null)
			{
				// Could not access file, can't show any other property
				return;
			}

			var list = await FileProperty.RetrieveAndInitializePropertiesAsync(file);

			list.Find(x => x.ID == "address").Value = await GetAddressFromCoordinatesAsync((double?)list.Find(x => x.Property == "System.GPS.LatitudeDecimal").Value,
																						   (double?)list.Find(x => x.Property == "System.GPS.LongitudeDecimal").Value);

			var query = list
				.Where(fileProp => !(fileProp.Value == null && fileProp.IsReadOnly))
				.GroupBy(fileProp => fileProp.SectionResource)
				.Select(group => new FilePropertySection(group) { Key = group.Key })
				.Where(section => !section.All(fileProp => fileProp.Value == null))
				.OrderBy(group => group.Priority);
			ViewModel.PropertySections = new ObservableCollection<FilePropertySection>(query);
			ViewModel.FileProperties = new ObservableCollection<FileProperty>(list.Where(i => i.Value != null));
		}

		public static async Task<string> GetAddressFromCoordinatesAsync(double? Lat, double? Lon)
		{
			if (!Lat.HasValue || !Lon.HasValue)
				return null;

			try
			{
				StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(@"ms-appx:///Resources/BingMapsKey.txt"));
				var lines = await FileIO.ReadTextAsync(file);
				using var obj = JsonDocument.Parse(lines);
				MapService.ServiceToken = obj.RootElement.GetProperty("key").GetString();
			}
			catch (Exception)
			{
				return null;
			}

			BasicGeoposition location = new BasicGeoposition();
			location.Latitude = Lat.Value;
			location.Longitude = Lon.Value;
			Geopoint pointToReverseGeocode = new Geopoint(location);

			// Reverse geocode the specified geographic location.

			var result = await MapLocationFinder.FindLocationsAtAsync(pointToReverseGeocode);
			return result?.Locations?.FirstOrDefault()?.DisplayName;
		}

		public async Task SyncPropertyChangesAsync()
		{
			BaseStorageFile file = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(Item.ItemPath));
			
			// Couldn't access the file to save properties
			if (file == null)
				return;

			var failedProperties = "";
			foreach (var group in ViewModel.PropertySections)
			{
				foreach (FileProperty prop in group)
				{
					if (!prop.IsReadOnly && prop.Modified)
					{
						var newDict = new Dictionary<string, object>();
						newDict.Add(prop.Property, prop.Value);

						try
						{
							if (file.Properties != null)
							{
								await file.Properties.SavePropertiesAsync(newDict);
							}
						}
						catch
						{
							failedProperties += $"{prop.Name}\n";
						}
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(failedProperties))
			{
				throw new Exception($"The following properties failed to save: {failedProperties}");
			}
		}

		/// <summary>
		/// This function goes through ever read-write property saved, then syncs it
		/// </summary>
		/// <returns></returns>
		public async Task ClearPropertiesAsync()
		{
			var failedProperties = new List<string>();
			BaseStorageFile file = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(Item.ItemPath));
			
			if (file == null)
				return;

			foreach (var group in ViewModel.PropertySections)
			{
				foreach (FileProperty prop in group)
				{
					if (!prop.IsReadOnly)
					{
						var newDict = new Dictionary<string, object>();
						newDict.Add(prop.Property, null);

						try
						{
							if (file.Properties != null)
							{
								await file.Properties.SavePropertiesAsync(newDict);
							}
						}
						catch
						{
							failedProperties.Add(prop.Name);
						}
					}
				}
			}

			GetSystemFileProperties();
		}

		private async void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case "IsReadOnly":
					if (ViewModel.IsReadOnly)
					{
						NativeFileOperationsHelper.SetFileAttribute(
							Item.ItemPath,
							System.IO.FileAttributes.ReadOnly
						);
					}
					else
					{
						NativeFileOperationsHelper.UnsetFileAttribute(
							Item.ItemPath,
							System.IO.FileAttributes.ReadOnly
						);
					}
					break;

				case "IsHidden":
					if (ViewModel.IsHidden)
					{
						NativeFileOperationsHelper.SetFileAttribute(
							Item.ItemPath,
							System.IO.FileAttributes.Hidden
						);
					}
					else
					{
						NativeFileOperationsHelper.UnsetFileAttribute(
							Item.ItemPath,
							System.IO.FileAttributes.Hidden
						);
					}
					break;

				case "ShortcutItemPath":
				case "ShortcutItemWorkingDir":
				case "ShortcutItemArguments":
					var tmpItem = (ShortcutItem)Item;
					if (string.IsNullOrWhiteSpace(ViewModel.ShortcutItemPath))
						return;

                    await FileOperationsHelpers.CreateOrUpdateLinkAsync(Item.ItemPath, ViewModel.ShortcutItemPath, ViewModel.ShortcutItemArguments, ViewModel.ShortcutItemWorkingDir, tmpItem.RunAsAdmin);
                    break;
            }
        }

        private async Task<string> GetHashForFileAsync(ListedItem fileItem, string nameOfAlg, CancellationToken token, IProgress<float> progress, IShellPage associatedInstance)
        {
            HashAlgorithmProvider algorithmProvider = HashAlgorithmProvider.OpenAlgorithm(nameOfAlg);
            BaseStorageFile file = await StorageHelpers.ToStorageItem<BaseStorageFile>((fileItem as ShortcutItem)?.TargetPath ?? fileItem.ItemPath);
            if (file == null)
            {
                return "";
            }

            Stream stream = await FilesystemTasks.Wrap(() => file.OpenStreamForReadAsync());
            if (stream == null)
            {
                return "";
            }

            uint capacity;
            var inputStream = stream.AsInputStream();
            bool isProgressSupported = false;

            try
            {
                var cap = (long)(0.5 * stream.Length) / 100;
                if (cap >= uint.MaxValue)
                {
                    capacity = uint.MaxValue;
                }
                else
                {
                    capacity = Convert.ToUInt32(cap);
                }
                isProgressSupported = true;
            }
            catch (NotSupportedException)
            {
                capacity = 64 * 1024;
            }

            Windows.Storage.Streams.Buffer buffer = new Windows.Storage.Streams.Buffer(capacity);
            var hash = algorithmProvider.CreateHash();
            while (!token.IsCancellationRequested)
            {
                await inputStream.ReadAsync(buffer, capacity, InputStreamOptions.None);
                if (buffer.Length > 0)
                {
                    hash.Append(buffer);
                }
                else
                {
                    break;
                }
                if (stream.Length > 0)
                {
                    progress?.Report(isProgressSupported ? (float)stream.Position / stream.Length * 100.0f : 20);
                }
            }
            inputStream.Dispose();
            stream.Dispose();
            if (token.IsCancellationRequested)
            {
                return "";
            }
            return CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset()).ToLowerInvariant();
        }
    }
}