// patchparity 热更侧：离线网络服务。
// 验证跑批不联网，但引擎的玩家名单与世界随机种子都取自网络服务：
// 名单决定 tick0 创建多少参与者槽位，种子决定道具生成等一切随机流。
// 引擎构造函数把随机种子默认设为 DateTime.Now.Ticks，故验证必须显式给定种子，
// 否则两次运行的世界不同，比对结果没有意义。
using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace PatchParity.Harness
{
    internal sealed class HarnessNetworkService : IKlothoNetworkService
    {
        private readonly List<IPlayerInfo> _players = new List<IPlayerInfo>();

        public HarnessNetworkService(int playerCount, int randomSeed)
        {
            RandomSeed = randomSeed;
            for (int i = 0; i < playerCount; i++)
            {
                _players.Add(new HarnessPlayerInfo(i));
            }
        }

        public SessionPhase Phase => SessionPhase.Playing;
        public SharedTimeClock SharedClock => default;
        public int PlayerCount => _players.Count;
        public int SpectatorCount => 0;
        public int PendingLateJoinCatchupCount => 0;
        public bool AllPlayersReady => true;
        public int LocalPlayerId => 0;
        public bool IsHost => true;
        public int RandomSeed { get; }
        public IReadOnlyList<IPlayerInfo> Players => _players;

        // 触发引擎的开局流程。引擎在 HandleGameStart 里才从网络服务读取世界随机种子，
        // 并在其中调用 Start()；直接调用 engine.Start() 会跳过这一步，
        // 使世界种子停留在引擎构造函数设的 DateTime.Now.Ticks，两次运行的世界因而不同。
        public void FireGameStart()
        {
            OnGameStart?.Invoke();
        }

        // 以下成员在离线验证下都不会被触发到有效路径，保留空实现
        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
        public void CreateRoom(string roomName, int maxPlayers) { }
        public void JoinRoom(string roomName) { }
        public void LeaveRoom(bool keepReconnectCredentials = false) { }
        public void SetReady(bool ready) { }
        public void SendCommand(ICommand command) { }
        public void RequestCommandsForTick(int tick) { }
        public void SendSyncHash(int tick, long hash, long cmdHash) { }
        public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
        public void BroadcastMatchAbort(byte reason) { }
        public void InvalidateLocalSyncHashes(int fromTick) { }
        public void InvalidateSyncHashes(int fromTick) { }
        public void Update() { }
        public void FlushSendQueue() { }
        public void ClearOldData(int tick) { }
        public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
        public void SetLocalTick(int tick) { }
        public void SetLocalAdvantage(int advantage) { }
        public void SendFullStateRequest(int tick) { }
        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind) { }

#pragma warning disable 67 // 除 OnGameStart 外，离线验证不产生网络事件
        public event Action OnGameStart;
        public event Action<long> OnCountdownStarted;
        public event Action<IPlayerInfo> OnPlayerJoined;
        public event Action<IPlayerInfo> OnPlayerLeft;
        public event Action<ICommand> OnCommandReceived;
        public event Action<int, int, long, long> OnDesyncDetected;
        public event Action<int, int> OnResyncFailureReported;
        public event Action<int> OnMatchAbortReceived;
        public event Action<int, int, bool> OnSyncHashCompared;
        public event Action<int, int, int> OnFrameAdvantageReceived;
        public event Action<int> OnLocalPlayerIdAssigned;
        public event Action<int, int> OnFullStateRequested;
        public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
        public event Action<IPlayerInfo> OnPlayerDisconnected;
        public event Action<IPlayerInfo> OnPlayerReconnected;
        public event Action OnReconnecting;
        public event Action<ReconnectRejectReason> OnReconnectFailed;
        public event Action OnReconnected;
        public event Action<int, int> OnLateJoinPlayerAdded;
        public event Action<SessionPhase> OnPhaseChanged;
        public event Action<int> OnPlayerCountChanged;
        public event Action<bool> OnAllPlayersReadyChanged;
#pragma warning restore 67

        private sealed class HarnessPlayerInfo : IPlayerInfo
        {
            public HarnessPlayerInfo(int playerId)
            {
                PlayerId = playerId;
            }

            public int PlayerId { get; }
            public string DisplayName => "p" + PlayerId;
            public string Account => DisplayName;
            public bool IsReady => true;
            public int Ping => 0;
            public PlayerConnectionState ConnectionState => PlayerConnectionState.Connected;
        }
    }
}
