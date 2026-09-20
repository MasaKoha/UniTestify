using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
#if ENABLE_INPUT_SYSTEM
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
#endif

namespace UniTestify
{
    /// <summary>
    /// 実機と同じ Input System 経路へ流し込むことで、UI だけでなくゲーム側の入力解決そのものを検証するための注入器です。
    /// </summary>
    public static class InputInjector
    {
        private const string PointerInputView = "game";

        /// <summary>自動復旧後も入力を届けられない場合に、原因と次の操作を同じ文面で伝えます。</summary>
        private const string FocusDependentInputFailureMessage =
            "Game View のフォーカス取得を試みましたが、1 フレーム待っても非フォーカスのため、入力を送信しませんでした。Unity アプリ自体が背面にある可能性があります。Unity を前面にして Game View にフォーカスを合わせてから再実行してください。";

        private const string TextUnchangedFailureMessage = "textUnchanged: 選択中の TMP_InputField の値が文字送信後も変わりませんでした。";

#if ENABLE_INPUT_SYSTEM
        private const string GamepadDeviceName = "UniLabAI Gamepad";
        private const string KeyboardDeviceName = "UniLabAI Keyboard";
        private const string MouseDeviceName = "UniLabAI Mouse";
        private const string TouchscreenDeviceName = "UniLabAI Touchscreen";
        private const float DefaultClickFrameDelaySeconds = 0.0f;

        private static readonly HashSet<Key> PressedKeys = new HashSet<Key>();

        private static Gamepad _gamepad;
        private static Keyboard _keyboard;
        private static Mouse _mouse;
        private static Touchscreen _touchscreen;
        private static InputInjectorDriver _driver;
        private static GamepadState _gamepadState;
        private static MouseState _mouseState;
#endif

        /// <summary>
        /// Input System が無いプロジェクトでも呼び出し側を分岐できるようにするための対応可否です。
        /// </summary>
        public static bool IsSupported
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Editor の既定の Input System 設定では Game View 非フォーカス時にポインタ入力の UI への伝播が捨てられるため、
        /// 送出前に到達可否を確認します。実機ではこの Editor 固有の制約がないため常に true を返します。
        /// </summary>
        /// <summary>
        /// Input System の既定 PointersAndKeyboardsRespectGameViewFocus では、
        /// ポインタ「と キーボード」が Game View のフォーカスを要求する。
        /// 非フォーカスでは Keyboard.current にも届かない。ゲームパッドだけは対象外。
        /// </summary>
        public static bool IsFocusDependentInputAvailable
        {
            get
            {
#if UNITY_EDITOR
                return Application.isFocused;
#else
                return true;
#endif
            }
        }

        /// <summary>非フォーカス時だけ復旧を試み、フレーム反映後の失敗理由を入力の呼び出し元へ返します。</summary>
        internal static IEnumerator<object> EnsureFocusDependentInputAsync(System.Action<string> completed)
        {
            if (IsFocusDependentInputAvailable)
            {
                completed(string.Empty);
                yield break;
            }

            AiPlayModeViewFocus.TryFocus(PointerInputView);
            // Focus の戻り値は要求の成否であり、Input System が入力を受け取れる状態とは限らない。
            // perf: フォーカス反映は要求時のコルーチンで 1 フレームだけ待ち、常駐処理を増やさない。
            yield return null;
            completed(IsFocusDependentInputAvailable ? string.Empty : FocusDependentInputFailureMessage);
        }

        /// <summary>フレームを待てない同期呼び出しでもフォーカスを要求し、未送信であることを伝えます。</summary>
        internal static string RequestPointerInputFocus()
        {
            AiPlayModeViewFocus.TryFocus(PointerInputView);
            return "Game View が非フォーカスのため、フォーカス取得を要求しました。同期呼び出しでは 1 フレーム後の反映を確認できないため、ポインタ入力を送信していません。次のフレーム以降に再実行するか、復旧を待って入力するメールボックス・HTTP を利用してください。非フォーカスが続く場合は Unity アプリ自体が背面にある可能性があるため、Unity を前面にして Game View にフォーカスを合わせてください。";
        }

        /// <summary>
        /// 単打を次フレーム解放にし、wasPressedThisFrame / wasReleasedThisFrame の両方を自然に通すためのボタン入力です。
        /// </summary>
        public static void Press(GamepadButton button)
        {
#if ENABLE_INPUT_SYSTEM
            EnsureDriver();
            _driver.StartCoroutine(PressCoroutine(button));
#endif
        }

        /// <summary>
        /// 長押しは UI のホールド分岐や戻る長押しを検証するため、継続時間を明示して送ります。
        /// </summary>
        public static IEnumerator Hold(GamepadButton button, float seconds)
        {
#if ENABLE_INPUT_SYSTEM
            var gamepad = EnsureGamepad();
            SetGamepadButton(button, true);
            InputSystem.QueueStateEvent(gamepad, _gamepadState);
            InputSystem.Update();
            yield return WaitForSeconds(seconds);
            SetGamepadButton(button, false);
            InputSystem.QueueStateEvent(gamepad, _gamepadState);
            InputSystem.Update();
#else
            yield break;
#endif
        }

        /// <summary>
        /// move を D-Pad 単打へ正規化し、フォーカス移動をボタン語彙と同じ経路に乗せるための入力です。
        /// </summary>
        public static void Move(FocusDirection direction)
        {
#if ENABLE_INPUT_SYSTEM
            Press(ResolveDirectionButton(direction));
#endif
        }

        /// <summary>
        /// キー単打を次フレーム解放にし、Submit や Escape のような 1 発入力を実機同様に扱うための入力です。
        /// </summary>
        public static void Key(Key key)
        {
#if ENABLE_INPUT_SYSTEM
            EnsureDriver();
            _driver.StartCoroutine(KeyCoroutine(key));
#endif
        }

        /// <summary>
        /// スティック入力を一定時間維持し、アナログ移動や慣性付き UI を実機同様に通すための入力です。
        /// </summary>
        public static IEnumerator Stick(string axisName, float x, float y, float seconds)
        {
#if ENABLE_INPUT_SYSTEM
            var gamepad = EnsureGamepad();
            var startRealtime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startRealtime < seconds)
            {
                if (axisName == "right")
                {
                    _gamepadState.rightStick = new Vector2(x, y);
                }
                else
                {
                    _gamepadState.leftStick = new Vector2(x, y);
                }

                InputSystem.QueueStateEvent(gamepad, _gamepadState);
                InputSystem.Update();
                yield return null;
            }

            if (axisName == "right")
            {
                _gamepadState.rightStick = Vector2.zero;
            }
            else
            {
                _gamepadState.leftStick = Vector2.zero;
            }

            InputSystem.QueueStateEvent(gamepad, _gamepadState);
            InputSystem.Update();
#else
            yield break;
#endif
        }

        /// <summary>
        /// 既存の呼び出しでも入力欄の有効化と送出結果の検証を行います。
        /// </summary>
        public static IEnumerator Text(string text)
        {
            return Text(text, ReportTextInputFailure);
        }

        /// <summary>同期入口が値変更を検証できない入力欄だけを、未送信の失敗として扱うために使います。</summary>
        internal static bool RequiresTextInputVerification
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return GetSelectedTextInputField() != null;
#else
                return false;
#endif
            }
        }

        /// <summary>選択だけでは文字を受け取れない入力欄を有効化し、値が変わらない失敗を呼び出し元へ返します。</summary>
        public static IEnumerator<object> Text(string text, System.Action<string> completed)
        {
            if (string.IsNullOrEmpty(text))
            {
                completed(string.Empty);
                yield break;
            }

#if ENABLE_INPUT_SYSTEM
            var inputField = GetSelectedTextInputField();
            var verifiesInputField = inputField != null;
            if (verifiesInputField)
            {
                inputField.ActivateInputField();
                // TMP の入力受付状態は LateUpdate で反映されるため、送出前に一度フレームを進める。
                yield return null;
            }

            var previousValue = inputField == null ? string.Empty : inputField.text;
            var keyboard = EnsureKeyboard();
            for (var characterIndex = 0; characterIndex < text.Length; characterIndex++)
            {
                InputSystem.QueueTextEvent(keyboard, text[characterIndex]);
                InputSystem.Update();
                yield return null;
            }

            var unchanged = verifiesInputField && (inputField == null || inputField.text == previousValue);
            completed(unchanged ? TextUnchangedFailureMessage : string.Empty);
#else
            completed("Input System が有効ではありません。");
            yield break;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static TMP_InputField GetSelectedTextInputField()
        {
            // perf: text 要求時だけ探索し、常駐処理や一文字ごとの送信には探索を追加しない。
            var selectedObject = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            return selectedObject == null ? null : selectedObject.GetComponent<TMP_InputField>();
        }
#endif

        private static void ReportTextInputFailure(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                UnityEngine.Debug.LogWarning($"[InputInjector] {message}");
            }
        }

        /// <summary>
        /// ポインタ移動を先に行うことで、hover 解決や currentMouse 依存の UI を自然な順で通すための入力です。
        /// 非フォーカス時は入力の欠落を防ぐため、復旧後のフレームで送ります。
        /// </summary>
        public static void PointerMove(Vector2 screenPosition)
        {
#if ENABLE_INPUT_SYSTEM
            if (!IsFocusDependentInputAvailable)
            {
                EnsureDriver();
                _driver.StartCoroutine(PointerMoveCoroutine(screenPosition));
                return;
            }

            SendPointerMove(screenPosition);
#endif
        }

        /// <summary>
        /// クリックは移動と押下解放を 1 つの操作にまとめ、要素名指定時の JSON を簡潔に保つための入力です。
        /// </summary>
        public static void Click(Vector2 screenPosition, PointerButton button = PointerButton.Left)
        {
#if ENABLE_INPUT_SYSTEM
            EnsureDriver();
            _driver.StartCoroutine(ClickCoroutine(screenPosition, button));
#endif
        }

        /// <summary>
        /// ドラッグは押下中の軌跡が本体であり、途中フレームも送ってスクロールやスライダを実機同様に動かすための入力です。
        /// </summary>
        public static IEnumerator Drag(Vector2 from, Vector2 to, float seconds, PointerButton button = PointerButton.Left)
        {
#if ENABLE_INPUT_SYSTEM
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var mouse = EnsureMouse();
            SendPointerMove(from);
            SetMouseButton(button, true);
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();

            if (seconds <= 0.0f)
            {
                SendPointerMove(to);
            }
            else
            {
                var startRealtime = Time.realtimeSinceStartup;
                while (true)
                {
                    var elapsedSeconds = Time.realtimeSinceStartup - startRealtime;
                    var normalized = Mathf.Clamp01(elapsedSeconds / seconds);
                    SendPointerMove(Vector2.Lerp(from, to, normalized));
                    if (normalized >= 1.0f)
                    {
                        break;
                    }

                    yield return null;
                }
            }

            SetMouseButton(button, false);
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();
#else
            yield break;
#endif
        }

        /// <summary>
        /// スクロールは位置と同時に送ることで、ポインタ位置依存 UI でも対象を外さないための入力です。
        /// 非フォーカス時は入力の欠落を防ぐため、復旧後のフレームで送ります。
        /// </summary>
        public static void Scroll(Vector2 screenPosition, float amount)
        {
#if ENABLE_INPUT_SYSTEM
            if (!IsFocusDependentInputAvailable)
            {
                EnsureDriver();
                _driver.StartCoroutine(ScrollCoroutine(screenPosition, amount));
                return;
            }

            SendScroll(screenPosition, amount);
#endif
        }

        /// <summary>
        /// 1 指タップを Touchscreen へ送ることで、マウス前提コードと区別されるタッチ UI も検証できるようにします。
        /// </summary>
        public static void Tap(Vector2 screenPosition)
        {
#if ENABLE_INPUT_SYSTEM
            EnsureDriver();
            _driver.StartCoroutine(TapCoroutine(screenPosition));
#endif
        }

        /// <summary>
        /// スワイプは 1 指の軌跡を複数フレームで送り、ページ送りやスクロール判定が velocity を見ても再現できるようにします。
        /// </summary>
        public static IEnumerator Swipe(Vector2 from, Vector2 to, float seconds)
        {
#if ENABLE_INPUT_SYSTEM
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var touchscreen = EnsureTouchscreen();
            var startTime = Time.realtimeSinceStartupAsDouble;
            var previousPosition = from;
            QueueTouchState(touchscreen, 1, TouchPhase.Began, from, Vector2.zero, startTime, from);
            yield return null;

            if (seconds > 0.0f)
            {
                while (true)
                {
                    var elapsedSeconds = Time.realtimeSinceStartupAsDouble - startTime;
                    var normalized = Mathf.Clamp01((float)(elapsedSeconds / seconds));
                    var currentPosition = Vector2.Lerp(from, to, normalized);
                    QueueTouchState(touchscreen, 1, TouchPhase.Moved, currentPosition, currentPosition - previousPosition, startTime, from);
                    previousPosition = currentPosition;
                    if (normalized >= 1.0f)
                    {
                        break;
                    }

                    yield return null;
                }
            }

            QueueTouchState(touchscreen, 1, TouchPhase.Ended, to, to - previousPosition, startTime, from);
#else
            yield break;
#endif
        }

        /// <summary>
        /// ピンチは 2 指を対称に動かし、ズーム系 UI をタッチ専用の経路で検証できるようにします。
        /// </summary>
        public static IEnumerator Pinch(Vector2 center, float fromDistance, float toDistance, float seconds)
        {
#if ENABLE_INPUT_SYSTEM
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var touchscreen = EnsureTouchscreen();
            var startTime = Time.realtimeSinceStartupAsDouble;
            var currentFromDistance = fromDistance;
            var startFirst = center + Vector2.left * (fromDistance * 0.5f);
            var startSecond = center + Vector2.right * (fromDistance * 0.5f);
            QueueTouchState(touchscreen, 1, TouchPhase.Began, startFirst, Vector2.zero, startTime, startFirst);
            QueueTouchState(touchscreen, 2, TouchPhase.Began, startSecond, Vector2.zero, startTime, startSecond);
            yield return null;

            if (seconds > 0.0f)
            {
                while (true)
                {
                    var elapsedSeconds = Time.realtimeSinceStartupAsDouble - startTime;
                    var normalized = Mathf.Clamp01((float)(elapsedSeconds / seconds));
                    currentFromDistance = Mathf.Lerp(fromDistance, toDistance, normalized);
                    var first = center + Vector2.left * (currentFromDistance * 0.5f);
                    var second = center + Vector2.right * (currentFromDistance * 0.5f);
                    QueueTouchState(touchscreen, 1, TouchPhase.Moved, first, Vector2.zero, startTime, startFirst);
                    QueueTouchState(touchscreen, 2, TouchPhase.Moved, second, Vector2.zero, startTime, startSecond);
                    if (normalized >= 1.0f)
                    {
                        break;
                    }

                    yield return null;
                }
            }

            var endFirst = center + Vector2.left * (toDistance * 0.5f);
            var endSecond = center + Vector2.right * (toDistance * 0.5f);
            QueueTouchState(touchscreen, 1, TouchPhase.Ended, endFirst, Vector2.zero, startTime, startFirst);
            QueueTouchState(touchscreen, 2, TouchPhase.Ended, endSecond, Vector2.zero, startTime, startSecond);
#else
            yield break;
#endif
        }

        /// <summary>
        /// 仮想デバイスを必ず外し、次回 Play に幽霊 current デバイスを残さないための終了処理です。
        /// </summary>
        public static void Dispose()
        {
#if ENABLE_INPUT_SYSTEM
            if (_gamepad != null)
            {
                InputSystem.RemoveDevice(_gamepad);
                _gamepad = null;
            }

            if (_keyboard != null)
            {
                InputSystem.RemoveDevice(_keyboard);
                _keyboard = null;
            }

            if (_mouse != null)
            {
                InputSystem.RemoveDevice(_mouse);
                _mouse = null;
            }

            if (_touchscreen != null)
            {
                InputSystem.RemoveDevice(_touchscreen);
                _touchscreen = null;
            }

            PressedKeys.Clear();
            _gamepadState = default;
            _mouseState = default;
            if (_driver != null)
            {
                Object.Destroy(_driver.gameObject);
                _driver = null;
            }
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private sealed class InputInjectorDriver : MonoBehaviour
        {
        }

        private static IEnumerator PressCoroutine(GamepadButton button)
        {
            var gamepad = EnsureGamepad();
            SetGamepadButton(button, true);
            // 手動 Update は UI モジュールが読む押下フレームを先に消費するため、通常更新へ任せる。
            InputSystem.QueueStateEvent(gamepad, _gamepadState);
            yield return null;
            SetGamepadButton(button, false);
            InputSystem.QueueStateEvent(gamepad, _gamepadState);
        }

        private static IEnumerator KeyCoroutine(Key key)
        {
            // キーボードもフォーカスを要する。確認せずに送ると Input System が黙って捨て、
            // 応答は成功のまま何も起きない（利用側プロジェクトで報告された事例）。
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var keyboard = EnsureKeyboard();
            PressedKeys.Add(key);
            // Submit が UI のフレーム処理まで残るよう、押下と解放は通常更新で処理させる。
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(ToPressedKeysArray()));
            yield return null;
            PressedKeys.Remove(key);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(ToPressedKeysArray()));
        }

        private static IEnumerator ClickCoroutine(Vector2 screenPosition, PointerButton button)
        {
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var mouse = EnsureMouse();
            SendPointerMove(screenPosition);
            SetMouseButton(button, true);
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();
            if (DefaultClickFrameDelaySeconds > 0.0f)
            {
                yield return new WaitForSeconds(DefaultClickFrameDelaySeconds);
            }
            else
            {
                yield return null;
            }

            SetMouseButton(button, false);
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();
        }

        private static IEnumerator TapCoroutine(Vector2 screenPosition)
        {
            if (!IsFocusDependentInputAvailable)
            {
                yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
                if (!IsFocusDependentInputAvailable)
                {
                    yield break;
                }
            }

            var touchscreen = EnsureTouchscreen();
            var startTime = Time.realtimeSinceStartupAsDouble;
            QueueTouchState(touchscreen, 1, TouchPhase.Began, screenPosition, Vector2.zero, startTime, screenPosition);
            yield return null;
            QueueTouchState(touchscreen, 1, TouchPhase.Ended, screenPosition, Vector2.zero, startTime, screenPosition);
        }

        private static IEnumerator PointerMoveCoroutine(Vector2 screenPosition)
        {
            yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
            if (IsFocusDependentInputAvailable)
            {
                SendPointerMove(screenPosition);
            }
        }

        private static IEnumerator ScrollCoroutine(Vector2 screenPosition, float amount)
        {
            yield return EnsureFocusDependentInputAsync(ReportFocusDependentInputFailure);
            if (IsFocusDependentInputAvailable)
            {
                SendScroll(screenPosition, amount);
            }
        }

        private static void ReportFocusDependentInputFailure(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                UnityEngine.Debug.LogWarning(message);
            }
        }

        private static void SendPointerMove(Vector2 screenPosition)
        {
            // perf: ドラッグの毎フレーム送出では、フォーカス復旧用のコルーチンを生成しない。
            var mouse = EnsureMouse();
            var delta = screenPosition - _mouseState.position;
            _mouseState.position = screenPosition;
            _mouseState.delta = delta;
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();
            _mouseState.delta = Vector2.zero;
        }

        private static void SendScroll(Vector2 screenPosition, float amount)
        {
            var mouse = EnsureMouse();
            SendPointerMove(screenPosition);
            _mouseState.scroll = new Vector2(0.0f, amount);
            InputSystem.QueueStateEvent(mouse, _mouseState);
            InputSystem.Update();
            _mouseState.scroll = Vector2.zero;
        }

        private static WaitForSeconds WaitForSeconds(float seconds)
        {
            if (seconds <= 0.0f)
            {
                return new WaitForSeconds(0.0f);
            }

            return new WaitForSeconds(seconds);
        }

        private static void EnsureDriver()
        {
            if (_driver != null)
            {
                return;
            }

            var driverObject = new GameObject(nameof(InputInjector));
            Object.DontDestroyOnLoad(driverObject);
            _driver = driverObject.AddComponent<InputInjectorDriver>();
        }

        private static Gamepad EnsureGamepad()
        {
            if (_gamepad != null && _gamepad.added)
            {
                return _gamepad;
            }

            // perf: ドメインリロード後に残ったデバイスを再利用し、有効な参照がないときだけ探索する。
            foreach (var device in InputSystem.devices)
            {
                if (device is Gamepad gamepad && gamepad.name == GamepadDeviceName && gamepad.added)
                {
                    _gamepad = gamepad;
                    return _gamepad;
                }
            }

            _gamepad = InputSystem.AddDevice<Gamepad>(GamepadDeviceName);
            return _gamepad;
        }

        private static Keyboard EnsureKeyboard()
        {
            if (_keyboard != null && _keyboard.added)
            {
                return _keyboard;
            }

            // perf: ドメインリロード後に残ったデバイスを再利用し、有効な参照がないときだけ探索する。
            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard keyboard && keyboard.name == KeyboardDeviceName && keyboard.added)
                {
                    _keyboard = keyboard;
                    return _keyboard;
                }
            }

            _keyboard = InputSystem.AddDevice<Keyboard>(KeyboardDeviceName);
            return _keyboard;
        }

        private static Mouse EnsureMouse()
        {
            if (_mouse != null && _mouse.added)
            {
                return _mouse;
            }

            _mouse = null;
            // perf: ドメインリロード後に残ったデバイスを再利用し、有効な参照がないときだけ探索する。
            foreach (var device in InputSystem.devices)
            {
                if (device is Mouse mouse && mouse.name == MouseDeviceName && mouse.added)
                {
                    _mouse = mouse;
                    break;
                }
            }

            if (_mouse == null)
            {
                _mouse = InputSystem.AddDevice<Mouse>(MouseDeviceName);
            }

            _mouseState.position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            InputSystem.QueueStateEvent(_mouse, _mouseState);
            InputSystem.Update();
            // 注入中はこちらを current にしておく。物理マウスと 2 台並ぶため、
            // UI 側がどちらを見るかを送信側で決めておかないと取り合いになる（#445）。
            _mouse.MakeCurrent();
            return _mouse;
        }

        /// <summary>
        /// 仮想マウスを用意し、その名前を返します。取り合いの検出を確かめる用途に使います。
        ///
        /// 入力の送信は行わないためフォーカスを要しません。
        /// フォーカスに依存すると、非フォーカスの環境で検査が空振りしたまま緑になります。
        /// </summary>
        public static string EnsurePointerDeviceForDiagnostics()
        {
#if ENABLE_INPUT_SYSTEM
            return EnsureMouse().name;
#else
            return string.Empty;
#endif
        }

        /// <summary>
        /// 送った直後に、仮想マウスが current を保てているかを調べます。
        ///
        /// 物理マウスが動くと current を奪い、送信自体は成功しているのに UI へ届かないことがある
        /// （2026-09-20 実測: 34 回中 1 回。失敗時は Mouse.current が物理側だった）。
        /// **黙って成功として返さない**ための検出で、競合そのものを防ぐものではありません。
        /// </summary>
        public static string DescribePointerDeviceContention()
        {
#if ENABLE_INPUT_SYSTEM
            if (_mouse == null || !_mouse.added)
            {
                return string.Empty;
            }

            var current = Mouse.current;
            if (current == null || ReferenceEquals(current, _mouse))
            {
                return string.Empty;
            }

            return "ポインタ入力が届いていない可能性があります。"
                + "注入した『" + MouseDeviceName + "』ではなく『" + current.name + "』が current です。"
                + "物理マウスが動くと current を奪い、送信は成功しても UI が反応しないことがあります。"
                + "画面が変わったかを observe で確かめ、変わっていなければ送り直してください。";
#else
            return string.Empty;
#endif
        }

        private static Touchscreen EnsureTouchscreen()
        {
            if (_touchscreen != null && _touchscreen.added)
            {
                return _touchscreen;
            }

            // perf: ドメインリロード後に残ったデバイスを再利用し、有効な参照がないときだけ探索する。
            foreach (var device in InputSystem.devices)
            {
                if (device is Touchscreen touchscreen && touchscreen.name == TouchscreenDeviceName && touchscreen.added)
                {
                    _touchscreen = touchscreen;
                    return _touchscreen;
                }
            }

            _touchscreen = InputSystem.AddDevice<Touchscreen>(TouchscreenDeviceName);
            return _touchscreen;
        }

        private static void QueueTouchState(Touchscreen touchscreen, int touchId, TouchPhase phase, Vector2 position, Vector2 delta, double startTime, Vector2 startPosition)
        {
            var touchState = new TouchState
            {
                touchId = touchId,
                phase = phase,
                position = position,
                delta = delta,
                pressure = phase == TouchPhase.Ended ? 0.0f : 1.0f,
                startTime = startTime,
                startPosition = startPosition,
            };
            InputSystem.QueueStateEvent(touchscreen, touchState);
            InputSystem.Update();
        }

        private static void SetGamepadButton(GamepadButton button, bool isPressed)
        {
            _gamepadState = _gamepadState.WithButton(button, isPressed);
        }

        private static void SetMouseButton(PointerButton button, bool isPressed)
        {
            var mouseButton = ResolveMouseButton(button);
            _mouseState = _mouseState.WithButton(mouseButton, isPressed);
        }

        private static MouseButton ResolveMouseButton(PointerButton button)
        {
            switch (button)
            {
                case PointerButton.Right:
                    return MouseButton.Right;
                case PointerButton.Middle:
                    return MouseButton.Middle;
                default:
                    return MouseButton.Left;
            }
        }

        private static GamepadButton ResolveDirectionButton(FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up:
                    return GamepadButton.DpadUp;
                case FocusDirection.Down:
                    return GamepadButton.DpadDown;
                case FocusDirection.Left:
                    return GamepadButton.DpadLeft;
                case FocusDirection.Right:
                    return GamepadButton.DpadRight;
                default:
                    return GamepadButton.DpadUp;
            }
        }

        private static Key[] ToPressedKeysArray()
        {
            var pressedKeys = new Key[PressedKeys.Count];
            var keyIndex = 0;
            foreach (var key in PressedKeys)
            {
                pressedKeys[keyIndex] = key;
                keyIndex++;
            }

            return pressedKeys;
        }
#endif
    }
}
#endif
