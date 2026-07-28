using System.Drawing;
using System;
using System.Collections.Generic;
using RatEye.Processing;
using Inventory = RatEye.Processing.Inventory;

namespace RatEye
{
	/// <summary>
	/// Core class which allows creating new processing objects
	/// </summary>
	/// <remarks>
	/// The engine owns processing resources stored on its <see cref="Config"/>.
	/// Do not share that config with another engine, use returned processing
	/// objects after disposal, or call <see cref="Dispose"/> concurrently with
	/// processing.
	/// </remarks>
	public class RatEyeEngine : System.IDisposable
	{
		private readonly object _lifecycleSync = new object();
		private bool _disposed;

		/// <summary>
		/// The config which is used for this <see cref="RatEyeEngine"/>
		/// </summary>
		/// <remarks>Do not modify this config object</remarks>
		public Config Config { get; }

		/// <summary>
		/// Create a <see cref="RatEyeEngine"/> instance which is the basis of all processing
		/// </summary>
		/// <param name="config">The config to use for this instance</param>
		/// <param name="itemDatabase">The <see cref="RatStash.Database"/> which contains all matchable items. For example, if no quest items should be matched, pass a previously filtered <see cref="RatStash.Database"/>.</param>
		/// <remarks>Do not modify the config after passing it</remarks>
		public RatEyeEngine(Config config, RatStash.Database itemDatabase)
		{
			Config = config;

			config.RatStashDB = itemDatabase;

			System.IO.Directory.CreateDirectory(config.PathConfig.CacheDir);

			Config.IconManager = new IconManager(config);
		}

		/// <summary>
		/// Create new <see cref="MultiInspection"/> instance
		/// </summary>
		/// <param name="image">The image to process</param>
		public MultiInspection NewMultiInspection(Bitmap image)
		{
			lock (_lifecycleSync)
			{
				ThrowIfDisposed();
				return new MultiInspection(image, Config);
			}
		}

		/// <summary>
		/// Create new <see cref="Inspection"/> instance
		/// </summary>
		/// <param name="image">The image to process</param>
		public Inspection NewInspection(Bitmap image)
		{
			lock (_lifecycleSync)
			{
				ThrowIfDisposed();
				return new Inspection(image, Config);
			}
		}

		/// <summary>
		/// Create new <see cref="Inventory"/> instance
		/// </summary>
		/// <param name="image">The image to process</param>
		public Inventory NewInventory(Bitmap image)
		{
			lock (_lifecycleSync)
			{
				ThrowIfDisposed();
				return new Inventory(image, Config);
			}
		}

		/// <summary>
		/// Create a new icon-processing instance from an already cropped icon image.
		/// </summary>
		/// <param name="image">The cropped icon image to process.</param>
		/// <param name="position">Position of the crop in its source image.</param>
		/// <param name="size">Size of the detected item region.</param>
		public Processing.Icon NewIcon(Bitmap image, Vector2 position, Vector2 size)
		{
			lock (_lifecycleSync)
			{
				ThrowIfDisposed();
				return new Processing.Icon(image, position, size, Config, ownsIcon: false);
			}
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new System.ObjectDisposedException(nameof(RatEyeEngine));
		}

		/// <summary>
		/// Releases processing resources owned by this engine instance.
		/// </summary>
		/// <remarks>
		/// Disposal mutates the caller-supplied <see cref="Config"/> by releasing
		/// its icon manager, Tesseract engines, and inspection marker. Complete
		/// all work on returned processing objects before disposing the engine.
		/// </remarks>
		public void Dispose() => DisposeCore(throwOnFailure: false);

		/// <summary>
		/// Releases processing resources and throws if any cleanup operation fails.
		/// </summary>
		/// <remarks>
		/// Prefer normal <see cref="Dispose"/> for using blocks so a cleanup failure cannot mask
		/// an active processing exception. Call this method when explicit teardown must fail fast.
		/// </remarks>
		public void DisposeStrict() => DisposeCore(throwOnFailure: true);

		/// <summary>
		/// Gets the aggregate failure from the cleanup attempt, if any.
		/// </summary>
		public AggregateException CleanupFailure { get; private set; }

		private void DisposeCore(bool throwOnFailure)
		{
			lock (_lifecycleSync)
			{
				if (_disposed)
				{
					if (throwOnFailure && CleanupFailure != null)
						throw CleanupFailure;
					return;
				}
				_disposed = true;

				List<Exception> cleanupErrors = new List<Exception>();
				TryCleanup(
					() =>
					{
						try
						{
							Config.IconManager?.Dispose();
						}
						finally
						{
							Config.IconManager = null;
						}
					},
					cleanupErrors
				);
				TryCleanup(
					() =>
					{
						lock (Config.ProcessingConfig.InspectionConfig.TesseractSync)
						{
							Config.ProcessingConfig.InspectionConfig.TesseractReleased = true;
							try
							{
								Config.ProcessingConfig.InspectionConfig.TesseractEngine?.Dispose();
							}
							finally
							{
								Config.ProcessingConfig.InspectionConfig.TesseractEngine = null;
							}
						}
					},
					cleanupErrors
				);
				TryCleanup(
					() =>
					{
						lock (Config.ProcessingConfig.IconConfig.TesseractSync)
						{
							Config.ProcessingConfig.IconConfig.TesseractReleased = true;
							try
							{
								Config.ProcessingConfig.IconConfig.TesseractEngine?.Dispose();
							}
							finally
							{
								Config.ProcessingConfig.IconConfig.TesseractEngine = null;
							}
						}
					},
					cleanupErrors
				);
				TryCleanup(
					Config.ProcessingConfig.InspectionConfig.DisposeMarker,
					cleanupErrors
				);
				GC.SuppressFinalize(this);

				if (cleanupErrors.Count > 0)
				{
					CleanupFailure = new AggregateException(
						"One or more RatEye resources could not be released.",
						cleanupErrors
					);
					try
					{
						Logger.LogDebug(
							"One or more RatEye resources could not be released.",
							CleanupFailure
						);
					}
					catch
					{
						// Dispose is best effort and must not mask an active processing exception.
					}

					if (throwOnFailure)
						throw CleanupFailure;
				}
			}
		}

		private static void TryCleanup(Action cleanup, ICollection<Exception> errors)
		{
			try
			{
				cleanup();
			}
			catch (Exception exception)
			{
				errors.Add(exception);
			}
		}
	}
}
