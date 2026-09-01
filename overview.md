# HTF Cheat — PlayerOps 队友操作模块实现报告

## 完成内容

在现有 IMGUI 菜单中新增**「队友」页签**（位于「玩家」与「房主」之间），实现参考图中的队友操作能力，并拓展出信息监控、单体/批量操作、限制控制、物理恶搞、循环执行、运行日志六大功能域。

## 新增文件

| 文件 | 职责 |
| --- | --- |
| `源码/CheatPlugin/OperationLog.cs` | 操作日志队列（200 条上限、分类、等级、时间戳、复制/清空） |
| `源码/CheatPlugin/TaskScheduler.cs` | 循环/定时任务调度器（基于 Time.unscaledTime） |
| `源码/CheatPlugin/PlayerMonitor.cs` | 玩家信息缓存（0.5s 刷新）、玩家 ESP 数据准备 |
| `源码/CheatPlugin/PlayerOps.cs` | 玩家操作核心逻辑（传送、击杀、治疗、给物、限制、恶搞、房间工具） |

## 修改文件

| 文件 | 变更 |
| --- | --- |
| `CheatState.cs` | 新增 PlayerOps 状态字段：吸全队、玩家 ESP、目标范围、循环次数/间隔 |
| `CheatOps.cs` | `LiftToSafeGround` 改为 internal 供 PlayerOps 复用；其余复用其 CmdTeleport、ParseAmount、DisplayName 等 API |
| `Patches.cs` | 新增 4 个动态 Harmony 补丁：禁止购买、禁止拾取、禁止跳跃、禁止攻击 |
| `CheatMenuModel.cs` | 新增「队友」页签及 4 个子菜单（玩家列表/批量/恶搞/房间） |
| `CheatPanel.cs` | 新增 4 个自定义绘制区、玩家 ESP 绘制、PlayerOps.Update 接入 |

## 已实现功能

- **玩家列表**：下拉选择队友、刷新、信息卡（血量/位置/距离/持有/SteamID）
- **单体操作**：传到他、拉到我、治疗、杀死、点燃、中毒、复活自己
- **批量操作**：目标范围（全队/除自己/仅选中/仅非主机）、循环次数/间隔、给全队物资、给全队金钱、全部掉落、杀死全队、杀死所有动物、全员治疗、全员传送到我/Boss、全员清背包
- **限制控制**：锁定禁动、禁止购买/拾取/跳跃/攻击、持续卸装备、击飞、一键解除
- **物理恶搞**：吸队友、吸全队、高空抛投、旋转、缩小/放大/恢复、倒吊、弹球
- **房间管理**：复制房间信息、踢人、封禁、切换昼夜/天气（部分标记为实验性）
- **运行日志**：滚动显示、分类过滤、复制全部、清空

## 编译与部署

```bash
cd "D:/Desktop/项目/How to Fish/源码/CheatPlugin"
"D:/Desktop/项目/How to Fish/工具/dotnet-sdk/dotnet.exe" build HTFCheat.csproj -c Release -o "D:/Desktop/项目/How to Fish/工具/build-out/htfcheat" -v m

# 产物已同步到注入器 payload
cp -f htfcheat/HTFCheat.dll htf-injector/payload/HTFCheat.dll
cp -f htfcheat/HTFCheat.pdb htf-injector/payload/HTFCheat.pdb
```

- **编译结果**：0 错误 / 5 警告（与基线一致，无新增警告）
- **注入入口**：`工具/build-out/htf-injector/双击注入.bat`，进游戏按 F9，页签切到「队友」

---

## 审计修复记录（第二版）

对首版实现做全量审计，发现并修复以下问题（均为"能编译但运行时不成立"的缺陷）：

### P0 致命（已修复）

| 编号 | 问题 | 后果 | 修复 |
| --- | --- | --- | --- |
| P0-1 | `TaskScheduler.Tick()` 从未被调用 | 所有循环任务与批量操作（注册后依赖 Tick 执行）完全不触发 | 在 `CheatPanel.Update()` 接入 `TaskScheduler.Tick()` |
| P0-2 | 4 个动态补丁的 `TargetMethod()` 目标缺失时返回 null | Harmony 的 `PatchAll` 抛异常 → `PatchFailed=true` → **整个作弊器不可用** | 新增 `PlayerOpsPatchStubs` 兜底桩方法，找不到真实目标就打在不被调用的桩上，功能静默失效但绝不拖垮其它补丁 |

### P1 严重（已修复）

| 编号 | 问题 | 修复 |
| --- | --- | --- |
| P1-1 | `ConfirmButton` 被套在 `if (GUILayout.Button(...))` 内部，二次确认逻辑失效（点第一下才冒出确认按钮） | 7 处全部改为直接调用 `ConfirmButton`，由它自己画按钮并检测点击 |
| P1-2 | `HTF_NoPickup`/`HTF_NoAttack` 用 `GetComponent<PlayerMovement>()`/`GetComponentInParent<PlayerAimAssist>()` 再反射取私有字段，组件缺失时 `GetValue(null)` 抛 TargetException | 新增 `PlayerOpsPatchUtil.FindPlayer()`：统一用 `GetComponentInParent<Player>()`，null 安全 |
| P1-3 | 玩家 ESP 被 `if (!CheatState.EspOn) return` 连带屏蔽，开玩家 ESP 关生物 ESP 时完全不画 | `DrawEsp` 重构为 `DrawCreatureEsp()` + 独立玩家 ESP 绘制，两个开关互不依赖 |
| P1-4 | 新增的 3 个文本框（循环次数/间隔/金额）未纳入 `_uiTyping` 聚焦判断，打字时按 F9 会误关面板 | 文本框命名 + `_uiTyping` 判断补充 `tm_loop`/`tm_interval`/`tm_money` |

### P2 中等（已修复）

| 编号 | 问题 | 修复 |
| --- | --- | --- |
| P2-1 | `ResurrectSelf` 无 DeadPlayer 时传 null 进 `Server.ResurrectPlayer`，可能内部 NRE | 改为明确提示"未死亡/无组件"，不赌运气 |
| P2-2 | `HealPlayer` 兜底只查字段，Health/MaxHealth 若是属性会静默失败 | 属性、字段各查一次，都拿不到记警告 |
| P2-3 | 给全队金钱是空实现却记"成功"日志，误导用户 | 按钮改为"给全队金钱(不可用)"，日志明确说明 MoneyManager 仅支持本地玩家 |
| P2-4 | `LiftToSafeGround` 通过反射调用 CheatOps 私有方法，有失败风险 | 改为 `internal` 直接调用，删除反射壳类 |
| P2-5 | `UpdateDisarm` 每帧反射 `DropItem` | 缓存为静态 `MethodInfo` |
| P2-6 | 生成器未选物品时批量给物静默失败 | 日志提示"请先在生成页选择物品" |

---

## 游戏内实测反馈修复（第三版）

用户实弹测试反馈三个问题，用 ilspycmd 反编译 `Assembly-CSharp.dll` 定位根因并修复：

### 1. Steam 刷屏 `k_EResultLimitExceeded`（P0）

- **根因**：`PlayerOps.Update()` 在**客户端**每帧改远端玩家 transform/velocity。FishNet 是服务器权威架构，客户端对非本机玩家对象写 transform 属"非法写"，触发同步风暴 → Steam P2P 发送限流刷屏。
- **修复**：所有每帧恶搞统一加两道闸——① 非主机直接跳过（`if (!CheatOps.IsServerReady) return;`）；② 节流到 0.15s（~6.7Hz）。位置类（锁定/吸人）改用 `Server.Instance.TeleportPlayer` 走合法同步通道。

### 2. 点燃/中毒无效（P1）

- **根因**：反射方法名错误——`PlayerVitals` 里**没有** `SetOnFire`/`Poison`，反编译确认公开入口是 `ApplyNewFire()` / `ApplyNewPoison()`（主机端把 `_syncedFire`/`_syncedPoison` SyncVar 置 100）。
- **修复**：`IgnitePlayer`/`PoisonPlayer` 改为直接调用 `vitals.ApplyNewFire()` / `vitals.ApplyNewPoison()`，删除失效反射。
- **连带**：`HealPlayer` 改为直接调用公开 `vitals.Heal(9999)`（内部有主机门控 + clamp 0~100）；`KillPlayer` 改为 `target.Dying.ServerDie(Vector3.zero)`（绕过伤害计算与 0.25s 无敌窗口，**显式加主机检查**——ServerDie 无门控，非主机调用会生成幽灵尸体）。

### 3. 传送到好友旁边落点飘到海上（P1）

- **根因**：`CmdTeleport("player", idx)` 走 `LiftToSafeGround` 统一出口——它从 y+60m 向下射线查 `GameInfo.LevelLayer`，好友站在船上/码头/海面时探不到 Level 层 → 返回 `anchor+2.5m` → 落点直接飘到海上。
- **修复**：`"player"` 分支改为直接传送 `target.transform.position + Vector3.up * 2f` 并提前 return，不经过抬升逻辑。玩家 transform.position 即脚下位置，+2m 必然落在身边。

### 编译验证

- 全量重编译（`--no-incremental`）：0 错误 / 5 警告（与基线一致）
- payload 已同步：`工具/build-out/htf-injector/payload/HTFCheat.dll`

## 已知限制

1. **主机权限**：修改他人状态的操作需主机权限；非主机时按钮置灰（Custom 区块未自动置灰，实际执行时会通过 CheatOps.IsServerReady / Server 调用自身拒绝）。
2. **反射兜底**：禁止购买/拾取/跳跃/攻击补丁使用动态方法名解析；目标缺失时该功能跳过（日志有 "未绑定" 警告），不再影响其它补丁。点燃/中毒/治疗/击杀已改为反编译确认的公开 API（ApplyNewFire/ApplyNewPoison/Heal/ServerDie）。
3. **给金钱**：MoneyManager 仅暴露本地玩家通道，无法给指定玩家/全队真实到账；按钮已标注"不可用"，点击只记录警告日志。
4. **房间密码/最大人数/昼夜天气/封禁**：FishNet 房间相关 API 未在源码中直接暴露，当前为实验性占位，实际游戏中可能无效。
5. **玩家 ESP**：最多显示 16 个最近玩家，超出不画。
6. **吸人/锁定/旋转等每帧恶搞**：仅在主机端执行（非主机静默跳过），节流 0.15s，位置类走 Server.TeleportPlayer 合法通道，避免 Steam 发送限流。

## 后续建议

- 游戏内实测「禁止购买」「禁止拾取」补丁是否命中正确方法名；如未生效，在 Patches.cs 的 TargetMethod 中补充版本别名。
- 若需要真正的「给指定玩家金钱」，需进一步反编译 MoneyManager 找到按 Player 加钱的 ServerRpc。
- 危险操作（杀死全队/高空抛投/清背包）已实现二次确认（按钮 2 秒内需再点一次）。
