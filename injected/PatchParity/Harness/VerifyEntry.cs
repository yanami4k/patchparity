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
using xpTURN.Klotho.Deterministic.Physics;
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
            var callbacks = new HarnessCallbacks(logger, opt);
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
                BuildCommands(commands, opt, tick, simulation.Frame);
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
        //
        // 场景参数只在给定时改变输入：未给定的参数沿用原先的抽取方式与顺序，
        // 缺省场景因而与加入这些参数之前逐字节相同，已有用例的实测结论不受影响。
        private static void BuildCommands(List<ICommand> commands, Options opt, int tick, Frame frame)
        {
            if (tick == 0)
            {
                for (int pid = 0; pid < opt.Players; pid++)
                {
                    var rng = DeterministicRandom.FromSeed((ulong)opt.Seed, InputFeatureKey, (ulong)pid);
                    int characterClass = rng.NextIntInclusive(0, 1);
                    var spawn = new FPVector2(
                        rng.NextFixed(FP64.FromInt(-8), FP64.FromInt(8)),
                        rng.NextFixed(FP64.FromInt(-8), FP64.FromInt(8)));
                    if (opt.Classes != null) characterClass = opt.Classes[pid];
                    if (opt.Spawn != null) spawn = opt.Spawn[pid];
                    commands.Add(new SpawnCharacterCommand
                    {
                        PlayerId = pid,
                        Tick = tick,
                        CharacterClass = characterClass,
                        SpawnPosition = spawn,
                    });
                }
                return;
            }

            Dictionary<int, FPVector3> positions = opt.Chase ? AlivePositions(frame) : null;

            for (int pid = 0; pid < opt.Players; pid++)
            {
                // 每个 (tick, player) 取独立的 RNG 实例：与回滚重放同值，且不依赖调用顺序
                var rng = DeterministicRandom.FromSeed((ulong)opt.Seed, InputFeatureKey, (ulong)(tick * 64 + pid));

                byte buttons = PlayerInputCommand.HAS_MOVE_BIT;
                bool attack = opt.AttackPermille < 0 ? rng.NextIntInclusive(0, 9) == 0 : rng.NextIntInclusive(0, 999) < opt.AttackPermille;
                bool jump = opt.JumpPermille < 0 ? rng.NextIntInclusive(0, 19) == 0 : rng.NextIntInclusive(0, 999) < opt.JumpPermille;
                if (attack) buttons |= PlayerInputCommand.ATTACK_BIT;
                if (jump) buttons |= PlayerInputCommand.JUMP_PRESSED_BIT;

                FP64 h = rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1));
                FP64 v = rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1));

                // 技能放在移动轴之后抽取：不给 skill 时不消耗随机数，缺省场景不变
                if (opt.SkillPermille > 0 && rng.NextIntInclusive(0, 999) < opt.SkillPermille)
                {
                    buttons |= PlayerInputCommand.HAS_SKILL_BIT;
                    if (rng.NextBool()) buttons |= PlayerInputCommand.SKILL_SLOT_BIT;
                }

                // 追逐：朝最近的另一名存活角色移动，让近战、击退真的发生
                if (positions != null && positions.TryGetValue(pid, out FPVector3 self))
                {
                    FPVector3 target = default;
                    FP64 best = FP64.MaxValue;
                    foreach (var other in positions)
                    {
                        if (other.Key == pid) continue;
                        FP64 dx = other.Value.x - self.x, dz = other.Value.z - self.z;
                        FP64 d = dx * dx + dz * dz;
                        if (d < best) { best = d; target = other.Value; }
                    }
                    if (best != FP64.MaxValue)
                    {
                        h = Toward(target.x - self.x);
                        v = Toward(target.z - self.z);
                    }
                }

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

            // 以下为场景生成用的参数（裁决 019）。取值的范围由流水线一侧校验，这里只按给定的值执行；
            // 未给定时保持原先的行为。
            public bool Ground;              // 放一块覆盖整个场地的地面：角色能站住、跳跃
            public string MoveSpeed;         // 各职业的移动速度（十进制字符串），缺省沿用数据资产里的 0
            public int[] Classes;            // 每名玩家的职业 0..3，缺省按种子取 0 或 1
            public FPVector2[] Spawn;        // 每名玩家的出生位置 (x, z)，缺省按种子取
            public int AttackPermille = -1;  // 每帧攻击的千分概率，-1 为原先的 1/10
            public int JumpPermille = -1;    // 每帧跳跃的千分概率，-1 为原先的 1/20
            public int SkillPermille;        // 每帧放技能的千分概率，缺省不放
            public bool Chase;               // 朝最近的另一名角色移动

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
                    switch (key)
                    {
                        case "moveSpeed": o.MoveSpeed = raw; continue;
                        case "classes": o.Classes = ParseInts(raw); continue;
                        case "spawn": o.Spawn = ParsePoints(raw); continue;
                    }
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) continue;
                    switch (key)
                    {
                        case "seed": o.Seed = value; break;
                        case "ticks": o.Ticks = value; break;
                        case "maxEntities": o.MaxEntities = value; break;
                        case "deltaTimeMs": o.DeltaTimeMs = value; break;
                        case "players": o.Players = value; break;
                        case "bots": o.Bots = value; break;
                        case "ground": o.Ground = value != 0; break;
                        case "attack": o.AttackPermille = value; break;
                        case "jump": o.JumpPermille = value; break;
                        case "skill": o.SkillPermille = value; break;
                        case "chase": o.Chase = value != 0; break;
                    }
                }
                return o;
            }

            private static int[] ParseInts(string raw)
            {
                string[] parts = raw.Split(',');
                var values = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    values[i] = int.Parse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                return values;
            }

            // "x:z,x:z"，以整数或一位小数给出
            private static FPVector2[] ParsePoints(string raw)
            {
                string[] parts = raw.Split(',');
                var points = new FPVector2[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string[] xz = parts[i].Split(':');
                    points[i] = new FPVector2(ParseFixed(xz[0]), ParseFixed(xz[1]));
                }
                return points;
            }

            public static FP64 ParseFixed(string raw) =>
                FP64.FromDouble(double.Parse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        // 存活角色的位置，按玩家号
        private static Dictionary<int, FPVector3> AlivePositions(Frame frame)
        {
            var positions = new Dictionary<int, FPVector3>();
            var filter = frame.Filter<CharacterComponent, TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (character.IsDead) continue;
                positions[character.PlayerId] = frame.GetReadOnly<TransformComponent>(entity).Position;
            }
            return positions;
        }

        // 追逐时的移动轴：相距超过半个单位就全速靠近，否则停下
        private static FP64 Toward(FP64 delta)
        {
            FP64 deadzone = FP64.FromDouble(0.5);
            if (delta > deadzone) return FP64.One;
            if (delta < -deadzone) return -FP64.One;
            return FP64.Zero;
        }

        // 只做验证所需的最小回调：注册玩法系统与初始化世界，不涉及网络、回放与存档
        private sealed class HarnessCallbacks : ISimulationCallbacks
        {
            private readonly IKLogger _logger;
            private readonly Options _opt;

            public HarnessCallbacks(IKLogger logger, Options opt)
            {
                _logger = logger;
                _opt = opt;
            }

            public void RegisterSystems(EcsSimulation simulation)
            {
                List<IDataAsset> assets = BrawlerSimSetup.CreateDefaultDataAssets();
                if (_opt.MoveSpeed != null)
                {
                    FP64 speed = Options.ParseFixed(_opt.MoveSpeed);
                    foreach (IDataAsset asset in assets)
                    {
                        if (asset is CharacterStatsAsset stats) stats.MoveSpeed = speed;
                    }
                }

                // 地面：顶面在 y=0、覆盖整个 40×40 场地的静态盒。缺省没有地面，角色一直下落（GroundClampSystem 的射线落空）
                List<FPStaticCollider> colliders = null;
                if (_opt.Ground)
                {
                    colliders = new List<FPStaticCollider>
                    {
                        new FPStaticCollider
                        {
                            id = 1,
                            collider = FPCollider.FromBox(new FPBoxShape(
                                new FPVector3(FP64.FromInt(20), FP64.FromDouble(0.5), FP64.FromInt(20)),
                                new FPVector3(FP64.Zero, FP64.FromDouble(-0.5), FP64.Zero))),
                            friction = FP64.FromDouble(0.5),
                        },
                    };
                }

                BrawlerSimSetup.RegisterSystems(simulation, _logger, assets, colliders);
            }

            public void OnInitializeWorld(IKlothoEngine engine)
            {
                BrawlerSimSetup.InitializeWorldState(engine, _opt.Players, _opt.Bots);
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
