using System;
using System.Collections.Generic;

namespace RatEye.Diagnostics
{
	/// <summary>
	/// Versioned, application-neutral description of a captured scan that RatEye can replay.
	/// </summary>
	public sealed class ScanReplayManifest
	{
		/// <summary>
		/// Current replay contract version.
		/// </summary>
		public const int CurrentSchemaVersion = 1;

		/// <summary>
		/// Replay contract version used by this manifest.
		/// </summary>
		public int SchemaVersion { get; set; } = CurrentSchemaVersion;

		/// <summary>
		/// Stable fixture or diagnostic identifier.
		/// </summary>
		public string Id { get; set; } = "";

		/// <summary>
		/// Scan operation: inspection, multi-inspection, inventory, or icon.
		/// </summary>
		public string ScanType { get; set; } = "inspection";

		/// <summary>
		/// Image path relative to this manifest.
		/// </summary>
		public string ImageFile { get; set; } = "capture.png";

		/// <summary>
		/// Optional expected item identifiers used for regression assertions.
		/// </summary>
		public List<string> ExpectedItemIds { get; set; } = new List<string>();

		/// <summary>
		/// RatEye processing settings required to replay the capture.
		/// </summary>
		public ScanReplayConfiguration Configuration { get; set; } = new ScanReplayConfiguration();

		/// <summary>
		/// Capture metadata supplied by the host application.
		/// </summary>
		public ScanReplayContext Context { get; set; } = new ScanReplayContext();

		/// <summary>
		/// Result observed by the host when it created the bundle.
		/// </summary>
		public ScanReplayObservedResult Observed { get; set; } = new ScanReplayObservedResult();
	}

	/// <summary>
	/// RatEye processing settings stored with a replay.
	/// </summary>
	public sealed class ScanReplayConfiguration
	{
		public float Scale { get; set; } = 1;
		public string Language { get; set; } = "English";
		public bool OptimizeHighlighted { get; set; }
		public bool UseStaticIcons { get; set; } = true;
		public bool ScanRotatedIcons { get; set; } = true;
		public float MarkerThreshold { get; set; } = 0.82f;
		public float MinItemConfidence { get; set; } = 0.55f;
	}

	/// <summary>
	/// Host capture coordinates and environment facts used to reproduce geometry.
	/// </summary>
	public sealed class ScanReplayContext
	{
		public DateTime CapturedAtUtc { get; set; }
		public string ApplicationVersion { get; set; } = "";
		public int CaptureX { get; set; }
		public int CaptureY { get; set; }
		public int CaptureWidth { get; set; }
		public int CaptureHeight { get; set; }
		public int DisplayX { get; set; }
		public int DisplayY { get; set; }
		public int DisplayWidth { get; set; }
		public int DisplayHeight { get; set; }
		public float DpiScale { get; set; } = 1;
		public int? CursorX { get; set; }
		public int? CursorY { get; set; }
	}

	/// <summary>
	/// Result observed during the original host scan.
	/// </summary>
	public sealed class ScanReplayObservedResult
	{
		public List<string> ItemIds { get; set; } = new List<string>();
		public List<string> ItemNames { get; set; } = new List<string>();
		public List<float> Confidences { get; set; } = new List<float>();
		public Dictionary<string, double> StageMilliseconds { get; set; } =
			new Dictionary<string, double>();
	}
}
