# Screen Receiver

局域网单向屏幕投射原型：Windows 采集屏幕 → 本地 HTTP 服务 → iPhone Safari 显示最新截图。

## 项目结构

```
ScreenReceiver/
├── src/
│   ├── ScreenReceiver.Contracts/   # 共享 DTO（FrameMetadata、错误码）
│   ├── ScreenReceiver.Server/       # ASP.NET Core Minimal API + wwwroot 前端
│   │   ├── Api/                     # 端点 + Bearer 鉴权中间件
│   │   ├── Services/                # FrameStore（最新帧缓存）、SessionService
│   │   └── wwwroot/                 # iPhone 端 HTML/JS/CSS
│   └── ScreenReceiver.Desktop/      # WPF 启动器（采集 + 后台宿主 Server）
│       └── Capture/                 # IScreenCaptureService（可替换 Graphics Capture）
└── tests/
    └── ScreenReceiver.UnitTests/    # xUnit 单元测试
```

## 前置条件

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## 构建与运行

```powershell
# 还原并构建
cd C:\PROJECTS\辅助\ScreenReceiver
dotnet build

# 运行 WPF 桌面端
dotnet run --project src\ScreenReceiver.Desktop
```

启动后：
1. Windows 窗口会显示一个配对 URL，形如 `http://192.168.x.x:PORT/?token=xxxx`
2. 确保 iPhone 和 Windows 连同一个 Wi-Fi
3. iPhone Safari 打开该 URL（可用相机扫二维码，或手动输入）
4. 点击 Windows 窗口的 **Start Sharing**，iPhone 上每秒刷新一帧画面

> 首次运行 Windows 防火墙会弹窗，必须勾选"专用网络"放行。
> 路由器若开启"客户端隔离/AP 隔离"，设备间将无法互访，需在路由器关闭。

## 配置视觉模型 API

默认走 DeepSeek 视觉模型 `deepseek-flash`。在启动 Windows 程序前，设置环境变量：

```powershell
# 当前 PowerShell 窗口临时生效
$env:LLM_API_KEY = "sk-你的deepseek-key"
# 模型和地址有默认值，不设也行；想切换其他厂商再改：
# $env:LLM_MODEL = "deepseek-flash"
# $env:LLM_BASE_URL = "https://api.deepseek.com/v1"
dotnet run --project src\ScreenReceiver.Desktop
```

DeepSeek 视觉模型文档：https://api-docs.deepseek.com/guides/vision/

### 切换其他厂商（可选）

代码是 OpenAI 兼容接口，改环境变量就能切：

| 厂商 | LLM_BASE_URL | LLM_MODEL 示例 |
|---|---|---|
| DeepSeek（默认） | `https://api.deepseek.com/v1` | `deepseek-flash` |
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-vl-plus` |
| 火山引擎豆包 | `https://ark.cn-beijing.volces.com/api/v3` | `doubao-seed-vision` |

不想用 DeepSeek 的话，qwen-vl-plus（输入 0.8 元/百万 token）性价比最高；豆包 Seed-Vision（输入 0.15 元/百万 token）最便宜。

## 手机端两种识别模式

| 模式 | 用法 |
|---|---|
| **手动识别** | 切到这道题后，点"📷 识别这道题"按钮，等几秒出答案和解析 |
| **自动识别** | 选间隔（5/10/15/30 秒），点"开始自动"，每隔 N 秒自动识别当前画面 |

> 自动模式会持续消耗 API 额度，建议用 10–15 秒间隔。识别结果替换上一次。

## HTTP API（/api/v1）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查（无需 token） |
| GET | `/session` | 当前会话状态 |
| POST | `/session/start` | 开启共享 |
| POST | `/session/stop` | 停止共享 |
| POST | `/frames` | 上传最新 JPEG（`X-Frame-Id` 头） |
| GET | `/frames/latest` | 获取最新 JPEG（二进制） |
| GET | `/frames/latest/meta` | 获取最新帧元数据 |
| POST | `/analyze` | 把最新帧发给 DeepSeek，返回答案解析 |
| GET | `/deepseek/status` | 查询 API 是否配置好 |

所有 `/api/v1/*`（除 `/health`）需要 `Authorization: Bearer <token>` 头。

## 安全边界

- 只监听局域网网卡，不暴露公网
- 每次启动生成随机会话 token
- 只保留最新一帧，不写历史截图到磁盘
- 停止共享后立即清空缓存、撤销会话
- **仅用于合法的远程查看与学习辅助场景**，勿用于正式考试。

## 后续扩展（不在第一版）

- 把 `ScreenCaptureService` 换成 `Windows.Graphics.Capture`（窗口选择器）
- 加 QR Code 生成（如 `QRCoder` 包）替代手动输入 URL
- JPEG 质量可调、采集间隔可调
- 断网重连退避、帧大小统计 UI
