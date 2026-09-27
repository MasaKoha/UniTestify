#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;

namespace UniTestify
{
    /// <summary>
    /// シナリオやエージェントの行動に書かれた方向の文字列を <see cref="FocusDirection"/> へ変換します。
    /// 解釈できない文字列を既定の方向へ丸めないため、成否を戻り値で返します。
    /// </summary>
    public static class FocusDirectionParser
    {
        /// <summary>
        /// 大文字小文字を区別せずに up / down / left / right を解釈します。それ以外は false を返します。
        /// </summary>
        public static bool TryParse(string text, out FocusDirection direction)
        {
            // "Down" のような大文字始まりを受け付けず、既定の方向（上）へ丸めていたため、
            // 下を押したつもりの検証が上を押していた（2026-09-27 に人形詳細で「フォーカス飛び」と誤認した）。
            switch (text?.Trim().ToLowerInvariant())
            {
                case "up":
                    direction = FocusDirection.Up;
                    return true;
                case "down":
                    direction = FocusDirection.Down;
                    return true;
                case "left":
                    direction = FocusDirection.Left;
                    return true;
                case "right":
                    direction = FocusDirection.Right;
                    return true;
                default:
                    direction = FocusDirection.None;
                    return false;
            }
        }
    }
}
#endif
