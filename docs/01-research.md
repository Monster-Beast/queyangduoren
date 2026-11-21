# 调研与技术决策

调研日期为 2026-11-21。依据包括项目 README、源码入口、发布记录、用户故障报告和库作者文档。现有项目的功能描述按作者声明记录；本次没有启动游戏或独立复现这些问题。以下链接中的源码基线使用提交号，便于以后区分 ONI 更新造成的差异。

## 可借鉴的前案

| 来源 | 核对到的事实 | 对本项目的影响 |
|---|---|---|
| ONI Multiplayer | 支持共管殖民地、各自视角、Steam 邀请与多种操作同步。各端独立模拟，每个游戏日及主机保存时硬同步。最新公开 Release 为 v1.1.3-alpha，更新说明针对游戏 build 647408。 | 参考邀请和工具入口；采用单房主模拟，日常保存不触发全体重新加载。旧版本号只用于历史定位。 [README](https://github.com/onimp/oni_multiplayer/blob/c0626280fb8a3d725a6960375b0356a1f23a642b/README.md) · [Release](https://github.com/onimp/oni_multiplayer/releases/tag/v1.1.3-alpha) |
| 上游源码 | 挖掘已有显式命令类型；项目通过本地游戏程序集引用 Harmony、Unity 和 Steamworks.NET。 | 可沿工具提交点追踪游戏调用链；签名、可见性和当前游戏附带的 Steam API 需要重新核对。 [Dig.cs](https://github.com/onimp/oni_multiplayer/blob/c0626280fb8a3d725a6960375b0356a1f23a642b/src/MultiplayerMod/Multiplayer/Commands/Tools/Dig.cs) · [工程引用](https://github.com/onimp/oni_multiplayer/blob/c0626280fb8a3d725a6960375b0356a1f23a642b/src/MultiplayerMod/MultiplayerMod.csproj) · [构建参数](https://github.com/onimp/oni_multiplayer/blob/c0626280fb8a3d725a6960375b0356a1f23a642b/Directory.Build.props) |
| 实际故障与未完成工作 | 有玩家报告大于 10 MB 存档卡在等待第二人；另有挖掘任务同步工单及传送装载器不同步报告。 | 大快照分块、有界排队、慢客机隔离；挖掘从下令一直测到完成；需要状态读取和侧栏操作覆盖矩阵。10 MB 是上游故障案例。 [#353](https://github.com/onimp/oni_multiplayer/issues/353) · [#374](https://github.com/onimp/oni_multiplayer/issues/374) · [#354](https://github.com/onimp/oni_multiplayer/issues/354) |
| 中文衍生项目 | 文档列出存档同步失败恢复、Chore 同步、权限校验与 NUnit，仍采用多端本地模拟。 | 参考错误恢复和测试场景；新增玩家专属复制人会改变合作规则，首版共享全部复制人。 [中文说明](https://github.com/xiyuxx/oni_multiplayer/blob/907d0992007554c49317e52276109d1fc142aad3/README.md) |
| Lineo-OD | README 给出 Steam P2P、房主权威、复制人归属的设计，作者明确称早期原型尚不能正常工作。 | 说明已有同路线尝试；无法由架构图推定原生渲染、AI 隔离和完整联机已可用。 [作者说明](https://github.com/Lineo-OD/Oxygen-Not-Included-Multiplayer-Mod/blob/1367be05fa124b00d5cf0578681e94fa35885100/README.md) |
| 独立服实验 | Issue 描述加载游戏 DLL、SimDLL 与可视化，并将多人命令列为后续工作。 | 本版使用房主正在运行的游戏进程执行完整模拟，避免同时承担独立启动游戏与重做客户端两项工作。 [实验记录](https://github.com/onimp/oni_multiplayer/issues/383) |

## 工具、平台和证据边界

- Harmony 支持 Prefix、Postfix、Transpiler。优先用可定位的输入提交点、变更通知和角色守卫；脆弱的 IL 修改需有目标签名、场景测试和版本失配处理。社区 Mod 仓库可用于交叉查找生命周期、UI 实例和读表方式。 [Harmony 作者文档](https://github.com/pardeike/Harmony/blob/master/Documentation/articles/patching.md) · [ONI 社区 Mod 源码](https://github.com/peterhaneve/ONIMods) · [ONI Modding 社区资料](https://github.com/Cairath/Oxygen-Not-Included-Modding)
- Steam Lobby 能承载发现、邀请和少量房间元数据；实时消息通过 Networking Sockets 的监听、连接、接受连接和消息 API 发送。房主同时是 P2P 端点和逻辑权威。SDR 的中继可用性与具体初始化、游戏进程身份和网络有关。首轮须用 ONI 自己的 Steam 上下文以及两个实际账号测试好友加入，检查回调和断线原因。 [Lobby API](https://partner.steamgames.com/doc/api/ISteamMatchmaking) · [Sockets API](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets) · [SDR](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay) · [C# 封装](https://github.com/rlabrecque/Steamworks.NET)
- 上游近期有 DLC 维护提案，查询时 PR 为 open。目标 build、DLC 与测试覆盖通过本工程自己的构建矩阵记录。 [上游 DLC PR](https://github.com/onimp/oni_multiplayer/pull/386)
- 调研时远端元数据返回默认分支 main，分支列表为空。此环境可见 .NET SDK 9.0.316，检查的三个常用 Steam 安装位置未发现 ONI。没有从游戏程序集验证补丁签名、原生 Sim 边界或原生 UI 在停算后的可用性。Steam 工坊实时评价也未纳入结论。 [目标仓库](https://github.com/Monster-Beast/queyangduoren)

## 实施决策及何时重新评估

| 决策 | 原因 | 可推翻假设的实验与处理 |
|---|---|---|
| 单房主模拟，客机持显示用副本 | 资源消耗、任务竞争、气体演化共享同一事实来源。 | 验证两端模拟推进计数和复制人行为；若原生 UI 耦合高，切到进程内显示适配层，维持单房主。 |
| 先从空仓库实现独立协议与状态模型 | 上游协议服务于双端模拟，复用完整网络层会把假设带入新实现。 | 比较上游工具入口与实机签名；仅局部复用适合的代码。上游 MIT 代码如有复制，保留出处、版权和许可；其他仓库先核对相应许可。 |
| Steam 正式通道，回环与 LAN 为开发通道 | 相同协议能在无 Steam 的 CI 中验证；真实好友联机复用游戏登录态。 | 核对本机绑定、游戏已初始化的接口和回调所有权；有版本差异则适配已验证接口，保留 Steam 实测门槛。 |
| 定型协议承载可见状态与操作 | 可缩小消息体、对同一条命令复测，又能做版本协商。 | 对晚期殖民地采样 CPU、带宽、积压；先降非关键层更新率，再调分块/压缩。保留对象寿命和操作确认的可靠性。 |
| 主机继续使用原生存档 | 存档应可继续单人游玩；客机加入只需要显示状态。 | 使用存档副本验证新建、保存、重开、卸载后回单人；若确需附加数据，使用独立版本化 sidecar 并说明缺失时的恢复。 |

## 每次游戏更新时应保存的证据

记录 build、活动内容包、程序集 SHA-256、补丁原始方法签名和匹配数量、Steam 绑定可见方法、主客机系统/Mod 清单和测试存档指纹。记录证据和本项目的接口摘要即可；构建机通过本地安装取得商业游戏 DLL。
