using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Containers.ContainerRegistry;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CleanupFunctions
{
	public static class ContainerRegistryFunctions
	{
		private static AzureConfig _azureConfig;

		[FunctionName("CleanupRegistry")]
		public static async Task<IActionResult> Run(
			[HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
			ILogger log)
		{
			log.LogInformation("C# HTTP trigger function processed a request.");

			_azureConfig = new AzureConfig()
			{
				RegistryEndpoint = Environment.GetEnvironmentVariable("RegistryUrl"),
				TenantId = Environment.GetEnvironmentVariable("TenantId"),
				ClientId = Environment.GetEnvironmentVariable("ClientId"),
				ClientSecret = Environment.GetEnvironmentVariable("ClientSecret"),
				RetentionDays = Convert.ToInt32(Environment.GetEnvironmentVariable("RetentionDays"))
			};

			var result = await CleanupRegistryAsync();

			string responseMessage =
				$"Cleanup Complete - Deleted: {result.DeletedImages.Count} Safe: {result.IgnoredImages.Count}";

			return new OkObjectResult(responseMessage);
		}

		private static async Task<CleanupResult> CleanupRegistryAsync()
		{
			var result = new CleanupResult();

			// Create a new ContainerRegistryClient
			Uri endpoint = new Uri(_azureConfig.RegistryEndpoint);
			TokenCredential azureCredential = new ClientSecretCredential(_azureConfig.TenantId, _azureConfig.ClientId, _azureConfig.ClientSecret);
			ContainerRegistryClient client = new ContainerRegistryClient(endpoint, azureCredential);

			var targetDate = DateTime.UtcNow.AddDays(-_azureConfig.RetentionDays);

			Console.WriteLine($"Searching for artifacts older than {targetDate}");

			// Loop repos
			var repositoryNames = client.GetRepositoryNamesAsync();
			await foreach (string repositoryName in repositoryNames)
			{
				ContainerRepository repository = client.GetRepository(repositoryName);

				// Get images, new => old
				var imageManifests = repository.GetAllManifestPropertiesAsync(manifestOrder: ArtifactManifestOrder.LastUpdatedOnDescending);

				// Loop Images
				await foreach (ArtifactManifestProperties imageManifest in imageManifests)
				{
					RegistryArtifact image = repository.GetArtifact(imageManifest.Digest);

					if (imageManifest.LastUpdatedOn < targetDate)
					{
						result.DeletedImages.Add(imageManifest.Digest);

						foreach (var tagName in imageManifest.Tags)
						{
							Console.WriteLine($"Deleting tag {imageManifest.RepositoryName}:{tagName}");
							await image.DeleteTagAsync(tagName);
						}

						await image.DeleteAsync();
					}
					else
					{   // safe to ignore
						result.IgnoredImages.Add(imageManifest.Digest);
					}
				}
			}

			return result;
		}
	}

	public class AzureConfig
	{
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string TenantId { get; set; }
		public int RetentionDays { get; set; }

		/// <summary>
		/// e.g. https://xxx.azurecr.io
		/// </summary>
		public string RegistryEndpoint { get; set; }
	}

	public class CleanupResult
	{
		public CleanupResult()
		{
			DeletedImages = new List<string>();
			IgnoredImages = new List<string>();
		}

		public List<string> DeletedImages { get; set; }
		public List<string> IgnoredImages { get; set; }
	}
}