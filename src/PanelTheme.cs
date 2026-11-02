using System.Collections.Generic;
using UnityEngine;

namespace FarmProgress
{
    public sealed class PanelTheme
    {
        readonly List<Texture2D> textures = new List<Texture2D>();
        public GUISkin Skin { get; private set; }
        public GUIStyle MainWindow { get; private set; }
        public static GUIStyle Title, Heading, Muted, Card, Primary, Nav, NavSelected, Stat;
        public static readonly Color Mint = new Color(0.43f, 0.88f, 0.70f);
        Texture2D Texture(Color color)
        {
            var texture = new Texture2D(1, 1); texture.SetPixel(0, 0, color); texture.Apply(); textures.Add(texture); return texture;
        }
        public void Ensure(Font font)
        {
            if (Skin != null) return;
            Skin = Object.Instantiate(GUI.skin); Skin.font = font;
            var baseBg = Texture(new Color(0.035f, 0.055f, 0.075f, 0.99f));
            var cardBg = Texture(new Color(0.065f, 0.095f, 0.12f));
            var hover = Texture(new Color(0.11f, 0.18f, 0.21f));
            var fieldBg = Texture(new Color(0.025f, 0.045f, 0.06f));
            var selected = Texture(new Color(0.12f, 0.28f, 0.25f));
            Skin.label.fontSize = 15; Skin.label.wordWrap = true; Skin.label.normal.textColor = new Color(0.89f, 0.94f, 0.96f);
            Skin.label.margin = new RectOffset(0, 0, 3, 3);
            Skin.button.fontSize = 14; Skin.button.wordWrap = true; Skin.button.padding = new RectOffset(12, 12, 8, 8);
            Skin.button.margin = new RectOffset(3, 3, 3, 3); Skin.button.border = new RectOffset();
            Skin.button.normal.background = cardBg; Skin.button.hover.background = hover; Skin.button.active.background = selected;
            Skin.button.normal.textColor = Skin.button.hover.textColor = Skin.button.active.textColor = Color.white;
            Skin.button.onNormal.background = selected; Skin.button.onHover.background = selected;
            Skin.button.onNormal.textColor = Skin.button.onHover.textColor = Mint;
            Skin.toggle.fontSize = 15; Skin.toggle.wordWrap = true; Skin.toggle.normal.textColor = Color.white;
            Skin.toggle.onNormal.textColor = Skin.toggle.onHover.textColor = Mint; Skin.toggle.margin = new RectOffset(2, 2, 6, 6);
            Skin.textField.fontSize = 15; Skin.textField.padding = new RectOffset(10, 10, 8, 8);
            Skin.textField.normal.background = fieldBg; Skin.textField.focused.background = selected;
            Skin.textField.normal.textColor = Skin.textField.focused.textColor = Color.white;
            Skin.textField.border = new RectOffset();
            Skin.window.normal.background = baseBg; Skin.window.onNormal.background = baseBg; Skin.window.border = new RectOffset();
            Skin.window.padding = new RectOffset(18, 18, 28, 12); Skin.window.fontSize = 15; Skin.window.normal.textColor = Color.white;
            MainWindow = new GUIStyle(Skin.window) { padding = new RectOffset(18, 18, 14, 12) };
            Skin.horizontalSlider.normal.background = fieldBg; Skin.horizontalSliderThumb.normal.background = hover;
            Skin.horizontalSliderThumb.hover.background = selected; Skin.horizontalSliderThumb.active.background = selected;
            Skin.verticalScrollbar.normal.background = baseBg; Skin.verticalScrollbarThumb.normal.background = hover;
            Title = new GUIStyle(Skin.label) { fontSize = 25, fontStyle = FontStyle.Bold, wordWrap = false };
            Heading = new GUIStyle(Skin.label) { fontSize = 17, fontStyle = FontStyle.Bold }; Heading.normal.textColor = Mint;
            Muted = new GUIStyle(Skin.label) { fontSize = 12 }; Muted.normal.textColor = new Color(0.60f, 0.70f, 0.76f);
            Card = new GUIStyle(Skin.box) { padding = new RectOffset(14, 14, 12, 12), margin = new RectOffset(0, 0, 0, 12), border = new RectOffset() };
            Card.normal.background = cardBg;
            Primary = new GUIStyle(Skin.button) { fontStyle = FontStyle.Bold }; Primary.normal.background = selected; Primary.normal.textColor = Mint;
            Nav = new GUIStyle(Skin.button) { alignment = TextAnchor.MiddleLeft, fixedHeight = 44, fontSize = 15 };
            NavSelected = new GUIStyle(Nav); NavSelected.normal.background = selected; NavSelected.normal.textColor = Mint;
            Stat = new GUIStyle(Skin.button) { fixedHeight = 92, fontSize = 23, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }
        public static void BeginSection(string title, string subtitle = null)
        {
            GUILayout.BeginVertical(Card);
            if (!string.IsNullOrEmpty(title)) GUILayout.Label(title, Heading);
            if (!string.IsNullOrEmpty(subtitle)) GUILayout.Label(subtitle, Muted);
            GUILayout.Space(5);
        }
        public static void EndSection() { GUILayout.EndVertical(); }
        public void Dispose()
        {
            foreach (var texture in textures) Object.Destroy(texture);
            textures.Clear(); if (Skin != null) Object.Destroy(Skin); Skin = null;
        }
    }
}
