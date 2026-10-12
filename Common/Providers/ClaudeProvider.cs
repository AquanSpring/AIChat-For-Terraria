using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;
using AiChat.Common.Config;

namespace AiChat.Common.Providers
{
	public sealed class ClaudeProvider : HttpProviderBase
	{
		public ClaudeProvider(AiProviderProfile profile) : base(profile) { }

		public override async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(request.Model))
				return ChatCompletionResult.Failed("A model name is required.");

			var contents = new List<object>();
			string? system = null;
			foreach (var message in request.Messages)
			{
				if (message.Role == ChatRole.System)
				{
					system = string.IsNullOrWhiteSpace(system) ? message.Content : system + "\n\n" + message.Content;
					continue;
				}
				contents.Add(new { role = message.Role == ChatRole.Assistant ? "assistant" : "user", content = message.Content });
			}

			var payload = new Dictionary<string, object?>
			{
				["model"] = request.Model,
				["messages"] = contents,
				["max_tokens"] = request.MaxTokens ?? 2048,
				["stream"] = onDelta != null || request.Stream
			};
			if (system != null) payload["system"] = system;
			if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
			if (request.WebSearch)
			{
				payload["tools"] = new[]
				{
					new { type = "web_search_20250305", name = "web_search", max_uses = 5 }
				};
			}

			using var client = CreateClient();
			client.DefaultRequestHeaders.Authorization = null;
			if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
				client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", Profile.ApiKey);
			client.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
			using var requestMessage = new HttpRequestMessage(HttpMethod.Post, Endpoint("v1/messages"))
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
				var content = string.Empty;
				if (document.RootElement.TryGetProperty("content", out var contentBlocks))
				{
					foreach (var block in contentBlocks.EnumerateArray())
					{
						ReportWebSearch(block, request.OnWebSearchStatus);
						if (block.TryGetProperty("text", out var text))
							content += text.GetString() ?? string.Empty;
					}
				}
				return ChatCompletionResult.Ok(content);
			}
			catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
			{
				return ChatCompletionResult.Failed("The provider returned an invalid Claude response: " + exception.Message);
			}
		}

		public override Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
		{
			// Anthropic does not expose a stable public model-list endpoint. Use manually configured models.
			IReadOnlyList<AiModelInfo> models = Profile.Models;
			return Task.FromResult(models);
		}

		private static void ReportWebSearch(JsonElement block, Action<WebSearchStatus>? onWebSearchStatus)
		{
			if (onWebSearchStatus == null || !block.TryGetProperty("type", out var type))
				return;
			if (type.GetString() == "server_tool_use" && block.TryGetProperty("name", out var name) && name.GetString() == "web_search")
				onWebSearchStatus(WebSearchStatus.Searching);
			else if (type.GetString() == "web_search_tool_result" && block.TryGetProperty("content", out var content))
			{
				if (content.ValueKind == JsonValueKind.Array)
					onWebSearchStatus(WebSearchStatus.Completed);
				else if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("type", out var errorType)
					&& errorType.GetString() == "web_search_tool_result_error")
					onWebSearchStatus(WebSearchStatus.Failed);
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
				try
				{
					using var json = JsonDocument.Parse(line[5..].Trim());
					if (!json.RootElement.TryGetProperty("type", out var type)) continue;
					if (type.GetString() == "error")
						return ChatCompletionResult.Failed(json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) ? message.GetString() ?? "API stream failed." : line[5..].Trim());
					if (type.GetString() == "content_block_start" && json.RootElement.TryGetProperty("content_block", out var block))
						ReportWebSearch(block, onWebSearchStatus);
					if (type.GetString() != "content_block_delta") continue;
					if (!json.RootElement.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("text", out var text)) continue;
					var content = text.GetString() ?? string.Empty;
					complete += content;
					onDelta?.Invoke(content);
				}
				catch (JsonException)
				{
					// Ignore non-content SSE events.
				}
			}
			return ChatCompletionResult.Ok(complete);
		}
	}
}
