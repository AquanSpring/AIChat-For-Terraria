using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using AiChat.Common.Config;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace AiChat.UI
{
    [Autoload(Side = ModSide.Client)]
    internal sealed class AiChatUISystem : ModSystem
    {
        private UserInterface userInterface;
        private AiChatPanelState panelState;
        private ModKeybind toggleKeybind;
        private On_Main.orig_DrawInterface_36_Cursor deferredCursorDraw;
        private bool deferCursorDraw;

        internal static AiChatUISystem Instance { get; private set; }
        internal AiChatPanelState Panel => panelState;

        public override void Load()
        {
            if (Main.dedServ) return;
            Instance = this;
            toggleKeybind = KeybindLoader.RegisterKeybind(ModContent.GetInstance<AiChat>(), "ToggleChat", Keys.F8);
            userInterface = new UserInterface();
            panelState = new AiChatPanelState(this);
            panelState.Activate();
            TextInputEXT.TextInput += HandleTextInput;
            On_Main.DrawInterface += DrawInterfaceAfterVanilla;
            On_Main.DrawInterface_36_Cursor += DeferCursorUntilAfterAiChat;
        }

        public override void Unload()
        {
            if (Main.dedServ) return;
            TextInputEXT.StopTextInput();
            TextInputEXT.TextInput -= HandleTextInput;
            On_Main.DrawInterface_36_Cursor -= DeferCursorUntilAfterAiChat;
            On_Main.DrawInterface -= DrawInterfaceAfterVanilla;
            panelState = null; userInterface = null; toggleKeybind = null;
            deferredCursorDraw = null; deferCursorDraw = false; Instance = null;
        }

        public override void UpdateUI(GameTime gameTime)
        {
            if (userInterface?.CurrentState != null) userInterface.Update(gameTime);
        }

        public override void PostUpdateInput()
        {
            if (Main.gameMenu || panelState == null) return;
            bool isOpen = userInterface?.CurrentState != null;
            if (toggleKeybind?.JustPressed == true && (!isOpen || !panelState.InputFocused))
            {
                if (isOpen) Close(); else Open();
                return;
            }
            if (isOpen)
            {
                if (Main.LocalPlayer != null)
                    Main.LocalPlayer.mouseInterface = true;
                Main.blockMouse = true;
                panelState.CaptureTextInput();
                Main.chatRelease = false;
                Main.npcChatRelease = false;
            }
        }

        internal void Open()
        {
            if (Main.dedServ || panelState == null) return;
            panelState.ResetForOpen();
            userInterface.SetState(panelState);
            Main.clrInput();
        }

        internal void Close()
        {
            PlayerInput.WritingText = false;
            TextInputEXT.StopTextInput();
            userInterface?.SetState(null);
        }

        private void HandleTextInput(char character)
        {
            if (userInterface?.CurrentState == panelState) panelState.QueueTextInput(character);
            AiChatLongTextConfigElement.QueueTextInputToActive(character);
        }

        private void DrawInterfaceAfterVanilla(On_Main.orig_DrawInterface original, Main self, GameTime gameTime)
        {
            UserInterface activeInterface = userInterface;
            bool drawAiChat = activeInterface?.CurrentState != null;
            deferredCursorDraw = null; deferCursorDraw = drawAiChat;
            try { original(self, gameTime); } finally { deferCursorDraw = false; }
            if (!drawAiChat) return;
            bool batchStarted = false; PlayerInput.SetZoom_UI();
            try
            {
                Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
                batchStarted = true; activeInterface.Draw(Main.spriteBatch, gameTime); deferredCursorDraw?.Invoke();
            }
            finally
            {
                if (batchStarted) Main.spriteBatch.End();
                PlayerInput.SetZoom_World(); deferredCursorDraw = null;
            }
        }

        private void DeferCursorUntilAfterAiChat(On_Main.orig_DrawInterface_36_Cursor original)
        {
            if (deferCursorDraw) { deferredCursorDraw = original; return; }
            original();
        }

        internal static void SetConversationSnapshots(IEnumerable<AiChatConversationSnapshot> snapshots) => Instance?.panelState?.SetConversationSnapshots(snapshots);
        internal static void SetMessages(string conversationId, IEnumerable<AiChatMessageSnapshot> messages) => Instance?.panelState?.SetMessages(conversationId, messages);
        internal static void SetStatus(string conversationId, string statusKey, AiChatStatusKind kind) => Instance?.panelState?.SetStatus(conversationId, statusKey, kind);
        internal static void SetProviderOptions(IEnumerable<string> providers, IEnumerable<string> models) => Instance?.panelState?.SetProviderOptions(providers, models);
    }

    internal sealed class AiChatConversationSnapshot
    {
        internal string Id { get; set; }
        internal string Title { get; set; }
        internal string Provider { get; set; }
        internal string Model { get; set; }
    }

    internal sealed class AiChatMessageSnapshot
    {
        internal string Role { get; set; }
        internal string Content { get; set; }
    }
}
