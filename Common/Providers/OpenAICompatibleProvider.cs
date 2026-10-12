using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;
using AiChat.Common.Config;

namespace AiChat.Common.Providers
{
	public class OpenAICompatibleProvider : HttpProviderBase
	{
		public OpenAICompatibleProvider(AiProviderProfile profile) : base(profile) { }

		public override async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(request.Model))
				return ChatCompletionResult.Failed("A model name is required.");

			var messages = new List<object>();
			foreach (var message in request.Messages)
				messages.Add(new { role = RoleName(message.Role), content = message.Content });

			var payload = new Dictionary<string, object?>
			{
				["model"] = request.Model,
				["messages"] = messages,
				["stream"] = onDelta != null || request.Stream
			};
			if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
			if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;
			if (request.WebSearch) payload["web_search_options"] = new Dictionary<string, object?>();

			using var client = CreateClient();
			using var requestMessage = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
			{
				Content = JsonContent(payload)
			};
			using var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				return await ErrorFromResponseAsync(response, cancellationToken).ConfigureAwait(false);

			if (onDelta != null || request.Stream)
				return await ReadStreamingResponseAsync(response, onDelta, request.OnWebSearchStatus, cancellationToken).ConfigureAwait(false);

			try
			{
				using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
				var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
				ReportWebSearch(message, request.OnWebSearchStatus);
				var content = message.GetProperty("content").GetString() ?? string.Empty;
				return ChatCompletionResult.Ok(content);
			}
			catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
			{
				return ChatCompletionResult.Failed("The provider returned an invalid chat response: " + exception.Message);
			}
		}

		public override async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
		{
			using var client = CreateClient();
			using var response = await client.GetAsync(Endpoint("models"), cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				var failure = await ErrorFromResponseAsync(response, cancellationToken).ConfigureAwait(false);
				throw new HttpRequestException(failure.Error);
			}

			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			var models = new List<AiModelInfo>();
			if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				return models;
			foreach (var item in data.EnumerateArray())
			{
				if (!item.TryGetProperty("id", out var idElement)) continue;
				var id = idElement.GetString();
				if (string.IsNullOrWhiteSpace(id)) continue;
				models.Add(new AiModelInfo
				{
					Id = id,
					DisplayName = id,
					OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null
				});
			}
			return models;
		}

		public static string RoleName(ChatRole role) => role switch
		{
			ChatRole.System => "system",
			ChatRole.Assistant => "assistant",
			_ => "user"
		};

		private static void ReportWebSearch(JsonElement message, Action<WebSearchStatus>? onWebSearchStatus)
		{
			if (onWebSearchStatus == null || !message.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
				return;
			foreach (var annotation in annotations.EnumerateArray())
			{
				if (annotation.TryGetProperty("type", out var type) && type.GetString() == "url_citation"
					&& annotation.TryGetProperty("url_citation", out var citation)
					&& citation.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
				{
					onWebSearchStatus(WebSearchStatus.Completed);
					return;
				}
			}
		}

		private static async Task<ChatCompletionResult> ReadStreamingResponseAsync(HttpResponseMessage response, Action<string>? onDelta, Action<WebSearchStatus>? onWebSearchStatus, CancellationToken cancellationToken)
		{
			var complete = string.Empty;
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using var reader = new StreamReader(stream);
			while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
				var data = line[5..].Trim();
				if (data == "[DONE]") break;
				try
				{
					using var json = JsonDocument.Parse(data);
					if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
						return ChatCompletionResult.Failed(error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() ?? "API stream failed." : error.ToString());
					if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
					var choice = choices[0];
					var delta = choice.TryGetProperty("delta", out var deltaObject) ? deltaObject : choice.GetProperty("message");
					ReportWebSearch(delta, onWebSearchStatus);
					if (!delta.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String) continue;
					var content = contentElement.GetString() ?? string.Empty;
					complete += content;
					onDelta?.Invoke(content);
				}
				catch (JsonException)
				{
					// Ignore keep-alive or provider-specific SSE events.
				}
			}
			return ChatCompletionResult.Ok(complete);
		}
	}

	// GPT Chat Completions uses the same wire format and can be configured with a first-party or proxy URL.
	public sealed class GptProvider : OpenAICompatibleProvider
	{
		public GptProvider(AiProviderProfile profile) : base(profile) { }
	}
}
