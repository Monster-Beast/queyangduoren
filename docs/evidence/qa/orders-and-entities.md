# Klei 764025 原生订单、食物与装备验证

以下记录来自 Windows Steam 基础版，Unity 日志报告 `U60-764025-V (release)`。运行器使用存档副本。房主模拟、独立客机回放分别启动一次游戏，因此可以用一个 Steam 账号复现。报告、录像和存档留在本机的 `artifacts/qa/game-<运行编号>/`。

## 施工材料竞争

第 39 周期的存档副本里，夹具生成 100 kg 合法梯材异构树脂。两条同帧建造命令进入房主正式命令处理函数，在格子 48484 和 48496 建立梯子订单。

| 原版读数 | 两单已创建、尚未施工 | 两单取消后 | B 重新下单并完成后 |
|---|---:|---:|---:|
| 订单需求 | 200 kg | 0 kg | — |
| 可用库存 | 0 kg | 100 kg | — |
| 地面、持有及容器中剩余实物 | 100 kg | 100 kg | 0 kg |
| B 的建筑材料质量 | — | — | 100 kg |

复制人 76497 接到原版 `BuildFetch` 差事，预留、捡起并交付同一物堆 108328，最终建筑编号 108331。夹具提高了工人和待施工地点的优先级，搬运与消耗由 ONI 执行。证据来自运行 `137ac6d274bc452c81378d5c41e9480c/host.report` 的 `MATERIAL_RESERVED`、`MATERIAL_REFUND`、`MATERIAL_HAUL`、`MATERIAL_DELIVER` 和 `PASS material competition`。

## 食物与动物的生命周期

第 3 周期房主记录 `f0f25b5244c44a308331d32934a7a15f`。夹具让米虱木成熟、把孵化进度推过阈值，原版收获生成食物，蛋孵化为幼哈奇。之后通过容器自身的 `Storage.Store/Drop` 放入和取出这份食物，分别对照容器的原版质量和快照。夹具主动压低腐败计量并触发幼哈奇死亡，等待 ONI 生成实际掉落物。

| 观察对象 | 房主实物读数 | 客机回放的判定 |
|---|---|---|
| 收获与孵化 | 食物 2416、新鲜度约 25%，幼哈奇 2421 | 作物重新生长，旧蛋的选中状态关闭，幼体可选 |
| 食物存取 | 1 kg、600 kcal，容器 11 持有同一物体 | 地面详情关闭，归属正确，取出后可重新选中 |
| 腐败替换 | 旧食物销毁，腐烂物 2427 保留 1 kg；原来的 600 kcal 消失 | 旧食物的状态被移除，新的腐烂物可选、质量相符 |
| 幼体死亡 | 新肉堆 2430，2 kg、3200 kcal | 旧动物选择关闭，新肉堆的质量和热量与房主相符 |
| 穿戴 | 复制人 79 穿上、脱下保暖背心 2431 | 穿上时归属复制人，脱下后能重新选中地面背心 |

上述客机证据来自 `3e637c0639c04a6eb8c4a901116ef818/guest-native.report`。该次录像记录了装备归属。

后续房主运行 `10850d87a5274314a0cd15637e67fcba` 跟踪复制人 79 穿脱背心 2483，直接读取原版 `SymbolOverrideController` 的最高优先级来源与动画控制器可见性，记录服装资源 1874709884、部位符号 1188544747。独立客机运行 `4958d4f30947420ea1b0c4c7f421dc92` 的 `NATIVE_EQUIPMENT_GUEST ... symbolReadback=True` 通过穿上可见、脱下消失的断言。该轮客机随后在新复制人详情阶段超时，此处只引用当轮已经完成的服装检查。

## 订单编辑

同一房主记录中的 `NATIVE_ORDER_HOST` 和 `NATIVE_MOP_HOST` 确认种子清扫和米虱木采收开关可以取消，第二个编辑者持旧优先级写入时，原版组件保留第一个编辑者的值。取消后给同一物品重开清扫单，旧令牌无法取消新单。拖地测试用 8 kg 水造出真实浅水格，创建原版拖地标记，拒绝重复命令，取消后格子标记消失并拒绝迟到的取消。

新版夹具为扫地、调优先级、采收、拖地各写入一份 `order-i.bin` 和操作前、后的两张视区，供客机用原版拖动工具回放。运行 `10850d87a5274314a0cd15637e67fcba` 的重叠格测试在建筑锚格 49792 与非锚格 49791 读到同一建筑菜单。建筑令牌 115、种子清扫令牌 179 分别生效。过期清扫失败后建筑菜单保持可用，结果为 `NATIVE_OVERLAP_HOST ... wrongObjectRejected=true buildingMenuIntact=true`。

## 正式房主复验与设施并发修改

正式运行 `2d5928897d7146b7ac77054bbbc7f6f9` 使用第 3 周期 Vanilla 存档。测试包的 SHA-256 为 `398CB883BC2410AE0988A4F6758F581F1A47242EA83BCC747253167B282604DC`，原始存档为 `F115FE51875668F0AA027A576E3C468291BF5D6B25267227ED9F5FB08A594993`。该轮包含独立的复制人分页，并重新通过上面的生态、装备、订单检查。

| 对象 | 房主正式命令及原版组件读数 |
|---|---|
| 仓储设施 11 | 一批添加两种物品。下一批的第一项合法、第二项包含已添加物品，房主拒绝整批，`TreeFilterable.AcceptedTags` 与提交前完全一致。随后一批移除两项成功。|
| 糊糊制造机 2157 | 泥土和水制软泥糕配方的 `ComplexFabricator.GetRecipeQueueCount` 依次为 0、2、3、2、无限。两位编辑者都看到初始 0，第二笔基于 0 的写入被拒绝，队列保持 2。|
| 门 2497 | 从正式建筑菜单取得带版本的操作，读原版 `Door.RequestedState` 确认为 Opened，重发旧操作被拒绝。修改复制人 79 的权限后，`AccessControl.GetSetPermission` 对 79 返回 GoLeft，复制人 81 的权限保持原值。旧授权变更被拒绝。此项验证模式请求，门的实际运动和自动化信号另见地形与管网实测。|
| 新打印人 2487 | 新人存在于原版实时名单，拥有 Mining1 技能，食物和药物限制可查询。设施测试后，夹具触发 ONI 的 Slain 死亡流程。在格子 49795 确认其从在世名单移除，`nm/vitals` 与 `nm/profile` 都返回缺失。录下死亡前、后分别覆盖该存档 ID 的原始视图区。|

房主证据在该轮 `host.report` 的 `NATIVE_FILTER_HOST`、`NATIVE_RECIPE_HOST`、`NATIVE_DOOR_HOST`、`NATIVE_PRINTED_DEATH_HOST` 和 `FEATURE_CHECKS_DONE`。随后运行器用不含生产联机程序集的独立游戏进程重开房主手动保存的档，`readback.report` 报告 `PASS native host save re-opened`，最终退出码 0，输入哈希校验通过。

## 本地复测

1. 将 `$materialSave` 指向有解锁梯子、可用工人和可达施工地点的 Vanilla 存档。运行以下命令，预期末尾出现 `PASS material competition`。

   ```powershell
   .\tests\Integration\Game\run.ps1 -SavePath $materialSave -MaterialCompetition
   ```

2. 将 `$colonySave` 指向含复制人、可用储物容器和可种植区域的 Vanilla 存档。运行后在本轮输出目录找到 `host.report` 和 `native-recording`，预期有 `FEATURE_CHECKS_DONE`。

   ```powershell
   .\tests\Integration\Game\run.ps1 -SavePath $colonySave -NativeRecord -Persistence
   ```

3. 将 `$recording` 设为上一步的 `native-recording` 目录。回放的 `guest-native.report` 应包含对应夹具的 `NATIVE_*_GUEST` 行。旧录像可以跳过尚未采集的项目，报告中的 `SKIPPED` 和 `symbolReadback=False` 应保留在结果里。

   ```powershell
   .\tests\Integration\Game\run.ps1 -NativeReplay $recording
   ```
