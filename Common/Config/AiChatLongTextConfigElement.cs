using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ModLoader.Config.UI;
using Terraria.UI;

namespace AiChat.Common.Config
{
    internal class AiChatLongTextConfigElement : ConfigElement
    {
        private const int MaxTextLength = 4096;
        private const int BackspaceRepeatDelayFrames = 21;
        private const int BackspaceRepeatIntervalFrames = 3;
        private const int NavigationRepeatDelayFrames = 21;
        private const int NavigationRepeatIntervalFrames = 3;
        private const float InputTextScale = 0.9f;
        private static readonly RasterizerState ScissorRasterizerState = new RasterizerState { ScissorTestEnable = true };
        private static AiChatLongTextConfigElement activeInputElement;

        private readonly List<UIElement> actionButtons = new List<UIElement>();
        private readonly List<char> queuedTextInput = new List<char>();
        private UIPanel textBoxBackground;
        private AiChatConfigInputDisplay inputDisplay;
        private UITextPanel<string> secretToggleButton;
        private bool isSecretField;
        private bool revealSecretText;
        private bool inputFocused;
        private bool previousMouseLeft;
        private bool mouseSelecting;
        private KeyboardState lastKeyState;
        private int inputCursorIndex;
        private int selectionAnchorIndex;
        private int backspaceHeldFrames;
        private int backspaceRepeatFrames;
        private Keys navigationRepeatKey;
        private int navigationHeldFrames;
        private int navigationRepeatFrames;
        private long caretBlinkResetMilliseconds;
        private float horizontalScrollPixels;
        private Rectangle inputTextAreaRect;
        private string actualText = string.Empty;

        internal static void QueueTextInputToActive(char character)
        {
            activeInputElement?.QueueTextInput(character);
        }

        public override void OnBind()
        {
            base.OnBind();

            isSecretField = IsApiKeyField();
            revealSecretText = false;
            inputFocused = false;
            mouseSelecting = false;
            previousMouseLeft = Main.mouseLeft;
            lastKeyState = Main.keyState;
            horizontalScrollPixels = 0f;
            caretBlinkResetMilliseconds = GetCurrentMilliseconds();
            queuedTextInput.Clear();
            actionButtons.Clear();
            secretToggleButton = null;
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();

            string rawText = (GetObject() as string) ?? string.Empty;
            actualText = TruncateToMaxLength(SanitizeInputText(rawText));
            if (!string.Equals(rawText, actualText, StringComparison.Ordinal))
            {
                SetObject(actualText);
            }

            inputCursorIndex = actualText.Length;
            selectionAnchorIndex = inputCursorIndex;

            Height.Set(isSecretField ? 110f : 78f, 0f);
            TextDisplayFunction = () => Label;

            textBoxBackground = new UIPanel();
            textBoxBackground.Left.Set(12f, 0f);
            textBoxBackground.Top.Set(36f, 0f);
            textBoxBackground.Width.Set(isSecretField ? -238f : -142f, 1f);
            textBoxBackground.Height.Set(34f, 0f);
            textBoxBackground.BackgroundColor = new Color(14, 20, 36) * 0.9f;
            textBoxBackground.BorderColor = new Color(90, 116, 190);
            textBoxBackground.OverflowHidden = true;
            textBoxBackground.SetPadding(0f);
            textBoxBackground.OnLeftClick += FocusInput;

            inputDisplay = new AiChatConfigInputDisplay(this);
            inputDisplay.Left.Set(8f, 0f);
            inputDisplay.Top.Set(5f, 0f);
            inputDisplay.Width.Set(-16f, 1f);
            inputDisplay.Height.Set(26f, 0f);
            inputDisplay.OnLeftClick += FocusInput;

            textBoxBackground.Append(inputDisplay);
            Append(textBoxBackground);

            if (isSecretField)
            {
                secretToggleButton = AppendSmallButton(GetSecretToggleButtonText(), -218f, 82f, ToggleSecretVisibility);
            }

            AppendSmallButton(AiChatText.Get("ConfigUI.Paste"), -130f, 52f, PasteFromClipboard);
            AppendSmallButton(AiChatText.Get("ConfigUI.Clear"), -72f, 56f, ClearText);

            if (isSecretField)
            {
                UIText hintText = new UIText(AiChatText.Get("ConfigUI.ApiKeyHint"), 0.78f);
                hintText.Left.Set(14f, 0f);
                hintText.Top.Set(76f, 0f);
                hintText.TextColor = new Color(174, 186, 214);
                Append(hintText);
            }
        }

        private void QueueTextInput(char character)
        {
            if (!inputFocused || activeInputElement != this)
            {
                return;
            }

            queuedTextInput.Add(character);
        }

        private void FocusInput(UIMouseEvent evt, UIElement listeningElement)
        {
            inputFocused = true;
            revealSecretText = false;
            activeInputElement = this;
            lastKeyState = Main.keyState;
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();
            MoveCursor(GetCursorIndexFromMouse(new Point(Main.mouseX, Main.mouseY)), IsShiftDown(Main.keyState));
            ShowCaretNow();
            mouseSelecting = true;
            Main.clrInput();
        }

        private UITextPanel<string> AppendSmallButton(string text, float left, float width, UIElement.MouseEvent handler)
        {
            UITextPanel<string> button = new UITextPanel<string>(text, 0.78f, false);
            button.Left.Set(left, 1f);
            button.Top.Set(39f, 0f);
            button.Width.Set(width, 0f);
            button.Height.Set(28f, 0f);
            button.BackgroundColor = new Color(42, 52, 82) * 0.95f;
            button.BorderColor = new Color(110, 132, 196);
            button.OnLeftClick += handler;
            actionButtons.Add(button);
            Append(button);
            return button;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            bool leftClicked = Main.mouseLeft && !previousMouseLeft;
            Point mousePoint = new Point(Main.mouseX, Main.mouseY);
            HandleBlankClickBlur(leftClicked, mousePoint);
            HandleMouseSelection(mousePoint);
            UpdateSecretToggleButtonText();

            if (inputFocused)
            {
                CaptureTextInput();
            }

            if (ContainsPoint(new Vector2(Main.mouseX, Main.mouseY)) || inputFocused)
            {
                if (Main.LocalPlayer != null)
                {
                    Main.LocalPlayer.mouseInterface = true;
                }
            }

            previousMouseLeft = Main.mouseLeft;
        }

        private void CaptureTextInput()
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            TextInputEXT.StartTextInput();
            TextInputEXT.SetInputRectangle(GetImeRectangle());

            string oldText = actualText;
            KeyboardState keyState = Main.keyState;
            bool commandHandled = HandleCommandShortcuts(keyState);
            HandleNavigationKeys(keyState);

            if (IsControlDown(keyState))
            {
                queuedTextInput.Clear();
            }
            else if (!commandHandled)
            {
                ApplyQueuedTextInput();
            }

            if (ShouldApplyBackspace(keyState))
            {
                DeleteBackward();
            }

            if (IsNewKeyPress(keyState, Keys.Delete))
            {
                DeleteForward();
            }

            if (IsNewKeyPress(keyState, Keys.Enter) || Main.inputTextEnter || IsNewKeyPress(keyState, Keys.Escape) || Main.inputTextEscape)
            {
                Main.inputTextEnter = false;
                Main.inputTextEscape = false;
                BlurInput(isSecretField);
                Main.clrInput();
                lastKeyState = keyState;
                return;
            }

            if (!string.Equals(oldText, actualText, StringComparison.Ordinal))
            {
                SetObject(actualText);
            }

            Main.chatRelease = false;
            Main.npcChatRelease = false;
            lastKeyState = keyState;
        }

        private Rectangle GetImeRectangle()
        {
            if (inputTextAreaRect != Rectangle.Empty)
            {
                return inputTextAreaRect;
            }

            return textBoxBackground?.GetDimensions().ToRectangle() ?? Rectangle.Empty;
        }

        private void ApplyQueuedTextInput()
        {
            if (queuedTextInput.Count == 0)
            {
                return;
            }

            for (int i = 0; i < queuedTextInput.Count && actualText.Length < MaxTextLength; i++)
            {
                char character = queuedTextInput[i];
                if (character == '\b')
                {
                    continue;
                }

                if (character == '\t')
                {
                    InsertTextAtCursor(" ");
                    continue;
                }

                if (character == '\r' || character == '\n' || char.IsControl(character))
                {
                    continue;
                }

                InsertTextAtCursor(character.ToString());
            }

            queuedTextInput.Clear();
        }

        private bool HandleCommandShortcuts(KeyboardState keyState)
        {
            if (!IsControlDown(keyState))
            {
                return false;
            }

            if (IsNewKeyPress(keyState, Keys.A))
            {
                SelectAllText();
                return true;
            }

            if (IsNewKeyPress(keyState, Keys.C))
            {
                CopySelectionToClipboard();
                return true;
            }

            if (IsNewKeyPress(keyState, Keys.X))
            {
                CutSelectionToClipboard();
                return true;
            }

            if (IsNewKeyPress(keyState, Keys.V))
            {
                PasteClipboardText();
                return true;
            }

            return false;
        }

        private void HandleNavigationKeys(KeyboardState keyState)
        {
            bool shiftDown = IsShiftDown(keyState);
            bool controlDown = IsControlDown(keyState);
            bool moved = false;
            if (ShouldApplyNavigationKey(keyState, Keys.Left))
            {
                MoveCursor(controlDown ? FindPreviousWordBoundary(inputCursorIndex) : GetPreviousTextElementStart(inputCursorIndex), shiftDown);
                moved = true;
            }
            else if (ShouldApplyNavigationKey(keyState, Keys.Right))
            {
                MoveCursor(controlDown ? FindNextWordBoundary(inputCursorIndex) : GetNextTextElementStart(inputCursorIndex), shiftDown);
                moved = true;
            }

            if (!moved && !keyState.IsKeyDown(Keys.Left) && !keyState.IsKeyDown(Keys.Right))
            {
                ResetNavigationRepeatState();
            }

            if (IsNewKeyPress(keyState, Keys.Home))
            {
                ResetNavigationRepeatState();
                MoveCursor(0, shiftDown);
            }

            if (IsNewKeyPress(keyState, Keys.End))
            {
                ResetNavigationRepeatState();
                MoveCursor(actualText.Length, shiftDown);
            }
        }

        private bool ShouldApplyBackspace(KeyboardState keyState)
        {
            if (!keyState.IsKeyDown(Keys.Back))
            {
                ResetBackspaceRepeatState();
                return false;
            }

            if (!lastKeyState.IsKeyDown(Keys.Back))
            {
                backspaceHeldFrames = 1;
                backspaceRepeatFrames = 0;
                return true;
            }

            backspaceHeldFrames++;
            if (backspaceHeldFrames < BackspaceRepeatDelayFrames)
            {
                return false;
            }

            backspaceRepeatFrames++;
            if (backspaceRepeatFrames < BackspaceRepeatIntervalFrames)
            {
                return false;
            }

            backspaceRepeatFrames = 0;
            return true;
        }

        private void ResetBackspaceRepeatState()
        {
            backspaceHeldFrames = 0;
            backspaceRepeatFrames = 0;
        }

        private bool ShouldApplyNavigationKey(KeyboardState keyState, Keys key)
        {
            if (!keyState.IsKeyDown(key))
            {
                if (navigationRepeatKey == key)
                {
                    ResetNavigationRepeatState();
                }

                return false;
            }

            if (navigationRepeatKey != key || !lastKeyState.IsKeyDown(key))
            {
                navigationRepeatKey = key;
                navigationHeldFrames = 1;
                navigationRepeatFrames = 0;
                return true;
            }

            navigationHeldFrames++;
            if (navigationHeldFrames < NavigationRepeatDelayFrames)
            {
                return false;
            }

            navigationRepeatFrames++;
            if (navigationRepeatFrames < NavigationRepeatIntervalFrames)
            {
                return false;
            }

            navigationRepeatFrames = 0;
            return true;
        }

        private void ResetNavigationRepeatState()
        {
            navigationRepeatKey = Keys.None;
            navigationHeldFrames = 0;
            navigationRepeatFrames = 0;
        }

        private void InsertTextAtCursor(string text)
        {
            text = SanitizeInputText(text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            DeleteSelection();
            int available = MaxTextLength - actualText.Length;
            if (available <= 0)
            {
                return;
            }

            if (text.Length > available)
            {
                text = text.Substring(0, available);
            }

            inputCursorIndex = ClampTextIndex(inputCursorIndex);
            actualText = actualText.Insert(inputCursorIndex, text);
            MoveCursor(inputCursorIndex + text.Length, false);
            SetObject(actualText);
        }

        private void DeleteBackward()
        {
            if (DeleteSelection())
            {
                return;
            }

            if (inputCursorIndex <= 0)
            {
                return;
            }

            int removeStart = GetPreviousTextElementStart(inputCursorIndex);
            actualText = actualText.Remove(removeStart, inputCursorIndex - removeStart);
            MoveCursor(removeStart, false);
            SetObject(actualText);
        }

        private void DeleteForward()
        {
            if (DeleteSelection())
            {
                return;
            }

            if (inputCursorIndex >= actualText.Length)
            {
                return;
            }

            int removeEnd = GetNextTextElementStart(inputCursorIndex);
            actualText = actualText.Remove(inputCursorIndex, removeEnd - inputCursorIndex);
            MoveCursor(inputCursorIndex, false);
            SetObject(actualText);
        }

        private bool DeleteSelection()
        {
            if (!HasSelection())
            {
                return false;
            }

            int start = GetSelectionStart();
            int end = GetSelectionEnd();
            actualText = actualText.Remove(start, end - start);
            MoveCursor(start, false);
            SetObject(actualText);
            return true;
        }

        private void MoveCursor(int index, bool selecting)
        {
            int oldCursorIndex = inputCursorIndex;
            int oldSelectionAnchorIndex = selectionAnchorIndex;
            inputCursorIndex = ClampTextIndex(index);
            if (!selecting)
            {
                selectionAnchorIndex = inputCursorIndex;
            }

            if (inputCursorIndex != oldCursorIndex || selectionAnchorIndex != oldSelectionAnchorIndex)
            {
                ShowCaretNow();
            }
        }

        private void SelectAllText()
        {
            selectionAnchorIndex = 0;
            inputCursorIndex = actualText.Length;
            ShowCaretNow();
        }

        private void ShowCaretNow()
        {
            caretBlinkResetMilliseconds = GetCurrentMilliseconds();
        }

        private bool ShouldDrawCaret()
        {
            if (!inputFocused)
            {
                return false;
            }

            long elapsed = GetCurrentMilliseconds() - caretBlinkResetMilliseconds;
            return elapsed % 1000L < 500L;
        }

        private bool HasSelection()
        {
            return inputCursorIndex != selectionAnchorIndex;
        }

        private int GetSelectionStart()
        {
            return Math.Min(ClampTextIndex(inputCursorIndex), ClampTextIndex(selectionAnchorIndex));
        }

        private int GetSelectionEnd()
        {
            return Math.Max(ClampTextIndex(inputCursorIndex), ClampTextIndex(selectionAnchorIndex));
        }

        private string GetSelectedText()
        {
            if (!HasSelection())
            {
                return string.Empty;
            }

            int start = GetSelectionStart();
            return actualText.Substring(start, GetSelectionEnd() - start);
        }

        private void CopySelectionToClipboard()
        {
            string selectedText = GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                SetClipboardText(selectedText);
            }
        }

        private void CutSelectionToClipboard()
        {
            string selectedText = GetSelectedText();
            if (string.IsNullOrEmpty(selectedText))
            {
                return;
            }

            SetClipboardText(selectedText);
            DeleteSelection();
        }

        private void PasteClipboardText()
        {
            string clipboardText = GetClipboardText();
            if (!string.IsNullOrEmpty(clipboardText))
            {
                InsertTextAtCursor(clipboardText);
            }
        }

        private void HandleBlankClickBlur(bool leftClicked, Point mousePoint)
        {
            bool needsBlur = inputFocused || (isSecretField && revealSecretText);
            if (!needsBlur || !leftClicked || IsInteractivePoint(mousePoint.ToVector2()))
            {
                return;
            }

            BlurInput(isSecretField);
            Main.clrInput();
        }

        private void HandleMouseSelection(Point mousePoint)
        {
            if (!mouseSelecting)
            {
                return;
            }

            if (!Main.mouseLeft)
            {
                mouseSelecting = false;
                return;
            }

            if (!inputFocused)
            {
                mouseSelecting = false;
                return;
            }

            MoveCursor(GetCursorIndexFromMouse(mousePoint), true);
        }

        private bool IsInteractivePoint(Vector2 mousePosition)
        {
            if (textBoxBackground != null && textBoxBackground.ContainsPoint(mousePosition))
            {
                return true;
            }

            for (int i = 0; i < actionButtons.Count; i++)
            {
                if (actionButtons[i] != null && actionButtons[i].ContainsPoint(mousePosition))
                {
                    return true;
                }
            }

            return false;
        }

        private void BlurInput(bool hideSecret)
        {
            if (hideSecret)
            {
                revealSecretText = false;
            }

            inputFocused = false;
            mouseSelecting = false;
            queuedTextInput.Clear();
            ResetBackspaceRepeatState();
            ResetNavigationRepeatState();
            MoveCursor(inputCursorIndex, false);
            if (activeInputElement == this)
            {
                activeInputElement = null;
                TextInputEXT.StopTextInput();
            }

            Main.inputTextEnter = false;
            Main.inputTextEscape = false;
            UpdateSecretToggleButtonText();
        }

        private void ToggleSecretVisibility(UIMouseEvent evt, UIElement listeningElement)
        {
            if (IsSecretCurrentlyVisible())
            {
                BlurInput(true);
                Main.clrInput();
                return;
            }

            BlurInput(false);
            revealSecretText = true;
            horizontalScrollPixels = 0f;
            Main.clrInput();
            UpdateSecretToggleButtonText();
        }

        private void UpdateSecretToggleButtonText()
        {
            if (secretToggleButton == null)
            {
                return;
            }

            secretToggleButton.SetText(GetSecretToggleButtonText(), 0.78f, false);
        }

        private string GetSecretToggleButtonText()
        {
            return IsSecretCurrentlyVisible()
                ? AiChatText.Get("ConfigUI.HideSecret")
                : AiChatText.Get("ConfigUI.ShowSecret");
        }

        private bool IsSecretCurrentlyVisible()
        {
            return isSecretField && (revealSecretText || inputFocused);
        }

        private void PasteFromClipboard(UIMouseEvent evt, UIElement listeningElement)
        {
            string clipboardText = SanitizeInputText(GetClipboardText().Trim());
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                Main.clrInput();
                return;
            }

            SetWholeText(clipboardText);
            BlurInput(isSecretField);
            Main.clrInput();
        }

        private void ClearText(UIMouseEvent evt, UIElement listeningElement)
        {
            SetWholeText(string.Empty);
            BlurInput(isSecretField);
            Main.clrInput();
        }

        private void SetWholeText(string text)
        {
            actualText = TruncateToMaxLength(SanitizeInputText(text));
            MoveCursor(actualText.Length, false);
            horizontalScrollPixels = 0f;
            SetObject(actualText);
        }

        private void DrawInputText(SpriteBatch spriteBatch, Rectangle area)
        {
            inputTextAreaRect = area;
            bool showingMask = IsShowingMask();
            string displayText = GetDisplayedText();
            if (showingMask)
            {
                horizontalScrollPixels = 0f;
            }
            else
            {
                EnsureCursorVisible(area);
            }

            GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
            Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
            spriteBatch.End();
            graphicsDevice.ScissorRectangle = GetScaledScissorRectangle(area);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

            Texture2D pixel = TextureAssets.MagicPixel.Value;
            float lineHeight = FontAssets.MouseText.Value.LineSpacing * InputTextScale;
            float textY = area.Y + (area.Height - lineHeight) * 0.5f - 1f;
            float textX = area.X - horizontalScrollPixels;

            if (inputFocused && !showingMask)
            {
                DrawSelection(spriteBatch, pixel, area, textY, lineHeight);
            }

            if (!string.IsNullOrEmpty(displayText))
            {
                Utils.DrawBorderString(spriteBatch, displayText, new Vector2(textX, textY), Color.White, InputTextScale);
            }

            if (ShouldDrawCaret() && !showingMask)
            {
                float caretX = area.X - horizontalScrollPixels + MeasurePrefix(actualText, inputCursorIndex) * InputTextScale;
                spriteBatch.Draw(pixel, new Rectangle((int)caretX, (int)textY, 2, Math.Max(14, (int)lineHeight)), Color.White);
            }

            spriteBatch.End();
            graphicsDevice.ScissorRectangle = oldScissorRectangle;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
        }

        private void DrawSelection(SpriteBatch spriteBatch, Texture2D pixel, Rectangle area, float textY, float lineHeight)
        {
            if (!HasSelection())
            {
                return;
            }

            int selectionStart = GetSelectionStart();
            int selectionEnd = GetSelectionEnd();
            float startX = area.X - horizontalScrollPixels + MeasurePrefix(actualText, selectionStart) * InputTextScale;
            float endX = area.X - horizontalScrollPixels + MeasurePrefix(actualText, selectionEnd) * InputTextScale;
            int width = Math.Max(2, (int)(endX - startX));
            spriteBatch.Draw(pixel, new Rectangle((int)startX, (int)textY, width, Math.Max(14, (int)lineHeight)), new Color(80, 120, 210) * 0.55f);
        }

        private void EnsureCursorVisible(Rectangle area)
        {
            if (!inputFocused)
            {
                horizontalScrollPixels = MathHelper.Clamp(horizontalScrollPixels, 0f, GetMaxHorizontalScroll(area));
                return;
            }

            float caretX = MeasurePrefix(actualText, inputCursorIndex) * InputTextScale;
            float rightPadding = Math.Max(10f, area.Width * 0.08f);
            if (caretX - horizontalScrollPixels > area.Width - rightPadding)
            {
                horizontalScrollPixels = caretX - area.Width + rightPadding;
            }
            else if (caretX - horizontalScrollPixels < 4f)
            {
                horizontalScrollPixels = caretX - 4f;
            }

            horizontalScrollPixels = MathHelper.Clamp(horizontalScrollPixels, 0f, GetMaxHorizontalScroll(area));
        }

        private float GetMaxHorizontalScroll(Rectangle area)
        {
            float textWidth = MeasurePrefix(GetDisplayedText(), GetDisplayedText().Length) * InputTextScale;
            return Math.Max(0f, textWidth - area.Width + 8f);
        }

        private int GetCursorIndexFromMouse(Point mousePoint)
        {
            Rectangle area = inputTextAreaRect != Rectangle.Empty
                ? inputTextAreaRect
                : inputDisplay?.GetDimensions().ToRectangle() ?? Rectangle.Empty;

            if (area == Rectangle.Empty || string.IsNullOrEmpty(actualText))
            {
                return 0;
            }

            float relativeX = Math.Max(0f, (mousePoint.X - area.X + horizontalScrollPixels) / InputTextScale);
            int[] indexes = StringInfo.ParseCombiningCharacters(actualText);
            for (int i = 0; i < indexes.Length; i++)
            {
                int elementStart = indexes[i];
                int elementEnd = i + 1 < indexes.Length ? indexes[i + 1] : actualText.Length;
                float startWidth = MeasurePrefix(actualText, elementStart);
                float endWidth = MeasurePrefix(actualText, elementEnd);
                if (relativeX < (startWidth + endWidth) * 0.5f)
                {
                    return elementStart;
                }
            }

            return actualText.Length;
        }

        private float MeasurePrefix(string text, int absoluteIndex)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0f;
            }

            int clampedIndex = Math.Min(Math.Max(absoluteIndex, 0), text.Length);
            if (clampedIndex <= 0)
            {
                return 0f;
            }

            return FontAssets.MouseText.Value.MeasureString(text.Substring(0, clampedIndex)).X;
        }

        private string GetDisplayedText()
        {
            if (!IsShowingMask())
            {
                return actualText;
            }

            int maskLength = Math.Min(Math.Max(actualText.Length, 8), 24);
            return new string('*', maskLength);
        }

        private bool IsShowingMask()
        {
            return isSecretField && !IsSecretCurrentlyVisible() && !string.IsNullOrEmpty(actualText);
        }

        private static string SanitizeInputText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (character == '\r' || character == '\n')
                {
                    continue;
                }

                if (character == '\t')
                {
                    builder.Append(' ');
                    continue;
                }

                if (!char.IsControl(character))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        private static string TruncateToMaxLength(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxTextLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, MaxTextLength);
        }

        private static long GetCurrentMilliseconds()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private int ClampTextIndex(int index)
        {
            return Math.Min(Math.Max(index, 0), actualText.Length);
        }

        private int GetPreviousTextElementStart(int index)
        {
            index = ClampTextIndex(index);
            if (index <= 0)
            {
                return 0;
            }

            int[] indexes = StringInfo.ParseCombiningCharacters(actualText);
            int previous = 0;
            for (int i = 0; i < indexes.Length; i++)
            {
                if (indexes[i] >= index)
                {
                    break;
                }

                previous = indexes[i];
            }

            return previous;
        }

        private int GetNextTextElementStart(int index)
        {
            index = ClampTextIndex(index);
            if (index >= actualText.Length)
            {
                return actualText.Length;
            }

            int[] indexes = StringInfo.ParseCombiningCharacters(actualText);
            for (int i = 0; i < indexes.Length; i++)
            {
                if (indexes[i] > index)
                {
                    return indexes[i];
                }
            }

            return actualText.Length;
        }

        private int FindPreviousWordBoundary(int index)
        {
            index = ClampTextIndex(index);
            while (index > 0 && char.IsWhiteSpace(actualText[index - 1]))
            {
                index--;
            }

            while (index > 0 && !char.IsWhiteSpace(actualText[index - 1]))
            {
                index--;
            }

            return index;
        }

        private int FindNextWordBoundary(int index)
        {
            index = ClampTextIndex(index);
            while (index < actualText.Length && char.IsWhiteSpace(actualText[index]))
            {
                index++;
            }

            while (index < actualText.Length && !char.IsWhiteSpace(actualText[index]))
            {
                index++;
            }

            return index;
        }

        private bool IsNewKeyPress(KeyboardState keyState, Keys key)
        {
            return keyState.IsKeyDown(key) && !lastKeyState.IsKeyDown(key);
        }

        private static bool IsControlDown(KeyboardState keyState)
        {
            return keyState.IsKeyDown(Keys.LeftControl) || keyState.IsKeyDown(Keys.RightControl);
        }

        private static bool IsShiftDown(KeyboardState keyState)
        {
            return keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);
        }

        private static string GetClipboardText()
        {
            try
            {
                if (Platform.Has<IClipboard>())
                {
                    return Platform.Get<IClipboard>().MultiLineValue ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void SetClipboardText(string text)
        {
            try
            {
                if (Platform.Has<IClipboard>())
                {
                    Platform.Get<IClipboard>().Value = text ?? string.Empty;
                }
            }
            catch
            {
            }
        }

        private bool IsApiKeyField()
        {
            string memberName = MemberInfo?.Name ?? string.Empty;
            if (string.Equals(memberName, nameof(AiChatModConfig.ApiKey), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string label = Label ?? string.Empty;
            return label.IndexOf("ApiKey", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("API Key", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsLegacyEndpointField()
        {
            return false;

            /*
            string memberName = MemberInfo?.Name ?? string.Empty;
            if (string.Equals(memberName, "LegacyEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string label = Label ?? string.Empty;
            return label.IndexOf("LegacyEndpoint", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("API Base", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("基础地址", StringComparison.OrdinalIgnoreCase) >= 0;
            */
        }

        private static Rectangle GetScaledScissorRectangle(Rectangle rectangle)
        {
            Vector2 topLeft = Vector2.Transform(new Vector2(rectangle.X, rectangle.Y), Main.UIScaleMatrix);
            Vector2 bottomRight = Vector2.Transform(new Vector2(rectangle.Right, rectangle.Bottom), Main.UIScaleMatrix);
            return new Rectangle(
                (int)topLeft.X,
                (int)topLeft.Y,
                Math.Max(1, (int)(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)(bottomRight.Y - topLeft.Y)));
        }

        private sealed class AiChatConfigInputDisplay : UIElement
        {
            private readonly AiChatLongTextConfigElement owner;

            public AiChatConfigInputDisplay(AiChatLongTextConfigElement owner)
            {
                this.owner = owner;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                base.DrawSelf(spriteBatch);
                owner.DrawInputText(spriteBatch, GetDimensions().ToRectangle());
            }
        }
    }
}
