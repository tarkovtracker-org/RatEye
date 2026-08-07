using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = OpenCvSharp.Point;

namespace RatEye.Processing
{
	/// <summary>
	/// Represents multiple <see cref="RatEye.Processing.Inspection"/>
	/// </summary>
	public class MultiInspection
	{
		private readonly Config _config;
		private readonly Bitmap _image;

		private Config.Processing ProcessingConfig => _config.ProcessingConfig;
		private Config.Processing.Inspection InspectionConfig => ProcessingConfig.InspectionConfig;

		// Backing property fields
		private List<Inspection> _inspections;

		/// <summary>
		/// Elapsed processing time recorded while locating inspection markers.
		/// </summary>
		public ProcessingTimings Timings { get; } = new ProcessingTimings();

		/// <summary>
		/// List of all inspections found in the image
		/// </summary>
		public List<Inspection> Inspections
		{
			get
			{
				SatisfyState(State.SearchedMarkers);
				return _inspections;
			}
			private set => _inspections = value;
		}

		/// <summary>
		/// Constructor for MultiInspection view processing object
		/// </summary>
		/// <param name="image">Image of the multiInspection view which will be processed</param>
		/// <param name="config">The config to use for this instance></param>
		/// <remarks>Provided image has to be in RGB</remarks>
		internal MultiInspection(Bitmap image, Config config)
		{
			_config = config;
			_image = image;
		}

		#region Processing state handling

		private enum State
		{
			Default,
			SearchedMarkers,
		}

		private State _currentState = State.Default;

		private void SatisfyState(State targetState)
		{
			while (_currentState < targetState)
			{
				switch (_currentState + 1)
				{
					case State.Default:
						break;
					case State.SearchedMarkers:
						SearchMarker();
						break;
					default:
						throw new InvalidOperationException("Cannot satisfy unknown state.");
				}

				_currentState++;
			}
		}

		#endregion

		/// <summary>
		/// Search for all different marker types and pick the best matching one
		/// </summary>
		private void SearchMarker()
		{
			long started = ProcessingTimings.Start();
			SatisfyState(State.Default);

			using Bitmap marker = Inspection.GetScaledMarker(_config);
			var markers = GetMarkerPositions(marker);
			_inspections = markers
				.Select(match => new Inspection(_image, _config, match.position, match.confidence))
				.ToList();
			Timings.RecordSince("multi_inspection.marker_search", started);
		}

		/// <summary>
		/// Identify the give marker inside the source
		/// </summary>
		/// <param name="marker">The marker template to identify</param>
		/// <remarks>Provided marker has to be in RGB</remarks>
		/// <returns>List of markers which confidence is above <see cref="Config.Processing.Inspection.MarkerThreshold"/></returns>
		private List<(Vector2 position, float confidence)> GetMarkerPositions(Bitmap marker)
		{
			using var refMat = _image.ToMat();
			using var tplMat = marker.ToMat(); // tpl = template
			using var res = new Mat(
				refMat.Rows - tplMat.Rows + 1,
				refMat.Cols - tplMat.Cols + 1,
				MatType.CV_32FC1
			);

			// Gray scale both reference and template image
			using var gref = refMat.CvtColor(ColorConversionCodes.RGB2GRAY);
			using var gtpl = tplMat.CvtColor(ColorConversionCodes.RGB2GRAY);

			Cv2.MatchTemplate(gref, gtpl, res, TemplateMatchModes.CCoeffNormed);

			return ExtractMarkerPeaks(res, marker.Size, InspectionConfig.MarkerThreshold);
		}

		internal static List<(Vector2 position, float confidence)> ExtractMarkerPeaks(
			Mat response,
			System.Drawing.Size markerSize,
			float threshold
		)
		{
			var matches = new List<(Vector2 position, float confidence)>();
			if (response.Empty())
				return matches;

			float effectiveThreshold = float.IsNaN(threshold)
				? 1f
				: Math.Max(threshold, -1f);
			var candidates = new List<(Point location, float confidence)>();
			Mat.UnsafeIndexer<float> responseIndexer =
				response.GetUnsafeGenericIndexer<float>();
			int rows = response.Rows;
			int columns = response.Cols;
			for (int row = 0; row < rows; row++)
			{
				for (int column = 0; column < columns; column++)
				{
					float confidence = responseIndexer[row, column];
					if (
						!float.IsNaN(confidence)
						&& confidence >= effectiveThreshold
						&& IsPlateauRepresentative(
							responseIndexer,
							rows,
							columns,
							row,
							column,
							confidence
						)
					)
						candidates.Add((new Point(column, row), confidence));
				}
			}
			candidates.Sort(
				(left, right) =>
				{
					int confidenceOrder = right.confidence.CompareTo(left.confidence);
					if (confidenceOrder != 0)
						return confidenceOrder;
					int rowOrder = left.location.Y.CompareTo(right.location.Y);
					return rowOrder != 0
						? rowOrder
						: left.location.X.CompareTo(right.location.X);
				}
			);

			using var suppressed = new Mat(
				rows,
				columns,
				MatType.CV_8UC1,
				Scalar.All(0)
			);
			Mat.UnsafeIndexer<byte> suppressionIndexer =
				suppressed.GetUnsafeGenericIndexer<byte>();
			foreach ((Point location, float confidence) in candidates)
			{
				if (suppressionIndexer[location.Y, location.X] != 0)
					continue;

				matches.Add((new Vector2(location), confidence));
				int left = Math.Max(0, location.X - markerSize.Width + 1);
				int top = Math.Max(0, location.Y - markerSize.Height + 1);
				int right = Math.Min(response.Width, location.X + markerSize.Width);
				int bottom = Math.Min(response.Height, location.Y + markerSize.Height);
				using Mat suppressionRegion = suppressed[
					new Rect(left, top, right - left, bottom - top)
				];
				suppressionRegion.SetTo(Scalar.All(1));
			}

			return matches;
		}

		private static bool IsPlateauRepresentative(
			Mat.UnsafeIndexer<float> response,
			int rows,
			int columns,
			int row,
			int column,
			float confidence
		)
		{
			for (int neighborRow = Math.Max(0, row - 1); neighborRow <= Math.Min(rows - 1, row + 1); neighborRow++)
			{
				for (
					int neighborColumn = Math.Max(0, column - 1);
					neighborColumn <= Math.Min(columns - 1, column + 1);
					neighborColumn++
				)
				{
					if (neighborRow == row && neighborColumn == column)
						continue;

					float neighborConfidence = response[neighborRow, neighborColumn];
					if (
						neighborConfidence == confidence
						&& (
							neighborRow < row
							|| (neighborRow == row && neighborColumn < column)
						)
					)
						return false;
				}
			}

			return true;
		}
	}
}
