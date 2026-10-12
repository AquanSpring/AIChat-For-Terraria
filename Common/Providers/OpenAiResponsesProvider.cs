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
	/// <summary>OpenAI's newer Responses API. It is separate from the Chat Completions-compatible adapter.</summary>
	public sealed class OpenAiResponsesProvider : HttpProviderBase
	{
		public OpenAiResponsesProvider(AiProviderProfile profile) : base(profile) { }

		public override async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(request.Model))
				return ChatCompletionResult.Failed("A model name is required.");

			var input = new List<object>();
			foreach (var message in request.Messages)
				input.Add(new { role = OpenAICompatibleProvider.RoleName(message.Role), content = message.Content });
			var payload = new Dictionary<string, object?>
			{
				["model"] = request.Model,
				["input"] = input,
				["stream"] = onDelta != null || request.Stream
			};
			if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
			if (request.MaxTokens.HasValue) payload["max_output_tokens"] = request.MaxTokens.Value;
			if (request.WebSearch) payload["tools"] = new[] { new { type = "web_search_preview" } };

			using var client = CreateClient();
			using var requestMessage = new HttpRequestMessage(HttpMethod.Post, Endpoint("responses"))
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
				var result = string.Empty;
				if (document.RootElement.TryGetProperty("output", out var output))
					foreach (var item in output.EnumerateArray())
					{
						ReportWebSearch(item, request.OnWebSearchStatus);
						if (item.TryGetProperty("content", out var content))
							foreach (var part in content.EnumerateArray())
								if (part.TryGetProperty("text", out var text)) result += text.GetString();
					}
				if (document.RootElement.TryGetProperty("output_text", out var outputText))
					result = outputText.GetString() ?? string.Empty;
				return ChatCompletionResult.Ok(result);
			}
			catch (Exception exception) when (exception is JsonException or InvalidOperationException)
			{
				return ChatCompletionResult.Failed("The provider returned an invalid Responses API response: " + exception.Message);
			}
		}

		public override async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
		{
			// The Responses API uses the same model catalogue endpoint as OpenAI-compatible APIs.
			return await new OpenAICompatibleProvider(Profile).GetModelsAsync(cancellationToken).ConfigureAwait(false);
		}

		private static void ReportWebSearch(JsonElement item, Action<WebSearchStatus>? onWebSearchStatus)
		{
			if (onWebSearchStatus == null || !item.TryGetProperty("type", out var type) || type.GetString() != "web_search_call"
				|| !item.TryGetProperty("status", out var status))
				return;
			switch (status.GetString())
			{
				case "in_progress":
				case "searching":
					onWebSearchStatus(WebSearchStatus.Searching);
					break;
				case "completed":
					onWebSearchStatus(WebSearchStatus.Completed);
					break;
				case "failed":
					onWebSearchStatus(WebSearchStatus.Failed);
					break;
			}
		}

		private static async Task<ChatCompletionResult> ReadStreamingResponseAsync(System.Net.Http.HttpResponseMessage response, Action<string>? onDelta, Action<WebSearchStatus>? onWebSearchStatus, CancellationToken cancellationToken)
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
					var type = json.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
					if (type == "error")
						return ChatCompletionResult.Failed(json.RootElement.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() ?? "API stream failed." : data);
					if (type == "response.failed")
						return ChatCompletionResult.Failed(json.RootElement.TryGetProperty("response", out var failed) && failed.TryGetProperty("error", out var error)
							&& error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.GetString() ?? "API stream failed." : data);
					if (type == "response.web_search_call.in_progress" || type == "response.web_search_call.searching")
						onWebSearchStatus?.Invoke(WebSearchStatus.Searching);
					else if (type == "response.web_search_call.completed")
						onWebSearchStatus?.Invoke(WebSearchStatus.Completed);
					if (json.RootElement.TryGetProperty("item", out var item))
						ReportWebSearch(item, onWebSearchStatus);
					if (type == "response.completed" && json.RootElement.TryGetProperty("response", out var completed)
						&& completed.TryGetProperty("output", out var output))
						foreach (var outputItem in output.EnumerateArray())
							ReportWebSearch(outputItem, onWebSearchStatus);
					if (type != "response.output_text.delta" || !json.RootElement.TryGetProperty("delta", out var delta)) continue;
					var content = delta.GetString() ?? string.Empty;
					complete += content;
					onDelta?.Invoke(content);
				}
				catch (JsonException)
				{
					// Ignore provider-specific SSE events.
				}
			}
			return ChatCompletionResult.Ok(complete);
		}
	}
}
