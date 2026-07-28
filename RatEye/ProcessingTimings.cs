using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace RatEye
{
	/// <summary>
	/// Collects elapsed time for named scan-processing stages.
	/// </summary>
	public sealed class ProcessingTimings
	{
		private readonly object _sync = new object();
		private readonly Dictionary<string, double> _stageMilliseconds =
			new Dictionary<string, double>();

		/// <summary>
		/// Returns a stable copy of the currently recorded stage timings.
		/// </summary>
		public IReadOnlyDictionary<string, double> Snapshot()
		{
			lock (_sync)
			{
				return new ReadOnlyDictionary<string, double>(
					new Dictionary<string, double>(_stageMilliseconds)
				);
			}
		}

		internal static long Start() => Stopwatch.GetTimestamp();

		internal void RecordSince(string stage, long startTimestamp)
		{
			double milliseconds =
				(Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;

			lock (_sync)
			{
				if (_stageMilliseconds.TryGetValue(stage, out double existing))
					_stageMilliseconds[stage] = existing + milliseconds;
				else
					_stageMilliseconds.Add(stage, milliseconds);
			}

			if (Config.LogDebug)
				Logger.LogDebug($"Timing {stage}: {milliseconds:F3} ms");
		}
	}
}
