using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Common.Chat;

namespace AiChat.Common.Storage
{
	/// <summary>Stores each conversation independently so deleting one chat never rewrites the others.</summary>
	public sealed class ConversationStorage
	{
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};
		private readonly SemaphoreSlim gate = new(1, 1);

		public ConversationStorage(string? directory = null)
		{
			DirectoryPath = directory ?? GetDefaultDirectory();
			Directory.CreateDirectory(DirectoryPath);
		}

		public string DirectoryPath { get; }

		public async Task<ChatConversation?> LoadAsync(string id, CancellationToken cancellationToken = default)
		{
			if (!TryGetPath(id, out var path) || !File.Exists(path)) return null;
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await using var file = File.OpenRead(path);
				return await JsonSerializer.DeserializeAsync<ChatConversation>(file, JsonOptions, cancellationToken).ConfigureAwait(false);
			}
			catch (JsonException)
		{
				return null;
			}
			finally { gate.Release(); }
		}

		public async Task<IReadOnlyList<ConversationSummary>> LoadSummariesAsync(CancellationToken cancellationToken = default)
		{
			var summaries = new List<ConversationSummary>();
			foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					await using var file = File.OpenRead(path);
					var conversation = await JsonSerializer.DeserializeAsync<ChatConversation>(file, JsonOptions, cancellationToken).ConfigureAwait(false);
					if (conversation != null) summaries.Add(ConversationSummary.FromConversation(conversation));
				}
				catch (IOException) { }
				catch (JsonException) { }
			}
			return summaries.OrderByDescending(summary => summary.UpdatedAt).ToArray();
		}

		public async Task SaveAsync(ChatConversation conversation, CancellationToken cancellationToken = default)
		{
			if (conversation == null) throw new ArgumentNullException(nameof(conversation));
			ChatConversation snapshot;
			lock (conversation)
			{
				if (!Guid.TryParse(conversation.Id, out var conversationGuid))
					conversation.Id = Guid.NewGuid().ToString("N");
				else
					conversation.Id = conversationGuid.ToString("N");
				conversation.UpdateTitleFromFirstUserMessage();
				conversation.UpdatedAt = DateTimeOffset.UtcNow;
				snapshot = CloneConversation(conversation);
			}
			var path = GetPath(snapshot.Id);
			var temporaryPath = path + ".tmp";

			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				Directory.CreateDirectory(DirectoryPath);
				await using (var file = File.Create(temporaryPath))
					await JsonSerializer.SerializeAsync(file, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
				File.Move(temporaryPath, path, true);
			}
			finally
			{
				if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
				gate.Release();
			}
		}

		public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
		{
			if (!TryGetPath(id, out var path)) return false;
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (!File.Exists(path)) return false;
				File.Delete(path);
				return true;
			}
			finally { gate.Release(); }
		}

		public async Task ClearAsync(CancellationToken cancellationToken = default)
		{
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
				{
					cancellationToken.ThrowIfCancellationRequested();
					File.Delete(path);
				}
			}
			finally { gate.Release(); }
		}

		private bool TryGetPath(string id, out string path)
		{
			path = string.Empty;
			if (!Guid.TryParse(id, out var guid)) return false;
			path = GetPath(guid.ToString("N"));
			return true;
		}

		private string GetPath(string id) => Path.Combine(DirectoryPath, id + ".json");

		private static ChatConversation CloneConversation(ChatConversation source)
		{
			var clone = new ChatConversation
			{
				Id = source.Id,
				Title = source.Title,
				CreatedAt = source.CreatedAt,
				UpdatedAt = source.UpdatedAt,
				ProviderProfileId = source.ProviderProfileId,
				ModelId = source.ModelId
			};
			foreach (var message in source.Messages)
			{
				clone.Messages.Add(new ChatMessage
				{
					Role = message.Role,
					Content = message.Content,
					CreatedAt = message.CreatedAt,
					ProviderProfileId = message.ProviderProfileId,
					ModelId = message.ModelId
				});
			}
			return clone;
		}

		private static string GetDefaultDirectory()
		{
			var savePath = Terraria.Main.SavePath;
			return Path.Combine(savePath, "ModConfigs", "AiChat", "conversations");
		}
	}
}
