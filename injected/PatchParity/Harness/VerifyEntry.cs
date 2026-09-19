// patchparity 热更侧入口：驱动 Brawler 的确定性模拟，逐 tick 输出状态哈希。
// 本程序集被划为热更程序集，母包通过反射调用 Run，因此母包不需要引用它。
// 构造顺序参照样例自带的无头专用服务器（Samples/Brawler/Server）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Brawler;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace PatchParity.Harness
{
    public static class VerifyEntry
    {
        // 供母包启动器反射调用。哈希写入 csvPath，返回人类可读的一行摘要。
        public static string Run(string csvPath, int seed, int ticks, int maxEntities, int deltaTimeMs)
        {
            var logger = new NullLogger();
            var simulation = new EcsSimulation(maxEntities, maxRollbackTicks: 2, deltaTimeMs: deltaTimeMs, logger: logger);

            var callbacks = new HarnessCallbacks(logger);
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, logger, callbacks);
            engine.SetCommandFactory(new CommandFactory());

            var sb = new StringBuilder(ticks * 24);
            sb.Append("tick,hash\n");

            var commands = new List<ICommand>();
            long lastHash = 0;
            for (int tick = 0; tick < ticks; tick++)
            {
                commands.Clear();
                simulation.Tick(commands);
                lastHash = simulation.GetStateHash();
                sb.Append(tick.ToString(CultureInfo.InvariantCulture))
                  .Append(',')
                  .Append(lastHash.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }

            Directory.CreateDirectory(Path.GetDirectoryName(csvPath));
            File.WriteAllText(csvPath, sb.ToString());

            return $"ticks={ticks} seed={seed} lastHash={lastHash} csv={csvPath}";
        }

        // 只做验证所需的最小回调：注册玩法系统与初始化世界，不涉及网络、回放与存档
        private sealed class HarnessCallbacks : ISimulationCallbacks
        {
            private readonly IKLogger _logger;

            public HarnessCallbacks(IKLogger logger)
            {
                _logger = logger;
            }

            public void RegisterSystems(EcsSimulation simulation)
            {
                BrawlerSimSetup.RegisterSystems(simulation, _logger, BrawlerSimSetup.CreateDefaultDataAssets());
            }

            public void OnInitializeWorld(IKlothoEngine engine)
            {
                BrawlerSimSetup.InitializeWorldState(engine, maxPlayers: 4, botCount: 0);
            }

            // 验证不接输入：命令序列由 harness 自己按固定规则产生，保证可复现
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }

            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }
        }

        // 验证过程不产生日志：日志内容不参与判定，且写日志会拖慢批量运行
        private sealed class NullLogger : IKLogger
        {
            public bool IsEnabled(KLogLevel level) => false;
            public void Log(KLogLevel level, string message, Exception exception) { }
        }
    }
}
