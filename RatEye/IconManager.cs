using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using RatEye.Properties;
using RatStash;
using Color = RatStash.Color;

namespace RatEye
{
    internal class IconManager : IDisposable
    {
        private const long MaxCacheBytes = 256L * 1024 * 1024;
        private const int MaxCacheFiles = 10_000;
        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan MaxTemporaryCacheFileAge = TimeSpan.FromDays(1);
        private static readonly TimeSpan DefaultStaticIconSourceFallbackPollInterval =
            TimeSpan.FromSeconds(30);

        private readonly Config _config;
        private readonly string _cacheDirectory;
        private readonly Func<string, FileSystemWatcher> _staticIconSourceWatcherFactory;
        private readonly TimeSpan _staticIconSourceFallbackPollInterval;

        /// <summary>
        /// Static icons are those which are rendered ahead of time.
        /// For example keys, medical supply's, containers, standalone mods,
        /// and especially items like screws, drill, wires, milk and so on.
        /// <para/>
        /// <c>Dictionary&lt;slotSize, Dictionary&lt;iconKey, icon&gt;&gt;</c>
        /// </summary>
        /// <remarks>
        /// Use the <see cref="StaticIconsLock"/> when accessing this collection.
        /// Icon is of type 8UC3.
        /// </remarks>
        internal Dictionary<Vector2, Dictionary<string, Mat>> StaticIcons = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="StaticIcons"/>
        /// </summary>
        internal readonly ReaderWriterLockSlim StaticIconsLock = new();

        /// <summary>
        /// The data used to match icon keys of static icons to their item
        /// <para/>
        /// <c>Dictionary&lt;iconKey, item&gt;</c>
        /// </summary>
        private Dictionary<string, Item> _staticCorrelationData = new();

        /// <summary>
        /// Reader / Writer lock of <see cref="_staticCorrelationDataLock"/>
        /// </summary>
        private readonly ReaderWriterLockSlim _staticCorrelationDataLock = new();

        private readonly object _staticIconLoadLock = new();
        private readonly object _staticIconSourceWatcherLock = new();
        private readonly HashSet<Vector2> _loadedStaticIconSizes = new();
        private string _staticIconDirectoryFingerprint;
        private Dictionary<string, string> _staticIconSourceHashes =
            new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _staticIconSourceWatcher;
        private bool _staticIconSourceWatcherUnavailable;
        private DateTime _nextStaticIconSourceWatcherRetryUtc = DateTime.MinValue;
        private long _staticIconSourceGeneration = 1;
        private long _committedStaticIconSourceGeneration;
        private DateTime _nextStaticIconSourceFallbackPollUtc = DateTime.MinValue;
        internal IReadOnlyList<(Item Item, string NormalizedName)> NormalizedItems { get; }
        private bool _disposed;

        /// <summary>
        /// Constructor for icon manager object
        /// </summary>
        /// <param name="config">The config to use for this instance></param>
        /// <remarks>Depends on <see cref="Config.Processing.Icon"/> and <see cref="Config.Path"/></remarks>
        internal IconManager(Config config)
            : this(config, config.PathConfig.CacheDir) { }

        internal IconManager(
            Config config,
            string cacheDirectory,
            Func<string, FileSystemWatcher> staticIconSourceWatcherFactory = null,
            TimeSpan? staticIconSourceFallbackPollInterval = null
        )
        {
            _config = config;
            _cacheDirectory = cacheDirectory;
            _staticIconSourceWatcherFactory =
                staticIconSourceWatcherFactory
                ?? (path => new FileSystemWatcher(path, "*.png"));
            _staticIconSourceFallbackPollInterval =
                staticIconSourceFallbackPollInterval
                ?? DefaultStaticIconSourceFallbackPollInterval;
            NormalizedItems = _config
                .RatStashDB.GetItems()
                .Select(item => (item, (item.Name ?? "").CyrillicToLatin().ToLowerInvariant()))
                .ToList()
                .AsReadOnly();

            if (_config.ProcessingConfig.UseCache)
            {
                Directory.CreateDirectory(_cacheDirectory);
                PruneCacheBestEffort();
            }

            var iconConfig = _config.ProcessingConfig.IconConfig;
            if (iconConfig.UseStaticIcons)
            {
                if (Directory.Exists(_config.PathConfig.StaticIcons))
                {
                    try
                    {
                        EnsureStaticIconSourceWatcher();
                        ReplaceStaticCorrelationData(BuildStaticCorrelationData());
                    }
                    catch
                    {
                        DisposeStaticIconSourceWatcher();
                        throw;
                    }
                }
                else
                {
                    Logger.LogDebug(
                        "Static icon folder is missing; name scanning remains available but icon matching is disabled."
                    );
                }
            }
        }

        #region Icon loading

        internal void EnsureStaticIconsLoaded(Vector2 slotSize)
        {
            lock (_staticIconLoadLock)
            {
                const int maxSnapshotAttempts = 3;
                for (int attempt = 0; attempt < maxSnapshotAttempts; attempt++)
                {
                    Dictionary<Vector2, Dictionary<string, Mat>> newIcons;
                    Dictionary<string, Item> newCorrelationData;
                    bool replaceExistingIcons;
                    bool watcherAvailable = EnsureStaticIconSourceWatcher();
                    long sourceGeneration = Volatile.Read(ref _staticIconSourceGeneration);
                    bool refreshIconSources =
                        _staticIconSourceHashes.Count == 0
                        || sourceGeneration != _committedStaticIconSourceGeneration
                        || DateTime.UtcNow >= _nextStaticIconSourceFallbackPollUtc;
                    string directoryFingerprint = _staticIconDirectoryFingerprint;
                    Dictionary<string, string> sourceHashes = _staticIconSourceHashes;
                    try
                    {
                        if (refreshIconSources)
                        {
                            (directoryFingerprint, sourceHashes) =
                                GetStaticIconDirectorySnapshot(
                                    _config.PathConfig.StaticIcons
                                );
                        }

                        replaceExistingIcons = !string.Equals(
                            _staticIconDirectoryFingerprint,
                            directoryFingerprint,
                            StringComparison.Ordinal
                        );
                        if (
                            !replaceExistingIcons
                            && _loadedStaticIconSizes.Contains(slotSize)
                        )
                        {
                            if (refreshIconSources)
                            {
                                if (
                                    sourceGeneration
                                    != Volatile.Read(ref _staticIconSourceGeneration)
                                )
                                    continue;

                                CommitStaticIconSourceSnapshot(
                                    directoryFingerprint,
                                    sourceHashes,
                                    sourceGeneration,
                                    watcherAvailable
                                );
                            }
                            return;
                        }

                        newCorrelationData =
                            replaceExistingIcons
                                ? BuildStaticCorrelationData(sourceHashes.Keys)
                                : GetStaticCorrelationDataSnapshot();
                        newIcons = LoadNewIcons(
                            _config.PathConfig.StaticIcons,
                            slotSize,
                            sourceHashes,
                            newCorrelationData,
                            skipExistingIcons: !replaceExistingIcons
                        );
                    }
                    catch (StaticIconSnapshotChangedException)
                    {
                        InvalidateStaticIconSources();
                        continue;
                    }
                    catch (Exception e) when (IsRecoverableFileSystemException(e))
                    {
                        // Missing Data/icons is a recoverable packaging issue; keep scanning alive.
                        Logger.LogDebug(
                            "Static icon folder is missing; icon matching for this slot size will stay empty until data is installed.",
                            e
                        );
                        return;
                    }

                    if (sourceGeneration != Volatile.Read(ref _staticIconSourceGeneration))
                    {
                        DisposeIconCollection(newIcons);
                        continue;
                    }

                    StaticIconsLock.EnterWriteLock();
                    try
                    {
                        if (
                            sourceGeneration
                            != Volatile.Read(ref _staticIconSourceGeneration)
                        )
                        {
                            DisposeIconCollection(newIcons);
                            continue;
                        }

                        if (replaceExistingIcons)
                        {
                            _staticCorrelationDataLock.EnterWriteLock();
                            try
                            {
                                Dictionary<Vector2, Dictionary<string, Mat>> replacedIcons =
                                    StaticIcons;
                                StaticIcons = newIcons;
                                _staticCorrelationData = newCorrelationData;
                                _loadedStaticIconSizes.Clear();
                                DisposeIconCollection(replacedIcons);
                            }
                            finally
                            {
                                _staticCorrelationDataLock.ExitWriteLock();
                            }
                        }
                        else
                        {
                            foreach (var icons in newIcons)
                            {
                                if (!StaticIcons.ContainsKey(icons.Key))
                                    StaticIcons.Add(
                                        icons.Key,
                                        new Dictionary<string, Mat>()
                                    );
                                foreach (var icon in icons.Value)
                                    StaticIcons[icons.Key].Add(icon.Key, icon.Value);
                            }
                        }

                        _loadedStaticIconSizes.Add(slotSize);
                        if (refreshIconSources)
                            CommitStaticIconSourceSnapshot(
                                directoryFingerprint,
                                sourceHashes,
                                sourceGeneration,
                                watcherAvailable
                            );
                        return;
                    }
                    finally
                    {
                        StaticIconsLock.ExitWriteLock();
                    }
                }

                Logger.LogDebug(
                    "Static icon sources kept changing while loading; icon matching for this slot size stays empty until the next scan."
                );
            }
        }

        internal void InvalidateStaticIconSources()
        {
            Interlocked.Increment(ref _staticIconSourceGeneration);
        }

        private bool EnsureStaticIconSourceWatcher()
        {
            lock (_staticIconSourceWatcherLock)
            {
                if (_disposed)
                    return false;
                if (_staticIconSourceWatcher != null)
                    return true;
                if (!Directory.Exists(_config.PathConfig.StaticIcons))
                    return false;
                if (
                    _staticIconSourceWatcherUnavailable
                    && DateTime.UtcNow < _nextStaticIconSourceWatcherRetryUtc
                )
                    return false;

                FileSystemWatcher watcher = null;
                try
                {
                    watcher = _staticIconSourceWatcherFactory(
                        _config.PathConfig.StaticIcons
                    );
                    watcher.IncludeSubdirectories = false;
                    watcher.NotifyFilter =
                        NotifyFilters.FileName
                        | NotifyFilters.Size
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime;
                    watcher.Changed += OnStaticIconSourceChanged;
                    watcher.Created += OnStaticIconSourceChanged;
                    watcher.Deleted += OnStaticIconSourceChanged;
                    watcher.Renamed += OnStaticIconSourceRenamed;
                    watcher.Error += OnStaticIconSourceWatcherError;
                    _staticIconSourceWatcher = watcher;
                    watcher.EnableRaisingEvents = true;
                    _staticIconSourceWatcherUnavailable = false;
                    _nextStaticIconSourceWatcherRetryUtc = DateTime.MaxValue;
                    return true;
                }
                catch (Exception e) when (IsRecoverableStaticIconWatcherException(e))
                {
                    _staticIconSourceWatcher = null;
                    _staticIconSourceWatcherUnavailable = true;
                    _nextStaticIconSourceWatcherRetryUtc = DateTime.UtcNow.Add(
                        _staticIconSourceFallbackPollInterval
                    );
                    watcher?.Dispose();
                    Logger.LogDebug(
                        "Static icon source watching is unavailable; falling back to periodic refresh checks.",
                        e
                    );
                    return false;
                }
            }
        }

        private void OnStaticIconSourceChanged(object sender, FileSystemEventArgs eventArgs)
        {
            InvalidateStaticIconSources();
        }

        private void OnStaticIconSourceRenamed(object sender, RenamedEventArgs eventArgs)
        {
            InvalidateStaticIconSources();
        }

        private void OnStaticIconSourceWatcherError(object sender, ErrorEventArgs eventArgs)
        {
            InvalidateStaticIconSources();
            lock (_staticIconSourceWatcherLock)
            {
                if (!ReferenceEquals(sender, _staticIconSourceWatcher))
                    return;

                _staticIconSourceWatcher.Dispose();
                _staticIconSourceWatcher = null;
                _staticIconSourceWatcherUnavailable = false;
                _nextStaticIconSourceWatcherRetryUtc = DateTime.MinValue;
            }
        }

        private void DisposeStaticIconSourceWatcher()
        {
            lock (_staticIconSourceWatcherLock)
            {
                _staticIconSourceWatcher?.Dispose();
                _staticIconSourceWatcher = null;
            }
        }

        private void CommitStaticIconSourceSnapshot(
            string directoryFingerprint,
            Dictionary<string, string> sourceHashes,
            long sourceGeneration,
            bool watcherAvailable
        )
        {
            _staticIconDirectoryFingerprint = directoryFingerprint;
            _staticIconSourceHashes = sourceHashes;
            _committedStaticIconSourceGeneration = sourceGeneration;
            _nextStaticIconSourceFallbackPollUtc =
                watcherAvailable
                    ? DateTime.MaxValue
                    : DateTime.UtcNow.Add(_staticIconSourceFallbackPollInterval);
        }

        private static bool IsRecoverableStaticIconWatcherException(Exception exception) =>
            IsRecoverableFileSystemException(exception)
            || exception is ArgumentException or InvalidOperationException;

        private static (
            string fingerprint,
            Dictionary<string, string> sourceHashes
        ) GetStaticIconDirectorySnapshot(string directory)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);

            var sourceHashes = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
            string[] iconPaths = Directory
                .GetFiles(directory, "*.png")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string iconPath in iconPaths)
                sourceHashes[iconPath] = GetFileContentHash(iconPath);

            string fingerprint = string
                .Join(
                    "|",
                    iconPaths.Select(path =>
                        $"{System.IO.Path.GetFileName(path)}:{sourceHashes[path]}"
                    )
                )
                .SHA256Hash();
            return (fingerprint, sourceHashes);
        }

        private static string GetFileContentHash(string path)
        {
            using FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using SHA256 sha256 = SHA256.Create();
            return string.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("X2")));
        }

        private static string GetContentHash(byte[] content)
        {
            using SHA256 sha256 = SHA256.Create();
            return string.Concat(sha256.ComputeHash(content).Select(value => value.ToString("X2")));
        }

        private Dictionary<Vector2, Dictionary<string, Mat>> LoadNewIcons(
            string folderPath,
            Vector2 slotSizeFilter = null,
            IReadOnlyDictionary<string, string> sourceHashes = null,
            IReadOnlyDictionary<string, Item> correlationData = null,
            bool skipExistingIcons = true
        )
        {
            if (!Directory.Exists(folderPath))
            {
                var message = "Could not find icon folder at: " + folderPath;
                throw new FileNotFoundException(message);
            }

            var loadedIcons = new Dictionary<Vector2, Dictionary<string, Mat>>();
            int sourceSnapshotChanged = 0;
            try
            {
                IEnumerable<string> iconPaths =
                    sourceHashes?.Keys ?? Directory.GetFiles(folderPath, "*.png");
                StaticIconsLock.EnterReadLock();
                try
                {
                    var configHash = GetConfigHash();

                    Parallel.ForEach(
                        iconPaths,
                        iconPath =>
                        {
                            Mat icon = null;
                            try
                            {
                                var iconKey = GetIconKey(iconPath);

                                if (
                                    correlationData == null
                                    || !correlationData.TryGetValue(iconKey, out Item item)
                                )
                                    return;
                                if (slotSizeFilter != null && new Vector2(item.GetSlotSize()) != slotSizeFilter)
                                    return;

                                // Skip existing icons unless a changed source set is being rebuilt.
                                if (
                                    skipExistingIcons
                                    && StaticIcons.Any(x => x.Value.ContainsKey(iconKey))
                                )
                                    return;

                                string sourceHash =
                                    sourceHashes != null
                                    && sourceHashes.TryGetValue(
                                        iconPath,
                                        out string snapshotSourceHash
                                    )
                                        ? snapshotSourceHash
                                        : GetFileContentHash(iconPath);
                                var useCache = _config.ProcessingConfig.UseCache;
                                var cacheIdentity =
                                    $"{iconKey}|{sourceHash}"
                                    + $"|{item.GetType().FullName}|{item.BackgroundColor}";
                                var cacheIconPath = Path.Combine(
                                    _cacheDirectory,
                                    $"{cacheIdentity.CacheKey(configHash)}.bmp"
                                );
                                icon = useCache ? TryLoadCachedIcon(cacheIconPath) : null;
                                var cacheHit = icon != null;

                                if (cacheHit && !HasExpectedRenderedSize(icon, item))
                                {
                                    icon.Dispose();
                                    icon = null;
                                    cacheHit = false;
                                    DeleteCacheFileBestEffort(cacheIconPath);
                                    Logger.LogDebug(
                                        "Regenerating cached icon with unexpected rendered size: "
                                            + cacheIconPath
                                    );
                                }

                                if (!cacheHit)
                                {
                                    byte[] sourceBytes;
                                    try
                                    {
                                        sourceBytes = File.ReadAllBytes(iconPath);
                                    }
                                    catch (Exception e) when (IsRecoverableFileSystemException(e))
                                    {
                                        Interlocked.Exchange(ref sourceSnapshotChanged, 1);
                                        return;
                                    }

                                    if (
                                        sourceHashes != null
                                        && !string.Equals(
                                            GetContentHash(sourceBytes),
                                            sourceHash,
                                            StringComparison.Ordinal
                                        )
                                    )
                                    {
                                        Interlocked.Exchange(ref sourceSnapshotChanged, 1);
                                        return;
                                    }

                                    using var mat = Cv2.ImDecode(
                                        sourceBytes,
                                        ImreadModes.Unchanged
                                    );
                                    if (mat.Empty())
                                        throw new InvalidDataException("The icon image is empty or unreadable.");
                                    if (!HasVisibleIconContent(mat))
                                    {
                                        Logger.LogDebug("Skipping icon without visible pixels: " + iconPath);
                                        return;
                                    }
                                    icon = GetIconWithBackground(mat, item);
                                }

                                // Do not add the icon to the list, if its size cannot be converted to slots
                                if (!IsValidPixelSize(icon.Width) || !IsValidPixelSize(icon.Height))
                                    return;

                                var size = new Vector2(PixelsToSlots(icon.Width), PixelsToSlots(icon.Height));
                                var expectedSize = new Vector2(item.GetSlotSize());
                                if (size != expectedSize)
                                {
                                    Logger.LogDebug(
                                        $"Skipping icon whose rendered slot size {size} does not match catalog size {expectedSize}: {iconPath}"
                                    );
                                    return;
                                }

                                // Add the icon to the cache if caching is enabled and doesn't already contain it
                                if (useCache && !cacheHit)
                                    SaveCacheIconAtomic(icon, cacheIconPath);

                                lock (loadedIcons)
                                {
                                    if (!loadedIcons.ContainsKey(size))
                                        loadedIcons.Add(size, new Dictionary<string, Mat>());

                                    // Ownership of the Mat transfers to the returned collection.
                                    loadedIcons[size][iconKey] = icon;
                                    icon = null;
                                }
                            }
                            catch (Exception e) when (IsRecoverableIconLoadException(e))
                            {
                                Logger.LogDebug("Could not load icon: " + iconPath, e);
                            }
                            finally
                            {
                                icon?.Dispose();
                            }
                        }
                    );
                }
                finally
                {
                    StaticIconsLock.ExitReadLock();
                }

                if (Volatile.Read(ref sourceSnapshotChanged) != 0)
                    throw new StaticIconSnapshotChangedException();
            }
            catch
            {
                DisposeIconCollection(loadedIcons);
                throw;
            }

            if (_config.ProcessingConfig.UseCache)
                PruneCacheBestEffort();

            return loadedIcons;
        }

        private static bool HasVisibleIconContent(Mat icon)
        {
            if (icon.Channels() == 4)
            {
                using var alpha = icon.ExtractChannel(3);
                return Cv2.CountNonZero(alpha) > 0;
            }

            using var gray =
                icon.Channels() == 1 ? icon.Clone() : icon.CvtColor(ColorConversionCodes.BGR2GRAY);
            return Cv2.CountNonZero(gray) > 0;
        }

        private static bool IsRecoverableIconLoadException(Exception exception) =>
            IsRecoverableFileSystemException(exception)
            || exception is ArgumentException or OpenCVException or OpenCvSharpException;

        private static bool IsRecoverableFileSystemException(Exception exception) =>
            exception
                is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or System.Security.SecurityException;

        private Mat TryLoadCachedIcon(string cacheIconPath)
        {
            if (!File.Exists(cacheIconPath))
                return null;

            Mat icon = null;
            try
            {
                icon = Cv2.ImRead(cacheIconPath, ImreadModes.Unchanged);
                var valid =
                    !icon.Empty()
                    && icon.Type() == MatType.CV_8UC3
                    && IsValidPixelSize(icon.Width)
                    && IsValidPixelSize(icon.Height);
                if (valid)
                    return icon;

                icon.Dispose();
                icon = null;
                DeleteCacheFileBestEffort(cacheIconPath);
                Logger.LogDebug("Regenerating invalid cached icon: " + cacheIconPath);
                return null;
            }
            catch (Exception e) when (IsRecoverableIconLoadException(e))
            {
                icon?.Dispose();
                DeleteCacheFileBestEffort(cacheIconPath);
                Logger.LogDebug("Regenerating unreadable cached icon: " + cacheIconPath, e);
                return null;
            }
        }

        private static void SaveCacheIconAtomic(Mat icon, string cacheIconPath)
        {
            var cacheDirectory = Path.GetDirectoryName(cacheIconPath);
            var temporaryPath = Path.Combine(
                cacheDirectory,
                $"{Path.GetFileNameWithoutExtension(cacheIconPath)}.{Guid.NewGuid():N}.tmp.bmp"
            );

            try
            {
                Directory.CreateDirectory(cacheDirectory);
                icon.SaveImage(temporaryPath);
                if (new FileInfo(temporaryPath).Length == 0)
                    throw new InvalidDataException("The encoded cache image was empty.");

                if (File.Exists(cacheIconPath))
                {
                    File.Replace(temporaryPath, cacheIconPath, null, true);
                }
                else
                {
                    try
                    {
                        File.Move(temporaryPath, cacheIconPath);
                    }
                    catch (IOException) when (File.Exists(cacheIconPath))
                    {
                        // Another process published the same complete cache entry first.
                    }
                }
            }
            catch (Exception e)
                when (IsRecoverableFileSystemException(e) || e is OpenCVException or OpenCvSharpException)
            {
                // Cache persistence must never prevent the in-memory icon from being used.
                Logger.LogDebug("Could not persist cached icon: " + cacheIconPath, e);
            }
            finally
            {
                DeleteCacheFileBestEffort(temporaryPath);
            }
        }

        private void PruneCacheBestEffort()
        {
            PruneCache(_cacheDirectory, DateTime.UtcNow, MaxCacheAge, MaxCacheBytes, MaxCacheFiles);
        }

        internal static void PruneCache(
            string cacheDirectory,
            DateTime utcNow,
            TimeSpan maxAge,
            long maxBytes,
            int maxFiles
        )
        {
            try
            {
                if (!Directory.Exists(cacheDirectory))
                    return;

                var cacheFiles = new List<(FileInfo file, long length)>();
                var staleCutoff = utcNow - maxAge;
                var temporaryCutoff = utcNow - MaxTemporaryCacheFileAge;

                foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.bmp", SearchOption.TopDirectoryOnly))
                {
                    var file = new FileInfo(path);
                    var isTemporary = file.Name.EndsWith(".tmp.bmp", StringComparison.OrdinalIgnoreCase);
                    if (
                        (isTemporary && file.LastWriteTimeUtc <= temporaryCutoff)
                        || file.LastWriteTimeUtc <= staleCutoff
                    )
                    {
                        DeleteCacheFileBestEffort(path);
                        continue;
                    }

                    if (!isTemporary)
                        cacheFiles.Add((file, file.Length));
                }

                var totalBytes = cacheFiles.Sum(entry => entry.length);
                var fileCount = cacheFiles.Count;
                foreach (var entry in cacheFiles.OrderBy(entry => entry.file.LastWriteTimeUtc))
                {
                    if (totalBytes <= maxBytes && fileCount <= maxFiles)
                        break;
                    if (!DeleteCacheFileBestEffort(entry.file.FullName))
                        continue;

                    totalBytes -= entry.length;
                    fileCount--;
                }
            }
            catch (Exception e) when (IsRecoverableFileSystemException(e))
            {
                // Cleanup is opportunistic; cache failures must not block scanning.
                Logger.LogDebug("Could not prune the icon cache.", e);
            }
        }

        private static bool DeleteCacheFileBestEffort(string path)
        {
            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception e) when (IsRecoverableFileSystemException(e))
            {
                return false;
            }
        }

        /// <summary>
        /// Generate the background of an item as it would appear in game
        /// </summary>
        /// <param name="transparentIcon">The transparent icon of the item</param>
        /// <param name="item">The item of the icon</param>
        /// <returns>8UC3 matrix of transparent icon with blended background</returns>
        private Mat GetIconWithBackground(Mat transparentIcon, Item item)
        {
            // Generate layers
            var black = new Scalar(0, 0, 0, 255);
            using var background = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(black);

            using var cellStream = new MemoryStream(Resources.cell_full_border);
            using var cellBitmap = new Bitmap(cellStream);
            using var baseGridCell = cellBitmap.ToMat();
            using var gridCell = baseGridCell.Repeat(
                PixelsToSlots(transparentIcon.Width),
                PixelsToSlots(transparentIcon.Height),
                -1,
                -1
            );

            var optimizeHighlighted = _config.ProcessingConfig.InventoryConfig.OptimizeHighlighted;
            var bgColor = optimizeHighlighted ? new Color(255, 255, 255) : item.BackgroundColor.ToColor();
            var bgAlpha = _config.ProcessingConfig.InventoryConfig.BackgroundAlpha;
            var bgScalar = new Scalar(bgColor.B, bgColor.G, bgColor.R, bgAlpha);
            using var gridColor = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(bgScalar);

            using var border = new Mat(transparentIcon.Size(), MatType.CV_8UC4).SetTo(new Scalar(0, 0, 0, 0));
            var borderRect = new Rect(Vector2.Zero, transparentIcon.Size());
            border.Rectangle(borderRect, _config.ProcessingConfig.InventoryConfig.GridColor);

            // Blend layers
            using var blendedGrid = gridCell.AlphaBlend(background);
            using var tmp1 = blendedGrid.RemoveTransparency();
            using var tmp2 = gridColor.AlphaBlend(tmp1);
            using var tmp3 = border.AlphaBlend(tmp2);
            using var result = transparentIcon.AlphaBlend(tmp3);

            // Add weapon mod icon
            if (item is WeaponMod)
            {
                using var weaponModIcon = GetWeaponModIcon(item);
                if (weaponModIcon != null)
                {
                    var top = result.Height - weaponModIcon.Height - 2;
                    var right = result.Width - weaponModIcon.Width - 2;
                    using var weaponModIconPadded = weaponModIcon.AddPadding(2, top, right, 2);
                    using var resultWithMod = weaponModIconPadded.AlphaBlend(result);
                    return resultWithMod.CvtColor(ColorConversionCodes.BGRA2BGR, 3);
                }
            }

            // Convert to 8UC3 and return
            return result.CvtColor(ColorConversionCodes.BGRA2BGR, 3);
        }

        private Mat GetWeaponModIcon(Item item)
        {
            var bg = GetWeaponModIconBackground(item);
            using var fg = GetWeaponModIconForeground(item);
            if (fg == null)
                return bg;

            using (bg)
            {
                using var paddedBg = bg.AddPadding(bg.Width, bg.Height, bg.Width, bg.Height);

                var hPadding = (bg.Width * 3) - fg.Width;
                var vPadding = (bg.Height * 3) - fg.Height;

                var left = hPadding / 2 + hPadding % 2;
                var top = vPadding / 2 + vPadding % 2;
                var right = hPadding / 2;
                var bottom = vPadding / 2;
                using var paddedFg = fg.AddPadding(left, top, right, bottom);

                using var blended = paddedFg.AlphaBlend(paddedBg);
                return blended.RemovePadding(bg.Width, bg.Height, bg.Width, bg.Height);
            }
        }

        private Mat GetWeaponModIconBackground(Item item)
        {
            var background = item switch
            {
                EssentialMod => Resources.mod_vital,
                FunctionalMod => Resources.mod_generic,
                GearMod => Resources.mod_gear,
                _ => null,
            };
            if (background == null)
                return null;
            using var stream = new MemoryStream(background);
            using var bitmap = new Bitmap(stream);
            return bitmap.ToMat();
        }

        private Mat GetWeaponModIconForeground(Item item)
        {
            var foreground = item switch
            {
                AuxiliaryMod => Resources.icon_mod_aux,
                Barrel => Resources.icon_mod_barrel,
                Bipod => Resources.icon_mod_bipod,
                ChargingHandle => Resources.icon_mod_charge,
                Flashlight => Resources.icon_mod_flashlight,
                GasBlock => Resources.icon_mod_gasblock,
                Handguard => Resources.icon_mod_handguard,
                IronSight => Resources.icon_mod_ironsight,
                Launcher => Resources.icon_mod_launcher,
                LaserDesignator => Resources.icon_mod_lightlaser,
                Magazine => Resources.icon_mod_magazine,
                Mount => Resources.icon_mod_mount,
                MuzzleDevice => Resources.icon_mod_muzzle,
                PistolGrip => Resources.icon_mod_pistol_grip,
                RailCovers => Resources.icon_mod_railcovers,
                Receiver => Resources.icon_mod_receiver,
                Sights => Resources.icon_mod_sight,
                Stock => Resources.icon_mod_stock,
                CombTactDevice => Resources.icon_mod_tactical,
                Foregrip => Resources.icon_mod_tactical,
                _ => null,
            };
            if (foreground == null)
                return null;
            using var stream = new MemoryStream(foreground);
            using var bitmap = new Bitmap(stream);
            return bitmap.ToMat();
        }

        #endregion

        #region Correlation Data Loading

        private Dictionary<string, Item> BuildStaticCorrelationData(
            IEnumerable<string> iconPaths = null
        )
        {
            var correlationData = new Dictionary<string, Item>();

            IEnumerable<string> sourcePaths =
                iconPaths ?? Directory.GetFiles(_config.PathConfig.StaticIcons, "*.png");
            foreach (var iconPath in sourcePaths)
            {
                var itemId = System.IO.Path.GetFileNameWithoutExtension(iconPath);
                var item = _config.RatStashDB.GetItem(itemId);

                // Filter out items which are not in the item database
                if (item == null)
                    continue;

                // Add the item to the correlation data
                var iconKey = GetIconKey(iconPath);
                correlationData[iconKey] = item;
            }

            return correlationData;
        }

        private Dictionary<string, Item> GetStaticCorrelationDataSnapshot()
        {
            _staticCorrelationDataLock.EnterReadLock();
            try
            {
                return _staticCorrelationData;
            }
            finally
            {
                _staticCorrelationDataLock.ExitReadLock();
            }
        }

        private void ReplaceStaticCorrelationData(Dictionary<string, Item> correlationData)
        {
            _staticCorrelationDataLock.EnterWriteLock();
            try
            {
                _staticCorrelationData = correlationData;
            }
            finally
            {
                _staticCorrelationDataLock.ExitWriteLock();
            }
        }

        #endregion

        /// <summary>
        /// Get the unique icon key for a icon path and its type
        /// </summary>
        /// <param name="iconPath">The path to the icon</param>
        /// <returns>Unique identifier of the icon</returns>
        private string GetIconKey(string iconPath) =>
            System.IO.Path.Combine(
                _config.PathConfig.StaticIcons,
                System.IO.Path.GetFileName(iconPath)
            );

        /// <summary>
        /// Get the item, referenced by its icon key
        /// </summary>
        /// <remarks>
        /// Keep this method coherent with <see cref="GetIconKey"/>
        /// </remarks>
        /// <param name="iconKey">The icon key</param>
        /// <returns>The matching item</returns>
        internal Item GetItem(string iconKey)
        {
            if (iconKey.StartsWith(_config.PathConfig.StaticIcons))
            {
                _staticCorrelationDataLock.EnterReadLock();
                try
                {
                    _staticCorrelationData.TryGetValue(iconKey, out var item);
                    return item;
                }
                finally
                {
                    _staticCorrelationDataLock.ExitReadLock();
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve the icon path for a item possible item extra info
        /// </summary>
        /// <param name="item">The item which icon path shall be resolved</param>
        /// <returns>The path to the icon of the item</returns>
        internal string GetIconPath(Item item)
        {
            _staticCorrelationDataLock.EnterReadLock();
            try
            {
                return _staticCorrelationData.FirstOrDefault(entry => entry.Value == item).Key;
            }
            finally
            {
                _staticCorrelationDataLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Calculate the hash of the current config but only consider used values
        /// </summary>
        /// <returns>Hash as hex string</returns>
        private string GetConfigHash()
        {
            string configHash = new Config()
            {
                ProcessingConfig = new Config.Processing()
                {
                    // Language = _config.ProcessingConfig.Language,
                    BaseSlotSize = _config.ProcessingConfig.BaseSlotSize,
                    IconConfig = new Config.Processing.Icon()
                    {
                        ScanRotatedIcons = _config.ProcessingConfig.IconConfig.ScanRotatedIcons,
                    },
                    InventoryConfig = _config.ProcessingConfig.InventoryConfig,
                },
            }.GetHash();
            return ("template-v2:" + configHash).SHA256Hash();
        }

        /// <summary>
        /// Converts the pixel unit of a icon into the slot unit
        /// </summary>
        /// <param name="pixels">The pixel size of the icon</param>
        /// <returns>Slot size of the icon</returns>
        private int PixelsToSlots(int pixels)
        {
            // Use converter class to round to nearest int instead of always rounding down
            return Convert.ToInt32((pixels - 1) / _config.ProcessingConfig.BaseSlotSize);
        }

        /// <summary>
        /// Checks if the give pixels can be converted into slot unit
        /// </summary>
        /// <param name="pixels">The pixel size of the icon</param>
        /// <returns>True if the pixels can be converted to slots</returns>
        private bool IsValidPixelSize(int pixels)
        {
            return Math.Abs(1 - pixels % _config.ProcessingConfig.BaseSlotSize) < 0.01f;
        }

        private bool HasExpectedRenderedSize(Mat icon, Item item)
        {
            if (!IsValidPixelSize(icon.Width) || !IsValidPixelSize(icon.Height))
                return false;

            return new Vector2(PixelsToSlots(icon.Width), PixelsToSlots(icon.Height))
                == new Vector2(item.GetSlotSize());
        }

        private void ClearStaticIcons()
        {
            StaticIconsLock.EnterWriteLock();
            try
            {
                DisposeIconCollection(StaticIcons);
                StaticIcons.Clear();
            }
            finally
            {
                StaticIconsLock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            lock (_staticIconSourceWatcherLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _staticIconSourceWatcher?.Dispose();
                _staticIconSourceWatcher = null;
            }
            ClearStaticIcons();

            StaticIconsLock.Dispose();
            _staticCorrelationDataLock.Dispose();
        }

        private static void DisposeIconCollection(
            Dictionary<Vector2, Dictionary<string, Mat>> icons
        )
        {
            foreach (Mat icon in icons.Values.SelectMany(group => group.Values))
                icon.Dispose();
        }

        private sealed class StaticIconSnapshotChangedException : Exception { }
    }
}
