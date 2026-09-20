#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && ENABLE_INPUT_SYSTEM
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace UniTestify.Tests
{
    /// <summary>
    /// 仮想マウスが current を奪われた状態を検出できることを検証します。
    ///
    /// 送信自体は成功しているのに UI へ届かないことがあり、成功だけを返すと
    /// 「効いていない」と区別がつかない（#445。2026-09-20 実測で 34 回中 1 回再現）。
    /// 競合そのものを防ぐ検査ではなく、**黙って成功として返さない**ことを守る。
    /// </summary>
    public sealed class PointerDeviceContentionTest
    {
        private const string RivalDeviceName = "Rival Mouse";
        private const string InjectedDeviceName = "UniLabAI Mouse";

        private Mouse _rival;

        [TearDown]
        public void RemoveRival()
        {
            if (_rival != null && _rival.added)
            {
                InputSystem.RemoveDevice(_rival);
            }

            _rival = null;
        }

        /// <summary>
        /// 仮想マウスを用意し、実在することまで確かめる。
        ///
        /// 入力の送信はしない。送信経路を通すとフォーカスに依存し、
        /// 非フォーカスの環境では検査が空振りしたまま緑になる。
        /// </summary>
        private static IEnumerator InjectPointer()
        {
            var name = InputInjector.EnsurePointerDeviceForDiagnostics();
            Assert.That(name, Is.EqualTo(InjectedDeviceName), "仮想マウスの名前が想定と違う。");
            yield return null;

            var injected = false;
            foreach (var device in InputSystem.devices)
            {
                if (device is Mouse mouse && mouse.name == InjectedDeviceName && mouse.added)
                {
                    injected = true;
                    break;
                }
            }

            Assert.That(injected, Is.True, "仮想マウスが InputSystem へ登録されていない。");
        }

        /// <summary>注入した直後は仮想マウスが current なので、注意書きを出さない。</summary>
        [UnityTest]
        public IEnumerator 注入直後は注意書きを出さない()
        {
            yield return InjectPointer();

            Assert.That(InputInjector.DescribePointerDeviceContention(), Is.Empty,
                "仮想マウスが current なのに注意書きが出ている。");
        }

        /// <summary>
        /// 別のマウスが current を奪ったら注意書きを出す。
        ///
        /// これが出ないと、送信は成功・画面は変わらない、という状態を呼び出し側が区別できない。
        /// </summary>
        [UnityTest]
        public IEnumerator 別のマウスがcurrentを奪ったら注意書きを出す()
        {
            yield return InjectPointer();
            Assert.That(InputInjector.DescribePointerDeviceContention(), Is.Empty, "前提が崩れている。");

            // 物理マウスが動いた状況を、別デバイスを current にして再現する。
            _rival = InputSystem.AddDevice<Mouse>(RivalDeviceName);
            _rival.MakeCurrent();
            yield return null;

            var warning = InputInjector.DescribePointerDeviceContention();
            Assert.That(warning, Is.Not.Empty, "current を奪われているのに注意書きが出ていない。");
            Assert.That(warning, Does.Contain(RivalDeviceName), "どのデバイスが奪ったかを示していない。");
        }
    }
}
#endif
