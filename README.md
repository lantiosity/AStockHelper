# A股实时行情桌面小组件

基于 **TickDB WebSocket API** 的 Windows 桌面端 A 股实时行情悬浮窗。

- 显示：股票名称、股票代码、实时价格、实时涨跌幅
- 数据：TickDB WebSocket 实时推送 + REST 行情快照兜底（启动、新增自选、每分钟刷新，休市/断流时也能显示最近行情）
- 搜索：内置 A 股代码/名称数据库（沪、深、北），按名称或代码搜索
- 形态：无边框、可拖动、非始终置顶、窗口高度随自选股数量和屏幕 DPI 自适应
- 颜色：涨红、跌绿、持平灰；休市时股票名称灰色，开盘时黑色
- 字体：优先使用 `DINW10-Medium`，缺失时回退 `Segoe UI` / 微软雅黑

## 环境要求

- Windows 10 / Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 通常自带）
- TickDB API Key

## 配置 API Key

编辑 `src/AStockWidget/config.json`（或在编译输出目录中找到同名文件）：

```json
{
  "ApiKey": "在这里填写你的 TickDB API Key",
  "WebSocketUrl": "wss://api.tickdb.ai/v1/realtime"
}
```

> API Key 只保存在本地配置文件中，不会提交。

## 构建与运行

```bash
cd AStockWidget

# 还原 NuGet 包（需要能访问 nuget.org）
dotnet restore

# 编译
dotnet build -c Release

# 运行
dotnet run --project src/AStockWidget -c Release
```

也可以直接用 Visual Studio 2022+ 打开 `AStockWidget.sln`，按 F5 运行。

运行目录下会自动生成 `config.json`；首次运行时请在 `config.json` 中填入 API Key 后重启应用。

## 使用说明

1. 在顶部搜索框输入股票名称（如“贵州茅台”）或代码（如 `600519`）。
2. 点击搜索结果即可添加自选股。
3. 鼠标悬停自选股行，点击右侧 `×` 可删除。
4. 按住顶部标题栏可拖动窗口；右上角 `–` 最小化，`×` 关闭。
5. 自选股列表与窗口位置会自动保存到 `%AppData%\AStockWidget\settings.json`，下次启动自动恢复。

## 数据说明

- `Data/ashare_stocks.json`：内置 A 股股票数据库（约 4900+ 条，来源为公开交易所列表）。
- `Data/market_days.json`：2024–2026 年 A 股节假日/调休休市日历。
- 休市判断顺序：优先依赖 TickDB 推送中可能携带的市场状态/交易时段字段（当前 A 股 ticker 未提供时），否则按“北京时间工作日 + 节假日/调休日历 + 上午 09:30–11:30 / 下午 13:00–15:00”推算。

## 更新内置数据

如需刷新股票代码/名称数据库或节假日日历，可运行：

```bash
python tools/generate_data.py
```

脚本会从公开数据源下载最新 A 股代码列表和节假日/调休日期，并写入 `src/AStockWidget/Data/`。
## 项目结构

```
AStockWidget/
├── AStockWidget.sln
├── NuGet.Config
├── tools/
│   └── generate_data.py
└── src/AStockWidget/
    ├── Program.cs
    ├── MainForm.cs                 # WinForms 宿主窗口 + WebView2 协调
    ├── config.json                 # API Key 配置
    ├── app.manifest
    ├── Models/
    │   ├── StockInfo.cs
    │   ├── TickerQuote.cs
    │   └── AppSettings.cs
    ├── Services/
    │   ├── AppConfig.cs
    │   ├── StockDatabase.cs
    │   ├── SettingsStore.cs
    │   ├── MarketCalendar.cs
    │   ├── TickDbWebSocketClient.cs
    │   └── TickDbRestClient.cs
    ├── Data/
    │   ├── ashare_stocks.json
    │   └── market_days.json
    └── Web/
        ├── index.html
        ├── style.css
        └── app.js
```

## 开源协议

本项目采用 [MIT License](LICENSE) 开源协议发布，允许自由使用、修改、分发及商用，仅需保留版权与许可声明。

## 备注

构建时可能出现来自 WebView2 包对 WPF 程序集的 `MSB3277` 引用冲突警告，不影响生成与运行。
