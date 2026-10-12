using Terraria.Localization;

namespace AiChat.Common.Config
{
	internal static class AiChatText
	{
		private const string Prefix = "Mods.AiChat.";

		internal static string Get(string key)
		{
			return Language.GetTextValue(Prefix + key);
		}
	}
}
