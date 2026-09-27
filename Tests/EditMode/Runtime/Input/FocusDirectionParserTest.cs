#if UNITY_EDITOR
using NUnit.Framework;

namespace UniTestify.Tests
{
    /// <summary>
    /// シナリオの方向の文字列が、書き方の揺れで別の方向に化けないことを検証します。
    ///
    /// 以前は大文字始まりの "Down" を解釈できず、既定の方向（上）を押していた。
    /// 下を押したつもりの検証が上を押し、正しいフォーカス移動を不具合と誤認した（2026-09-27）。
    /// </summary>
    public sealed class FocusDirectionParserTest
    {
        /// <summary>大文字小文字と前後の空白に関わらず、4 方向を解釈する。</summary>
        [TestCase("up", FocusDirection.Up)]
        [TestCase("Down", FocusDirection.Down)]
        [TestCase("LEFT", FocusDirection.Left)]
        [TestCase(" right ", FocusDirection.Right)]
        public void 書き方の揺れがあっても方向を解釈する(string text, FocusDirection expected)
        {
            Assert.That(FocusDirectionParser.TryParse(text, out var direction), Is.True);
            Assert.That(direction, Is.EqualTo(expected));
        }

        /// <summary>解釈できない文字列を、どこかの方向へ丸めずに失敗として返す。</summary>
        [TestCase("")]
        [TestCase(null)]
        [TestCase("downward")]
        [TestCase("上")]
        public void 解釈できない方向は失敗として返す(string text)
        {
            Assert.That(FocusDirectionParser.TryParse(text, out var direction), Is.False);
            Assert.That(direction, Is.EqualTo(FocusDirection.None));
        }
    }
}
#endif
