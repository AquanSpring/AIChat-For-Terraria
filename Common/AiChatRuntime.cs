using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;
using AiChat.Common.Config;
using AiChat.Common.Providers;
using AiChat.Common.Storage;
using AiChat.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace AiChat.Common
{
	[Autoload(Side = ModSide.Client)]
	internal sealed class AiChatRuntime : ModSystem
	{
		private const string DefaultProviderName = "Default";
		private ConversationStorage storage;
		private readonly Dictionary<string, ChatConversation> conversations = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> loading = new(StringComparer.OrdinalIgnoreCase);
		private readonly object stateGate = new object();
		private CancellationTokenSource activeRequest;
		private string lastActiveConversationId = string.Empty;
		private string lastOptionsKey = string.Empty;
		private int syncTimer;

		private AiChatModConfig Config => ModContent.GetInstance<AiChatModConfig>();

		public override void Load()
		{
			if (Main.dedServ)
				return;

			storage = new ConversationStorage();
			AiChatPanelState.SendRequested += HandleSendRequested;
			AiChatPanelState.ConversationCreated += HandleConversationCreated;
			AiChatPanelState.ConversationDeleted += HandleConversationDeleted;
			_ = LoadSummariesAsync();
		}

		public override void Unload()
		{
			AiChatPanelState.SendRequested -= HandleSendRequested;
			AiChatPanelState.ConversationCreated -= HandleConversationCreated;
			AiChatPanelState.ConversationDeleted -= HandleConversationDeleted;
			activeRequest?.Cancel();
			activeRequest?.Dispose();
			activeRequest = null;
			storage = null;
			lock (stateGate)
			{
				conversations.Clear();
				loading.Clear();
			}
		}

		public override void UpdateUI(GameTime gameTime)
		{
			if (Main.dedServ || AiChatUISystem.Instance == null)
				return;

			if (++syncTimer < 10)
				return;
			syncTimer = 0;
			SyncUi();
		}

		private async Task LoadSummariesAsync()
		{
			if (storage == null)
				return;

			IReadOnlyList<ConversationSummary> summaries = await storage.LoadSummariesAsync();
			foreach (ConversationSummary summary in summaries)
			{
				ChatConversation conversation = await storage.LoadAsync(summary.Id);
				if (conversation != null)
					lock (stateGate)
						conversations[conversation.Id] = conversation;
			}
			Main.QueueMainThreadAction(SyncUi);
		}

		private void SyncUi()
		{
			List<ChatConversation> conversationSnapshot;
			lock (stateGate)
				conversationSnapshot = conversations.Values.ToList();
			AiChatUISystem.SetConversationSnapshots(conversationSnapshot
				.OrderByDescending(value => value.UpdatedAt)
				.Select(value => new AiChatConversationSnapshot
				{
					Id = value.Id,
					Title = value.Title,
					Provider = value.ProviderProfileId ?? DefaultProviderName,
					Model = value.ModelId ?? Config.Model
				}));

			AiChatPanelState panel = AiChatUISystem.Instance?.Panel;
			if (panel == null)
				return;

			AiProviderProfile profile = BuildProfile();
			string optionsKey = profile.Name + "|" + string.Join(",", profile.Models.Select(model => model.Id));
			if (!string.Equals(lastOptionsKey, optionsKey, StringComparison.Ordinal))
			{
				lastOptionsKey = optionsKey;
				AiChatUISystem.SetProviderOptions(new[] { profile.Name }, profile.Models.Select(model => model.Id));
			}

			string activeId = panel.ActiveConversationId;
			if (string.IsNullOrWhiteSpace(activeId))
				return;

			if (!string.Equals(lastActiveConversationId, activeId, StringComparison.OrdinalIgnoreCase))
			{
				lastActiveConversationId = activeId;
			ChatConversation selected;
			bool isLoading;
			lock (stateGate)
			{
				conversations.TryGetValue(activeId, out selected);
				isLoading = loading.Contains(activeId);
			}
			if (selected != null)
				AiChatUISystem.SetMessages(activeId, ToSnapshots(SnapshotMessages(selected)));
			else if (!isLoading)
					_ = LoadConversationAsync(activeId);
			}
		}

		private async Task LoadConversationAsync(string id)
		{
			lock (stateGate)
				loading.Add(id);
			try
			{
				ChatConversation conversation = await storage.LoadAsync(id);
				if (conversation != null)
					lock (stateGate)
						conversations[id] = conversation;
				Main.QueueMainThreadAction(SyncUi);
			}
			finally
			{
				lock (stateGate)
					loading.Remove(id);
			}
		}

		private void QueueStatus(string conversationId, CancellationTokenSource requestSource, string statusKey, AiChatStatusKind kind)
		{
			Main.QueueMainThreadAction(() =>
			{
				if (ReferenceEquals(activeRequest, requestSource))
					AiChatUISystem.SetStatus(conversationId, statusKey, kind);
			});
		}

		private async void HandleSendRequested(string conversationId, string providerName, string model, bool webSearch)
		{
			if (storage == null || AiChatUISystem.Instance?.Panel == null)
				return;
			AiChatMessageSnapshot latest = AiChatUISystem.Instance.Panel.Messages.LastOrDefault();
			if (latest == null || !string.Equals(latest.Role, "user", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(latest.Content))
				return;

			CancellationTokenSource requestSource = new CancellationTokenSource();
			activeRequest?.Cancel();
			activeRequest?.Dispose();
			activeRequest = requestSource;
			CancellationToken token = requestSource.Token;
			QueueStatus(conversationId, requestSource, webSearch ? "StatusSearchRequested" : "StatusConnecting", AiChatStatusKind.Working);

			ChatConversation conversation = await GetOrCreateConversationAsync(conversationId, providerName, model);
			conversation.ProviderProfileId = string.IsNullOrWhiteSpace(providerName) ? DefaultProviderName : providerName;
			conversation.ModelId = string.IsNullOrWhiteSpace(model) ? Config.Model : model;

			bool appendUserMessage;
			lock (conversation)
				appendUserMessage = conversation.Messages.Count == 0 || !string.Equals(conversation.Messages.Last().Content, latest.Content, StringComparison.Ordinal);
			if (appendUserMessage)
			{
				lock (conversation)
				{
					conversation.Messages.Add(new ChatMessage
					{
						Role = ChatRole.User,
						Content = latest.Content,
						ProviderProfileId = conversation.ProviderProfileId,
						ModelId = conversation.ModelId
					});
					conversation.UpdateTitleFromFirstUserMessage();
				}
				await storage.SaveAsync(conversation);
			}

			AiProviderProfile profile = BuildProfile();
			profile.Models.Clear();
			profile.Models.Add(new AiModelInfo { Id = conversation.ModelId ?? Config.Model, DisplayName = conversation.ModelId ?? Config.Model });
			IChatProvider provider = ChatProviderFactory.Create(profile);
			ChatMessage assistant = new ChatMessage
			{
				Role = ChatRole.Assistant,
				ProviderProfileId = profile.Id,
				ModelId = conversation.ModelId ?? Config.Model
			};
			lock (conversation)
				conversation.Messages.Add(assistant);
			AiChatUISystem.SetMessages(conversation.Id, ToSnapshots(SnapshotMessages(conversation)));
			List<ChatMessage> conversationMessages = SnapshotMessages(conversation);
			foreach (ChatMessage message in conversationMessages)
				if (message.Role == ChatRole.Assistant)
					message.Content = ProviderMetadataFilter.RemoveSafetyBlocks(message.Content);
			List<ChatMessage> requestMessages = new List<ChatMessage>();
			if (webSearch)
			{
				requestMessages.Add(new ChatMessage
				{
					Role = ChatRole.System,
					Content = "只有在本次请求中实际调用联网工具并获得结果，才可以声称已经联网搜索或读取网页。联网请求不代表搜索成功；如果没有可用的联网工具或没有获得搜索结果，请明确说明本次未联网核实，按已有知识回答，不要编造搜索过程或来源。历史对话中的联网声明不代表本次已联网。"
				});
			}
			requestMessages.AddRange(conversationMessages.Take(Math.Max(0, conversationMessages.Count - 1)));

			int searchConfirmed = 0;
			int searchFailed = 0;
			ChatRequest request = new ChatRequest
			{
				Model = conversation.ModelId ?? Config.Model,
				Messages = requestMessages,
				Temperature = Config.Temperature,
				MaxTokens = Config.MaxTokens,
				Stream = Config.StreamResponses,
				WebSearch = webSearch,
				OnWebSearchStatus = webSearch ? status =>
				{
					if (token.IsCancellationRequested)
						return;
					if (status == WebSearchStatus.Completed)
						Interlocked.Exchange(ref searchConfirmed, 1);
					else if (status == WebSearchStatus.Failed)
						Interlocked.Exchange(ref searchFailed, 1);
					QueueStatus(conversation.Id, requestSource,
						status == WebSearchStatus.Searching ? "StatusSearching" : status == WebSearchStatus.Completed ? "StatusSearchCompleted" : "StatusSearchFailed",
						status == WebSearchStatus.Failed ? AiChatStatusKind.Warning : AiChatStatusKind.Working);
				} : null
			};
			ChatCompletionResult result;
			StringBuilder streamedContent = new StringBuilder();
			object streamLock = new object();
			bool streamUpdateQueued = false;
			int generationStatusQueued = 0;
			void QueueStreamUpdate()
			{
				lock (streamLock)
				{
					if (streamUpdateQueued)
						return;
					streamUpdateQueued = true;
				}

				Main.QueueMainThreadAction(() =>
				{
					string content;
					lock (streamLock)
						content = streamedContent.ToString();

					lock (conversation)
						assistant.Content = ProviderMetadataFilter.RemoveSafetyBlocks(content);
					AiChatUISystem.SetMessages(conversation.Id, ToSnapshots(SnapshotMessages(conversation)));

					bool queueAgain;
					lock (streamLock)
					{
						queueAgain = streamedContent.Length != content.Length;
						if (!queueAgain)
							streamUpdateQueued = false;
					}
					if (queueAgain)
					{
						lock (streamLock)
							streamUpdateQueued = false;
						QueueStreamUpdate();
					}
				});
			}

			Action<string> onDelta = Config.StreamResponses
				? delta =>
				{
					if (string.IsNullOrEmpty(delta) || token.IsCancellationRequested)
						return;
					if (Interlocked.Exchange(ref generationStatusQueued, 1) == 0)
						QueueStatus(conversation.Id, requestSource, webSearch && Volatile.Read(ref searchConfirmed) == 0 ? "StatusGeneratingUnverified" : "StatusGenerating", AiChatStatusKind.Working);
					lock (streamLock)
						streamedContent.Append(delta);
					QueueStreamUpdate();
				}
				: null;
			try
			{
				result = await provider.CompleteAsync(request, onDelta, token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception exception)
			{
				result = ChatCompletionResult.Failed(exception.Message);
			}

			string finalContent;
			if (!result.Success)
				finalContent = "[Error] " + (result.Error ?? "The provider returned no response.");
			else
			{
				lock (streamLock)
					finalContent = streamedContent.Length > 0 ? streamedContent.ToString() : result.Content;
			}
			finalContent = ProviderMetadataFilter.RemoveSafetyBlocks(finalContent);
			lock (conversation)
				assistant.Content = finalContent;
			if (!result.Success)
				QueueStatus(conversation.Id, requestSource, webSearch ? "StatusSearchRequestFailed" : "StatusFailed", AiChatStatusKind.Failure);
			else if (webSearch && Volatile.Read(ref searchFailed) != 0)
				QueueStatus(conversation.Id, requestSource, "StatusCompletedSearchFailed", AiChatStatusKind.Warning);
			else if (webSearch && Volatile.Read(ref searchConfirmed) == 0)
				QueueStatus(conversation.Id, requestSource, "StatusCompletedUnverified", AiChatStatusKind.Warning);
			else
				QueueStatus(conversation.Id, requestSource, webSearch ? "StatusCompletedWithSearch" : "StatusCompleted", AiChatStatusKind.Success);

			await storage.SaveAsync(conversation);
			Main.QueueMainThreadAction(() =>
			{
				AiChatUISystem.SetMessages(conversation.Id, ToSnapshots(SnapshotMessages(conversation)));
				SyncUi();
			});
		}

		private async Task<ChatConversation> GetOrCreateConversationAsync(string id, string providerName, string model)
		{
			lock (stateGate)
			{
				if (conversations.TryGetValue(id, out ChatConversation existing))
					return existing;
			}

			ChatConversation conversation = new ChatConversation
			{
				Id = id,
				ProviderProfileId = string.IsNullOrWhiteSpace(providerName) ? DefaultProviderName : providerName,
				ModelId = string.IsNullOrWhiteSpace(model) ? Config.Model : model
			};
			lock (stateGate)
				conversations[conversation.Id] = conversation;
			await storage.SaveAsync(conversation);
			return conversation;
		}

		private async void HandleConversationCreated(string id, string providerName, string model)
		{
			bool exists;
			lock (stateGate)
				exists = conversations.ContainsKey(id);
			if (storage == null || exists)
				return;
			ChatConversation conversation = new ChatConversation
			{
				Id = id,
				ProviderProfileId = string.IsNullOrWhiteSpace(providerName) ? DefaultProviderName : providerName,
				ModelId = string.IsNullOrWhiteSpace(model) ? Config.Model : model
			};
			lock (stateGate)
				conversations[id] = conversation;
			await storage.SaveAsync(conversation);
		}

		private void HandleConversationDeleted(string id)
		{
			if (string.Equals(lastActiveConversationId, id, StringComparison.OrdinalIgnoreCase))
				activeRequest?.Cancel();
			lock (stateGate)
				conversations.Remove(id);
			_ = storage?.DeleteAsync(id);
		}

		private AiProviderProfile BuildProfile()
		{
			AiProviderProfile profile = new AiProviderProfile
			{
				Name = DefaultProviderName,
				Protocol = Config.Protocol,
				BaseUrl = Config.BaseUrl,
				ApiKey = Config.ApiKey,
				TimeoutSeconds = Config.TimeoutSeconds
			};
			foreach (string model in Config.Model.Split(','))
			{
				string trimmed = model.Trim();
				if (trimmed.Length > 0 && profile.FindModel(trimmed) == null)
					profile.Models.Add(new AiModelInfo { Id = trimmed, DisplayName = trimmed, IsManual = true });
			}
			return profile;
		}

		private static List<ChatMessage> SnapshotMessages(ChatConversation conversation)
		{
			lock (conversation)
				return conversation.Messages.Select(message => new ChatMessage
				{
					Role = message.Role,
					Content = message.Content,
					CreatedAt = message.CreatedAt,
					ProviderProfileId = message.ProviderProfileId,
					ModelId = message.ModelId
				}).ToList();
		}

		private static IEnumerable<AiChatMessageSnapshot> ToSnapshots(IEnumerable<ChatMessage> source) => source.Select(message => new AiChatMessageSnapshot
		{
			Role = message.Role == ChatRole.User ? "user" : message.Role == ChatRole.Assistant ? "assistant" : "system",
			Content = message.Content
		}).ToArray();
	}
}
