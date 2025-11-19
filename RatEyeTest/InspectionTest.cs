using System.Drawing;
using RatEye;
using RatEye.Processing;
using RatStash;
using Xunit;

namespace RatEyeTest
{
	public class InspectionTest : TestEnvironment
	{
		[Fact]
		public void ItemFHD()
		{
			var image = new Bitmap("TestData/FHD/Item.png");
			var title = "GSSh-01 active headset";
			ConductTest(1f, image, "5b432b965acfc47a8774094e");
		}

		[Fact]
		public void ItemFHD2()
		{
			var image = new Bitmap("TestData/FHD/Item2.png");
			ConductTest(1f, image, "5ae0973a5acfc4001562206c");
		}

		[Fact]
		public void ItemFHDRussian()
		{
			var image = new Bitmap("TestData/FHD/Item_Russian.png");
			ConductTest(1f, image, "61f7b234ea4ab34f2f59c3ec", Language.Russian, 0.7f);
		}

		[Fact]
		public void ItemFHDRussianMixed()
		{
			var image = new Bitmap("TestData/FHD/Item_Russian_Mixed.png");
			ConductTest(1f, image, "5e8488fa988a8701445df1e4", Language.Russian, 0.7f);
		}

		[Fact]
		public void ItemFHDChineseMixed()
		{
			var image = new Bitmap("TestData/FHD/Item_Chinese_Mixed.png");
			ConductTest(1f, image, "5c0e5bab86f77461f55ed1f3", Language.Chinese, 0.5f);
		}

		[Fact]
		public void ItemUHD()
		{
			var image = new Bitmap("TestData/UHD/Item.png");
			ConductTest(2f, image, "544a5cde4bdc2d39388b456b");
		}

		private static void ConductTest(
			float scale,
			Bitmap image,
			string id,
			Language language = Language.English,
			float confidenceMul = 1f)
		{
			var bestRatEye = GetRatEyeEngine(scale, language, "best");
			ConductTestSub(bestRatEye, image, id);

			var fastRatEye = GetRatEyeEngine(scale, language, "fast");
			ConductTestSub(fastRatEye, image, id);
		}

		private static void ConductTestSub(
			RatEyeEngine ratEye,
			Bitmap image,
			string id)
		{
			var inspection = ratEye.NewInspection(image);
			Assert.True(inspection.ContainsMarker);
			var threshold = ratEye.Config.ProcessingConfig.InspectionConfig.MarkerThreshold;
			Assert.InRange(inspection.MarkerConfidence, threshold, 1.0f);
			Assert.Equal(id, inspection.Item.Id);
		}

		private static RatEyeEngine GetRatEyeEngine(float scale, Language language, string modelType)
		{
			var config = new Config()
			{
				PathConfig = new Config.Path()
				{
					TrainedData = $"Data/traineddata/{modelType}",
				},
				ProcessingConfig = new Config.Processing()
				{
					Scale = scale,
					Language = language,
				}
			};
			return new RatEyeEngine(config, GetItemDatabase(language));
		}
	}
}
