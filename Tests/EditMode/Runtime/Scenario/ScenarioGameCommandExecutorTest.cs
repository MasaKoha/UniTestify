#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UniTestify.Tests
{
    /// <summary>資産付与の未実行を成功扱いしないことと、JSON からの引数受け渡しを検証する。</summary>
    public sealed class ScenarioGameCommandExecutorTest
    {
        /// <summary>実際のシナリオ形式から読み込んだコマンドを、一度だけ実行する。</summary>
        [Test]
        public void DeserializedCommandPassesArgumentsToHandler()
        {
            const string json = "{\"steps\":[{\"gameCommand\":\"addCorePoints\",\"gameArguments\":[\"amount=200\"]}]}";
            var scenario = JsonUtility.FromJson<UiScenario>(json);
            UiScenarioJsonPresence.Apply(json, scenario);
            var handler = new RecordingHandler();

            var succeeded = ScenarioGameCommandExecutor.TryExecute(scenario.steps[0], handler, out _);

            Assert.That(succeeded, Is.True);
            Assert.That(scenario.steps[0].monkey, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(1));
            Assert.That(handler.Command, Is.EqualTo("addCorePoints"));
            Assert.That(handler.Arguments["amount"], Is.EqualTo("200"));
        }

        /// <summary>引数不要の save などには空の辞書を渡す。</summary>
        [Test]
        public void CommandWithoutArgumentsPassesEmptyDictionary()
        {
            var handler = new RecordingHandler();
            Assert.That(ScenarioGameCommandExecutor.TryExecute(new UiScenarioStep { gameCommand = "save" }, handler, out _), Is.True);
            Assert.That(handler.Arguments, Is.Empty);
        }

        /// <summary>ハンドラ未登録は、何もせず成功するステップにしない。</summary>
        [Test]
        public void MissingHandlerFails()
        {
            Assert.That(ScenarioGameCommandExecutor.TryExecute(new UiScenarioStep { gameCommand = "save" }, null, out var message), Is.False);
            Assert.That(message, Does.Contain("登録されていません"));
        }

        /// <summary>不正な引数を見つけたら、部分的にも状態を変更しない。</summary>
        [TestCase(null)]
        [TestCase("amount")]
        [TestCase("=200")]
        [TestCase("amount=")]
        [TestCase(" =200")]
        [TestCase("amount= ")]
        public void InvalidArgumentDoesNotCallHandler(string argument)
        {
            var handler = new RecordingHandler();
            var step = new UiScenarioStep { gameCommand = "addCorePoints", gameArguments = new[] { argument } };
            Assert.That(ScenarioGameCommandExecutor.TryExecute(step, handler, out _), Is.False);
            Assert.That(handler.CallCount, Is.Zero);
        }

        /// <summary>重複した引数の後勝ち・前勝ちで付与量を変えない。</summary>
        [Test]
        public void DuplicateArgumentsDoNotCallHandler()
        {
            var handler = new RecordingHandler();
            var step = new UiScenarioStep { gameCommand = "addCorePoints", gameArguments = new[] { "amount=200", "amount=300" } };
            Assert.That(ScenarioGameCommandExecutor.TryExecute(step, handler, out _), Is.False);
            Assert.That(handler.CallCount, Is.Zero);
        }

        /// <summary>ハンドラ自身の失敗と理由をランナーへ返す。</summary>
        [Test]
        public void HandlerFailureIsPreserved()
        {
            var handler = new RecordingHandler { Succeeds = false };
            Assert.That(ScenarioGameCommandExecutor.TryExecute(new UiScenarioStep { gameCommand = "unknown" }, handler, out var message), Is.False);
            Assert.That(handler.CallCount, Is.EqualTo(1));
            Assert.That(message, Is.EqualTo("コマンド実行失敗"));
        }

        /// <summary>入力と資産操作を混ぜたステップで、片方だけが黙って無視されるのを防ぐ。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void MixedInputDoesNotCallHandler(bool useSubmit)
        {
            var handler = new RecordingHandler();
            var step = new UiScenarioStep { gameCommand = "save" };
            if (useSubmit)
            {
                step.submit = "SortieButton";
            }
            else
            {
                step.press = "south";
            }
            Assert.That(ScenarioGameCommandExecutor.TryExecute(step, handler, out _), Is.False);
            Assert.That(handler.CallCount, Is.Zero);
        }

        /// <summary>操作後の waitScene を操作前の待機へ変換しない。コマンド名を UI として探さない。</summary>
        [Test]
        public void CommandIsAnActionButNotAUiTarget()
        {
            var step = new UiScenarioStep { gameCommand = "save", waitScene = "Home" };
            Assert.That(UiScenarioStepReader.HasAnyAction(step), Is.True);
            Assert.That(UiScenarioStepReader.GetPrimaryTarget(step), Is.Empty);
            Assert.That(UiScenarioStepReader.CreateAnchor(step).waitForScene, Is.Null.Or.Empty);
            Assert.That(UiScenarioStepReader.CreateActionLabel(step), Is.EqualTo("game:save"));
            Assert.That(UiScenarioStepReader.GetStepFailureTarget(step), Is.EqualTo("save"));
        }

        private sealed class RecordingHandler : IGameCommandHandler
        {
            public IReadOnlyList<string> CommandNames => Array.Empty<string>();
            public bool Succeeds { get; set; } = true;
            public int CallCount { get; private set; }
            public string Command { get; private set; }
            public IReadOnlyDictionary<string, string> Arguments { get; private set; }

            public bool TryExecute(string commandName, IReadOnlyDictionary<string, string> arguments, out string message)
            {
                CallCount++;
                Command = commandName;
                Arguments = arguments;
                message = Succeeds ? "実行済み" : "コマンド実行失敗";
                return Succeeds;
            }
        }
    }
}
#endif
