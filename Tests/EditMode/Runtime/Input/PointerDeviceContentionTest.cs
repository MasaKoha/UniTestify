#if UNITY_EDITOR
using NUnit.Framework;

namespace UniTestify.Tests
{
    /// <summary>
    /// 仮想マウスが current を奪われた状態を検出できることを検証します。
    ///
    /// 送信自体は成功しているのに UI へ届かないことがあり、成功だけを返すと
    /// 「効いていない」と区別がつかない（2026-09-20 実測で 34 回中 1 回再現）。
    /// 競合そのものを防ぐ検査ではなく、**黙って成功として返さない**ことを守る。
    ///
    /// 以前は PlayMode で実デバイスを足し外ししていたが、Input System 内部の Assert が
    /// 不定期に出て落ちた（2026-09-24 実測で 5 回中 3 回）。判定だけを名前で検査する。
    /// </summary>
    public sealed class PointerDeviceContentionTest
    {
        private const string InjectedDeviceName = "UniLabAI Mouse";
        private const string RivalDeviceName = "Rival Mouse";

        /// <summary>注入したマウスが current なら注意書きを出さない。</summary>
        [Test]
        public void 注入したマウスがcurrentなら注意書きを出さない()
        {
            Assert.That(InputInjector.DescribePointerDeviceContention(InjectedDeviceName, InjectedDeviceName), Is.Empty);
        }

        /// <summary>current が無いときは取り合いと断定できないため注意書きを出さない。</summary>
        [TestCase(null)]
        [TestCase("")]
        public void currentが無ければ注意書きを出さない(string currentDeviceName)
        {
            Assert.That(InputInjector.DescribePointerDeviceContention(InjectedDeviceName, currentDeviceName), Is.Empty);
        }

        /// <summary>
        /// 別のマウスが current を奪ったら、奪ったデバイスと注入したデバイスを名指しで注意書きに出す。
        ///
        /// これが出ないと、送信は成功・画面は変わらない、という状態を呼び出し側が区別できない。
        /// </summary>
        [Test]
        public void 別のマウスがcurrentを奪ったら名指しで注意書きを出す()
        {
            var warning = InputInjector.DescribePointerDeviceContention(InjectedDeviceName, RivalDeviceName);

            Assert.That(warning, Does.Contain(RivalDeviceName), "どのデバイスが奪ったかを示していない。");
            Assert.That(warning, Does.Contain(InjectedDeviceName), "どのデバイスが奪われたかを示していない。");
        }
    }
}
#endif
