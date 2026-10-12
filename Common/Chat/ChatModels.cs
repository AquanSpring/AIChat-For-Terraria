using System;
using System.Collections.Generic;
using AiChat.Common.Config;

namespace AiChat.Common.Chat
{
	public enum ChatRole
	{
		System,
		User,
		Assistant
	}

	public sealed class ChatMessage
	{
		public ChatRole Role { get; set; }
		public string Content { get; set; } = string.Empty;
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		public string? ProviderProfileId { get; set; }
		public string? ModelId { get; set; }
	}

	public sealed class ChatConversation
	{
		public string Id { get; set; } = Guid.NewGuid().ToString("N");
		public string Title { get; set; } = "New chat";
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
		public string? ProviderProfileId { get; set; }
		public string? ModelId { get; set; }
		public List<ChatMessage> Messages { get; set; } = new();

		public void UpdateTitleFromFirstUserMessage()
		{
			if (!string.Equals(Title, "New chat", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Title))
				return;

			var firstUserMessage = Messages.Find(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Content));
			if (firstUserMessage == null)
				return;

			var title = firstUserMessage.Content.Trim().Replace("\r", " ").Replace("\n", " ");
			Title = title.Length <= 48 ? title : title[..48].TrimEnd() + "...";
		}
	}

	public sealed class ConversationSummary
	{
		public string Id { get; set; } = string.Empty;
		public string Title { get; set; } = string.Empty;
		public DateTimeOffset CreatedAt { get; set; }
		public DateTimeOffset UpdatedAt { get; set; }
		public string? ProviderProfileId { get; set; }
		public string? ModelId { get; set; }

		public static ConversationSummary FromConversation(ChatConversation conversation) => new()
		{
			Id = conversation.Id,
			Title = conversation.Title,
			CreatedAt = conversation.CreatedAt,
			UpdatedAt = conversation.UpdatedAt,
			ProviderProfileId = conversation.ProviderProfileId,
			ModelId = conversation.ModelId
		};
	}

	public enum WebSearchStatus
	{
		Searching,
		Completed,
		Failed
	}

	public sealed class ChatRequest
	{
		public string Model { get; set; } = string.Empty;
		public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();
		public double? Temperature { get; set; }
		public int? MaxTokens { get; set; }
		public bool Stream { get; set; }
		public bool WebSearch { get; set; }
		public Action<WebSearchStatus>? OnWebSearchStatus { get; set; }
	}

	public sealed class ChatCompletionResult
	{
		public bool Success { get; init; }
		public string Content { get; init; } = string.Empty;
		public string? Error { get; init; }
		public string? ErrorCode { get; init; }
		public IReadOnlyList<AiModelInfo> Models { get; init; } = Array.Empty<AiModelInfo>();

		public static ChatCompletionResult Ok(string content) => new() { Success = true, Content = content };
		public static ChatCompletionResult Failed(string error, string? errorCode = null) => new()
		{
			Success = false,
			Error = error,
			ErrorCode = errorCode
		};
	}
}
