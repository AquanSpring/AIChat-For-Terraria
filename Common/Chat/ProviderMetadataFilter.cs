using System;
using System.Text;

namespace AiChat.Common.Chat
{
	internal static class ProviderMetadataFilter
	{
		private const string SafetyTagStart = "<ds_safety";
		private const string SafetyTagEnd = "</ds_safety>";

		internal static string RemoveSafetyBlocks(string content)
		{
			if (string.IsNullOrEmpty(content))
				return string.Empty;

			int cursor = 0;
			StringBuilder visible = new StringBuilder(content.Length);
			while (cursor < content.Length)
			{
				int start = content.IndexOf(SafetyTagStart, cursor, StringComparison.OrdinalIgnoreCase);
				if (start < 0)
				{
					visible.Append(content, cursor, content.Length - cursor);
					break;
				}

				int tagEnd = content.IndexOf('>', start + SafetyTagStart.Length);
				if (tagEnd < 0)
					break;

				visible.Append(content, cursor, start - cursor);
				int end = content.IndexOf(SafetyTagEnd, tagEnd + 1, StringComparison.OrdinalIgnoreCase);
				if (end < 0)
					break;

				cursor = end + SafetyTagEnd.Length;
			}

			return visible.ToString();
		}
	}
}
