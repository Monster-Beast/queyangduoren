# 开发候选版验收记录

工作分支 feature/host-authoritative-coop，文档基线 9098722。目标为 Windows x64、Steam Vanilla、Klei changelist 744825（Steam 安装清单 buildid 24423041）。构建环境使用 .NET SDK 9.0.316。程序集 Assembly-CSharp.dll 的 SHA-256 为 A58E04D0FFDF89B86FB28B71AD900625B3B539DB30D67F8C6269F73A9F5AE599。

## 当前入口和决策

- 核心编译目标 netstandard2.0，游戏适配层 net48，直接引用本地游戏程序集。游戏构建号不匹配则停止安装多人补丁，输出原因。单机不启动会话。
- 房主用暂停菜单开房，原生任务、物理和存档继续由实际殖民地驱动。客机在主菜单呈现只读地图，显示格子的元素、质量、温度、建筑、管线、订单和复制人。客机可以发起语义操作，角色切换和退出清理见 CoopSession。编译目标、模块边界与设计差异见 [实际协议](protocol.md)。
- 复测方式和安装入口见 [项目首页](../../README.md)。完整游戏程序集、存档、账号及个人日志均保存在本机，代码仓库不包含这些文件。

## 已取得的证据

1. 在隔离的真实游戏进程中载入 Vanilla 存档副本，进入主菜单、加载殖民地、开设 LAN 房间、推进游戏时间。外部测试客机使用生产协议和 TCP 适配器，取得了 384 格视图及 3 名复制人，看到两个订单出现、挖掘后气体进入以及梯子建成。
2. 独立运行真实 Unity 客机，以测试房主驱动同一条 TCP 路径。客机未载入本地世界，建立 384 格视图、移动订阅、发出检查操作、收到结果并离开。此项和上一项分别验证游戏两端，尚需两台真实 Steam 游戏共同操作。
3. 单账号已验证生产 Steam Lobby 创建并正常关闭。受控传输验证还包括 3 个 TCP 客机、64 KiB 数据包、拆包、断开重连及异常帧隔离。
4. 房主实机测试观察到排班、个人优先级、部分建筑设置以及非法操作处理。一个已发展殖民地的 20 次预热后视口采集与编码观测为 p95 0.28 ms、8466 字节（早期显示格式）。该数字只表示单视口采集，不能作为四人整局主线程性能。
5. 纯核心测试覆盖并发竞争、命令去重与过期、构建不符、11 MiB 基线、坏摘要、跨订阅代迟到包、一个慢客机与另外两个健康客机、显示协议非法输入等场景。安装包只含项目 DLL 和两个 YAML，安装/备份/恢复用临时目录验证。

## 外部实机门槛

Steam 好友邀请和跨机两人游玩需要两个可用账号及客户端；四人真实会话需要四个。此机只能在一个 Steam 登录环境中执行针对性的真实游戏测试。UI 鼠标、镜头和分辨率操作按 [客机检查清单](client.md) 验证。正式版还要记录不同机器和后期殖民地的性能、真实 Steam 线路下的带宽及晚加入延迟。

## 可重跑命令

~~~powershell
dotnet test tests/Core.Tests/Core.Tests.csproj -c Release
dotnet build QueyangDuoren.sln -c Release
dotnet run --project tests/Integration/Transport/TransportTests.csproj -c Release
.\tools\package.ps1
.\tests\Integration\Packaging\smoke.ps1
~~~

隔离运行器位于 tests/Integration/Game/run.ps1。需要本机 Steam 游戏安装、能读取的 Vanilla 存档以及测试用磁盘空间；使用单独的游戏副本和数据目录。运行器在结束时比对原始存档、程序集与 Mod 配置哈希。分别使用 -SavePath 指定源档、-GuestOnly 验证独立客机。持续测试数据和进一步复现方式随验收记录更新。
