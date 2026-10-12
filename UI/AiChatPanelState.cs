using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AiChat.Common.Chat;
using AiChat.Common.Config;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.UI.Chat;

namespace AiChat.UI
{
    internal enum AiChatStatusKind
    {
        Working,
        Success,
        Warning,
        Failure
    }

    internal sealed class AiChatPanelState : UIState
    {
        private const int MaximumInputLength = 4000;
        private const int BackspaceRepeatDelayFrames = 21;
        private const int BackspaceRepeatIntervalFrames = 3;
        private const int NavigationRepeatDelayFrames = 21;
        private const int NavigationRepeatIntervalFrames = 3;
        private const int CaretForceVisibleFrames = 45;
        private const int ScrollBarTrackWidth = 3;
        private const int ScrollBarThumbWidth = 7;
        private const int ScrollBarHitPaddingX = 10;
        private const int ScrollBarHitPaddingY = 5;
        private const int MessageWheelPixelsPerNotch = 60;
        private const int InputWheelLinesPerNotch = 2;
        private const float InputTextScale = 0.82f;
        private static readonly RasterizerState ScissorRasterizerState = new RasterizerState { ScissorTestEnable = true };
        private readonly AiChatUISystem owner;
        private readonly List<ConversationItem> conversations = new List<ConversationItem>();
        private readonly List<AiChatMessageSnapshot> messages = new List<AiChatMessageSnapshot>();
        private readonly List<string> models = new List<string> { "default" };
        private readonly List<char> queuedText = new List<char>();
        private KeyboardState lastKeyboardState;
        private string inputText = string.Empty;
        private int cursor;
        private int selectionAnchor;
        private int backspaceHeldFrames;
        private int backspaceRepeatFrames;
        private Keys navigationRepeatKey;
        private int navigationHeldFrames;
        private int navigationRepeatFrames;
        private int inputFirstVisibleLine;
        private ulong caretForceVisibleUntilTick;
        private bool revealInputCursor;
        private string activeConversationId;
        private int scrollOffset;
        private int messageMaxScroll;
        private bool messageStickToBottom = true;
        private string statusText = string.Empty;
        private AiChatStatusKind statusKind;
        private bool inputFocused;
        private bool webSearchEnabled;
        private Rectangle inputRectangle;
        private Rectangle panelRectangle;
        private Rectangle sidebarRectangle;
        private Rectangle messagesRectangle;
        private Rectangle modelRectangle;
        private Rectangle webSearchRectangle;
        private Rectangle newConversationRectangle;
        private Rectangle messageScrollTrackRectangle;
        private Rectangle messageScrollThumbRectangle;
        private Rectangle inputScrollTrackRectangle;
        private Rectangle inputScrollThumbRectangle;
        private bool messageScrollDragging;
        private bool inputScrollDragging;
        private int messageScrollDragStartY;
        private int messageScrollDragStartOffset;
        private int inputScrollDragStartY;
        private int inputScrollDragStartLine;

        internal AiChatPanelState(AiChatUISystem owner)
        {
            this.owner = owner;
        }

        internal bool InputFocused => inputFocused;
        internal string ActiveConversationId => activeConversationId;
        internal IReadOnlyList<AiChatMessageSnapshot> Messages => messages;
        internal static event Action<string, string, string, bool> SendRequested;
        internal static event Action<string, string, string> ConversationCreated;
        internal static event Action<string> ConversationDeleted;

        internal void SetStatus(string conversationId, string statusKey, AiChatStatusKind kind)
        {
            if (!string.Equals(conversationId, activeConversationId, StringComparison.OrdinalIgnoreCase))
                return;

            statusText = statusKey ?? string.Empty;
            statusKind = kind;
        }

        internal void ResetForOpen()
        {
            if (conversations.Count == 0)
                CreateConversation();
            if (string.IsNullOrEmpty(activeConversationId))
                SelectConversation(conversations[0].Id);
            inputFocused = false;
            statusText = string.Empty;
            statusKind = AiChatStatusKind.Working;
            queuedText.Clear();
            cursor = 0;
            selectionAnchor = 0;
            inputFirstVisibleLine = 0;
            messageStickToBottom = true;
            lastKeyboardState = Main.keyState;
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();
            Main.clrInput();
        }

        internal void QueueTextInput(char character)
        {
            if (inputFocused)
                queuedText.Add(character);
        }

        internal void CaptureTextInput()
        {
            if (!inputFocused)
                return;

            PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            TextInputEXT.StartTextInput();
            TextInputEXT.SetInputRectangle(inputRectangle);

            KeyboardState keyboard = Main.keyState;
            bool enter = IsNewPress(keyboard, Keys.Enter) || Main.inputTextEnter;
            bool shift = IsShiftDown(keyboard);
            bool control = IsControlDown(keyboard);
            bool commandHandled = HandleCommandShortcuts(keyboard);

            HandleNavigationKeys(keyboard);
            if (control)
            {
                queuedText.Clear();
            }
            else if (!commandHandled)
            {
                ApplyQueuedTextInput();
            }

            if (ShouldApplyBackspace(keyboard))
            {
                DeleteBackward();
            }

            if (IsNewPress(keyboard, Keys.Delete))
            {
                DeleteForward();
            }

            if (enter)
            {
                ConsumeEnterInput();
                if (shift || control)
                {
                    InsertTextAtCursor("\n");
                }
                else
                {
                    SubmitInput();
                    lastKeyboardState = keyboard;
                    return;
                }
            }

            if (IsNewPress(keyboard, Keys.Escape) || Main.inputTextEscape)
            {
                Main.inputTextEscape = false;
                inputFocused = false;
                queuedText.Clear();
                ResetBackspaceRepeatState();
                ResetNavigationRepeatState();
                TextInputEXT.StopTextInput();
                PlayerInput.WritingText = false;
            }

            queuedText.Clear();
            lastKeyboardState = keyboard;
            Main.chatRelease = false;
            Main.npcChatRelease = false;
        }

        internal void SetConversationSnapshots(IEnumerable<AiChatConversationSnapshot> snapshots)
        {
            conversations.Clear();
            if (snapshots != null)
            {
                foreach (AiChatConversationSnapshot snapshot in snapshots)
                {
                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Id)) continue;
                    conversations.Add(new ConversationItem(snapshot));
                }
            }
            if (conversations.Count == 0) CreateConversation();
            if (conversations.All(item => item.Id != activeConversationId))
                SelectConversation(conversations[0].Id);
        }

        internal void SetMessages(string conversationId, IEnumerable<AiChatMessageSnapshot> newMessages)
        {
            if (conversationId != activeConversationId) return;
            messages.Clear();
            if (newMessages != null) messages.AddRange(newMessages.Where(message => message != null));
        }

        internal void SetProviderOptions(IEnumerable<string> providerOptions, IEnumerable<string> modelOptions)
        {
            models.Clear();
            if (modelOptions != null) models.AddRange(modelOptions.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (models.Count == 0) models.Add("default");
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            Layout();
            if (Main.LocalPlayer != null)
            {
                Main.LocalPlayer.mouseInterface = true;
                if (panelRectangle.Contains(Main.mouseX, Main.mouseY))
                    Main.LocalPlayer.releaseUseItem = false;
            }
            Main.blockMouse = true;
            PlayerInput.LockVanillaMouseScroll("AiChat.ChatUI");
            UpdateMessageScrollBarLayout();
            UpdateInputScrollBarLayout();
            HandleMessageScrollBar(new Point(Main.mouseX, Main.mouseY));
            HandleInputScrollBar(new Point(Main.mouseX, Main.mouseY));

            int wheelDelta = PlayerInput.ScrollWheelDeltaForUI;
            if (wheelDelta != 0)
            {
                Point mouse = new Point(Main.mouseX, Main.mouseY);
                int wheelNotches = Math.Max(1, Math.Abs(wheelDelta) / 120);
                if (messagesRectangle.Contains(mouse) && messageMaxScroll > 0)
                {
                    messageStickToBottom = false;
                    scrollOffset = Math.Min(
                        Math.Max(scrollOffset - Math.Sign(wheelDelta) * wheelNotches * MessageWheelPixelsPerNotch, 0),
                        messageMaxScroll);
                    messageStickToBottom = scrollOffset >= messageMaxScroll;
                    PlayerInput.ScrollWheelDelta = 0;
                    PlayerInput.ScrollWheelDeltaForUI = 0;
                }
                else if (inputRectangle.Contains(mouse))
                {
                    int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out _, out _);
                    if (maximumFirstVisibleLine > 0)
                    {
                        inputFirstVisibleLine = Math.Min(
                            Math.Max(inputFirstVisibleLine - Math.Sign(wheelDelta) * wheelNotches * InputWheelLinesPerNotch, 0),
                            maximumFirstVisibleLine);
                        revealInputCursor = false;
                        PlayerInput.ScrollWheelDelta = 0;
                        PlayerInput.ScrollWheelDeltaForUI = 0;
                    }
                }
            }
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);
            Layout();
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            spriteBatch.Draw(pixel, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.58f);
            DrawPanel(spriteBatch, pixel, panelRectangle, new Color(12, 15, 21), new Color(48, 57, 70));
            DrawPanel(spriteBatch, pixel, sidebarRectangle, new Color(19, 22, 29), new Color(86, 102, 126));
            DrawSidebar(spriteBatch, pixel);
            DrawHeader(spriteBatch, pixel);
            DrawMessages(spriteBatch);
            DrawInput(spriteBatch, pixel);
            DrawStatus(spriteBatch, pixel);
            HandleMouseInput();
            DrawImePanel(spriteBatch);
        }

        private void Layout()
        {
            int width = Math.Min(1100, Math.Max(640, Main.screenWidth - 80));
            int height = Math.Min(700, Math.Max(420, Main.screenHeight - 80));
            panelRectangle = new Rectangle((Main.screenWidth - width) / 2, (Main.screenHeight - height) / 2, width, height);
            sidebarRectangle = new Rectangle(panelRectangle.X, panelRectangle.Y, Math.Min(245, width / 3), height);
            int contentX = sidebarRectangle.Right;
            int contentWidth = panelRectangle.Width - sidebarRectangle.Width - 28;
            int headerLeft = contentX + 18;
            int headerRight = panelRectangle.Right - 18;
            int headerWidth = Math.Max(260, headerRight - headerLeft);
            int searchWidth = Math.Min(148, Math.Max(120, headerWidth / 3));
            int modelWidth = Math.Max(150, headerWidth - searchWidth - 12);
            modelRectangle = new Rectangle(headerLeft, panelRectangle.Y + 14, modelWidth, 34);
            webSearchRectangle = new Rectangle(modelRectangle.Right + 12, panelRectangle.Y + 14, searchWidth, 34);
            int inputHeight = Math.Min(112, Math.Max(82, height / 6));
            int inputY = panelRectangle.Bottom - inputHeight - 30;
            messagesRectangle = new Rectangle(contentX + 14, panelRectangle.Y + 64, contentWidth, Math.Max(120, inputY - panelRectangle.Y - 76));
            inputRectangle = new Rectangle(messagesRectangle.X, inputY, messagesRectangle.Width - 95, inputHeight);
            newConversationRectangle = new Rectangle(sidebarRectangle.X + 14, sidebarRectangle.Y + 42, sidebarRectangle.Width - 28, 34);
        }

        private void DrawSidebar(SpriteBatch spriteBatch, Texture2D pixel)
        {
            DrawText(spriteBatch, UiText("Title"), new Vector2(sidebarRectangle.X + 16, sidebarRectangle.Y + 14), Color.White, 1f);
            DrawButton(spriteBatch, pixel, newConversationRectangle, "+  " + UiText("NewConversation"), new Color(46, 93, 128));
            int y = newConversationRectangle.Bottom + 16;
            foreach (ConversationItem conversation in conversations)
            {
                Rectangle row = new Rectangle(sidebarRectangle.X + 10, y, sidebarRectangle.Width - 20, 42);
                bool selected = conversation.Id == activeConversationId;
                spriteBatch.Draw(pixel, row, selected ? new Color(45, 51, 64) : new Color(22, 26, 34));
                string title = IsUntitledConversation(conversation.Title) ? UiText("NewConversation") : conversation.Title;
                DrawText(spriteBatch, TrimToWidth(title, row.Width - 41, 0.78f), new Vector2(row.X + 9, row.Y + 7), Color.White, 0.78f);
                DrawText(spriteBatch, conversation.Model ?? "default", new Vector2(row.X + 9, row.Y + 24), new Color(150, 163, 181), 0.62f);
                Rectangle delete = new Rectangle(row.Right - 34, row.Y + 6, 28, 30);
                bool deleteHovered = delete.Contains(Main.mouseX, Main.mouseY);
                Color deleteFill = deleteHovered ? new Color(122, 57, 64) : new Color(35, 34, 40);
                DrawPanel(spriteBatch, pixel, delete, deleteFill, deleteHovered ? new Color(221, 132, 132) : new Color(79, 69, 79));
                Vector2 deleteSize = FontAssets.MouseText.Value.MeasureString("x") * 0.82f;
                DrawText(spriteBatch, "x", delete.Center.ToVector2() - deleteSize / 2f, deleteHovered ? Color.White : new Color(205, 125, 125), 0.82f);
                y += 46;
                if (y > sidebarRectangle.Bottom - 24) break;
            }
        }

        private void DrawHeader(SpriteBatch spriteBatch, Texture2D pixel)
        {
            DrawText(spriteBatch, UiText("Model"), new Vector2(modelRectangle.X, modelRectangle.Y - 10), new Color(165, 176, 193), 0.65f);
            DrawText(spriteBatch, models[0], new Vector2(modelRectangle.X + 10, modelRectangle.Y + 8), Color.White, 0.78f);
            DrawBorder(spriteBatch, modelRectangle, new Color(77, 91, 112));
            DrawWebSearchToggle(spriteBatch, pixel);
        }

        private void DrawWebSearchToggle(SpriteBatch spriteBatch, Texture2D pixel)
        {
            bool hovered = webSearchRectangle.Contains(Main.mouseX, Main.mouseY);
            Color fill = webSearchEnabled
                ? (hovered ? new Color(54, 92, 132) : new Color(42, 72, 108))
                : (hovered ? new Color(44, 52, 66) : new Color(24, 29, 39));
            DrawPanel(spriteBatch, pixel, webSearchRectangle, fill, webSearchEnabled ? new Color(116, 172, 220) : new Color(70, 82, 102));
            string state = webSearchEnabled ? UiText("WebSearchOn") : UiText("WebSearchOff");
            DrawText(spriteBatch, TrimToWidth(UiText("WebSearch") + ": " + state, webSearchRectangle.Width - 16, 0.68f), new Vector2(webSearchRectangle.X + 8, webSearchRectangle.Y + 8), Color.White, 0.68f);
            if (hovered)
                Main.instance.MouseText(UiText("WebSearchHint"));
        }

        private void DrawMessages(SpriteBatch spriteBatch)
        {
            const float textScale = 0.78f;
            const int messageGap = 10;
            int totalHeight = 0;
            List<MessageLayout> layouts = new List<MessageLayout>();
            foreach (AiChatMessageSnapshot message in messages)
            {
                bool user = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
                int bubbleX = messagesRectangle.X + (user ? 80 : 8);
                int bubbleWidth = messagesRectangle.Width - (user ? 90 : 18);
                Rectangle textArea = new Rectangle(bubbleX + 10, 0, bubbleWidth - 20, int.MaxValue);
                string content = user ? message.Content ?? string.Empty : ProviderMetadataFilter.RemoveSafetyBlocks(message.Content ?? string.Empty);
                List<string> lines = WrapDisplayText(content, textArea.Width, textScale);
                int bubbleHeight = 31 + lines.Count * (int)(FontAssets.MouseText.Value.LineSpacing * textScale) + 8;
                layouts.Add(new MessageLayout(user, bubbleX, bubbleWidth, bubbleHeight, lines));
                totalHeight += bubbleHeight + messageGap;
            }

            messageMaxScroll = Math.Max(0, totalHeight - messagesRectangle.Height);
            if (messageStickToBottom)
                scrollOffset = messageMaxScroll;
            else
                scrollOffset = Math.Min(Math.Max(scrollOffset, 0), messageMaxScroll);
            if (messages.Count == 0)
                totalHeight = 40;

            GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
            Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
            spriteBatch.End();
            graphicsDevice.ScissorRectangle = GetScaledScissorRectangle(messagesRectangle);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

            if (messages.Count == 0)
            {
                DrawText(spriteBatch, UiText("StartConversation"), new Vector2(messagesRectangle.X + 20, messagesRectangle.Y + 24), new Color(150, 160, 176), 0.9f);
            }
            else
            {
                Texture2D pixel = TextureAssets.MagicPixel.Value;
                int y = messagesRectangle.Y - scrollOffset;
                for (int i = 0; i < layouts.Count; i++)
                {
                    MessageLayout layout = layouts[i];
                    Rectangle bubble = new Rectangle(layout.X, y, layout.Width, layout.Height);
                    if (bubble.Bottom >= messagesRectangle.Y && bubble.Y <= messagesRectangle.Bottom)
                    {
                        spriteBatch.Draw(pixel, bubble, layout.User ? new Color(38, 72, 92) : new Color(35, 39, 49));
                        DrawText(spriteBatch, layout.User ? UiText("You") : UiText("Assistant"), new Vector2(bubble.X + 10, bubble.Y + 6), new Color(174, 193, 211), 0.66f);
                        int lineY = bubble.Y + 25;
                        int lineHeight = (int)(FontAssets.MouseText.Value.LineSpacing * textScale);
                        foreach (string line in layout.Lines)
                        {
                            DrawText(spriteBatch, line, new Vector2(bubble.X + 10, lineY), Color.White, textScale);
                            lineY += lineHeight;
                        }
                    }
                    y += layout.Height + messageGap;
                }
            }

            spriteBatch.End();
            graphicsDevice.ScissorRectangle = oldScissorRectangle;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
            DrawMessageScrollBar(spriteBatch);
        }

        private void DrawInput(SpriteBatch spriteBatch, Texture2D pixel)
        {
            DrawPanel(spriteBatch, pixel, inputRectangle, new Color(25, 29, 38), inputFocused ? new Color(92, 142, 177) : new Color(71, 82, 99));
            Rectangle textArea = GetInputTextArea();
            if (string.IsNullOrEmpty(inputText))
            {
                DrawWrappedInputText(spriteBatch, UiText("InputPlaceholder"), textArea, new Color(124, 135, 151), false);
            }
            DrawEditableInputText(spriteBatch, textArea);
            DrawInputScrollBar(spriteBatch);

            Rectangle send = new Rectangle(inputRectangle.Right + 10, inputRectangle.Y, 85, inputRectangle.Height);
            DrawButton(spriteBatch, pixel, send, UiText("Send"), new Color(46, 93, 128));
        }

        private void DrawStatus(SpriteBatch spriteBatch, Texture2D pixel)
        {
            if (string.IsNullOrEmpty(statusText))
                return;

            Color baseColor = statusKind switch
            {
                AiChatStatusKind.Success => new Color(132, 220, 157),
                AiChatStatusKind.Failure => new Color(236, 125, 125),
                _ => new Color(242, 194, 94)
            };
            float pulse = statusKind == AiChatStatusKind.Working
                ? 0.78f + 0.22f * (float)(0.5 + 0.5 * Math.Sin(Main.GlobalTimeWrappedHourly * 8f))
                : 1f;
            Color color = baseColor * pulse;
            string text = UiText(statusText);
            if (statusKind == AiChatStatusKind.Working)
                text += new string('.', 1 + (int)(Main.GameUpdateCount / 18 % 3));

            int statusY = inputRectangle.Bottom + 6;
            spriteBatch.Draw(pixel, new Rectangle(inputRectangle.X, statusY, 3, 18), color * 0.9f);
            DrawText(spriteBatch, text, new Vector2(inputRectangle.X + 9, statusY - 1), color, 0.66f);
        }

        private void HandleMouseInput()
        {
            bool click = Main.mouseLeft && Main.mouseLeftRelease;
            if (!click) return;
            Main.mouseLeftRelease = false;
            Point mouse = new Point(Main.mouseX, Main.mouseY);
            if (newConversationRectangle.Contains(mouse)) { CreateConversation(); return; }
            if (modelRectangle.Contains(mouse)) { RotateModel(); return; }
            if (webSearchRectangle.Contains(mouse)) { webSearchEnabled = !webSearchEnabled; return; }
            Rectangle send = new Rectangle(inputRectangle.Right + 10, inputRectangle.Y, 85, inputRectangle.Height);
            if (send.Contains(mouse)) { SubmitInput(); return; }
            if (inputRectangle.Contains(mouse))
            {
                inputFocused = true;
                lastKeyboardState = Main.keyState;
                ResetBackspaceRepeatState();
                ResetNavigationRepeatState();
                MoveCursor(GetCursorIndexFromMouse(mouse), IsShiftDown(Main.keyState));
                revealInputCursor = true;
                Main.clrInput();
                return;
            }
            int y = newConversationRectangle.Bottom + 16;
            foreach (ConversationItem conversation in conversations.ToArray())
            {
                Rectangle row = new Rectangle(sidebarRectangle.X + 10, y, sidebarRectangle.Width - 20, 42);
                Rectangle delete = new Rectangle(row.Right - 34, row.Y + 6, 28, 30);
                if (delete.Contains(mouse)) { DeleteConversation(conversation.Id); return; }
                if (row.Contains(mouse)) { SelectConversation(conversation.Id); return; }
                y += 46;
            }
            if (!panelRectangle.Contains(mouse)) owner.Close();
        }

        private int GetMaximumInputFirstVisibleLine(out int lineCount, out int visibleLineCount)
        {
            Rectangle area = GetInputTextArea();
            lineCount = WrapInputText(inputText, area).Count;
            float lineHeight = FontAssets.MouseText.Value.LineSpacing * InputTextScale;
            visibleLineCount = Math.Max(1, (int)(area.Height / lineHeight));
            return Math.Max(0, lineCount - visibleLineCount);
        }

        private void UpdateMessageScrollBarLayout()
        {
            if (messageMaxScroll <= 0)
            {
                messageScrollTrackRectangle = Rectangle.Empty;
                messageScrollThumbRectangle = Rectangle.Empty;
                return;
            }

            Rectangle track = new Rectangle(messagesRectangle.Right - 10, messagesRectangle.Y + 8, ScrollBarTrackWidth, messagesRectangle.Height - 16);
            int thumbHeight = Math.Max(18, (int)(track.Height * messagesRectangle.Height / (float)(messagesRectangle.Height + messageMaxScroll)));
            int travel = Math.Max(1, track.Height - thumbHeight);
            int thumbY = track.Y + (int)(travel * (scrollOffset / (float)messageMaxScroll));
            messageScrollTrackRectangle = track;
            messageScrollThumbRectangle = new Rectangle(track.Center.X - ScrollBarThumbWidth / 2, thumbY, ScrollBarThumbWidth, thumbHeight);
        }

        private void UpdateInputScrollBarLayout()
        {
            int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out int lineCount, out int visibleLineCount);
            if (maximumFirstVisibleLine <= 0)
            {
                inputFirstVisibleLine = 0;
                inputScrollTrackRectangle = Rectangle.Empty;
                inputScrollThumbRectangle = Rectangle.Empty;
                return;
            }

            inputFirstVisibleLine = Math.Min(Math.Max(inputFirstVisibleLine, 0), maximumFirstVisibleLine);
            Rectangle track = new Rectangle(inputRectangle.Right - 10, inputRectangle.Y + 8, ScrollBarTrackWidth, inputRectangle.Height - 16);
            int thumbHeight = Math.Max(18, track.Height * visibleLineCount / lineCount);
            int travel = Math.Max(1, track.Height - thumbHeight);
            int thumbY = track.Y + travel * inputFirstVisibleLine / maximumFirstVisibleLine;
            inputScrollTrackRectangle = track;
            inputScrollThumbRectangle = new Rectangle(track.Center.X - ScrollBarThumbWidth / 2, thumbY, ScrollBarThumbWidth, thumbHeight);
        }

        private void HandleMessageScrollBar(Point mouse)
        {
            if (messageMaxScroll <= 0 || messageScrollTrackRectangle == Rectangle.Empty)
            {
                messageScrollDragging = false;
                return;
            }

            Rectangle hit = GetMessageScrollHitRectangle();
            if (!messageScrollDragging)
            {
                bool leftPressed = Main.mouseLeft && (Main.mouseLeftRelease || hit.Contains(mouse));
                if (!hit.Contains(mouse) || !leftPressed)
                    return;

                messageScrollDragging = true;
                messageScrollDragStartY = mouse.Y;
                if (messageScrollThumbRectangle.Contains(mouse))
                {
                    messageScrollDragStartOffset = scrollOffset;
                }
                else
                {
                    scrollOffset = GetMessageScrollOffsetFromTrack(mouse.Y);
                    messageScrollDragStartOffset = scrollOffset;
                }
                messageStickToBottom = false;
                Main.mouseLeftRelease = false;
            }

            if (!Main.mouseLeft)
            {
                messageScrollDragging = false;
                return;
            }

            int travel = Math.Max(1, messageScrollTrackRectangle.Height - messageScrollThumbRectangle.Height);
            scrollOffset = Math.Min(Math.Max(messageScrollDragStartOffset + (int)Math.Round(
                (mouse.Y - messageScrollDragStartY) * messageMaxScroll / (float)travel), 0), messageMaxScroll);
            messageStickToBottom = scrollOffset >= messageMaxScroll;
        }

        private void HandleInputScrollBar(Point mouse)
        {
            int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out _, out _);
            if (maximumFirstVisibleLine <= 0 || inputScrollTrackRectangle == Rectangle.Empty)
            {
                inputScrollDragging = false;
                return;
            }

            Rectangle hit = GetInputScrollHitRectangle();
            if (!inputScrollDragging)
            {
                bool leftPressed = Main.mouseLeft && (Main.mouseLeftRelease || hit.Contains(mouse));
                if (!hit.Contains(mouse) || !leftPressed)
                    return;

                inputScrollDragging = true;
                inputScrollDragStartY = mouse.Y;
                if (inputScrollThumbRectangle.Contains(mouse))
                {
                    inputScrollDragStartLine = inputFirstVisibleLine;
                }
                else
                {
                    inputFirstVisibleLine = GetInputScrollLineFromTrack(mouse.Y, maximumFirstVisibleLine);
                    inputScrollDragStartLine = inputFirstVisibleLine;
                }
                revealInputCursor = false;
                Main.mouseLeftRelease = false;
            }

            if (!Main.mouseLeft)
            {
                inputScrollDragging = false;
                return;
            }

            int travel = Math.Max(1, inputScrollTrackRectangle.Height - inputScrollThumbRectangle.Height);
            inputFirstVisibleLine = Math.Min(Math.Max(inputScrollDragStartLine + (int)Math.Round(
                (mouse.Y - inputScrollDragStartY) * maximumFirstVisibleLine / (float)travel), 0), maximumFirstVisibleLine);
            revealInputCursor = false;
        }

        private int GetMessageScrollOffsetFromTrack(int mouseY)
        {
            int travel = Math.Max(1, messageScrollTrackRectangle.Height - messageScrollThumbRectangle.Height);
            float ratio = (mouseY - messageScrollTrackRectangle.Y - messageScrollThumbRectangle.Height / 2f) / travel;
            return Math.Min(Math.Max((int)Math.Round(ratio * messageMaxScroll), 0), messageMaxScroll);
        }

        private int GetInputScrollLineFromTrack(int mouseY, int maximumFirstVisibleLine)
        {
            int travel = Math.Max(1, inputScrollTrackRectangle.Height - inputScrollThumbRectangle.Height);
            float ratio = (mouseY - inputScrollTrackRectangle.Y - inputScrollThumbRectangle.Height / 2f) / travel;
            return Math.Min(Math.Max((int)Math.Round(ratio * maximumFirstVisibleLine), 0), maximumFirstVisibleLine);
        }

        private Rectangle GetMessageScrollHitRectangle()
        {
            Rectangle hit = messageScrollThumbRectangle == Rectangle.Empty
                ? messageScrollTrackRectangle
                : Rectangle.Union(messageScrollTrackRectangle, messageScrollThumbRectangle);
            hit.Inflate(ScrollBarHitPaddingX, ScrollBarHitPaddingY);
            return hit;
        }

        private Rectangle GetInputScrollHitRectangle()
        {
            Rectangle hit = inputScrollThumbRectangle == Rectangle.Empty
                ? inputScrollTrackRectangle
                : Rectangle.Union(inputScrollTrackRectangle, inputScrollThumbRectangle);
            hit.Inflate(ScrollBarHitPaddingX, ScrollBarHitPaddingY);
            return hit;
        }

        private void DrawMessageScrollBar(SpriteBatch spriteBatch)
        {
            UpdateMessageScrollBarLayout();
            if (messageScrollTrackRectangle == Rectangle.Empty)
                return;

            Texture2D pixel = TextureAssets.MagicPixel.Value;
            bool hovered = GetMessageScrollHitRectangle().Contains(new Point(Main.mouseX, Main.mouseY));
            spriteBatch.Draw(pixel, messageScrollTrackRectangle, hovered || messageScrollDragging ? new Color(104, 124, 154) * 0.78f : new Color(54, 66, 88) * 0.58f);
            Color thumb = messageScrollDragging ? new Color(210, 218, 214) : hovered ? new Color(176, 196, 202) : new Color(126, 146, 156);
            spriteBatch.Draw(pixel, messageScrollThumbRectangle, thumb);
        }

        private void DrawInputScrollBar(SpriteBatch spriteBatch)
        {
            UpdateInputScrollBarLayout();
            if (inputScrollTrackRectangle == Rectangle.Empty)
                return;

            Texture2D pixel = TextureAssets.MagicPixel.Value;
            bool hovered = GetInputScrollHitRectangle().Contains(new Point(Main.mouseX, Main.mouseY));
            spriteBatch.Draw(pixel, inputScrollTrackRectangle, hovered || inputScrollDragging ? new Color(104, 124, 154) * 0.78f : new Color(54, 66, 88) * 0.58f);
            Color thumb = inputScrollDragging ? new Color(210, 218, 214) : hovered ? new Color(176, 196, 202) : new Color(126, 146, 156);
            spriteBatch.Draw(pixel, inputScrollThumbRectangle, thumb);
        }

        private void SubmitInput()
        {
            string text = inputText.Trim();
            if (text.Length == 0 || string.IsNullOrEmpty(activeConversationId)) return;
            messages.Add(new AiChatMessageSnapshot { Role = "user", Content = text });
            ConversationItem current = conversations.FirstOrDefault(item => item.Id == activeConversationId);
            if (current != null && IsUntitledConversation(current.Title)) current.Title = Trim(text, 26);
            inputText = string.Empty;
            cursor = 0;
            selectionAnchor = 0;
            inputFirstVisibleLine = 0;
            messageStickToBottom = true;
            inputFocused = false;
            statusText = string.Empty;
            statusKind = AiChatStatusKind.Working;
            queuedText.Clear();
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();
            TextInputEXT.StopTextInput();
            PlayerInput.WritingText = false;
            Main.clrInput();
            SendRequested?.Invoke(activeConversationId, "Default", models[0], webSearchEnabled);
        }

        private void CreateConversation()
        {
            string id = Guid.NewGuid().ToString("N");
            conversations.Insert(0, new ConversationItem(new AiChatConversationSnapshot { Id = id, Title = "New conversation", Provider = "Default", Model = models[0] }));
            SelectConversation(id);
            ConversationCreated?.Invoke(id, "Default", models[0]);
        }

        private void DeleteConversation(string id)
        {
            ConversationItem item = conversations.FirstOrDefault(value => value.Id == id);
            if (item == null) return;
            conversations.Remove(item);
            ConversationDeleted?.Invoke(id);
            if (conversations.Count == 0) CreateConversation();
            else if (activeConversationId == id) SelectConversation(conversations[0].Id);
        }

        private void SelectConversation(string id)
        {
            ConversationItem selected = conversations.FirstOrDefault(item => item.Id == id);
            activeConversationId = id;
            if (selected != null)
            {
                MoveToFront(models, selected.Model);
            }
            messages.Clear();
            inputText = string.Empty;
            cursor = 0;
            selectionAnchor = 0;
            inputFirstVisibleLine = 0;
            messageStickToBottom = true;
            scrollOffset = 0;
            statusText = string.Empty;
            statusKind = AiChatStatusKind.Working;
            queuedText.Clear();
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();
        }

        private void RotateModel()
        {
            if (models.Count > 1) models.Add(models[0]);
            if (models.Count > 1) models.RemoveAt(0);
            ConversationItem current = conversations.FirstOrDefault(item => item.Id == activeConversationId);
            if (current != null) current.Model = models[0];
        }

        private void ApplyQueuedTextInput()
        {
            foreach (char character in queuedText)
            {
                if (character == '\b')
                    continue;
                if (character == '\t')
                {
                    InsertTextAtCursor("    ");
                    continue;
                }
                if (character == '\r' || character == '\n' || char.IsControl(character))
                    continue;
                InsertTextAtCursor(character.ToString());
            }

            queuedText.Clear();
        }

        private bool HandleCommandShortcuts(KeyboardState keyboard)
        {
            if (!IsControlDown(keyboard))
                return false;

            if (IsNewPress(keyboard, Keys.A))
            {
                selectionAnchor = 0;
                cursor = inputText.Length;
                ShowCaretNow();
                return true;
            }

            if (IsNewPress(keyboard, Keys.C))
            {
                CopySelectionToClipboard();
                return true;
            }

            if (IsNewPress(keyboard, Keys.X))
            {
                CopySelectionToClipboard();
                DeleteSelection();
                return true;
            }

            if (IsNewPress(keyboard, Keys.V))
            {
                string clipboardText = GetClipboardText();
                if (!string.IsNullOrEmpty(clipboardText))
                    InsertTextAtCursor(clipboardText);
                return true;
            }

            return false;
        }

        private void HandleNavigationKeys(KeyboardState keyboard)
        {
            bool selecting = IsShiftDown(keyboard);
            bool control = IsControlDown(keyboard);
            bool moved = false;
            if (ShouldApplyNavigationKey(keyboard, Keys.Left))
            {
                MoveCursor(control ? FindPreviousWordBoundary(cursor) : GetPreviousTextElementStart(cursor), selecting);
                moved = true;
            }
            else if (ShouldApplyNavigationKey(keyboard, Keys.Right))
            {
                MoveCursor(control ? FindNextWordBoundary(cursor) : GetNextTextElementStart(cursor), selecting);
                moved = true;
            }
            else if (ShouldApplyNavigationKey(keyboard, Keys.Up))
            {
                MoveCursorVertically(-1, selecting);
                moved = true;
            }
            else if (ShouldApplyNavigationKey(keyboard, Keys.Down))
            {
                MoveCursorVertically(1, selecting);
                moved = true;
            }

            if (!moved && !keyboard.IsKeyDown(Keys.Left) && !keyboard.IsKeyDown(Keys.Right)
                && !keyboard.IsKeyDown(Keys.Up) && !keyboard.IsKeyDown(Keys.Down))
            {
                ResetNavigationRepeatState();
            }

            if (IsNewPress(keyboard, Keys.Home))
                MoveCursor(0, selecting);
            if (IsNewPress(keyboard, Keys.End))
                MoveCursor(inputText.Length, selecting);
        }

        private void MoveCursorVertically(int direction, bool selecting)
        {
            Rectangle area = GetInputTextArea();
            List<EditableTextLine> lines = WrapInputText(inputText, area);
            int currentLine = FindEditableLineIndex(lines, cursor);
            int targetLine = Math.Min(Math.Max(currentLine + direction, 0), lines.Count - 1);
            if (targetLine == currentLine)
                return;

            float horizontal = verticalNavigationHorizontalPosition >= 0f
                ? verticalNavigationHorizontalPosition
                : MeasureEditablePrefix(lines[currentLine], cursor);
            MoveCursor(FindEditableIndexAtPosition(lines[targetLine], horizontal), selecting);
            verticalNavigationHorizontalPosition = horizontal;
        }

        private bool ShouldApplyBackspace(KeyboardState keyboard)
        {
            if (!keyboard.IsKeyDown(Keys.Back))
            {
                ResetBackspaceRepeatState();
                return false;
            }

            if (!lastKeyboardState.IsKeyDown(Keys.Back))
            {
                backspaceHeldFrames = 1;
                backspaceRepeatFrames = 0;
                return true;
            }

            backspaceHeldFrames++;
            if (backspaceHeldFrames < BackspaceRepeatDelayFrames)
                return false;

            backspaceRepeatFrames++;
            if (backspaceRepeatFrames < BackspaceRepeatIntervalFrames)
                return false;

            backspaceRepeatFrames = 0;
            return true;
        }

        private void ResetBackspaceRepeatState()
        {
            backspaceHeldFrames = 0;
            backspaceRepeatFrames = 0;
        }

        private bool ShouldApplyNavigationKey(KeyboardState keyboard, Keys key)
        {
            if (!keyboard.IsKeyDown(key))
            {
                if (navigationRepeatKey == key)
                    ResetNavigationRepeatState();
                return false;
            }

            if (navigationRepeatKey != key || !lastKeyboardState.IsKeyDown(key))
            {
                navigationRepeatKey = key;
                navigationHeldFrames = 1;
                navigationRepeatFrames = 0;
                return true;
            }

            navigationHeldFrames++;
            if (navigationHeldFrames < NavigationRepeatDelayFrames)
                return false;

            navigationRepeatFrames++;
            if (navigationRepeatFrames < NavigationRepeatIntervalFrames)
                return false;

            navigationRepeatFrames = 0;
            return true;
        }

        private void ResetNavigationRepeatState()
        {
            navigationRepeatKey = Keys.None;
            navigationHeldFrames = 0;
            navigationRepeatFrames = 0;
            verticalNavigationHorizontalPosition = -1f;
        }

        private float verticalNavigationHorizontalPosition = -1f;

        private void InsertTextAtCursor(string text)
        {
            text = SanitizeInputText(text);
            if (string.IsNullOrEmpty(text))
                return;

            DeleteSelection();
            int available = MaximumInputLength - inputText.Length;
            if (available <= 0)
                return;
            if (text.Length > available)
                text = text.Substring(0, available);

            cursor = ClampTextIndex(cursor);
            inputText = inputText.Insert(cursor, text);
            MoveCursor(cursor + text.Length, false);
        }

        private void DeleteBackward()
        {
            if (DeleteSelection() || cursor <= 0)
                return;

            int start = GetPreviousTextElementStart(cursor);
            inputText = inputText.Remove(start, cursor - start);
            MoveCursor(start, false);
        }

        private void DeleteForward()
        {
            if (DeleteSelection() || cursor >= inputText.Length)
                return;

            int end = GetNextTextElementStart(cursor);
            inputText = inputText.Remove(cursor, end - cursor);
            MoveCursor(cursor, false);
        }

        private bool DeleteSelection()
        {
            if (!HasSelection())
                return false;

            int start = GetSelectionStart();
            inputText = inputText.Remove(start, GetSelectionEnd() - start);
            MoveCursor(start, false);
            return true;
        }

        private void MoveCursor(int index, bool selecting)
        {
            cursor = ClampTextIndex(index);
            if (!selecting)
                selectionAnchor = cursor;
            verticalNavigationHorizontalPosition = -1f;
            ShowCaretNow();
            revealInputCursor = true;
        }

        private bool HasSelection() => cursor != selectionAnchor;
        private int GetSelectionStart() => Math.Min(ClampTextIndex(cursor), ClampTextIndex(selectionAnchor));
        private int GetSelectionEnd() => Math.Max(ClampTextIndex(cursor), ClampTextIndex(selectionAnchor));

        private void CopySelectionToClipboard()
        {
            if (HasSelection())
                SetClipboardText(inputText.Substring(GetSelectionStart(), GetSelectionEnd() - GetSelectionStart()));
        }

        private static string GetClipboardText()
        {
            try
            {
                return Platform.Has<IClipboard>() ? Platform.Get<IClipboard>().MultiLineValue ?? string.Empty : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void SetClipboardText(string text)
        {
            try
            {
                if (Platform.Has<IClipboard>())
                    Platform.Get<IClipboard>().Value = text ?? string.Empty;
            }
            catch
            {
            }
        }

        private string SanitizeInputText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                if (character == '\n' || !char.IsControl(character))
                    builder.Append(character);
                else if (character == '\t')
                    builder.Append("    ");
            }
            return builder.ToString();
        }

        private int ClampTextIndex(int index) => Math.Min(Math.Max(index, 0), inputText.Length);

        private int GetPreviousTextElementStart(int index)
        {
            index = ClampTextIndex(index);
            if (index <= 0)
                return 0;
            int[] indexes = StringInfo.ParseCombiningCharacters(inputText);
            int previous = 0;
            foreach (int elementStart in indexes)
            {
                if (elementStart >= index)
                    break;
                previous = elementStart;
            }
            return previous;
        }

        private int GetNextTextElementStart(int index)
        {
            index = ClampTextIndex(index);
            if (index >= inputText.Length)
                return inputText.Length;
            int[] indexes = StringInfo.ParseCombiningCharacters(inputText);
            foreach (int elementStart in indexes)
            {
                if (elementStart > index)
                    return elementStart;
            }
            return inputText.Length;
        }

        private int FindPreviousWordBoundary(int index)
        {
            index = ClampTextIndex(index);
            while (index > 0 && char.IsWhiteSpace(inputText[index - 1])) index--;
            while (index > 0 && !char.IsWhiteSpace(inputText[index - 1])) index--;
            return index;
        }

        private int FindNextWordBoundary(int index)
        {
            index = ClampTextIndex(index);
            while (index < inputText.Length && char.IsWhiteSpace(inputText[index])) index++;
            while (index < inputText.Length && !char.IsWhiteSpace(inputText[index])) index++;
            return index;
        }

        private static bool IsControlDown(KeyboardState keyboard) => keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        private static bool IsShiftDown(KeyboardState keyboard) => keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);

        private void ConsumeEnterInput()
        {
            Main.inputTextEnter = false;
            Main.chatRelease = false;
            Main.npcChatRelease = false;
            Main.clrInput();
        }

        private void ShowCaretNow() => caretForceVisibleUntilTick = Main.GameUpdateCount + CaretForceVisibleFrames;

        private bool ShouldDrawCaret()
        {
            if (!inputFocused)
                return false;
            if (Main.GameUpdateCount <= caretForceVisibleUntilTick)
                return true;
            return (int)(Main.GlobalTimeWrappedHourly * 2f) % 2 == 0;
        }

        private Rectangle GetInputTextArea()
        {
            return new Rectangle(inputRectangle.X + 12, inputRectangle.Y + 8, inputRectangle.Width - 24, inputRectangle.Height - 16);
        }

        private List<EditableTextLine> WrapInputText(string text, Rectangle area)
        {
            List<EditableTextLine> lines = new List<EditableTextLine>();
            if (string.IsNullOrEmpty(text))
            {
                lines.Add(new EditableTextLine(string.Empty, 0, 0));
                return lines;
            }

            float maxWidth = Math.Max(1f, area.Width / InputTextScale);
            int[] indexes = StringInfo.ParseCombiningCharacters(text);
            StringBuilder currentLine = new StringBuilder();
            int lineStart = 0;
            for (int i = 0; i < indexes.Length; i++)
            {
                int elementStart = indexes[i];
                int elementEnd = i + 1 < indexes.Length ? indexes[i + 1] : text.Length;
                string element = text.Substring(elementStart, elementEnd - elementStart);
                if (element == "\n")
                {
                    lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, elementStart));
                    currentLine.Clear();
                    lineStart = elementEnd;
                    continue;
                }

                string proposed = currentLine + element;
                if (currentLine.Length > 0 && FontAssets.MouseText.Value.MeasureString(proposed).X > maxWidth)
                {
                    lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, elementStart));
                    currentLine.Clear();
                    lineStart = elementStart;
                }
                currentLine.Append(element);
            }
            lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, text.Length));
            return lines;
        }

        private static List<string> WrapDisplayText(string text, int width, float scale)
        {
            List<string> lines = new List<string>();
            string[] paragraphs = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            float maxWidth = Math.Max(1f, width / scale);
            foreach (string paragraph in paragraphs)
            {
                if (paragraph.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                StringBuilder line = new StringBuilder();
                int[] indexes = StringInfo.ParseCombiningCharacters(paragraph);
                for (int i = 0; i < indexes.Length; i++)
                {
                    int elementStart = indexes[i];
                    int elementEnd = i + 1 < indexes.Length ? indexes[i + 1] : paragraph.Length;
                    string element = paragraph.Substring(elementStart, elementEnd - elementStart);
                    if (line.Length > 0 && FontAssets.MouseText.Value.MeasureString(line + element).X > maxWidth)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    line.Append(element);
                }
                lines.Add(line.ToString());
            }
            if (lines.Count == 0)
                lines.Add(string.Empty);
            return lines;
        }

        private void DrawWrappedInputText(SpriteBatch spriteBatch, string text, Rectangle area, Color color, bool drawCaret)
        {
            List<EditableTextLine> lines = WrapInputText(text, area);
            int lineHeight = (int)(FontAssets.MouseText.Value.LineSpacing * InputTextScale);
            int visibleLineCount = Math.Max(1, area.Height / lineHeight);
            int maximumFirstLine = Math.Max(0, lines.Count - visibleLineCount);
            inputFirstVisibleLine = Math.Min(Math.Max(inputFirstVisibleLine, 0), maximumFirstLine);
            int firstLine = inputFirstVisibleLine;
            for (int i = firstLine; i < lines.Count && i < firstLine + visibleLineCount + 1; i++)
            {
                float y = area.Y + (i - firstLine) * lineHeight;
                if (y >= area.Bottom)
                    break;
                if (!string.IsNullOrEmpty(lines[i].Text))
                    DrawText(spriteBatch, lines[i].Text, new Vector2(area.X, y), color, InputTextScale);
            }
        }

        private void DrawEditableInputText(SpriteBatch spriteBatch, Rectangle area)
        {
            List<EditableTextLine> lines = WrapInputText(inputText, area);
            int lineHeight = (int)(FontAssets.MouseText.Value.LineSpacing * InputTextScale);
            int visibleLineCount = Math.Max(1, area.Height / lineHeight);
            int maximumFirstLine = Math.Max(0, lines.Count - visibleLineCount);
            int caretLine = FindEditableLineIndex(lines, cursor);
            if (revealInputCursor && inputFocused)
            {
                if (caretLine < inputFirstVisibleLine)
                    inputFirstVisibleLine = caretLine;
                else if (caretLine >= inputFirstVisibleLine + visibleLineCount)
                    inputFirstVisibleLine = caretLine - visibleLineCount + 1;
                revealInputCursor = false;
            }
            inputFirstVisibleLine = Math.Min(Math.Max(inputFirstVisibleLine, 0), maximumFirstLine);

            GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
            Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
            spriteBatch.End();
            graphicsDevice.ScissorRectangle = GetScaledScissorRectangle(area);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

            int selectionStart = GetSelectionStart();
            int selectionEnd = GetSelectionEnd();
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            for (int i = inputFirstVisibleLine; i < lines.Count && i < inputFirstVisibleLine + visibleLineCount + 1; i++)
            {
                EditableTextLine line = lines[i];
                float y = area.Y + (i - inputFirstVisibleLine) * lineHeight;
                if (y >= area.Bottom)
                    break;

                int start = Math.Max(selectionStart, line.StartIndex);
                int end = Math.Min(selectionEnd, line.EndIndex);
                if (start < end)
                {
                    float startX = area.X + MeasureEditablePrefix(line, start) * InputTextScale;
                    float endX = area.X + MeasureEditablePrefix(line, end) * InputTextScale;
                    spriteBatch.Draw(pixel, new Rectangle((int)startX, (int)y, Math.Max(2, (int)(endX - startX)), lineHeight), new Color(80, 120, 210) * 0.55f);
                }

                if (!string.IsNullOrEmpty(line.Text))
                    DrawText(spriteBatch, line.Text, new Vector2(area.X, y), Color.White, InputTextScale);

                if (ShouldDrawCaret() && i == caretLine)
                {
                    float caretX = area.X + MeasureEditablePrefix(line, cursor) * InputTextScale;
                    spriteBatch.Draw(pixel, new Rectangle((int)caretX, (int)y, 2, Math.Max(14, lineHeight)), new Color(200, 220, 237));
                }
            }

            spriteBatch.End();
            graphicsDevice.ScissorRectangle = oldScissorRectangle;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
        }

        private float MeasureEditablePrefix(EditableTextLine line, int absoluteIndex)
        {
            int relativeIndex = Math.Min(Math.Max(absoluteIndex - line.StartIndex, 0), line.Text.Length);
            return relativeIndex <= 0 ? 0f : FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, relativeIndex)).X;
        }

        private static int FindEditableLineIndex(List<EditableTextLine> lines, int cursorIndex)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (cursorIndex >= lines[i].StartIndex && cursorIndex <= lines[i].EndIndex)
                    return i;
            }
            return Math.Max(0, lines.Count - 1);
        }

        private static int FindEditableIndexAtPosition(EditableTextLine line, float horizontalPosition)
        {
            if (string.IsNullOrEmpty(line.Text))
                return line.StartIndex;
            int[] indexes = StringInfo.ParseCombiningCharacters(line.Text);
            for (int i = 0; i < indexes.Length; i++)
            {
                int start = indexes[i];
                int end = i + 1 < indexes.Length ? indexes[i + 1] : line.Text.Length;
                float startWidth = start == 0 ? 0f : FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, start)).X;
                float endWidth = FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, end)).X;
                if (horizontalPosition < (startWidth + endWidth) * 0.5f)
                    return line.StartIndex + start;
            }
            return line.EndIndex;
        }

        private int GetCursorIndexFromMouse(Point mousePoint)
        {
            Rectangle area = GetInputTextArea();
            List<EditableTextLine> lines = WrapInputText(inputText, area);
            int lineHeight = (int)(FontAssets.MouseText.Value.LineSpacing * InputTextScale);
            int lineIndex = Math.Min(Math.Max(inputFirstVisibleLine + (mousePoint.Y - area.Y) / lineHeight, 0), lines.Count - 1);
            float horizontal = Math.Max(0f, mousePoint.X - area.X) / InputTextScale;
            return FindEditableIndexAtPosition(lines[lineIndex], horizontal);
        }

        private static Rectangle GetScaledScissorRectangle(Rectangle area)
        {
            float scale = Main.UIScale;
            return new Rectangle((int)(area.X * scale), (int)(area.Y * scale), Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
        }

        private void DrawImePanel(SpriteBatch spriteBatch)
        {
            if (!inputFocused)
                return;

            Rectangle area = GetInputTextArea();
            List<EditableTextLine> lines = WrapInputText(inputText, area);
            if (lines.Count == 0)
                return;

            float lineHeight = FontAssets.MouseText.Value.LineSpacing * InputTextScale;
            int visibleLineCount = Math.Max(1, (int)(area.Height / lineHeight));
            int caretLineIndex = FindEditableLineIndex(lines, cursor);
            int firstVisibleLine = Math.Min(
                Math.Max(inputFirstVisibleLine, 0),
                Math.Max(0, lines.Count - visibleLineCount));
            int visibleCaretLine = Math.Min(
                Math.Max(caretLineIndex, firstVisibleLine),
                firstVisibleLine + visibleLineCount - 1);
            float caretX = area.X + MeasureEditablePrefix(lines[visibleCaretLine], cursor) * InputTextScale;
            float caretY = area.Y + (visibleCaretLine - firstVisibleLine) * lineHeight;
            float anchorY = Math.Min(caretY + lineHeight + 34f, Main.screenHeight - 36f);

            spriteBatch.End();
            Main.instance.SetIMEPanelAnchor(new Vector2(caretX, anchorY), 0f);
            Main.instance.DrawIMEPanel();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
        }

        private static void MoveToFront(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            int index = values.FindIndex(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            if (index <= 0) return;
            string selected = values[index];
            values.RemoveAt(index);
            values.Insert(0, selected);
        }

        private static string UiText(string key) => AiChatText.Get("UI." + key);

        private static bool IsUntitledConversation(string title)
        {
            return string.IsNullOrWhiteSpace(title)
                || string.Equals(title, "New conversation", StringComparison.Ordinal);
        }

        private bool IsNewPress(KeyboardState current, Keys key) => current.IsKeyDown(key) && !lastKeyboardState.IsKeyDown(key);
        private static string Trim(string value, int length) => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= length ? value : value.Substring(0, Math.Max(0, length - 1)) + "...");

        private static string TrimToWidth(string value, float maxWidth, float scale)
        {
            if (string.IsNullOrEmpty(value) || FontAssets.MouseText.Value.MeasureString(value).X * scale <= maxWidth)
                return value ?? string.Empty;

            const string suffix = "...";
            int[] indexes = StringInfo.ParseCombiningCharacters(value);
            int characterCount = indexes.Length;
            while (characterCount > 0)
            {
                int end = characterCount == indexes.Length ? value.Length : indexes[characterCount];
                string candidate = value.Substring(0, end) + suffix;
                if (FontAssets.MouseText.Value.MeasureString(candidate).X * scale <= maxWidth)
                    return candidate;
                characterCount--;
            }

            return suffix;
        }
        private static void DrawText(SpriteBatch spriteBatch, string text, Vector2 position, Color color, float scale) => Utils.DrawBorderString(spriteBatch, text ?? string.Empty, position, color, scale);
        private static void DrawPanel(SpriteBatch spriteBatch, Texture2D pixel, Rectangle rectangle, Color fill, Color border) { spriteBatch.Draw(pixel, rectangle, fill); DrawBorder(spriteBatch, rectangle, border); }
        private static void DrawBorder(SpriteBatch spriteBatch, Rectangle rectangle, Color color) { Texture2D pixel = TextureAssets.MagicPixel.Value; spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 1), color); spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 1, rectangle.Width, 1), color); spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, 1, rectangle.Height), color); spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 1, rectangle.Y, 1, rectangle.Height), color); }
        private static void DrawButton(SpriteBatch spriteBatch, Texture2D pixel, Rectangle rectangle, string text, Color fill)
        {
            bool hovered = rectangle.Contains(Main.mouseX, Main.mouseY);
            bool pressed = hovered && Main.mouseLeft;
            Color buttonFill = pressed
                ? Color.Lerp(fill, Color.Black, 0.14f)
                : hovered ? Color.Lerp(fill, Color.White, 0.16f) : fill;
            Color border = hovered ? new Color(157, 196, 221) : new Color(78, 116, 145);
            DrawPanel(spriteBatch, pixel, rectangle, buttonFill, border);
            Vector2 size = FontAssets.MouseText.Value.MeasureString(text) * 0.78f;
            Vector2 position = rectangle.Center.ToVector2() - size / 2f + (pressed ? new Vector2(1f, 1f) : Vector2.Zero);
            DrawText(spriteBatch, text, position, Color.White, 0.78f);
        }

        private sealed class ConversationItem
        {
            internal string Id;
            internal string Title;
            internal string Provider;
            internal string Model;
            internal ConversationItem(AiChatConversationSnapshot snapshot) { Id = snapshot.Id; Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "New conversation" : snapshot.Title; Provider = snapshot.Provider; Model = snapshot.Model; }
        }

        private sealed class EditableTextLine
        {
            internal readonly string Text;
            internal readonly int StartIndex;
            internal readonly int EndIndex;
            internal EditableTextLine(string text, int startIndex, int endIndex)
            {
                Text = text;
                StartIndex = startIndex;
                EndIndex = endIndex;
            }
        }

        private sealed class MessageLayout
        {
            internal readonly bool User;
            internal readonly int X;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly List<string> Lines;
            internal MessageLayout(bool user, int x, int width, int height, List<string> lines)
            {
                User = user;
                X = x;
                Width = width;
                Height = height;
                Lines = lines;
            }
        }
    }
}
