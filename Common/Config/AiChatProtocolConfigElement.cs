using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader.Config.UI;
using Terraria.UI;

namespace AiChat.Common.Config
{
    internal sealed class AiChatProtocolConfigElement : ConfigElement
    {
        private static readonly string[] ProtocolLabels =
        {
            "OpenAI Compatible",
            "Claude Messages",
            "OpenAI Responses"
        };

        public override void OnBind()
        {
            base.OnBind();
            Height.Set(66f, 0f);
            TextDisplayFunction = () => Label;
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            CalculatedStyle dimensions = GetInnerDimensions();
            float relativeX = evt.MousePosition.X - dimensions.X;
            float relativeY = evt.MousePosition.Y - dimensions.Y;
            if (relativeY < 28f || relativeY >= 60f)
                return;
            int index = (int)(relativeX / MathHelper.Max(1f, dimensions.Width) * ProtocolLabels.Length);
            if (index >= 0 && index < ProtocolLabels.Length)
                SetObject((AiProviderProtocol)index);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);
            CalculatedStyle dimensions = GetInnerDimensions();
            int x = (int)dimensions.X;
            int y = (int)dimensions.Y + 28;
            int width = (int)dimensions.Width;
            int height = 32;
            int gap = 4;
            int buttonWidth = (width - gap * (ProtocolLabels.Length - 1)) / ProtocolLabels.Length;
            int selected = GetObject() is AiProviderProtocol protocol ? (int)protocol : 0;
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            for (int i = 0; i < ProtocolLabels.Length; i++)
            {
                int buttonX = x + i * (buttonWidth + gap);
                Rectangle button = new Rectangle(buttonX, y, buttonWidth, height);
                bool hovered = button.Contains(Main.mouseX, Main.mouseY);
                Color fill = i == selected
                    ? (hovered ? new Color(54, 92, 132) : new Color(42, 72, 108))
                    : (hovered ? new Color(44, 52, 66) : new Color(24, 29, 39));
                Color border = i == selected ? new Color(116, 172, 220) : new Color(70, 82, 102);
                spriteBatch.Draw(pixel, button, fill);
                spriteBatch.Draw(pixel, new Rectangle(button.X, button.Y, button.Width, 1), border);
                spriteBatch.Draw(pixel, new Rectangle(button.X, button.Bottom - 1, button.Width, 1), border);
                spriteBatch.Draw(pixel, new Rectangle(button.X, button.Y, 1, button.Height), border);
                spriteBatch.Draw(pixel, new Rectangle(button.Right - 1, button.Y, 1, button.Height), border);
                string label = ProtocolLabels[i];
                Vector2 textSize = FontAssets.MouseText.Value.MeasureString(label) * 0.68f;
                Utils.DrawBorderString(spriteBatch, label, button.Center.ToVector2() - textSize / 2f, Color.White, 0.68f);
            }
        }
    }
}
