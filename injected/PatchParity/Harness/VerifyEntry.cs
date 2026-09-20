// patchparity 热更侧入口：驱动 Brawler 的确定性模拟，逐 tick 输出分层状态哈希。
// 本程序集被划为热更程序集，母包通过反射调用 Run，因此母包不需要引用它。
//
// 与母包的接口刻意做成 Run(string csvPath, string options)：母包一旦发布就不能再改，
// 若把场景参数写进方法签名，以后每加一个参数都要重打母包。
// options 为 "k=v;k=v" 形式，解析全在热更侧，新增场景参数不触及母包。
//
// 输出为分层 CSV：总哈希用于判定是否一致，分层列用于在不一致时定位到组件或系统。
// 表头写出列名，比对按列名对齐而不按位置，因为两次构建注册的组件集合未必相同。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Brawler;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace PatchParity.Harness
{
    public static class VerifyEntry
    {
        // 输入流的 RNG 特征键，与玩法内各随机流分开
        private const ulong InputFeatureKey = 0x494E505554534551UL;

        // 供母包启动器反射调用。哈希写入 csvPath，返回人类可读的一行摘要。
        public static string Run(string csvPath, string options)
        {
            var opt = Options.Parse(options);

            var logger = new NullLogger();
            var simulation = new EcsSimulation(opt.MaxEntities, maxRollbackTicks: 2, deltaTimeMs: opt.DeltaTimeMs, logger: logger);

            // 注册玩法系统与数据资产，然后锁定资产注册表。
            // 这一步由会话层（KlothoSession）负责，而验证直接使用引擎，故须自行照做；
            // 漏掉它会在世界初始化读取配置时抛 KeyNotFoundException: DataAsset not found。
            var callbacks = new HarnessCallbacks(logger, opt.Players, opt.Bots);
            callbacks.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            // 玩家名单与世界随机种子都取自网络服务，故验证必须自带一个离线实现并显式给定种子。
            // 引擎构造函数把随机种子默认设为 DateTime.Now.Ticks，不显式给定则两次运行的世界不同。
            var network = new HarnessNetworkService(opt.Players, opt.Seed);
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.SetCommandFactory(new CommandFactory());
            engine.Initialize(simulation, network, logger, callbacks);

            // 经网络服务的开局事件启动，而不是直接调用 engine.Start()。
            // 引擎只在开局事件的处理里从网络服务读取世界随机种子，随后才调用 Start()；
            // 绕过这一步会让世界种子停留在引擎构造函数设的墙上时钟，同一场景两次运行的世界就不同。
            // 世界由 Start 构建：参与者槽位、种子单例、对局结束单例，然后才是游戏侧的 OnInitializeWorld。
            engine.EnableReplayRecording = false;
            network.FireGameStart();

            var breakdown = new StateHashBreakdown();
            simulation.GetStateHash(breakdown);

            var componentNames = new List<string>();
            for (int i = 0; i < breakdown.ComponentHashes.Length; i++)
            {
                int typeId = EcsSimulation.ComponentTypeIdAt(i);
                componentNames.Add(EcsSimulation.ComponentTypeName(typeId) ?? ("type" + typeId.ToString(CultureInfo.InvariantCulture)));
            }
            var systemNames = new List<string>();
            for (int i = 0; i < simulation.SnapshotParticipantCount; i++)
            {
                systemNames.Add(simulation.SnapshotParticipantName(i) ?? ("participant" + i.ToString(CultureInfo.InvariantCulture)));
            }

            var sb = new StringBuilder(opt.Ticks * 256);
            sb.Append("tick,total,frame");
            foreach (string n in componentNames)
            {
                sb.Append(",c:").Append(n).Append(":count").Append(",c:").Append(n).Append(":hash");
            }
            foreach (string n in systemNames)
            {
                sb.Append(",s:").Append(n).Append(":hash");
            }
            sb.Append('\n');

            var commands = new List<ICommand>();
            long lastHash = 0;
            int liveComponents = 0;

            for (int tick = 0; tick < opt.Ticks; tick++)
            {
                commands.Clear();
                BuildCommands(commands, opt, tick);
                simulation.Tick(commands);
                lastHash = simulation.GetStateHash(breakdown);

                sb.Append(tick.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(lastHash.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(breakdown.FrameHash.ToString(CultureInfo.InvariantCulture));
                liveComponents = 0;
                for (int i = 0; i < componentNames.Count; i++)
                {
                    liveComponents += breakdown.ComponentCounts[i];
                    sb.Append(',').Append(breakdown.ComponentCounts[i].ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(breakdown.ComponentHashes[i].ToString(CultureInfo.InvariantCulture));
                }
                for (int i = 0; i < systemNames.Count; i++)
                {
                    sb.Append(',').Append(breakdown.SystemHashes[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }

            Directory.CreateDirectory(Path.GetDirectoryName(csvPath));
            File.WriteAllText(csvPath, sb.ToString());

            // lastLiveComponents 是末帧的组件实例总数：为 0 说明世界是空的，
            // 这种情况下两路"一致"不代表补丁等价，只代表什么都没跑到。
            return "ticks=" + opt.Ticks + " seed=" + opt.Seed + " players=" + opt.Players + " bots=" + opt.Bots +
                   " lastHash=" + lastHash + " lastLiveComponents=" + liveComponents +
                   " componentTypes=" + componentNames.Count + " systems=" + systemNames.Count + " csv=" + csvPath;
        }

        // 输入序列完全由种子与 tick 派生，不读外部设备，保证同一 options 每次产生同一串命令。
        // 开局先把角色生成出来，之后每帧发移动与偶发攻击，让战斗、拾取、边界等系统真的被执行到；
        // 覆盖不到的代码改了也不会反映在哈希里，那种"一致"是假阳性。
        private static void BuildCommands(List<ICommand> commands, Options opt, int tick)
        {
            if (tick == 0)
            {
                for (int pid = 0; pid < opt.Players; pid++)
                {
                    var rng = DeterministicRandom.FromSeed((ulong)opt.Seed, InputFeatureKey, (ulong)pid);
                    commands.Add(new SpawnCharacterCommand
                    {
                        PlayerId = pid,
                        Tick = tick,
                        CharacterClass = rng.NextIntInclusive(0, 1),
                        SpawnPosition = new FPVector2(
                            rng.NextFixed(FP64.FromInt(-8), FP64.FromInt(8)),
                            rng.NextFixed(FP64.FromInt(-8), FP64.FromInt(8))),
                    });
                }
                return;
            }

            for (int pid = 0; pid < opt.Players; pid++)
            {
                // 每个 (tick, player) 取独立的 RNG 实例：与回滚重放同值，且不依赖调用顺序
                var rng = DeterministicRandom.FromSeed((ulong)opt.Seed, InputFeatureKey, (ulong)(tick * 64 + pid));

                byte buttons = PlayerInputCommand.HAS_MOVE_BIT;
                if (rng.NextIntInclusive(0, 9) == 0) buttons |= PlayerInputCommand.ATTACK_BIT;
                if (rng.NextIntInclusive(0, 19) == 0) buttons |= PlayerInputCommand.JUMP_PRESSED_BIT;

                FP64 h = rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1));
                FP64 v = rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1));

                commands.Add(new PlayerInputCommand
                {
                    PlayerId = pid,
                    Tick = tick,
                    HorizontalAxis = h,
                    VerticalAxis = v,
                    Buttons = buttons,
                    AimDirection = new FPVector2(h, v),
                });
            }
        }

        private sealed class Options
        {
            public int Seed = 42;
            public int Ticks = 600;
            public int MaxEntities = 512;
            public int DeltaTimeMs = 25;
            public int Players = 2;
            // 机器人默认关闭：使用机器人需要额外构造 BotFSMSystem 并注册行为树根，
            // 否则世界初始化会抛 [HFSMManager] Unknown rootId。留作后续场景变体。
            public int Bots = 0;

            public static Options Parse(string text)
            {
                var o = new Options();
                if (string.IsNullOrEmpty(text)) return o;
                foreach (string pair in text.Split(';'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq).Trim();
                    string raw = pair.Substring(eq + 1).Trim();
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) continue;
                    switch (key)
                    {
                        case "seed": o.Seed = value; break;
                        case "ticks": o.Ticks = value; break;
                        case "maxEntities": o.MaxEntities = value; break;
                        case "deltaTimeMs": o.DeltaTimeMs = value; break;
                        case "players": o.Players = value; break;
                        case "bots": o.Bots = value; break;
                    }
                }
                return o;
            }
        }

        // 只做验证所需的最小回调：注册玩法系统与初始化世界，不涉及网络、回放与存档
        private sealed class HarnessCallbacks : ISimulationCallbacks
        {
            private readonly IKLogger _logger;
            private readonly int _players;
            private readonly int _bots;

            public HarnessCallbacks(IKLogger logger, int players, int bots)
            {
                _logger = logger;
                _players = players;
                _bots = bots;
            }

            public void RegisterSystems(EcsSimulation simulation)
            {
                BrawlerSimSetup.RegisterSystems(simulation, _logger, BrawlerSimSetup.CreateDefaultDataAssets());
            }

            public void OnInitializeWorld(IKlothoEngine engine)
            {
                BrawlerSimSetup.InitializeWorldState(engine, _players, _bots);
            }

            // 命令由 harness 按种子自行构造，不走引擎的输入轮询
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
