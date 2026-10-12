using System;
using System.Collections.Generic;
using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace AiChat.Common.Config
{
	[CustomModConfigItem(typeof(AiChatProtocolConfigElement))]
	public enum AiProviderProtocol
	{
		OpenAICompatible,
		ClaudeMessages,
		OpenAIResponses
	}

	public sealed class AiModelInfo
	{
		public string Id { get; set; } = string.Empty;
		public string DisplayName { get; set; } = string.Empty;
		public string? OwnedBy { get; set; }
		public bool IsManual { get; set; }

		public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
	}

	public sealed class AiProviderProfile
	{
		public string Id { get; set; } = Guid.NewGuid().ToString("N");
		public string Name { get; set; } = "New provider";
		public AiProviderProtocol Protocol { get; set; } = AiProviderProtocol.OpenAICompatible;
		public string BaseUrl { get; set; } = "https://api.openai.com/v1";
		public string ApiKey { get; set; } = string.Empty;
		public string? Organization { get; set; }
		public List<AiModelInfo> Models { get; set; } = new();
		public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public int TimeoutSeconds { get; set; } = 120;

		public AiModelInfo? FindModel(string modelId) => Models.Find(model =>
			string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));

		public void AddOrUpdateModel(AiModelInfo model)
		{
			if (string.IsNullOrWhiteSpace(model.Id))
				return;

			var existing = FindModel(model.Id);
			if (existing == null)
				Models.Add(model);
			else
			{
				existing.DisplayName = model.DisplayName;
				existing.OwnedBy = model.OwnedBy;
				// A manually entered model remains manual even after a remote refresh.
				existing.IsManual |= model.IsManual;
			}
		}
	}

	public sealed class AiChatSettings
	{
		public string? ActiveProviderProfileId { get; set; }
		public string? ActiveModelId { get; set; }
		public List<AiProviderProfile> Providers { get; set; } = new();
		public float? Temperature { get; set; } = 0.7f;
		public int? MaxTokens { get; set; } = 2048;
		public bool StreamResponses { get; set; } = true;
	}

	public sealed class AiChatModConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		[DefaultValue(AiProviderProtocol.OpenAICompatible)]
		public AiProviderProtocol Protocol = AiProviderProtocol.OpenAICompatible;

		[DefaultValue("https://api.openai.com/v1")]
		[CustomModConfigItem(typeof(AiChatLongTextConfigElement))]
		public string BaseUrl = "https://api.openai.com/v1";

		[DefaultValue("")]
		[CustomModConfigItem(typeof(AiChatLongTextConfigElement))]
		public string ApiKey = string.Empty;

		[DefaultValue("gpt-4o-mini")]
		[CustomModConfigItem(typeof(AiChatLongTextConfigElement))]
		public string Model = "gpt-4o-mini";

		[Range(0f, 2f)]
		[DefaultValue(0.7f)]
		public float Temperature = 0.7f;

		[Range(1, 600)]
		[DefaultValue(120)]
		public int TimeoutSeconds = 120;

		[Range(1, 32768)]
		[DefaultValue(2048)]
		public int MaxTokens = 2048;

		[DefaultValue(true)]
		public bool StreamResponses = true;
	}
}
