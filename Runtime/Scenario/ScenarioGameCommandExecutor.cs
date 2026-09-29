#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;

namespace UniTestify
{
    /// <summary>シナリオのデバッグ用引数を検査し、既存のゲームコマンドハンドラへ渡す。</summary>
    internal static class ScenarioGameCommandExecutor
    {
        internal static bool TryExecute(UiScenarioStep step, IGameCommandHandler handler, out string message)
        {
            if (!string.IsNullOrEmpty(step.submit) || ScenarioInputExecutor.IsInputStep(step))
            {
                message = "gameCommand と UI 入力は別ステップに分けてください。";
                return false;
            }

            if (handler == null)
            {
                message = "IGameCommandHandler が登録されていません。";
                return false;
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in step.gameArguments ?? Array.Empty<string>())
            {
                var separatorIndex = argument == null ? -1 : argument.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == argument.Length - 1)
                {
                    message = $"ゲームコマンド引数は「名前=値」で指定してください: {argument}";
                    return false;
                }

                var name = argument.Substring(0, separatorIndex);
                var value = argument.Substring(separatorIndex + 1);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) || arguments.ContainsKey(name))
                {
                    message = $"ゲームコマンド引数が空白または重複しています: {name}";
                    return false;
                }

                arguments.Add(name, value);
            }

            return handler.TryExecute(step.gameCommand, arguments, out message);
        }
    }
}
#endif
