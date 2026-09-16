#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && ENABLE_INPUT_SYSTEM
using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace UniTestify.Tests
{
    /// <summary>選択と入力受付が異なる状態を再現し、文字の到達と両呼び出し元への失敗伝播を検証します。</summary>
    public sealed class InputInjectorTextTest
    {
        private const string InitialValue = "Aria";
        private const string InjectedText = "New";
        private const int TestFontSize = 24;

        private GameObject _root;
        private EventSystem _eventSystem;
        private EventSystem _previousEventSystem;
        private TMP_InputField _inputField;
        private InputSettings.BackgroundBehavior _previousBackgroundBehavior;
        private InputSettings.EditorInputBehaviorInPlayMode _previousEditorInputBehavior;
        private string _sessionDirectory;
        private bool _ownsInputState;

        /// <summary>実際の入力モジュールを使い、Game View の状態と既存シーンの選択をテストから隔離します。</summary>
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(AgentSessionCommands.HasSession, Is.False, "既存の手動セッションを終了してから実行してください。");
            Assert.That(Resources.Load<TMP_Settings>("TMP Settings"), Is.Not.Null,
                "実際の TMP 入力欄を検証するため、実行プロジェクトへ TMP Essential Resources をインポートしてください。");
            Assert.That(TMP_Settings.defaultFontAsset, Is.Not.Null, "TMP Settings に既定フォントを設定してください。");
            _previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            _previousEditorInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            _previousEventSystem = EventSystem.current;
            _ownsInputState = true;
            InputInjector.Dispose();
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            _root = new GameObject(nameof(InputInjectorTextTest));
            _root.SetActive(false);
            var eventSystemObject = new GameObject("EventSystem");
            eventSystemObject.transform.SetParent(_root.transform, false);
            _eventSystem = eventSystemObject.AddComponent<EventSystem>();
            var inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();
            inputModule.AssignDefaultActions();
            CreateInputField();
            _root.SetActive(true);
            EventSystem.current = _eventSystem;
            yield return null;

            _eventSystem.SetSelectedGameObject(_inputField.gameObject);
            yield return null;
            Assert.That(_inputField.isFocused, Is.False, "EventSystem の選択だけを再現できていません。");
        }

        /// <summary>仮想デバイス・設定・描画資源が後続のテストへ残るのを防ぎます。</summary>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsInputState)
            {
                yield break;
            }

            if (!string.IsNullOrEmpty(_sessionDirectory))
            {
                AgentSessionCommands.End();
                Directory.Delete(_sessionDirectory, true);
                _sessionDirectory = null;
            }

            InputInjector.Dispose();
            Object.Destroy(_root);
            EventSystem.current = _previousEventSystem;
            yield return null;

            InputSystem.settings.backgroundBehavior = _previousBackgroundBehavior;
            InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorInputBehavior;
            _ownsInputState = false;
        }

        /// <summary>未受付の選択済み入力欄を有効化してから送信し、途中で完了を通知しません。</summary>
        [UnityTest]
        public IEnumerator ActivatesSelectedInputAndChangesValueBeforeCompletion()
        {
            RequireGameViewFocus();
            string failureMessage = null;
            using (var execution = InputInjector.Text(InjectedText, message => failureMessage = message))
            {
                Assert.That(execution.MoveNext(), Is.True);
                Assert.That(failureMessage, Is.Null);
                Assert.That(_inputField.text, Is.EqualTo(InitialValue));
                yield return execution.Current;
                Assert.That(_inputField.isFocused, Is.True);
                while (execution.MoveNext())
                {
                    yield return execution.Current;
                }
            }

            Assert.That(failureMessage, Is.Empty);
            Assert.That(_inputField.text, Is.EqualTo(InjectedText));
        }

        /// <summary>入力が拒否されて値が変わらない場合を成功として返しません。</summary>
        [UnityTest]
        public IEnumerator ReadOnlyInputReportsTextUnchanged()
        {
            _inputField.readOnly = true;
            string failureMessage = null;
            yield return InputInjector.Text(InjectedText, message => failureMessage = message);

            Assert.That(_inputField.text, Is.EqualTo(InitialValue));
            Assert.That(failureMessage, Does.StartWith("textUnchanged:"));
        }

        /// <summary>受付状態でも文字検証で全入力が拒否されたときは未反映として報告します。</summary>
        [UnityTest]
        public IEnumerator RejectedCharactersReportTextUnchanged()
        {
            _inputField.onValidateInput = (text, characterIndex, character) => '\0';
            _inputField.onFocusSelectAll = false;
            string failureMessage = null;
            yield return InputInjector.Text(InjectedText, message => failureMessage = message);

            Assert.That(_inputField.text, Is.EqualTo(InitialValue));
            Assert.That(failureMessage, Does.StartWith("textUnchanged:"));
        }

        /// <summary>空文字と null は無操作なので、値が変わらなくても失敗にしません。</summary>
        [UnityTest]
        public IEnumerator EmptyTextDoesNotActivateOrFail()
        {
            foreach (var text in new[] { string.Empty, null })
            {
                string failureMessage = null;
                yield return InputInjector.Text(text, message => failureMessage = message);
                Assert.That(failureMessage, Is.Empty);
                Assert.That(_inputField.text, Is.EqualTo(InitialValue));
                Assert.That(_inputField.isFocused, Is.False);
            }
        }

        /// <summary>TMP 以外の選択と選択なしでは、従来の TextEvent 送信をそのまま維持します。</summary>
        [UnityTest]
        public IEnumerator NonInputSelectionStillReceivesTextEvents()
        {
            var buttonObject = new GameObject("Button", typeof(RectTransform), typeof(Button));
            buttonObject.transform.SetParent(_root.transform, false);
            var receivedText = new StringBuilder();
            Keyboard observedKeyboard = null;
            void ReceiveCharacter(char character) => receivedText.Append(character);
            void ObserveKeyboard(InputDevice device, InputDeviceChange change)
            {
                if (change == InputDeviceChange.Added && device is Keyboard keyboard)
                {
                    observedKeyboard = keyboard;
                    keyboard.onTextInput += ReceiveCharacter;
                }
            }

            InputSystem.onDeviceChange += ObserveKeyboard;
            try
            {
                foreach (var selectedObject in new[] { buttonObject, null })
                {
                    _eventSystem.SetSelectedGameObject(selectedObject);
                    receivedText.Clear();
                    string failureMessage = null;
                    yield return InputInjector.Text(InjectedText, message => failureMessage = message);
                    Assert.That(failureMessage, Is.Empty);
                    Assert.That(receivedText.ToString(), Is.EqualTo(InjectedText));
                    Assert.That(_inputField.text, Is.EqualTo(InitialValue));
                }
            }
            finally
            {
                InputSystem.onDeviceChange -= ObserveKeyboard;
                if (observedKeyboard != null)
                {
                    observedKeyboard.onTextInput -= ReceiveCharacter;
                }
            }
        }

        /// <summary>共通処理の失敗がシナリオの addFailure に一度だけ届くことを検証します。</summary>
        [UnityTest]
        public IEnumerator ScenarioReportsUnchangedTextAsFailure()
        {
            _inputField.readOnly = true;
            var failureCount = 0;
            var executor = new ScenarioInputExecutor();
            yield return executor.ExecuteInputCoroutine(new UiScenarioStep { text = InjectedText },
                (kind, target, button, message, detail) =>
                {
                    failureCount++;
                    Assert.That(kind, Is.EqualTo("text"));
                    Assert.That(message, Does.StartWith("textUnchanged:"));
                });

            Assert.That(failureCount, Is.EqualTo(1));
        }

        /// <summary>エージェントの失敗応答と行動履歴が、入力完了後の検証結果と一致することを検証します。</summary>
        [UnityTest]
        public IEnumerator AgentReturnsFailureAndRecordsTextUnchanged()
        {
            BeginAgentSession();
            _inputField.readOnly = true;
            AgentCommandResult response = null;
            var completionCount = 0;
            using (var execution = AgentSessionCommands.ActAsync(new AgentAction { text = InjectedText }, result =>
            {
                completionCount++;
                response = JsonUtility.FromJson<AgentCommandResult>(result);
            }))
            {
                Assert.That(execution.MoveNext(), Is.True);
                Assert.That(response, Is.Null);
                Assert.That(AgentSessionCommands.IsInputBusy(), Is.True);
                yield return execution.Current;
                while (execution.MoveNext())
                {
                    yield return execution.Current;
                }
            }

            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(response.ok, Is.False);
            Assert.That(response.message, Does.StartWith("textUnchanged:"));
            Assert.That(response.text, Does.Contain("status=rejected"));
            Assert.That(AgentSessionCommands.IsInputBusy(), Is.False);
            var actionLog = File.ReadAllText(Path.Combine(_sessionDirectory, "actions.jsonl"));
            Assert.That(actionLog, Does.Contain("textUnchanged:"));
            Assert.That(actionLog, Does.Not.Contain("\"status\":\"acted\""));
        }

        /// <summary>正常な text は値変更後に一度だけ成功を返します。</summary>
        [UnityTest]
        public IEnumerator AgentReturnsSuccessAfterValueChanges()
        {
            RequireGameViewFocus();
            BeginAgentSession();
            AgentCommandResult response = null;
            yield return AgentSessionCommands.ActAsync(new AgentAction { text = InjectedText },
                result => response = JsonUtility.FromJson<AgentCommandResult>(result));

            Assert.That(response.ok, Is.True);
            Assert.That(_inputField.text, Is.EqualTo(InjectedText));
            Assert.That(response.text, Does.Contain("status=running"));
        }

        /// <summary>同期の TMP 入力では、確認できない成功を返さず入力前に拒否します。</summary>
        [Test]
        public void SynchronousTextRequiresAsyncWithoutSending()
        {
            BeginAgentSession();
            var response = JsonUtility.FromJson<AgentCommandResult>(AgentSessionCommands.Act("{\"text\":\"New\"}"));

            Assert.That(response.ok, Is.False);
            Assert.That(response.message, Is.EqualTo(AgentActionExecutor.TextRequiresAsyncMessage));
            Assert.That(_inputField.text, Is.EqualTo(InitialValue));
            Assert.That(_inputField.isFocused, Is.False);
            Assert.That(AgentSessionCommands.IsInputBusy(), Is.False);
        }

        /// <summary>最上位の応答にも失敗を伝え、一括要求の後続ステップを実行しません。</summary>
        [UnityTest]
        public IEnumerator AgentGatewayStopsFollowingStepsAfterTextUnchanged()
        {
            BeginAgentSession();
            _inputField.readOnly = true;
            RequireGameViewFocus();
            yield return null;

            AiCommandResponse response = null;
            yield return AiCommandDispatcher.ExecuteAsync(new AiCommandRequest
            {
                op = "agent.act",
                args = "{\"steps\":[{\"text\":\"New\"},{\"text\":\"Following\"}]}",
            }, result => response = result);

            Assert.That(response.ok, Is.False);
            Assert.That(response.message, Does.StartWith("textUnchanged:"));
            Assert.That(File.ReadAllLines(Path.Combine(_sessionDirectory, "actions.jsonl")), Has.Length.EqualTo(1));
        }

        private void BeginAgentSession()
        {
            Assert.That(AgentSessionCommands.HasSession, Is.False, "既存の手動セッションを破棄しないため、終了してから実行してください。");
            var response = JsonUtility.FromJson<AgentCommandResult>(AgentSessionCommands.Begin("{\"freePlay\":true}", "{}"));
            Assert.That(response.ok, Is.True, response.message);
            _sessionDirectory = response.path;
        }

        private void CreateInputField()
        {
            var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.transform.SetParent(_root.transform, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var inputObject = new GameObject("Input", typeof(RectTransform), typeof(Image));
            inputObject.transform.SetParent(canvasObject.transform, false);
            var textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(inputObject.transform, false);
            var textComponent = textObject.AddComponent<TextMeshProUGUI>();
            textComponent.font = TMP_Settings.defaultFontAsset;
            textComponent.fontSize = TestFontSize;

            _inputField = inputObject.AddComponent<TMP_InputField>();
            _inputField.textViewport = inputObject.GetComponent<RectTransform>();
            _inputField.textComponent = textComponent;
            _inputField.targetGraphic = inputObject.GetComponent<Image>();
            _inputField.shouldActivateOnSelect = false;
            _inputField.onFocusSelectAll = true;
            _inputField.text = InitialValue;
        }

        /// <summary>
        /// Game View が前面でないと TextEvent が届かない。環境の都合であって実装の失敗ではないため、
        /// 失敗ではなく判定不能として扱う（無人実行・CI で赤にしない）。
        /// </summary>
        private static void RequireGameViewFocus()
        {
            AiPlayModeViewFocus.TryFocus("game");
            if (!InputInjector.IsFocusDependentInputAvailable)
            {
                Assert.Inconclusive("この経路の検証には Game View のフォーカスが必要です。");
            }
        }

    }
}
#endif
