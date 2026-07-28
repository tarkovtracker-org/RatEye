using System.Drawing;
using RatEye.Processing;
using Inventory = RatEye.Processing.Inventory;

namespace RatEye
{
	/// <summary>
	/// Core class which allows creating new processing objects
	/// </summary>
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
		public void Dispose()
		{
			lock (_lifecycleSync)
			{
				if (_disposed)
					return;
				_disposed = true;

				Config.IconManager?.Dispose();
				Config.IconManager = null;
				lock (Config.ProcessingConfig.InspectionConfig.TesseractSync)
				{
					Config.ProcessingConfig.InspectionConfig.TesseractEngine?.Dispose();
					Config.ProcessingConfig.InspectionConfig.TesseractEngine = null;
				}
				lock (Config.ProcessingConfig.IconConfig.TesseractSync)
				{
					Config.ProcessingConfig.IconConfig.TesseractEngine?.Dispose();
					Config.ProcessingConfig.IconConfig.TesseractEngine = null;
				}
				Config.ProcessingConfig.InspectionConfig.DisposeMarker();
				System.GC.SuppressFinalize(this);
			}
		}
	}
}
