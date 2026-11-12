using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Zeichnet den Settings-Tooltip in einem eigenen OnGUI mit niedriger GUI.depth, also UEBER dem Fenster des
    /// Mod-Managers. Aussehen wie in LongerLoadingDelay: 460 px breit, Hintergrund schwarz 92 %, 2 px gelbe Linie
    /// oben, Text mit Zeilenumbruch und Rich-Text (fetter Titel), Padding 10/8, rechts der Maus, sonst links davon.
    /// </summary>
    internal sealed class TooltipOverlay : MonoBehaviour
    {
        private const float Width = 460f;
        private static string _text;
        private static Vector2 _screenPos;
        private static int _frame = -10;
        private static GUIStyle _style;

        /// <summary>Aus Settings.Draw (Repaint) - Mausposition in Bildschirm-GUI-Koordinaten.</summary>
        public static void Show(string text, Vector2 screenPos)
        {
            _text = text;
            _screenPos = screenPos;
            _frame = Time.frameCount;
        }

        private void OnGUI()
        {
            GUI.depth = -10000;   // vor allen anderen GUIs, auch vor dem Mod-Manager
            if (Event.current.type != EventType.Repaint) return;
            if (string.IsNullOrEmpty(_text) || Time.frameCount - _frame > 1) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true, fontSize = 13, padding = new RectOffset(10, 10, 8, 8) };
                _style.normal.textColor = Color.white;
            }

            var content = new GUIContent(_text);
            float h = _style.CalcHeight(content, Width);
            float x = _screenPos.x + 18f;
            if (x + Width > Screen.width) x = _screenPos.x - Width - 18f;   // rechts kein Platz -> links von der Maus
            if (x < 0f) x = 0f;
            float y = _screenPos.y + 12f;
            if (y + h > Screen.height) y = Mathf.Max(0f, Screen.height - h);
            var rect = new Rect(x, y, Width, h);

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.92f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.8f, 0.3f, 1f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2f), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(rect, content, _style);
        }
    }
}
