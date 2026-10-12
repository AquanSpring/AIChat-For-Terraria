using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;
using AiChat.Common.Config;

namespace AiChat.Common.Providers
{
	public abstract class HttpProviderBase : IChatProvider
	{
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		protected HttpProviderBase(AiProviderProfile profile)
		{
			Profile = profile ?? throw new ArgumentNullException(nameof(profile));
		}

		public AiProviderProfile Profile { get; }

		protected static JsonSerializerOptions SerializerOptions => JsonOptions;

		protected HttpClient CreateClient()
		{
			var client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(Math.Clamp(Profile.TimeoutSeconds, 1, 600))
			};
			if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Profile.ApiKey);
			if (!string.IsNullOrWhiteSpace(Profile.Organization))
				client.DefaultRequestHeaders.TryAddWithoutValidation("OpenAI-Organization", Profile.Organization);
			foreach (var header in Profile.Headers)
				client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
			return client;
		}

		protected Uri Endpoint(string path)
		{
			var baseUrl = (Profile.BaseUrl ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(baseUrl))
				throw new InvalidOperationException("Provider Base URL is empty.");
			return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
		}

		protected static StringContent JsonContent(object value) => new(
			JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

		protected static async Task<ChatCompletionResult> ErrorFromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var message = body;
			try
			{
				using var json = JsonDocument.Parse(body);
				if (json.RootElement.TryGetProperty("error", out var error))
				{
					if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var errorMessage))
						message = errorMessage.GetString() ?? body;
					else if (error.ValueKind == JsonValueKind.String)
						message = error.GetString() ?? body;
				}
				else if (json.RootElement.TryGetProperty("message", out var topMessage))
					message = topMessage.GetString() ?? body;
			}
			catch (JsonException)
			{
				// Keep the raw response for non-JSON proxy errors.
			}
			return ChatCompletionResult.Failed($"HTTP {(int)response.StatusCode}: {message}", response.StatusCode.ToString());
		}

		public abstract Task<ChatCompletionResult> CompleteAsync(Common.Chat.ChatRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default);
		public abstract Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default);
	}
}
