# 《缺氧》同图合作 Mod

每位玩家都运行自己的 Windows Steam 版《缺氧》。房主加载殖民地并推进物理、复制人 AI 和任务；好友收到房主当前的临时存档副本，在自己的原版殖民地场景中移动镜头、打开侧栏和下单。一个房间最多 4 人。本分支源码面向基础游戏（Vanilla）Klei 764025 / Steam build 26798728。

0.3.0-dev 的原版房主、客机截图及单份授权下的实测结果见[新版验收记录](docs/evidence/single-license-764025.md)。旧版本的测试记录见[0.2.0 原版客机实测](docs/evidence/native.md)。

## 构建和安装

当前游戏版本可按下方步骤从源码构建。仓库保留的 [0.2.0 历史包](dist/QueyangCoop-0.2.0-dev.zip) 适用于 Klei 744825。历史包哈希见[当时的验收记录](docs/evidence/native.md)。

好友只下载 ZIP 时，可退出游戏，将四个包文件解压到个人“文档”下的 `Klei\OxygenNotIncluded\mods\Local\QueyangCoop\`，然后在游戏 Mods 菜单启用。同一台机器选用一种安装方式即可，避免重复加载。

自行编译需要 Windows、Steam 版《缺氧》和 .NET SDK 9。项目按本机 ONI 构建号 764025 编译。若 Steam 不在默认库，先将环境变量 ONI_GAME_DIR 指向包含 OxygenNotIncluded.exe 的目录。两端使用同一个候选包并启用 Vanilla，关闭其他 Mod、DLC 后重启游戏。

1. 在仓库根目录运行环境检查和核心测试。预期 doctor 打印游戏库、程序集哈希和 SDK，测试全部通过。

~~~powershell
.\tools\doctor.ps1
dotnet test .\tests\Core.Tests\Core.Tests.csproj -c Release
~~~

2. 编译并打包。产物为 artifacts/QueyangCoop-dev.zip，两端解压后应得到两份 DLL 和两份 YAML。打包工具只收录本项目文件。

~~~powershell
dotnet build .\QueyangDuoren.sln -c Release
.\tools\package.ps1
~~~

3. 退出游戏后安装。先预览目标目录，确认后执行第二行。安装脚本会在替换旧开发包前保存备份并打印路径；可用 tools/restore-dev.ps1 -BackupPath 指定备份回滚。新装可在游戏 Mods 菜单禁用。

~~~powershell
.\tools\install-dev.ps1 -WhatIf
.\tools\install-dev.ps1
~~~

在 Mods 菜单启用 Queyang Co-op，按游戏提示重启。若安装后未出现入口，先检查重复 Mod、版本与 Player.log 中的 QueyangCoop 记录。首次试玩使用单人存档的副本。

## 两人进入同一局

1. **房主**载入殖民地副本。第一次用原单人档试玩时，游戏可能提示“Mod 差异”（MOD DIFFERENCES），核对新增项为 Queyang Co-op 后继续。打开暂停菜单的“多人合作”并创建 Steam 房间，点击“邀请 Steam 好友”或复制房间号。回到游戏并解除暂停后，复制人才会执行订单。
2. **客机**在游戏主菜单接受 Steam 邀请，或在“多人合作”中输入房间号。连接后等待临时存档传输、校验和原版加载画面完成，再用自己的镜头选区、挖掘和下蓝图。房主在不同区域移动镜头也能继续施工。
3. 使用游戏原有的研究、排班、个人优先级窗口和已接入的建筑侧栏。房主管理暂停、倍速、保存和关闭房间。
4. 客机离开后可从菜单重新加入，重新取得房主当时的殖民地副本与最新视图。房主重读存档时另开房间，旧命令随旧连接失效。

### 使用局域网调试

本机调试可用默认 127.0.0.1:24714。两台电脑处于同一可信局域网时，在房主启动游戏之前把 ONI_COOP_BIND 设为房主的局域网 IPv4 地址及端口（例如 192.168.1.23:24714），确保启动游戏的进程继承该变量，然后用“创建开发局域网房间”。客机填写该地址并点击“局域网加入”。此路径用于受控测试，玩家正式远程连接使用 Steam 好友房。

### 当前显示范围

连接好友房会传送并由本机游戏读取完整殖民地存档。与自己信任的好友开房，在副本上试玩。资源、环境和建筑状态按视口分区更新。打印舱选人、技能分配、复杂自动化及未接入的特殊面板由房主操作。大型殖民地、不同网络与多账号协作的实际范围见[原版客机验收](docs/evidence/native.md)。

## 开发入口

[设计总纲](docs/README.md) · [线协议实现](docs/evidence/protocol.md) · [开发验收矩阵](docs/05-verification.md) · [多 agent 委托书](docs/06-codex.md)
