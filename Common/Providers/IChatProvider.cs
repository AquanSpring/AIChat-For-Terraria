using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;
using AiChat.Common.Config;

namespace AiChat.Common.Providers
{
	public interface IChatProvider
	{
		AiProviderProfile Profile { get; }
		Task<ChatCompletionResult> CompleteAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default);
		Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default);
	}

	public static class ChatProviderFactory
	{
		public static IChatProvider Create(AiProviderProfile profile) => profile.Protocol switch
		{
			AiProviderProtocol.ClaudeMessages => new ClaudeProvider(profile),
			AiProviderProtocol.OpenAIResponses => new OpenAiResponsesProvider(profile),
			_ => new OpenAICompatibleProvider(profile)
		};
	}
}
