using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using RatEye;
using Xunit;

namespace RatEyeTest;

public class IconManagerTests
{
	[Fact]
	public void Static_icons_are_loaded_one_slot_size_at_a_time()
	{
		string root = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"RatEye-icon-test-" + Guid.NewGuid().ToString("N")
		);
		string icons = System.IO.Path.Combine(root, "icons");
		System.IO.Directory.CreateDirectory(icons);

		try
		{
			WriteIcon(System.IO.Path.Combine(icons, "one.png"), 64, 64);
			WriteIcon(System.IO.Path.Combine(icons, "two.png"), 127, 64);
			WriteIcon(System.IO.Path.Combine(icons, "blank.png"), 64, 64, visible: false);

			Config config = new()
			{
				PathConfig = new Config.Path { StaticIcons = icons },
				ProcessingConfig = new Config.Processing
				{
					UseCache = false,
					IconConfig = new Config.Processing.Icon { UseStaticIcons = true },
				},
				RatStashDB = RatStash.Database.FromItems([
					new()
					{
						Id = "one",
						Name = "One",
						ShortName = "One",
						Width = 1,
						Height = 1,
					},
					new()
					{
						Id = "two",
						Name = "Two",
						ShortName = "Two",
						Width = 2,
						Height = 1,
					},
					new()
					{
						Id = "blank",
						Name = "Blank",
						ShortName = "Blank",
						Width = 1,
						Height = 1,
					},
				]),
			};

			using IconManager manager = new(config);
			Assert.Empty(manager.StaticIcons);

			manager.EnsureStaticIconsLoaded(new Vector2(1, 1));

			Assert.Single(manager.StaticIcons);
			Assert.Single(manager.StaticIcons[new Vector2(1, 1)]);
			Assert.DoesNotContain(new Vector2(2, 1), manager.StaticIcons.Keys);
		}
		finally
		{
			System.IO.Directory.Delete(root, recursive: true);
		}
	}

	private static void WriteIcon(string path, int width, int height, bool visible = true)
	{
		using Bitmap bitmap = new(width, height);
		if (visible)
		{
			using Graphics graphics = Graphics.FromImage(bitmap);
			using Brush brush = new SolidBrush(Color.White);
			graphics.FillEllipse(brush, width / 4, height / 4, width / 2, height / 2);
		}
		bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
	}
}

public class ProcessingRegressionTests
{
	[Theory]
	[InlineData("", "", 0)]
	[InlineData("item", "", 0)]
	[InlineData("", "item", 0)]
	[InlineData("item", "item", 1)]
	public void Normalized_similarity_handles_empty_input(
		string source,
		string target,
		float expected
	) => Assert.Equal(expected, RatEye.Extensions.NormedLevenshteinDistance(source, target));

	[Fact]
	public void Crop_clamps_to_the_image_and_rejects_disjoint_regions()
	{
		using Bitmap source = new(20, 20);
		using Bitmap cropped = source.Crop(-5, -5, 10, 10);

		Assert.Equal(5, cropped.Width);
		Assert.Equal(5, cropped.Height);
		Assert.Throws<ArgumentOutOfRangeException>(() => source.Crop(30, 30, 5, 5));
	}

	[Theory]
	[InlineData("F-1/", "f-1")]
	[InlineData(" F-1[\r\n", "f-1")]
	[InlineData("", "")]
	public void Icon_OCR_short_name_normalization_removes_UI_noise(
		string source,
		string expected
	) => Assert.Equal(expected, RatEye.Processing.Icon.NormalizeOcrShortName(source));

	[Fact]
	public void Icon_OCR_short_name_verification_requires_a_unique_exact_match()
	{
		RatStash.Item expected = new() { Id = "f1", ShortName = "F-1" };
		RatStash.Item other = new() { Id = "other", ShortName = "Other" };

		Assert.Same(
			expected,
			RatEye.Processing.Icon.FindUniqueExactShortName([expected, other], "F-1/")
		);
		Assert.Null(
			RatEye.Processing.Icon.FindUniqueExactShortName(
				[expected, new RatStash.Item { Id = "duplicate", ShortName = "F-1" }],
				"F-1"
			)
		);
	}

	[Fact]
	public void Inventory_locates_adjacent_current_ui_cells_from_one_pixel_borders()
	{
		using Bitmap source = new(250, 150);
		using (Graphics graphics = Graphics.FromImage(source))
		{
			graphics.Clear(Color.Black);
			using Pen gridPen = new(Color.FromArgb(73, 81, 84), 1);
			graphics.DrawRectangle(gridPen, 11, 36, 84, 84);
			graphics.DrawRectangle(gridPen, 95, 36, 84, 84);
		}

		Config config = new()
		{
			ProcessingConfig = new Config.Processing
			{
				Scale = 4f / 3f,
				InventoryConfig = new Config.Processing.Inventory { OptimizeHighlighted = false },
			},
		};

		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using RatEye.Processing.Inventory inventory = engine.NewInventory(source);

		Assert.Equal(2, inventory.Icons.Count());
		Assert.NotNull(inventory.LocateIcon(new Vector2(53, 79)));
		Assert.NotNull(inventory.LocateIcon(new Vector2(137, 79)));
		RatEye.Processing.Icon first = inventory.Icons.First();
		Assert.Same(first, inventory.LocateIcon(first.Position));
	}

	[Fact]
	public void Highlighted_inventory_accepts_small_positive_scales()
	{
		using Bitmap source = new(16, 16);
		Config config = new()
		{
			ProcessingConfig = new Config.Processing
			{
				Scale = 0.25f,
				InventoryConfig = new Config.Processing.Inventory { OptimizeHighlighted = true },
			},
		};

		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using RatEye.Processing.Inventory inventory = engine.NewInventory(source);

		Assert.Empty(inventory.Icons);
	}

	[Fact]
	public void Multi_inspection_suppresses_duplicate_peaks_and_preserves_confidence()
	{
		using Bitmap marker = CreateMarker();
		using Bitmap source = new(100, 60);
		using (Graphics graphics = Graphics.FromImage(source))
		{
			graphics.Clear(Color.FromArgb(25, 27, 27));
			graphics.DrawImageUnscaled(marker, 10, 10);
			graphics.DrawImageUnscaled(marker, 65, 35);
		}

		Config.Processing.Inspection inspectionConfig = new()
		{
			MarkerItemScale = 1,
			MarkerThreshold = 0.8f,
		};
		inspectionConfig.Marker?.Dispose();
		inspectionConfig.Marker = new Bitmap(marker);
		Config config = new()
		{
			ProcessingConfig = new Config.Processing
			{
				Scale = 1,
				InspectionConfig = inspectionConfig,
			},
		};

		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		RatEye.Processing.MultiInspection result = engine.NewMultiInspection(source);

		Assert.Equal(2, result.Inspections.Count);
		Assert.All(
			result.Inspections,
			inspection => Assert.True(inspection.MarkerConfidence > 0.99f)
		);
	}

	[Fact]
	public void Marker_peak_extraction_suppresses_adjacent_peaks_but_keeps_separated_matches()
	{
		using Mat response = new(5, 15, MatType.CV_32FC1, Scalar.All(0));
		response.Set(2, 2, 0.99f);
		response.Set(2, 1, 0.98f);
		response.Set(2, 3, 0.98f);
		response.Set(2, 7, 0.97f);

		var matches = RatEye.Processing.MultiInspection.ExtractMarkerPeaks(
			response,
			new System.Drawing.Size(5, 5),
			0.8f
		);

		Assert.Equal(2, matches.Count);
		Assert.Equal(new Vector2(2, 2), matches[0].position);
		Assert.Equal(0.99f, matches[0].confidence);
		Assert.Equal(new Vector2(7, 2), matches[1].position);
		Assert.Equal(0.97f, matches[1].confidence);
	}

	[Fact]
	public void Config_hash_is_stable_across_current_cultures()
	{
		CultureInfo original = CultureInfo.CurrentCulture;
		try
		{
			Config config = new();
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
			string englishHash = config.GetHash();
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

			Assert.Equal(englishHash, config.GetHash());
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	[Fact]
	public void Concurrent_debug_bitmap_writes_allocate_unique_files()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-debug-test-" + Guid.NewGuid().ToString("N")
		);
		try
		{
			Parallel.For(
				0,
				8,
				_ =>
				{
					using Bitmap bitmap = new(4, 4);
					Logger.SaveDebugBitmap(bitmap, root, "shared");
				}
			);

			Assert.Equal(8, Directory.GetFiles(root, "shared(*).png").Length);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Marker_peak_extraction_terminates_for_threshold_at_response_floor()
	{
		using Mat response = new(2, 2, MatType.CV_32FC1, Scalar.All(0.5));

		var matches = RatEye.Processing.MultiInspection.ExtractMarkerPeaks(
			response,
			new System.Drawing.Size(1, 1),
			-1f
		);

		Assert.Equal(4, matches.Count);
	}

	[Fact]
	public void Inspection_marker_is_loaded_only_when_processing_needs_it()
	{
		Config config = new();
		Assert.False(config.ProcessingConfig.InspectionConfig.IsMarkerLoaded);

		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		Assert.False(config.ProcessingConfig.InspectionConfig.IsMarkerLoaded);

		using Bitmap scaledMarker = RatEye.Processing.Inspection.GetScaledMarker(config);
		Assert.True(config.ProcessingConfig.InspectionConfig.IsMarkerLoaded);
		Assert.NotSame(config.ProcessingConfig.InspectionConfig.Marker, scaledMarker);
	}

	[Fact]
	public void Public_inspection_marker_getter_preserves_the_non_null_default()
	{
		Config.Processing.Inspection inspection = new();

		Bitmap marker = inspection.Marker;

		Assert.NotNull(marker);
		Assert.True(inspection.IsMarkerLoaded);
		inspection.DisposeMarker();
	}

	[Fact]
	public void Scaled_marker_has_an_independent_lifetime_from_engine_disposal()
	{
		Config config = new();
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using Bitmap scaledMarker = RatEye.Processing.Inspection.GetScaledMarker(config);

		engine.Dispose();

		Assert.True(scaledMarker.Width > 0);
		Assert.True(scaledMarker.Height > 0);
	}

	[Fact]
	public void NewIcon_does_not_take_ownership_of_the_callers_bitmap()
	{
		Config config = new();
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using Bitmap source = new(16, 16);

		using (RatEye.Processing.Icon icon = engine.NewIcon(
			source,
			Vector2.Zero,
			new Vector2(16, 16)
		))
		{
		}

		source.SetPixel(0, 0, Color.Red);
		Assert.Equal(Color.Red.ToArgb(), source.GetPixel(0, 0).ToArgb());
	}

	[Fact]
	public void Engine_disposal_clears_the_config_icon_manager_reference()
	{
		Config config = new();
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		Assert.NotNull(config.IconManager);

		engine.Dispose();

		Assert.Null(config.IconManager);
	}

	[Fact]
	public void Engine_factories_fail_immediately_after_disposal()
	{
		Config config = new();
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using Bitmap source = new(16, 16);
		engine.Dispose();

		Assert.Throws<ObjectDisposedException>(() => engine.NewInspection(source));
		Assert.Throws<ObjectDisposedException>(() => engine.NewMultiInspection(source));
		Assert.Throws<ObjectDisposedException>(() => engine.NewInventory(source));
		Assert.Throws<ObjectDisposedException>(() =>
			engine.NewIcon(source, Vector2.Zero, new Vector2(16, 16))
		);
	}

	[Fact]
	public void Concurrent_engine_disposal_is_idempotent()
	{
		Config config = new();
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));

		Parallel.Invoke(engine.Dispose, engine.Dispose);

		Assert.Null(config.IconManager);
	}

	private static Bitmap CreateMarker()
	{
		Bitmap marker = new(9, 9);
		using Graphics graphics = Graphics.FromImage(marker);
		graphics.Clear(Color.FromArgb(25, 27, 27));
		using Pen pen = new(Color.White, 2);
		graphics.DrawLine(pen, 1, 1, 7, 7);
		graphics.DrawLine(pen, 7, 1, 1, 7);
		marker.SetPixel(4, 1, Color.Red);
		return marker;
	}
}
