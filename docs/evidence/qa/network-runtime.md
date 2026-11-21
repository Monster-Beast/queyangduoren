# 传输、安装与真实 ONI 运行记录

2026-11-21，Windows 版 Steam，Steam depot build 24423041，Klei build 744825。开发目标为 net48。程序集指纹可通过 `tools/doctor.ps1` 重新采集，本轮 Assembly-CSharp.dll 的 SHA-256 为 `A58E04D0FFDF89B86FB28B71AD900625B3B539DB30D67F8C6269F73A9F5AE599`。

## 可复现的基础检查

- `dotnet run --project tests/Integration/Transport/TransportTests.csproj -c Release` 在真实回环 TCP 套接字上测试单帧 65,536 字节往返、报头及载荷分片、无效长度隔离、3 个客机席位、断开及连接代更新。测试进程使用临时端口。
- `tools/package.ps1` 仅归档 QueyangCoop.dll、Queyang.Core.dll、mod.yaml、mod_info.yaml。相同输入连打两次 ZIP，SHA-256 相同。 `tests/Integration/Packaging/smoke.ps1` 在临时 mods 目录验证 -WhatIf、首次安装、重复安装、升级备份、恢复与重复 staticID 检查。
- Steam 传输使用游戏自带的 Steamworks.NET，Lobby 用于房间和好友邀请，Networking Sockets 建立可靠 P2P 连接。开发 TCP 默认回环 127.0.0.1:24714。显式设置 `ONI_COOP_BIND=局域网IPv4:端口` 后开房可供受控 LAN 访问。每个游戏进程读取自身环境变量。

## 实机测试如何隔离

`tests/Integration/Game/run.ps1` 把本机合法安装完整复制到仓库下忽略版本控制的 artifacts/qa 临时目录，在临时副本中将 Util.GetKleiRootPath 重定向到该测试角色的私人目录。测试版和正式版共用同一套包内 Mod DLL，仅测试目录另外加载自动操作的 SmokeDriver.dll。传给测试的 Vanilla 存档被复制成 Fixture.sav。启动时游戏以当前 Steam 身份正常运行。结束时检查原始游戏 DLL、选用存档和原始 mods.json 的哈希，然后清理复制的商业文件与存档。原始引擎日志只保存在本机，避免将玩家信息提交到仓库。

由主机载入的样本来自较早的 Vanilla 存档（游戏保存时 build 704096）。存档中已有的任务、库存、地形和活动复制人数会影响施工时间。更换样本后需独立记录结果。

## 房主加载真实殖民地，TCP 客机观察施工

在仓库根目录运行下列命令。需要至少 4 GiB 可用空间、活跃的 Steam 客户端以及本地游戏引用。若工作区尚未恢复测试包，先运行 `dotnet restore tests/Core.Tests/Core.Tests.csproj` 取得随测试依赖提供的 Cecil 程序集读取器。

~~~powershell
$save = Read-Host '输入可复制的 Vanilla .sav 完整路径'
.\tests\Integration\Game\run.ps1 -SavePath $save
~~~

记录中的房主为真正的 Unity 游戏进程，客机为独立进程运行的 RoomClient 与生产 TCP 传输。房主从主菜单加载测试存档并启动房间，原版时钟推进。客机取得 384 个可见格子、3 名复制人，对 (120,192) 提交挖掘，收到 `OK Dig designated`；对 (120,194) 提交梯子，收到 `OK Construction planned`。随后收到两处订单，挖开的格子内出现约 0.00175 kg 二氧化碳，梯子从蓝图变为成品。测试判定依据为主机经过原版施工后的状态变化。

同一轮的补充探针通过了操作权限、排班、个人工作优先级、三种建筑设置的检查。研究检查为 SKIP，原因是该档已有研究队列。连续 20 次预热后的单视口捕获与编码 p95 为 0.28 ms，最大载荷 8,466 字节。此数值属于该轮适配器和该早期存档的局部采样，整机帧预算、多人长期负载应另做实测。

## 客机在真实主菜单里查看世界

~~~powershell
.\tests\Integration\Game\run.ps1 -GuestOnly
~~~

使用真实 Unity 客机连接单独的测试夹具房主。夹具提供合成氧气网格和检查操作回执。客机从主菜单加入，生成生产面板的 `Co-op viewport` 纹理，显示 384 格，镜头从 (100,100) 重新订阅到 (108,102)，在 (110,104) 提交检查操作并收到确认。测试期间每帧检查 `Game.Instance` 为空，断开后客户角色清理通过。这一轮测到了实际游戏内的视图、命令和退房路径；网络另一端提供的是合成世界。

视图协议 v4 复跑通过，包 SHA-256 为 `A99D2EDD8DE84AA6DF88B520A3EB4D3A9BE4B32D1B38E334785BBAC8EF4E8956`。引擎日志中的角色退出计数为 `guest-closed simStep=0 findChore=0 setChore=0 saveAttempts=0 worldStarts=0 views=2`，两次成功应用的视图分别来自初次订阅和移动镜头后的订阅。

## 真实 Steam 单账户建房

~~~powershell
.\tests\Integration\Game\run.ps1 -SavePath $save -SteamLobby
~~~

授权 Steam 客户端中的真实游戏副本调用生产 `Host(true)` 成功。API 返回有效的 Lobby ID，房主身份与当前 Steam 用户相同，人数上限为 4，当前人数为 1，房间元数据匹配。测试随后调用生产 Disconnect 清理房间。全程没有发送好友邀请。

当前机器上曾通过 `-SecondGame` 尝试并行启动第二个 Unity 实例，第二个进程在产生引擎日志前已退出，因此未取得“双 Unity 客机经 Steam 好友邀请加入”的证据。T07 仍需两台具备独立 Steam 账号的测试环境完成邀请、P2P 建连和重新邀请。四个 TCP 端点和真实 Steam 四人会话应分别记录。首次实测还发现快照中的复制人 ID 未填充导致握手后断开，修正后上述真实挖掘与建造流程通过。
