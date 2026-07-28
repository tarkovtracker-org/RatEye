using System.Diagnostics;
using System.Drawing;
using Newtonsoft.Json;
using RatEye;
using RatEye.Diagnostics;
using RatStash;

try
{
	return RunBenchmark(args);
}
catch (Exception exception)
{
	return Fail(exception.ToString());
}

static int RunBenchmark(string[] arguments)
{
	Dictionary<string, string> options = ParseOptions(arguments);
	string fixtureDirectory = GetOption(options, "fixtures", "RATEYE_FIXTURES");
	string itemsPath = GetOption(options, "items", "RATEYE_ITEMS");
	string localePath = GetOption(options, "locale", "RATEYE_LOCALE");
	string iconsPath = GetOption(options, "icons", "RATEYE_ICONS", required: false);
	string trainedDataPath = GetOption(options, "traineddata", "RATEYE_TRAINEDDATA", required: false);

	if (!Directory.Exists(fixtureDirectory))
		return Fail($"Fixture directory does not exist: {fixtureDirectory}");
	if (!File.Exists(itemsPath))
		return Fail($"Item database does not exist: {itemsPath}");
	if (!File.Exists(localePath))
		return Fail($"Locale database does not exist: {localePath}");

	string[] manifestPaths = Directory.GetFiles(
		fixtureDirectory,
		"*.ratdiag.json",
		SearchOption.AllDirectories
	);
	if (manifestPaths.Length == 0)
		return Fail($"No *.ratdiag.json manifests found under {fixtureDirectory}");

	Database database = Database.FromFile(itemsPath, false, localePath);
	List<BenchmarkCaseReport> cases = new();
	foreach (
		string manifestPath in manifestPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
	)
	{
		ScanReplayManifest manifest =
			JsonConvert.DeserializeObject<ScanReplayManifest>(File.ReadAllText(manifestPath))
			?? throw new InvalidDataException($"Unable to deserialize {manifestPath}");

		cases.Add(
			RunCase(
				fixtureDirectory,
				manifestPath,
				manifest,
				database,
				iconsPath,
				trainedDataPath
			)
		);
	}

	BenchmarkReport report = new()
	{
		SchemaVersion = 1,
		GeneratedAtUtc = DateTime.UtcNow,
		FixtureDirectory = Path.GetFullPath(fixtureDirectory),
		Cases = cases,
	};

	string outputPath = options.TryGetValue("output", out string? configuredOutput)
		? Path.GetFullPath(configuredOutput)
		: Path.Combine(fixtureDirectory, "rateye-benchmark-report.json");
	File.WriteAllText(outputPath, JsonConvert.SerializeObject(report, Formatting.Indented));

	int asserted = cases.Count(result => result.MatchesExpected.HasValue);
	int matched = cases.Count(result => result.MatchesExpected == true);
	Console.WriteLine($"Wrote {cases.Count} case(s) to {outputPath}");
	Console.WriteLine(
		$"Expected-result matches: {matched}/{asserted} asserted; {cases.Count - asserted} unasserted"
	);
	return cases.Any(result => result.MatchesExpected == false) ? 2 : 0;
}

static BenchmarkCaseReport RunCase(
	string fixtureDirectory,
	string manifestPath,
	ScanReplayManifest manifest,
	Database database,
	string iconsPath,
	string trainedDataPath
)
{
	if (manifest.SchemaVersion != ScanReplayManifest.CurrentSchemaVersion)
		throw new InvalidDataException(
			$"{manifestPath} uses schema {manifest.SchemaVersion}; "
				+ $"expected {ScanReplayManifest.CurrentSchemaVersion}."
		);

	if (string.IsNullOrWhiteSpace(manifest.ImageFile) || Path.IsPathRooted(manifest.ImageFile))
		throw new InvalidDataException(
			$"{manifestPath} must reference an image relative to its manifest."
		);

	string manifestDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
	string imagePath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.ImageFile));
	string fixtureDirectoryPrefix = Path.GetFullPath(fixtureDirectory).TrimEnd(
		Path.DirectorySeparatorChar,
		Path.AltDirectorySeparatorChar
	) + Path.DirectorySeparatorChar;
	if (!imagePath.StartsWith(fixtureDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
		throw new InvalidDataException(
			$"{manifestPath} references an image outside the fixture directory."
		);
	if (!File.Exists(imagePath))
		throw new FileNotFoundException("Replay image was not found.", imagePath);

	Config config = CreateConfig(manifest.Configuration, iconsPath, trainedDataPath);
	using RatEyeEngine engine = new(config, database);
	using Bitmap image = new(imagePath);
	Stopwatch total = Stopwatch.StartNew();
	List<BenchmarkDetection> detections = new();
	Dictionary<string, double> timings = new(StringComparer.Ordinal);

	switch (manifest.ScanType.Trim().ToLowerInvariant())
	{
		case "inspection":
			{
				RatEye.Processing.Inspection inspection = engine.NewInspection(image);
				RatStash.Item? item = inspection.Item;
				detections.Add(
					new BenchmarkDetection
					{
						ItemId = item?.Id,
						ItemName = item?.Name,
						Confidence = inspection.ItemConfidence,
						MarkerConfidence = inspection.MarkerConfidence,
					}
				);
				AddTimings(timings, inspection.Timings.Snapshot());
				break;
			}
		case "multi-inspection":
			{
				RatEye.Processing.MultiInspection multi = engine.NewMultiInspection(image);
				int index = 0;
				foreach (RatEye.Processing.Inspection inspection in multi.Inspections)
				{
					RatStash.Item? item = inspection.Item;
					detections.Add(
						new BenchmarkDetection
						{
							ItemId = item?.Id,
							ItemName = item?.Name,
							Confidence = inspection.ItemConfidence,
							MarkerConfidence = inspection.MarkerConfidence,
						}
					);
					AddTimings(timings, inspection.Timings.Snapshot(), $"inspection[{index}].");
					index++;
				}
				AddTimings(timings, multi.Timings.Snapshot());
				break;
			}
		case "inventory":
			{
				using RatEye.Processing.Inventory inventory = engine.NewInventory(image);
				Vector2? cursor =
					manifest.Context.CursorX.HasValue && manifest.Context.CursorY.HasValue
						? new Vector2(manifest.Context.CursorX.Value, manifest.Context.CursorY.Value)
						: null;
				RatEye.Processing.Icon? icon = inventory.LocateIcon(cursor);
				if (icon is not null)
				{
					RatStash.Item? item = icon.Item;
					detections.Add(
						new BenchmarkDetection
						{
							ItemId = item?.Id,
							ItemName = item?.Name,
							Confidence = icon.DetectionConfidence,
						}
					);
					AddTimings(timings, icon.Timings.Snapshot());
				}
				AddTimings(timings, inventory.Timings.Snapshot());
				break;
			}
		case "icon":
			{
				using RatEye.Processing.Icon icon = engine.NewIcon(
					image,
					Vector2.Zero,
					new Vector2(image.Width, image.Height)
				);
				RatStash.Item? item = icon.Item;
				detections.Add(
					new BenchmarkDetection
					{
						ItemId = item?.Id,
						ItemName = item?.Name,
						Confidence = icon.DetectionConfidence,
					}
				);
				AddTimings(timings, icon.Timings.Snapshot());
				break;
			}
		default:
			throw new InvalidDataException(
				$"Unsupported scan type '{manifest.ScanType}' in {manifestPath}."
			);
	}

	total.Stop();
	timings["total"] = total.Elapsed.TotalMilliseconds;

	List<string> detectedIds = detections
		.Where(detection => !string.IsNullOrWhiteSpace(detection.ItemId))
		.Select(detection => detection.ItemId!)
		.ToList();
	bool? matchesExpected =
		manifest.ExpectedItemIds.Count == 0
			? null
			: manifest
				.ExpectedItemIds.OrderBy(id => id, StringComparer.Ordinal)
				.SequenceEqual(
					detectedIds.OrderBy(id => id, StringComparer.Ordinal),
					StringComparer.Ordinal
				);

	return new BenchmarkCaseReport
	{
		Id = string.IsNullOrWhiteSpace(manifest.Id)
			? Path.GetFileNameWithoutExtension(manifestPath)
			: manifest.Id,
		Manifest = Path.GetRelativePath(fixtureDirectory, manifestPath),
		ScanType = manifest.ScanType,
		ExpectedItemIds = manifest.ExpectedItemIds,
		Detections = detections,
		StageMilliseconds = timings,
		MatchesExpected = matchesExpected,
	};
}

static Config CreateConfig(ScanReplayConfiguration replay, string iconsPath, string trainedDataPath)
{
	Language language = Enum.TryParse(replay.Language, ignoreCase: true, out Language parsed)
		? parsed
		: Language.English;
	Config config = new()
	{
		ProcessingConfig = new Config.Processing
		{
			UseCache = false,
			Scale = replay.Scale,
			Language = language,
			InventoryConfig = new Config.Processing.Inventory
			{
				OptimizeHighlighted = replay.OptimizeHighlighted,
			},
			InspectionConfig = new Config.Processing.Inspection
			{
				MarkerThreshold = replay.MarkerThreshold,
				MinItemConfidence = replay.MinItemConfidence,
			},
			IconConfig = new Config.Processing.Icon
			{
				UseStaticIcons = replay.UseStaticIcons && Directory.Exists(iconsPath),
				ScanRotatedIcons = replay.ScanRotatedIcons,
			},
		},
	};

	if (Directory.Exists(iconsPath))
		config.PathConfig.StaticIcons = Path.GetFullPath(iconsPath);
	if (Directory.Exists(trainedDataPath))
		config.PathConfig.TrainedData = Path.GetFullPath(trainedDataPath);
	return config;
}

static void AddTimings(
	Dictionary<string, double> target,
	IReadOnlyDictionary<string, double> source,
	string prefix = ""
)
{
	foreach ((string stage, double elapsed) in source)
		target[prefix + stage] = elapsed;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
	Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
	for (int index = 0; index < arguments.Length; index++)
	{
		string argument = arguments[index];
		if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
			continue;
		parsed[argument.Substring(2)] = arguments[++index];
	}
	return parsed;
}

static string GetOption(
	IReadOnlyDictionary<string, string> options,
	string name,
	string environmentVariable,
	bool required = true
)
{
	if (options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
		return value;

	value = Environment.GetEnvironmentVariable(environmentVariable);
	if (!string.IsNullOrWhiteSpace(value))
		return value;

	if (!required)
		return "";

	throw new ArgumentException(
		$"Missing --{name} <path> (or {environmentVariable}).",
		nameof(options)
	);
}

static int Fail(string message)
{
	Console.Error.WriteLine(message);
	return 1;
}

internal sealed class BenchmarkReport
{
	public int SchemaVersion { get; set; }
	public DateTime GeneratedAtUtc { get; set; }
	public string FixtureDirectory { get; set; } = "";
	public List<BenchmarkCaseReport> Cases { get; set; } = new();
}

internal sealed class BenchmarkCaseReport
{
	public string Id { get; set; } = "";
	public string Manifest { get; set; } = "";
	public string ScanType { get; set; } = "";
	public List<string> ExpectedItemIds { get; set; } = new();
	public List<BenchmarkDetection> Detections { get; set; } = new();
	public Dictionary<string, double> StageMilliseconds { get; set; } = new();
	public bool? MatchesExpected { get; set; }
}

internal sealed class BenchmarkDetection
{
	public string? ItemId { get; set; }
	public string? ItemName { get; set; }
	public float Confidence { get; set; }
	public float? MarkerConfidence { get; set; }
}
