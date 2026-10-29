#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using RatEye;
using Xunit;

namespace RatEyeTest;

/// <summary>
/// Regression guards for native OpenCvSharp loading and core image ops used by ScanEngine.
/// Fixtures are synthetic bitmaps (not game assets) so they can ship in-repo without copyright risk.
/// </summary>
public class OpenCvPipelineTests
{
	[Fact]
	public void Native_runtime_loads_and_supports_core_image_ops()
	{
		using Mat solid = new(48, 64, MatType.CV_8UC3, new Scalar(10, 20, 30));
		Assert.False(solid.Empty());
		Assert.Equal(64, solid.Width);
		Assert.Equal(48, solid.Height);
		Assert.Equal(MatType.CV_8UC3, solid.Type());

		using Mat gray = solid.CvtColor(ColorConversionCodes.BGR2GRAY);
		Assert.Equal(MatType.CV_8UC1, gray.Type());

		using Mat resized = gray.Resize(new OpenCvSharp.Size(32, 24));
		Assert.Equal(32, resized.Width);
		Assert.Equal(24, resized.Height);

		using Mat binary = resized.Threshold(0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
		Assert.False(binary.Empty());
	}

	[Fact]
	public void Bitmap_roundtrip_and_template_match_succeed()
	{
		// Solid black field with a unique gradient patch so correlation peaks at one location.
		using Mat source = new(64, 64, MatType.CV_8UC3, new Scalar(0, 0, 0));
		for (int y = 0; y < 16; y++)
		{
			for (int x = 0; x < 16; x++)
				source.Set(
					12 + y,
					8 + x,
					new Vec3b((byte)(x * 8), (byte)(y * 8), (byte)((x + y) * 4))
				);
		}

		using Mat template = source[new Rect(8, 12, 16, 16)].Clone();

		using Bitmap roundTrip = BitmapConverter.ToBitmap(source);
		using Mat fromBitmap = roundTrip.ToMat();
		Assert.False(fromBitmap.Empty());
		Assert.Equal(source.Width, fromBitmap.Width);
		Assert.Equal(source.Height, fromBitmap.Height);

		using Mat result = source.MatchTemplate(template, TemplateMatchModes.SqDiffNormed);
		result.MinMaxLoc(out double minVal, out _, out OpenCvSharp.Point minLoc, out _);

		Assert.True(minVal < 1e-6, $"Expected near-zero template difference, got {minVal:E3}");
		Assert.Equal(8, minLoc.X);
		Assert.Equal(12, minLoc.Y);
	}

	[Fact]
	public void ImRead_writes_and_reads_temp_png()
	{
		string path = Path.Combine(
			Path.GetTempPath(),
            "RatEye-ocv-" + Guid.NewGuid().ToString("N") + ".png"
		);
		try
		{
			using (Mat mat = new(20, 20, MatType.CV_8UC3, new Scalar(40, 80, 120)))
			{
				Assert.True(Cv2.ImWrite(path, mat));
			}

			using Mat loaded = Cv2.ImRead(path, ImreadModes.Unchanged);
			Assert.False(loaded.Empty());
			Assert.Equal(20, loaded.Width);
			Assert.Equal(20, loaded.Height);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	[Fact]
	public void Unsafe_byte_indexer_honors_non_contiguous_row_stride()
	{
		using Mat parent = new(8, 10, MatType.CV_8UC1, Scalar.Black);
		parent.Set(4, 6, (byte)123);
		using Mat submatrix = parent[new Rect(3, 2, 4, 5)];

		Assert.False(submatrix.IsContinuous());
		Assert.Equal(MatType.CV_8UC1, submatrix.Type());

		Mat.UnsafeIndexer<byte> indexer = submatrix.GetUnsafeGenericIndexer<byte>();
		Assert.Equal((byte)123, indexer[2, 3]);

		indexer[1, 2] = 77;
		Assert.Equal((byte)77, parent.At<byte>(3, 5));
	}

	[Fact]
	public void Highlighted_inventory_locates_and_crops_the_hovered_item()
	{
		using Bitmap screenshot = new(300, 200);
		using (Graphics graphics = Graphics.FromImage(screenshot))
		{
			graphics.Clear(System.Drawing.Color.Black);
			using SolidBrush highlight = new(System.Drawing.Color.FromArgb(90, 90, 90));
			graphics.FillRectangle(highlight, 60, 40, 126, 63);
		}

		Config config = CreateProcessingConfig(optimizeHighlighted: true);
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using RatEye.Processing.Inventory inventory = engine.NewInventory(screenshot);

		RatEye.Processing.Icon? icon = inventory.LocateIcon(new Vector2(120, 70));

		Assert.NotNull(icon);
		Assert.Equal(new Vector2(53, 25), icon.Position);
		Assert.Equal(new Vector2(142, 95), icon.Size);
	}

	[Fact]
	public void Highlighted_inventory_keeps_previous_located_icons_alive()
	{
		using Bitmap screenshot = new(460, 200);
		using (Graphics graphics = Graphics.FromImage(screenshot))
		{
			graphics.Clear(System.Drawing.Color.Black);
			using SolidBrush highlight = new(System.Drawing.Color.FromArgb(90, 90, 90));
			graphics.FillRectangle(highlight, 60, 40, 126, 63);
			graphics.FillRectangle(highlight, 260, 40, 126, 63);
		}

		string missingDirectory = Path.Combine(
			Path.GetTempPath(),
			"RatEye-missing-" + Guid.NewGuid().ToString("N")
		);
		Config config = CreateProcessingConfig(optimizeHighlighted: true);
		config.PathConfig.StaticIcons = missingDirectory;
		config.PathConfig.TrainedData = missingDirectory;
		config.ProcessingConfig.IconConfig.UseStaticIcons = true;
		config.ProcessingConfig.IconConfig.ScanRotatedIcons = false;
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using RatEye.Processing.Inventory inventory = engine.NewInventory(screenshot);

		RatEye.Processing.Icon? first = inventory.LocateIcon(new Vector2(120, 70));
		RatEye.Processing.Icon? second = inventory.LocateIcon(new Vector2(320, 70));

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.NotSame(first, second);
		Assert.Equal(0, first.DetectionConfidence);
	}

	[Fact]
	public void Normal_inventory_rejects_a_slot_scale_too_small_for_safe_edge_walking()
	{
		using Bitmap screenshot = new(32, 32);
		Config config = CreateProcessingConfig(optimizeHighlighted: false);
		config.ProcessingConfig.Scale = 0.01f;
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		using RatEye.Processing.Inventory inventory = engine.NewInventory(screenshot);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			_ = inventory.Icons
		);

		Assert.Contains("at least two pixels", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Blank_inspection_is_a_low_confidence_failure_without_ocr()
	{
		using Bitmap screenshot = new(320, 180);
		Config config = CreateProcessingConfig(optimizeHighlighted: false);
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));

		RatEye.Processing.Inspection inspection = engine.NewInspection(screenshot);

		Assert.False(inspection.ContainsMarker);
		Assert.True(
			inspection.MarkerConfidence < config.ProcessingConfig.InspectionConfig.MarkerThreshold
		);
		Assert.Null(inspection.Item);
		Assert.Equal(0, inspection.ItemConfidence);
	}

	[Theory]
	[InlineData("Subject Search")]
	[InlineData("subject search")]
	[InlineData("SUBJECT SEARCH")]
	[InlineData("Subject Searcn")] // minor OCR noise
	[InlineData("Поиск предмета")]
	[InlineData("Rechercher un objet")]
	[InlineData("Buscar objeto")]
	public void Inventory_subject_search_chrome_is_not_treated_as_item_title(string title)
	{
		Assert.True(RatEye.Processing.Inspection.IsUiChromeTitle(title));
	}

	[Theory]
	[InlineData("Physical Bitcoin")]
	[InlineData("Pack of sugar")]
	[InlineData("Bolt-action sniper rifle Remington Model 700 7.62x51")]
	[InlineData("Suchen")] // short DE token kept out of denylist; MinItemConfidence is the guard
	[InlineData("")]
	[InlineData("   ")]
	public void Real_item_names_are_not_flagged_as_ui_chrome(string title)
	{
		Assert.False(RatEye.Processing.Inspection.IsUiChromeTitle(title));
	}

	[Fact]
	public void Ui_chrome_title_similarity_scores_stay_below_min_item_confidence()
	{
		// Normed Levenshtein of short UI chrome vs a long catalog name is often
		// nonzero; keep those scores below the configured acceptance threshold.
		float threshold = CreateProcessingConfig(
			optimizeHighlighted: false
		).ProcessingConfig.InspectionConfig.MinItemConfidence;

		Assert.True("Subject Search".NormedLevenshteinDistance("Physical Bitcoin") < threshold);
		Assert.True("Subject Search".NormedLevenshteinDistance("Pack of sugar") < threshold);
	}

	[Fact]
	public void Inspection_reports_the_exact_missing_traineddata_file()
	{
		using Bitmap marker = CreateMarker();
		using Bitmap screenshot = new(120, 60);
		using (Graphics graphics = Graphics.FromImage(screenshot))
		{
			graphics.Clear(System.Drawing.Color.FromArgb(25, 27, 27));
			graphics.DrawImageUnscaled(marker, 10, 10);
		}

		string missingDirectory = Path.Combine(
			Path.GetTempPath(),
			"RatEye-missing-" + Guid.NewGuid().ToString("N")
		);
		Config config = CreateProcessingConfig(optimizeHighlighted: false);
		config.PathConfig.TrainedData = missingDirectory;
		config.ProcessingConfig.InspectionConfig.Marker?.Dispose();
		config.ProcessingConfig.InspectionConfig.Marker = new Bitmap(marker);
		config.ProcessingConfig.InspectionConfig.MarkerItemScale = 1;
		config.ProcessingConfig.InspectionConfig.MarkerThreshold = 0.8f;

		RatStash.Item expected = new()
		{
			Id = "fixture",
			Name = "Fixture item",
			ShortName = "Fixture",
			Width = 1,
			Height = 1,
		};
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([expected]));
		RatEye.Processing.Inspection inspection = engine.NewInspection(screenshot);
		Assert.True(inspection.ContainsMarker);

		FileNotFoundException exception = Assert.Throws<FileNotFoundException>(() =>
			_ = inspection.Item
		);
		Assert.Equal(Path.Combine(missingDirectory, "eng.traineddata"), exception.FileName);
	}

	[Fact]
	public void Static_icon_template_matching_identifies_an_exact_generated_fixture()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-template-test-" + Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(root);
		try
		{
			string iconPath = Path.Combine(root, "fixture.png");
			using (Bitmap iconSource = new(64, 64))
			{
				using Graphics graphics = Graphics.FromImage(iconSource);
				graphics.Clear(System.Drawing.Color.Transparent);
				using SolidBrush body = new(System.Drawing.Color.FromArgb(255, 35, 140, 210));
				graphics.FillRectangle(body, 8, 12, 41, 29);
				using Mat iconMat = BitmapConverter.ToMat(iconSource);
				Assert.True(Cv2.ImWrite(iconPath, iconMat));
			}
			File.WriteAllText(Path.Combine(root, "broken.png"), "not an image");

			RatStash.Item expected = new()
			{
				Id = "fixture",
				Name = "Fixture item",
				ShortName = "Fixture",
				Width = 1,
				Height = 1,
			};
			RatStash.Item broken = new()
			{
				Id = "broken",
				Name = "Broken fixture",
				ShortName = "Broken",
				Width = 1,
				Height = 1,
			};
			Config config = CreateProcessingConfig(optimizeHighlighted: false);
			config.PathConfig.StaticIcons = root;
			config.ProcessingConfig.IconConfig.UseStaticIcons = true;
			config.ProcessingConfig.IconConfig.ScanRotatedIcons = false;

			using RatEyeEngine engine = new(
				config,
				RatStash.Database.FromItems([expected, broken])
			);
			engine.Config.IconManager.EnsureStaticIconsLoaded(new Vector2(1, 1));
			KeyValuePair<string, Mat> loaded = Assert.Single(
				engine.Config.IconManager.StaticIcons[new Vector2(1, 1)]
			);
			Assert.EndsWith("fixture.png", loaded.Key, StringComparison.Ordinal);
			Mat template = loaded.Value;
			Bitmap exactScan = template.ToBitmap();
			using RatEye.Processing.Icon result = new(
				exactScan,
				Vector2.Zero,
				new Vector2(template.Width, template.Height),
				engine.Config,
				ownsIcon: true
			);

			Assert.Equal(expected.Id, result.Item.Id);
			Assert.True(result.DetectionConfidence > 0.9999f);
			Assert.Equal(Vector2.Zero, result.ItemPosition);
			Assert.False(result.Rotated);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData(true, 1f, 8, 30, 8, 30)]
	[InlineData(true, 1f, 28, 8, 28, 8)]
	[InlineData(true, 1f, 0, 0, 0, 0)]
	[InlineData(false, 1f, 8, 30, 8, 30)]
	[InlineData(false, 1f, 28, 8, 28, 8)]
	[InlineData(false, 1f, 0, 0, 0, 0)]
	[InlineData(true, 1.25f, 9, 31, 11, 39)]
	[InlineData(false, 1.25f, 9, 31, 11, 39)]
	[InlineData(true, 1.5f, 9, 31, 14, 46)]
	[InlineData(false, 1.5f, 9, 31, 14, 46)]
	public void Icon_item_position_maps_back_to_screen_coordinates(
		bool rotated,
		float scale,
		int x,
		int y,
		int expectedX,
		int expectedY
	)
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-rotated-test-" + Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(root);
		try
		{
			// A 2x1 item template (127x64) with an asymmetric pattern, so only one
			// placement matches.
			using Mat template = new(64, 127, MatType.CV_8UC4, new Scalar(40, 140, 210, 255));
			Cv2.Rectangle(template, new Rect(4, 4, 30, 20), new Scalar(240, 240, 240, 255), -1);
			Cv2.Circle(template, new OpenCvSharp.Point(100, 45), 12, new Scalar(20, 60, 20, 255), -1);
			Assert.True(Cv2.ImWrite(Path.Combine(root, "wide.png"), template));

			RatStash.Item wide = new() { Id = "wide", Name = "Wide", ShortName = "W", Width = 2, Height = 1 };
			Config config = CreateProcessingConfig(optimizeHighlighted: false);
			config.PathConfig.StaticIcons = root;
			config.ProcessingConfig.Scale = scale;
			config.ProcessingConfig.IconConfig.UseStaticIcons = true;
			config.ProcessingConfig.IconConfig.ScanRotatedIcons = true;

			using RatEyeEngine engine = new(config, RatStash.Database.FromItems([wide]));
			engine.Config.IconManager.EnsureStaticIconsLoaded(new Vector2(2, 1));
			using Mat loaded = engine.Config.IconManager.StaticIcons[new Vector2(2, 1)].Values.Single().Clone();

			// Keep dimensions divisible by four so fractional scales do not change
			// the canvas dimensions after the scanner rescales it. Exercise unequal
			// padding, a border placement, and ordinary as well as rotated items.
			using Mat canvas = new(
				rotated ? 172 : 100,
				rotated ? 100 : 172,
				MatType.CV_8UC3,
				new Scalar(15, 15, 15)
			);
			using Mat shown = new();
			if (rotated)
				Cv2.Rotate(loaded, shown, RotateFlags.Rotate90Clockwise);
			else
				loaded.CopyTo(shown);
			using (Mat target = new(canvas, new Rect(x, y, shown.Width, shown.Height)))
				shown.CopyTo(target);

			using Mat screen = new();
			Cv2.Resize(
				canvas,
				screen,
				new OpenCvSharp.Size((int)(canvas.Width * scale), (int)(canvas.Height * scale)),
				0,
				0,
				InterpolationFlags.Cubic
			);
			using RatEye.Processing.Icon icon = new(
				screen.ToBitmap(),
				Vector2.Zero,
				new Vector2(screen.Width, screen.Height),
				engine.Config,
				ownsIcon: true
			);

			Assert.Equal("wide", icon.Item.Id);
			Assert.True(icon.DetectionConfidence > 0.99f);
			Assert.Equal(rotated, icon.Rotated);
			Assert.Equal(new Vector2(expectedX, expectedY), icon.ItemPosition);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void WarmUpIconTemplates_loads_the_requested_slot_size_before_any_scan()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-warmup-test-" + Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(root);
		try
		{
			foreach (string id in new[] { "small", "wide" })
			{
				int width = id == "small" ? 64 : 127;
				using Bitmap iconSource = new(width, 64);
				using Graphics graphics = Graphics.FromImage(iconSource);
				graphics.Clear(System.Drawing.Color.FromArgb(255, 35, 140, 210));
				using Mat iconMat = BitmapConverter.ToMat(iconSource);
				Assert.True(Cv2.ImWrite(Path.Combine(root, id + ".png"), iconMat));
			}

			RatStash.Item small = new() { Id = "small", Name = "Small", ShortName = "S", Width = 1, Height = 1 };
			RatStash.Item wide = new() { Id = "wide", Name = "Wide", ShortName = "W", Width = 2, Height = 1 };
			Config config = CreateProcessingConfig(optimizeHighlighted: false);
			config.PathConfig.StaticIcons = root;
			config.ProcessingConfig.IconConfig.UseStaticIcons = true;

			using RatEyeEngine engine = new(config, RatStash.Database.FromItems([small, wide]));
			Assert.Empty(engine.Config.IconManager.StaticIcons);

			engine.WarmUpIconTemplates(new Vector2(2, 1));

			IconManager icons = engine.Config.IconManager;
			KeyValuePair<string, Mat> loaded = Assert.Single(Assert.Single(icons.StaticIcons).Value);
			Assert.Equal(new Vector2(2, 1), Assert.Single(icons.StaticIcons).Key);
			Assert.EndsWith("wide.png", loaded.Key, StringComparison.Ordinal);

			// A repeat warm-up of a loaded size is a no-op, not a reload.
			Mat before = loaded.Value;
			engine.WarmUpIconTemplates(new Vector2(2, 1));
			Assert.Same(before, Assert.Single(icons.StaticIcons[new Vector2(2, 1)]).Value);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void WarmUpIconTemplates_after_dispose_throws_instead_of_reloading()
	{
		Config config = CreateProcessingConfig(optimizeHighlighted: false);
		config.ProcessingConfig.IconConfig.UseStaticIcons = true;
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		engine.Dispose();

		Assert.Throws<ObjectDisposedException>(() => engine.WarmUpIconTemplates(new Vector2(1, 1)));
	}

	[Fact]
	public void WarmUpIconTemplates_after_dispose_throws_when_static_icons_are_disabled()
	{
		Config config = CreateProcessingConfig(optimizeHighlighted: false);
		config.ProcessingConfig.IconConfig.UseStaticIcons = false;
		RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
		engine.Dispose();

		Assert.Throws<ObjectDisposedException>(() => engine.WarmUpIconTemplates(new Vector2(1, 1)));
	}

	[Fact]
	public void Missing_static_icon_data_degrades_without_blocking_engine_startup()
	{
		string missingDirectory = Path.Combine(
			Path.GetTempPath(),
			"RatEye-missing-icons-" + Guid.NewGuid().ToString("N")
		);
		Config config = CreateProcessingConfig(optimizeHighlighted: true);
		config.PathConfig.StaticIcons = missingDirectory;
		config.ProcessingConfig.IconConfig.UseStaticIcons = true;
		RatStash.Item item = new()
		{
			Id = "fixture",
			Name = "Fixture item",
			ShortName = "Fixture",
			Width = 1,
			Height = 1,
		};

		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([item]));
		engine.Config.IconManager.EnsureStaticIconsLoaded(new Vector2(1, 1));

		Assert.Empty(engine.Config.IconManager.StaticIcons);
	}

	[Fact]
	public void Low_confidence_icon_verification_skips_ocr_when_secondary_traineddata_is_missing()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"RatEye-partial-traineddata-" + Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "rus.traineddata"), "");
			Config config = CreateProcessingConfig(optimizeHighlighted: false);
			config.PathConfig.StaticIcons = Path.Combine(root, "missing-icons");
			config.PathConfig.TrainedData = root;
			config.ProcessingConfig.Language = RatStash.Language.Russian;
			config.ProcessingConfig.IconConfig.UseStaticIcons = true;
			config.ProcessingConfig.IconConfig.ScanRotatedIcons = false;
			using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
			using Bitmap source = new(64, 64);
			using RatEye.Processing.Icon icon = engine.NewIcon(
				source,
				Vector2.Zero,
				new Vector2(64, 64)
			);

			Assert.Equal(0, icon.DetectionConfidence);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static Config CreateProcessingConfig(bool optimizeHighlighted) =>
		new()
		{
			ProcessingConfig = new Config.Processing
			{
				UseCache = false,
				Scale = 1,
				IconConfig = new Config.Processing.Icon { UseStaticIcons = false },
				InventoryConfig = new Config.Processing.Inventory
				{
					OptimizeHighlighted = optimizeHighlighted,
				},
			},
		};

	private static Bitmap CreateMarker()
	{
		Bitmap marker = new(9, 9);
		using Graphics graphics = Graphics.FromImage(marker);
		graphics.Clear(System.Drawing.Color.FromArgb(25, 27, 27));
		using Pen pen = new(System.Drawing.Color.White, 2);
		graphics.DrawLine(pen, 1, 1, 7, 7);
		graphics.DrawLine(pen, 7, 1, 1, 7);
		marker.SetPixel(4, 1, System.Drawing.Color.Red);
		return marker;
	}
}
