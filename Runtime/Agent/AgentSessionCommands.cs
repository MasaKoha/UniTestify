#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniTestify
{
    /// <summary>
    /// AI ゲートウェイ（AiCommandDispatcher）から文字列だけで呼べる静的入口です。
    /// インスタンス参照を外側へ保持させず、セッション操作を JSON に寄せるために用意します。
    /// </summary>
    public static class AgentSessionCommands
    {
        private static AgentSession _currentSession;

        /// <summary>セッション不在の要求でアンカー待機を始めないための状態です。</summary>
        internal static bool HasSession => _currentSession != null;

        /// <summary>目標に期待値が無いときの拒否メッセージ。呼び出し側がキー違いに気づけるよう正しい形を示す。</summary>
        public const string EmptyGoalMessage = "目標 JSON に期待値がありません。{\"goal\":[{\"kind\":\"textVisible\",\"value\":\"...\"}]} の形で 1 件以上、または {\"freePlay\":true} を指定してください。";

        /// <summary>
        /// 目標 JSON とオプション JSON から現在セッションを開始します。
        /// </summary>
        public static string Begin(string goalJson, string optionsJson)
        {
            var goal = string.IsNullOrEmpty(goalJson) ? new AgentGoal() : JsonUtility.FromJson<AgentGoal>(goalJson);
            // 期待値が 0 件の目標は「常に達成」と評価され、1 手ごとにセッションが終了する。
            // JsonUtility はキー違いを黙って null にするため、ここで弾かないと無音で毎手終了する
            if (!AgentGoalValidator.Validate(goal, out var validationMessage))
            {
                return ToJson(false, string.Empty, validationMessage, string.Empty, string.Empty);
            }

            if (!Application.isPlaying)
            {
                return ToJson(false, string.Empty, "playMode が必要です", string.Empty, string.Empty);
            }

            var options = string.IsNullOrEmpty(optionsJson) ? new AgentOptions() : JsonUtility.FromJson<AgentOptions>(optionsJson);
            if (!string.IsNullOrEmpty(options.adaptersDirectory))
            {
                var loadResult = GameAdapterLoader.Load(options.adaptersDirectory);
                if (!loadResult.Ok)
                {
                    return ToJson(false, string.Empty, loadResult.Message, string.Empty, string.Empty);
                }
            }

            // 注入に失敗した要求では、開始済みセッションを破棄しない。
            _currentSession?.Dispose();
            _currentSession = AgentSession.Begin(goal, options);
            return ToJson(true, _currentSession.SessionId, "セッションを開始しました。", _currentSession.Observe(false), _currentSession.OutputDirectory);
        }

        /// <summary>現在セッションが継続入力の途中なら true。セッションが無ければ false。</summary>
        public static bool IsInputBusy()
        {
            return _currentSession != null && _currentSession.IsInputBusy;
        }

        /// <summary>
        /// 現在セッションの観測を返し、外部 LLM の次手選択に使える形へ整えます。
        /// </summary>
        public static string Observe(bool diffOnly, string scope = "visible")
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, "セッションが開始されていません。", string.Empty, string.Empty);
            }

            return ToJson(true, _currentSession.SessionId, "観測しました。", _currentSession.Observe(diffOnly, scope), _currentSession.OutputDirectory);
        }

        /// <summary>
        /// 1 手 JSON を実行し、実行後の観測と拒否理由を同じ戻り値で返します。
        /// </summary>
        public static string Act(string actionJson)
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, "セッションが開始されていません。", string.Empty, string.Empty);
            }

            var action = new AgentAction();
            if (!string.IsNullOrEmpty(actionJson))
            {
                JsonUtility.FromJsonOverwrite(actionJson, action);
            }

            AiCommandArguments.ValidateDuration(action.timeoutSeconds, nameof(action.timeoutSeconds), true);
            if (AgentActionWait.HasConditions(action) && !UiInputLocator.IsAnchorSatisfied(AgentActionWait.CreateAnchor(action)))
            {
                return RejectAction(action, AgentActionWait.SynchronousWaitRequiredMessage);
            }

            if (AgentActionExecutor.GetActionKind(action) == "text" && InputInjector.RequiresTextInputVerification)
            {
                return RejectAction(action, AgentActionExecutor.TextRequiresAsyncMessage);
            }

            return ToJson(true, _currentSession.SessionId, "行動を処理しました。", _currentSession.Act(action), _currentSession.OutputDirectory);
        }

        /// <summary>text の未反映を失敗応答に変換し、非同期の呼び出し元が完了を待てるようにします。</summary>
        internal static IEnumerator<object> ActAsync(AgentAction action, Action<string> completed)
        {
            if (_currentSession == null || AgentActionExecutor.GetActionKind(action) != "text")
            {
                completed(Act(JsonUtility.ToJson(action)));
                yield break;
            }

            AiCommandArguments.ValidateDuration(action.timeoutSeconds, nameof(action.timeoutSeconds), true);
            if (AgentActionWait.HasConditions(action) && !UiInputLocator.IsAnchorSatisfied(AgentActionWait.CreateAnchor(action)))
            {
                completed(RejectAction(action, AgentActionWait.SynchronousWaitRequiredMessage));
                yield break;
            }

            // 待機中のセッション切り替えで、結果が別セッションの識別子へ混入するのを防ぐ。
            var session = _currentSession;
            using (var execution = session.ActAsync(action, (success, message, observation) =>
                completed(ToJson(success, session.SessionId, message, observation, session.OutputDirectory))))
            {
                while (execution.MoveNext())
                {
                    yield return execution.Current;
                }
            }
        }

        /// <summary>未成立の待機要求を入力として送らず、失敗応答と履歴へ同じ理由を残します。</summary>
        internal static string RejectAction(AgentAction action, string message)
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, message, string.Empty, string.Empty);
            }

            return ToJson(false, _currentSession.SessionId, message,
                _currentSession.RejectAction(action, message), _currentSession.OutputDirectory);
        }

        /// <summary>
        /// 現在セッションの目標達成を評価し、成功自己申告なしで外側へ返します。
        /// </summary>
        public static string IsGoalReached()
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, "セッションが開始されていません。", string.Empty, string.Empty);
            }

            var reached = _currentSession.IsGoalReached();
            return ToJson(true, _currentSession.SessionId, reached ? "目標を達成しています。" : "目標は未達です。", reached ? "true" : "false", _currentSession.OutputDirectory);
        }

        /// <summary>
        /// 成功した現在セッションを 02 のシナリオ JSON として書き出します。
        /// </summary>
        public static string ExportAsScenario(string name)
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, "セッションが開始されていません。", string.Empty, string.Empty);
            }

            var path = _currentSession.ExportAsScenario(name);
            var ok = !string.IsNullOrEmpty(path);
            var message = ok ? "scenario.json を書き出しました。" : _currentSession.StatusMessage;
            return ToJson(ok, _currentSession.SessionId, message, ok ? _currentSession.ExportSummary : string.Empty, path);
        }

        /// <summary>
        /// 現在セッションを終了し、入力状態とドライバを破棄します。
        /// </summary>
        public static string End()
        {
            if (_currentSession == null)
            {
                return ToJson(false, string.Empty, "セッションが開始されていません。", string.Empty, string.Empty);
            }

            var sessionId = _currentSession.SessionId;
            var outputDirectory = _currentSession.OutputDirectory;
            _currentSession.End();
            _currentSession.Dispose();
            _currentSession = null;
            return ToJson(true, sessionId, "セッションを終了しました。", string.Empty, outputDirectory);
        }

        /// <summary>評価前の行動履歴位置をゲートウェイと共有します。</summary>
        internal static int RecordedStepCount => _currentSession?.RecordedStepCount ?? 0;

        /// <summary>今回実行された手へ事後条件の評価結果を反映します。</summary>
        internal static void RecordActExpectation(int previousStepCount, bool expectOk)
        {
            _currentSession?.RecordActExpectation(previousStepCount, expectOk);
        }

        private static string ToJson(bool ok, string session, string message, string text, string path)
        {
            var result = new AgentCommandResult
            {
                ok = ok,
                session = session ?? string.Empty,
                message = message ?? string.Empty,
                text = text ?? string.Empty,
                path = path ?? string.Empty,
            };
            return JsonUtility.ToJson(result, true);
        }
    }
}
#endif
