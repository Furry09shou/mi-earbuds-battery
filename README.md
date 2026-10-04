# 绒汇耳机助手（RonghuiEarbuds）

Windows 托盘工具：通过解析耳机 BLE 广播，实时显示**左耳 / 右耳 / 充电仓**电量。无需连接、无需配对，纯被动监听。不限品牌——每种广播格式逆向后登记解析档案即可适配。

## 已适配机型

| 机型 | 状态 | 产品标识（0x038F [8..10]） |
|---|---|---|
| Xiaomi Air2 SE | ✅ 完整支持（双耳 + 充电仓） | `25 18 67` |

其他型号？用软件内置的「适配新耳机」向导采集数据并上传 Issue，即可众包扩展。

## 目录结构

```
├─ src/RonghuiEarbuds.App      主程序（WPF .NET 8，托盘 + 电量面板）
├─ setup/RonghuiEarbuds.Setup  安装/卸载程序（一个 exe 两用，--uninstall 卸载）
├─ probe/RonghuiEarbuds.Probe  逆向采集工具（广播监听 + GATT dump，适配新耳机用）
├─ probe_logs/                 采集原始数据（gitignore，不入库）
├─ assets/                     品牌资源（logo、多尺寸 .ico）
├─ dist/                       发布产物（构建脚本输出，gitignore）
├─ build.cmd                   一键构建：主程序 → 安装器 → dist\
└─ .github/ISSUE_TEMPLATE      适配数据提交模板
```

## 构建与安装包

需要 .NET 8 SDK（Windows）。**一键构建**（自动打包主程序进安装器）：

```
build.cmd
```

最终安装包输出在 **`dist\RonghuiEarbuds.Setup.exe`**——单个 exe 即可分发，双击安装。

手动构建：

```
dotnet build src/RonghuiEarbuds.App/RonghuiEarbuds.App.csproj -c Release
dotnet build setup/RonghuiEarbuds.Setup/RonghuiEarbuds.Setup.csproj -c Release
```

## 功能

- 三张电量卡片：左耳 / 右耳 / 充电仓，动画圆环 + 充电状态（在仓充电 ⚡ / 使用中）
- 托盘图标显示最低电量数字，悬浮提示三路电量
- 低电量（<20%）气泡提醒
- 自动绑定附近的耳机，重启免重连（配置持久化）
- 开机自启开关（写注册表 Run 键）
- 关闭/最小化到托盘，窗口位置记忆

## 原理

Air2 SE 在**充电仓盖打开**或**耳机（主机）工作中**会持续发送 BLE 广播，厂商数据 `CompanyId = 0x038F` 中携带三路电量：

```
数据段: 16 01 01 XX [b4] [b5] [b6] [b7] 25 18 67 ...
                      │    │    │    └─ 充电仓电量（低7位）
                      │    │    └─ 右耳电量（低7位）| 0x80=在仓充电
                      │    └─ 左耳电量（低7位）| 0x80=在仓充电
                      └─ 恒定值字段（含义未知）
[8..10] = 产品标识 25 18 67（Air2 SE）
[3]     = 状态标志（0x01=左耳在仓外，0x80=放回充电瞬间闪现）
```

- 广播停止（仓盖合上）后约 8 秒，面板变暗显示"信号丢失"，电量冻结为最后已知值
- 新型号只需用 `probe/` 采集广播、确认字节布局后在 `XiaomiAdvParser.Profiles` 登记新档案

## 免责声明

仅供学习研究，与小米官方无关。
