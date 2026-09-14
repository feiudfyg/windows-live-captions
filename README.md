# LiveCaptions — 系统音频实时字幕 / 实时翻译（全本地 GPU 推理）

Windows 桌面应用：捕获**系统播放的音频**（WASAPI 回环），在本地 GPU 上运行语音识别（ASR）与大语言模型（LLM）翻译，将双语字幕显示在一个**可拖拽、置顶、半透明的悬浮窗**中。全程离线，音频不出本机。

```
系统音频 ──WASAPI 回环──▶ 16 kHz 单声道 ──▶ Qwen3-ASR（llama.cpp, GPU）
                                                   │ 文本
                                                   ▼
                                     Qwen3.5-9B（llama.cpp, GPU 翻译）
                                                   │ 译文
                                                   ▼
                                   透明置顶悬浮窗（原文 + 译文，可拖动）
```

## 功能

- **系统音频捕获**：WASAPI loopback（NAudio），自动重采样为 16 kHz 单声道；自适应噪声底噪的语音端点检测（VAD），无需手动调参
- **实时识别**（三种引擎可选）：
  - **Qwen3-ASR-1.7B**（默认）：52 种语言/方言、自动语种识别，GPU 加速；内置重复论惩罚与复读截断防退化
  - **Zipformer（sherpa-onnx）**：RNN-T 架构，**天然不会复读退化**，抗噪能力强，CPU 实时（RTF ≈ 0.05），提供 日语 / 中英双语 / 粤语 / 韩语 模型
  - **Whisper large-v3-turbo**：whisper.cpp CUDA 加速，兼容性最好
- **实时翻译**（三种后端可选）：本地 GGUF（内置 llama.cpp，开箱即用）；**托管 llama.cpp 服务**（官方 nightly，支持最新架构如 Qwen3.8-27B，应用自动启停进程，实测 27B IQ4_XS 约 200 ms/句）；外部 HTTP 服务（vLLM / LM Studio / 远程 OpenAI 兼容端点）
- **GPU 加速**：llama.cpp **CUDA 12**（默认）或 **Vulkan** 后端，全层 offload + KV cache 显存驻留；Whisper 走 CUDA 13；Zipformer 走 CPU（模型小、延迟低）
- **悬浮窗**：无边框、置顶、亚克力半透明；按住面板任意位置拖动、右下角拖动缩放；悬停显示工具栏（开始/暂停、清空、设置、退出）；右键菜单
- **设置窗口**：引擎/模型选择与一键下载、GPU 后端、源语言/目标语言、字号/透明度/行数、灵敏度，改动实时预览
- **模型管理**：内置模型目录、断点续传下载、tar.bz2 模型包自动解压（HuggingFace / GitHub releases）

## 管线与抗退化设计

```
系统音频 ─WASAPI 回环─▶ 16 kHz 单声道 ─▶ 自适应 VAD 分段
                                             │
              ┌──────────── 部分结果（间隔可调，按上一轮解码耗时自动退避）
              │                    │
              ▼                    ▼
      ASR 引擎（Qwen3-ASR / Zipformer / Whisper）
              │  句末标点或 ≥5s 提交；≥10s 强制提交；静音 750ms 断句
              ├─▶ 重复惩罚采样 + 复读截断 + 退化丢弃（三层防护）
              ▼
      文本守卫 ─▶ 逐句翻译（Qwen3.5-9B，CUDA/Vulkan，token 流式）
              ▼
      透明置顶悬浮窗（原文 + 译文，最多 N 行滚动）
```

## 环境要求

| 组件 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 19041+ / Windows 11 |
| .NET | .NET 10 SDK（运行时随构建输出，自包含 Windows App SDK） |
| GPU | NVIDIA GPU（CUDA 路径需要 CUDA 12 运行时，见下）或任意支持 Vulkan 1.3 的 GPU |
| 构建 | Visual Studio 2026 或 `dotnet` CLI + Windows SDK 10.0.26100 |

### 关于 CUDA 12 运行时

LLamaSharp 的 CUDA 后端二进制链接的是 **CUDA 12** 运行时（`cudart64_12.dll` / `cublas64_12.dll` / `cublasLt64_12.dll`）。
本机若只装了 CUDA 13 工具包（例如 CUDA 13.3），这些 DLL 互不兼容，需要单独获取 CUDA 12 运行时：

```powershell
pwsh scripts/fetch-cuda12-runtime.ps1
```

脚本从 NVIDIA 官方 redist 下载并解压到 `third_party/cuda12/x64`（约 740 MB，已在 .gitignore 中），
构建时会自动复制到输出目录。**未执行该脚本时**，应用会自动回退到 Vulkan 后端，功能不受影响。

## 构建与运行

```powershell
# 1) 获取 CUDA 12 运行时（可选，缺失则使用 Vulkan）
pwsh scripts/fetch-cuda12-runtime.ps1

# 2) 构建
dotnet build src\LiveCaptions\LiveCaptions.csproj -c Release

# 3) 运行（未打包应用，直接运行 exe）
src\LiveCaptions\bin\Release\net10.0-windows10.0.26100.0\win-x64\LiveCaptions.exe
```

首次启动若本地没有模型，会在状态栏提示；打开"设置"→ 下载推荐模型（ASR ≈ 2.4 GB、LLM ≈ 8.9 GB），
或手动把 GGUF 文件放入 `%LOCALAPPDATA%\LiveCaptions\models` 后在设置中选择。

## 性能参考（RTX 5090 D v2, 24 GB）

| 项目 | 结果 |
| --- | --- |
| Qwen3-ASR-1.7B (Q8_0) 识别 9 s 音频 | ~165 ms（≈ 54× 实时，CUDA） |
| Zipformer 日语 识别 5–10 s 音频 | ~150–400 ms（CPU，≈ 20–40× 实时） |
| Qwen3.5-9B (Q8_0) 单句翻译 | 首 token ≈ 40–80 ms，整句 ≈ 100–200 ms |
| 显存占用（ASR + LLM + KV 8192 上下文） | ≈ 13 GB |
| 应用冷启动（加载两个模型并预热） | ≈ 10 s |

Vulkan 与 CUDA 在短句场景下推理速度基本一致（差异 < 20 ms）；CUDA 整体启动更快（无 Vulkan 着色器编译），首次运行会有一次性内核初始化开销（约 18 s，驱动会缓存）。

## 模型下载说明

- 应用内下载使用 HuggingFace / GitHub releases 官方地址；若这些域名受限（代理路由问题），
  可手动下载后放入 `%LOCALAPPDATA%\LiveCaptions\models`：
  - Qwen3-ASR / Whisper / Qwen3.5：直接放 GGUF/bin 文件
  - Zipformer：解压 `.tar.bz2` 得到模型目录（含 `encoder*.onnx`、`decoder*.onnx`、`joiner*.onnx`、`tokens.txt`）
- GitHub 受限时可使用镜像，例如：
  `https://ghfast.top/https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/<模型包>`
- 再次打开设置窗口时，已存在的模型会自动识别

### llama.cpp 托管服务运行时

「本地 llama.cpp 服务」后端需要官方 nightly 运行时（llama-server.exe，约 500 MB 下载）：

```powershell
pwsh scripts/fetch-llama-server.ps1            # CUDA 13.3 版（默认）
pwsh scripts/fetch-llama-server.ps1 -Variant vulkan   # 或轻量 Vulkan 版（30 MB）
```

安装到 `data/runtime/llama.cpp/`。该后端支持最新模型架构（如 Qwen3.8-27B 的 MTP），
默认以 `--reasoning off` 启动（关闭思考，降低延迟），可自定义端口与附加参数。

## 目录结构

```
src/LiveCaptions/
  App.xaml(.cs)                应用入口、共享 UI 状态（App.Ui）
  MainWindow.xaml(.cs)         悬浮窗：字幕渲染、拖拽/缩放、工具栏、启动流程
  SettingsWindow.xaml(.cs)     设置窗口（模型、语言、外观、灵敏度、后端）
  Models/
    AppSettings.cs             配置模型（%LOCALAPPDATA%\LiveCaptions\settings.json）
    CaptionItem.cs             单条字幕（原文/译文/状态），INotifyPropertyChanged
    LanguageCatalog.cs         源/目标语言表 + "已是目标语言"启发式
    UiState.cs                 字号、面板颜色等可绑定 UI 状态
  Services/
    AudioCaptureService.cs     WASAPI 回环 → 16 kHz 单声道 float
    CaptionPipeline.cs         分段/VAD → ASR → 翻译 编排；跨线程 UI 调度
    Asr/Qwen3AsrEngine.cs      Qwen3-ASR（llama.cpp mtmd 音频输入）
    Asr/WhisperAsrEngine.cs    Whisper（whisper.cpp，CUDA/CPU 自动回退）
    TranslationService.cs      LLamaSharp 逐句翻译（Qwen3.5 ChatML + 关闭思考预填充）
    LlamaRuntime.cs            llama.cpp 原生库单次配置（CUDA/Vulkan 选择）
    Cuda12FirstSelectingPolicy.cs  自定义原生库选择策略（本地 cuda12 目录优先）
    ModelCatalog.cs            内置模型清单与下载地址
    ModelDownloadService.cs    断点续传下载（进度上报）
    SettingsService.cs         JSON 配置读写
    Log.cs                     轻量日志（%LOCALAPPDATA%\LiveCaptions\livecaptions.log）
tools/LiveCaptions.SmokeTest/  命令行验证工具（不依赖 UI）
scripts/fetch-cuda12-runtime.ps1
```

## 验证工具（SmokeTest）

用于在没有 UI 的情况下验证 GPU 管线：

```powershell
$exe = "tools\LiveCaptions.SmokeTest\bin\Release\net10.0-windows10.0.26100.0\LiveCaptions.SmokeTest.exe"
$models = "$env:LOCALAPPDATA\LiveCaptions\models"

# 回环捕获电平诊断（N 秒，可选存 wav）
& $exe capture 15 out.wav

# Qwen3-ASR 识别（最后参数为后端 auto|cuda|vulkan|cpu）
& $exe asr-qwen "$models\Qwen3-ASR-1.7B-Q8_0.gguf" "$models\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf" sample.wav English cuda

# Whisper 识别
& $exe asr-whisper "$models\ggml-large-v3-turbo.bin" sample.wav

# LLM 翻译
& $exe mt "$models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q8_0.gguf" "Hello world." "Simplified Chinese" cuda
```

## 配置说明（settings.json）

| 字段 | 说明 |
| --- | --- |
| `AsrEngine` | `qwen3-asr`（默认）/ `whisper` |
| `AsrModelPath` / `AsrMmprojPath` / `LlmModelPath` | 模型路径；留空使用 `%LOCALAPPDATA%\LiveCaptions\models` 下的推荐模型 |
| `GpuBackend` | `auto`（优先 CUDA，缺失时 Vulkan）/ `vulkan` / `cpu` |
| `SourceLanguage` / `TargetLanguage` / `TranslateEnabled` | `auto` 为自动语种识别；目标语言为翻译目标 |
| `VadThreshold` | 语音判定阈值上限（实际阈值 = 噪声底噪 ×3，自动适应） |
| `PartialIntervalMs` / `FinalSilenceMs` / `MaxUtteranceSeconds` | 部分结果间隔、断句静音时长、单句最大时长 |
| `TranslatePartials` / `PartialTranslateDelayMs` | 是否翻译进行中的部分结果及防抖延迟 |
| `FontSize` / `PanelOpacity` / `MaxLines` / `ShowOriginal` | 外观 |
| `ClickThrough` | 鼠标穿透（开启后只能通过本文件改回） |

## 已知限制

- 目前固定捕获**默认播放设备**；切换设备需重启监听
- 悬浮窗与字幕列表以 3 行滚动显示；更长历史未持久化
- Whisper 引擎使用 RMS 分段（Qwen3-ASR 为主要引擎，质量与语言覆盖更好）
- 无 CUDA 12 运行时的机器上 llama.cpp 自动使用 Vulkan（Whisper 仍可用 CUDA 13）
