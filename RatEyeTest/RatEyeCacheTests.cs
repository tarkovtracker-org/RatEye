using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using RatEye;
using RatStash;
using Xunit;

namespace RatEyeTest;

public class RatEyeCacheTests
{
	[Fact]
	public void Corrupt_cached_icon_is_regenerated_atomically()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-cache-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		string cacheDirectory = Path.Combine(root, "cache");
		Directory.CreateDirectory(iconsDirectory);
		Directory.CreateDirectory(cacheDirectory);

		Config config = CreateConfig(iconsDirectory);
		try
		{
			WriteIcon(Path.Combine(iconsDirectory, "one.png"));

			using (IconManager manager = new(config, cacheDirectory))
			{
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
				Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
			}

			string cachePath = Assert.Single(Directory.GetFiles(cacheDirectory, "*.bmp"));
			File.WriteAllText(cachePath, "not a bitmap");

			using (IconManager manager = new(config, cacheDirectory))
			{
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
				Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
			}

			using Mat cachedIcon = Cv2.ImRead(cachePath, ImreadModes.Unchanged);
			Assert.False(cachedIcon.Empty());
			Assert.Equal(MatType.CV_8UC3, cachedIcon.Type());
			Assert.Empty(Directory.GetFiles(cacheDirectory, "*.tmp.bmp"));
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Wrong_sized_cached_icon_is_regenerated_from_source()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-cache-size-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		string cacheDirectory = Path.Combine(root, "cache");
		Directory.CreateDirectory(iconsDirectory);
		Directory.CreateDirectory(cacheDirectory);

		Config config = CreateConfig(iconsDirectory);
		try
		{
			WriteIcon(Path.Combine(iconsDirectory, "one.png"));
			using (IconManager manager = new(config, cacheDirectory))
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));

			string cachePath = Assert.Single(Directory.GetFiles(cacheDirectory, "*.bmp"));
			using (Bitmap wrongSize = new(127, 64))
				wrongSize.Save(cachePath, System.Drawing.Imaging.ImageFormat.Bmp);

			using (IconManager manager = new(config, cacheDirectory))
			{
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
				Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
			}

			using Mat cachedIcon = Cv2.ImRead(cachePath, ImreadModes.Unchanged);
			Assert.Equal(64, cachedIcon.Width);
			Assert.Equal(64, cachedIcon.Height);
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Cache_pruning_removes_stale_and_oldest_oversize_entries()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-cache-prune-test-" + Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(root);

		try
		{
			DateTime now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
			string stale = WriteCacheFile(root, "stale.bmp", 20, now - TimeSpan.FromDays(31));
			string abandonedTemporary = WriteCacheFile(
				root,
				"abandoned.tmp.bmp",
				20,
				now - TimeSpan.FromDays(2)
			);
			string oldest = WriteCacheFile(root, "oldest.bmp", 40, now - TimeSpan.FromHours(2));
			string newest = WriteCacheFile(root, "newest.bmp", 40, now - TimeSpan.FromHours(1));
			string unrelated = WriteCacheFile(root, "keep.txt", 20, now - TimeSpan.FromDays(90));

			IconManager.PruneCache(root, now, TimeSpan.FromDays(30), maxBytes: 60, maxFiles: 10);

			Assert.False(File.Exists(stale));
			Assert.False(File.Exists(abandonedTemporary));
			Assert.False(File.Exists(oldest));
			Assert.True(File.Exists(newest));
			Assert.True(File.Exists(unrelated));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Icon_with_rendered_size_that_disagrees_with_catalog_is_skipped()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-icon-size-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		Directory.CreateDirectory(iconsDirectory);

		Config config = CreateConfig(iconsDirectory);
		try
		{
			WriteIcon(Path.Combine(iconsDirectory, "one.png"), width: 127, height: 64);

			using IconManager manager = new(config, Path.Combine(root, "cache"));
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));

			Assert.Empty(manager.StaticIcons);
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Missing_static_icon_directory_can_be_installed_without_restarting_the_manager()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-icon-install-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		Config config = CreateConfig(iconsDirectory);

		try
		{
			using IconManager manager = new(config, Path.Combine(root, "cache"));
			Directory.CreateDirectory(iconsDirectory);
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			Assert.Empty(manager.StaticIcons);

			WriteIcon(Path.Combine(iconsDirectory, "one.png"));

			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));

			Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Cache_identity_includes_catalog_rendering_properties()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-cache-render-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		string cacheDirectory = Path.Combine(root, "cache");
		Directory.CreateDirectory(iconsDirectory);
		WriteIcon(Path.Combine(iconsDirectory, "one.png"));

		Config blueConfig = CreateConfig(iconsDirectory, TaxonomyColor.Blue);
		Config redConfig = CreateConfig(iconsDirectory, TaxonomyColor.Red);
		try
		{
			using (IconManager manager = new(blueConfig, cacheDirectory))
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			using (IconManager manager = new(redConfig, cacheDirectory))
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));

			Assert.Equal(2, Directory.GetFiles(cacheDirectory, "*.bmp").Length);
		}
		finally
		{
			blueConfig.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			redConfig.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Static_icon_changes_replace_and_remove_loaded_templates()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-icon-refresh-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		string iconPath = Path.Combine(iconsDirectory, "one.png");
		Directory.CreateDirectory(iconsDirectory);
		WriteIcon(iconPath);

		Config config = CreateConfig(iconsDirectory);
		try
		{
			using IconManager manager = new(config, Path.Combine(root, "cache"));
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			Mat original = manager.StaticIcons[new Vector2(1, 1)].Values.Single();

			WriteIcon(iconPath, color: System.Drawing.Color.Red);
			File.SetLastWriteTimeUtc(iconPath, DateTime.UtcNow.AddSeconds(2));
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			Mat replacement = manager.StaticIcons[new Vector2(1, 1)].Values.Single();

			Assert.NotSame(original, replacement);

			File.Delete(iconPath);
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			Assert.Empty(manager.StaticIcons);
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Static_icon_refresh_keeps_previous_templates_available_until_replacements_are_ready()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-icon-atomic-refresh-test-" + Guid.NewGuid().ToString("N")
		);
		string iconsDirectory = Path.Combine(root, "icons");
		Directory.CreateDirectory(iconsDirectory);

		const int iconCount = 64;
		RatStash.Item[] items = Enumerable
			.Range(0, iconCount)
			.Select(index => new RatStash.Item
			{
				Id = $"item-{index}",
				Name = $"Item {index}",
				ShortName = $"Item {index}",
				Width = 1,
				Height = 1,
			})
			.ToArray();
		foreach (RatStash.Item item in items)
			WriteIcon(Path.Combine(iconsDirectory, item.Id + ".png"));

		Config config = CreateConfig(iconsDirectory);
		config.RatStashDB = Database.FromItems(items);
		try
		{
			using IconManager manager = new(config, Path.Combine(root, "cache"));
			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			Assert.Equal(
				iconCount,
				manager.StaticIcons[new Vector2(1, 1)].Count
			);

			DateTime refreshedTimestamp = DateTime.UtcNow.AddSeconds(2);
			foreach (RatStash.Item item in items)
			{
				string iconPath = Path.Combine(iconsDirectory, item.Id + ".png");
				WriteIcon(iconPath, color: System.Drawing.Color.Red);
				File.SetLastWriteTimeUtc(iconPath, refreshedTimestamp);
			}

			using ManualResetEventSlim observerStarted = new();
			using CancellationTokenSource stopObserver = new();
			int observedEmptyTemplates = 0;
			Task observer = Task.Factory.StartNew(
				() =>
				{
					observerStarted.Set();
					while (!stopObserver.IsCancellationRequested)
					{
						manager.StaticIconsLock.EnterReadLock();
						try
						{
							if (manager.StaticIcons.Count == 0)
								Interlocked.Exchange(ref observedEmptyTemplates, 1);
						}
						finally
						{
							manager.StaticIconsLock.ExitReadLock();
						}

						Thread.Yield();
					}
				},
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default
			);
			observerStarted.Wait();

			try
			{
				manager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			}
			finally
			{
				stopObserver.Cancel();
				await observer;
			}

			Assert.Equal(0, observedEmptyTemplates);
			Assert.Equal(
				iconCount,
				manager.StaticIcons[new Vector2(1, 1)].Count
			);
		}
		finally
		{
			config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	private static Config CreateConfig(
		string iconsDirectory,
		TaxonomyColor backgroundColor = TaxonomyColor.Default
	)
	{
		Config config = new()
		{
			PathConfig = new Config.Path { StaticIcons = iconsDirectory },
			ProcessingConfig = new Config.Processing
			{
				UseCache = true,
				IconConfig = new Config.Processing.Icon { UseStaticIcons = true },
			},
		};
		config.RatStashDB = Database.FromItems(
			new Item[]
			{
				new()
				{
					Id = "one",
					Name = "One",
					ShortName = "One",
					Width = 1,
					Height = 1,
					BackgroundColor = backgroundColor,
				},
			}
		);
		return config;
	}

	private static void WriteIcon(
		string path,
		int width = 64,
		int height = 64,
		System.Drawing.Color? color = null
	)
	{
		using Bitmap bitmap = new(width, height);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			using Brush brush = new SolidBrush(color ?? System.Drawing.Color.White);
			graphics.FillEllipse(brush, width / 4, height / 4, width / 2, height / 2);
		}
		bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
	}

	private static string WriteCacheFile(
		string root,
		string name,
		int length,
		DateTime lastWriteTimeUtc
	)
	{
		string path = Path.Combine(root, name);
		File.WriteAllBytes(path, new byte[length]);
		File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
		return path;
	}
}
