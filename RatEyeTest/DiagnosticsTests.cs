using System;
using System.Drawing;
using RatEye;
using RatEye.Diagnostics;
using Xunit;

namespace RatEyeTest;

public class DiagnosticsTests
{
	[Fact]
	public void Replay_manifest_defaults_to_the_current_contract()
	{
		ScanReplayManifest manifest = new();

		Assert.Equal(ScanReplayManifest.CurrentSchemaVersion, manifest.SchemaVersion);
		Assert.Equal("inspection", manifest.ScanType);
		Assert.Equal(1, manifest.Configuration.Scale);
		Assert.Equal(0.82f, manifest.Configuration.MarkerThreshold);
		Assert.Equal(0.55f, manifest.Configuration.MinItemConfidence);
		Assert.Empty(manifest.ExpectedItemIds);
	}

	[Fact]
	public void Inspection_records_marker_stage_timing()
	{
		using Bitmap image = new(120, 80);
		Config config = new() { ProcessingConfig = new Config.Processing { UseCache = false } };
		using RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));

		RatEye.Processing.Inspection inspection = engine.NewInspection(image);
		_ = inspection.MarkerConfidence;

		Assert.Contains("inspection.marker_search", inspection.Timings.Snapshot().Keys);
		Assert.True(inspection.Timings.Snapshot()["inspection.marker_search"] >= 0);
	}
}
