# LiveCaptions — 系统音频实时字幕 / 实时翻译（全本地推理）

Windows 桌面应用：捕获**系统播放的音频**（WASAPI 回环）或**麦克风**，在本机 GPU 上运行语音识别（ASR）和大语言模型（LLM）翻译，把双语字幕显示在一个**可拖拽、置顶、半透明的悬浮窗**里。音频与文本全程不出本机。

```
系统音频 / 麦克风 ─▶ 16 kHz 单声道 ─▶ 自适应增益 ─▶ 流式 VAD 分段
                                                        │ 逐段识别
                                                        ▼
                              Cohere Transcribe（PyTorch bf16，GPU）
                                                        │ 原文 + 标点
                                                        ▼
                              Qwen3.8-27B（llama.cpp，GPU 翻译）
                                                        │ 译文
                                                        ▼
                                   透明置顶悬浮窗（原文 + 译文）
```

## 功能

- **音频捕获**：系统音频回环 / 麦克风 / 两者混合三种来源；自动重采样 16 kHz 单声道；自适应输入增益（AGC）补偿系统音量过低；设备切换与音频服务重启后自动重连。
- **语音分段**：默认 **FireRedVAD（流式）**，由本地 PyTorch sidecar 提供（最小静音 200 ms，10 ms 帧粒度）；sidecar 不可用时回退到内置 sherpa-onnx VAD（TEN VAD，缺失时自动下载）。停顿切句 + 窗口切分，不会截断语义词。
- **实时识别**（引擎可选）：
  - **Cohere Transcribe（PyTorch 全精度，推荐）**：CohereLabs 官方 bf16 权重，14 语言，自带标点与数字归一化；RTX 5090 上约 150 倍实时，8 秒窗口约 80–110 ms。
  - **sherpa-onnx**：Cohere Transcribe int8、Zipformer 日语/中英/粤语/韩语 RNN-T；默认 CPU，安装 CUDA 运行库后可切 GPU（设置里的「识别后端」）。
  - **Whisper large-v3-turbo**（whisper.cpp，CUDA/CPU 自动回退）：目前需在 `settings.json` 手动填写 `AsrModelPath`。
- **实时翻译**：本地 GGUF（进程内 LLamaSharp，CUDA 12 / Vulkan / CPU）、**托管 llama.cpp 服务**（官方 nightly，应用自动启停、自定义端口与参数，支持 Qwen3.8-27B 等新架构）、或任意 OpenAI 兼容 HTTP 端点（LM Studio / vLLM / 远程服务）。翻译带上下文连贯（上一条字幕）、术语与语气保持、脚本校验与三段自修复（加硬重试 / 直译修复 / 重写）。
- **仅原文字幕模式**：一键关闭翻译，只显示原文（不启动 LLM，节省显存）。
- **悬浮窗**：无边框置顶；按住面板任意位置拖动（DPI 精确）、右下角缩放；悬停显示工具栏（开始/暂停、翻译开关、清空、设置、退出）；右键菜单；三种背景效果——亚克力 / 高斯模糊（去色调的实时模糊）/ 简单半透明，不透明度最低 5%。
- **加载状态**：模型加载期间显示旋转指示并禁用开始/翻译按钮，避免误判卡死；设置窗口点关闭或叉叉 = 不保存、不重载。
- **模型管理**：内置模型目录与一键下载（HTTP 断点续传、tar.bz2 自动解压），也可手动放置文件。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 19041+ / Windows 11 |
| .NET | .NET 10 SDK（输出自包含 Windows App SDK 的未打包应用） |
| 构建 | Visual Studio 2026 或 `dotnet` CLI + Windows SDK 10.0.26100 |
| GPU | NVIDIA GPU（推荐；CUDA 路径）或任意 Vulkan 1.3 GPU / CPU |
| Python | 仅「Cohere Transcribe（PyTorch）」引擎需要：Python 3.12+（本仓库用 3.14） |

## 快速开始

```powershell
# 1) 克隆
git clone https://github.com/feiudfyg/windows-live-captions.git
cd windows-live-captions

# 2) 可选组件（按需）
pwsh scripts/fetch-llama-server.ps1        # 托管 llama.cpp 运行时（翻译后端 2，约 500 MB）
pwsh scripts/fetch-cohere-asr.ps1          # 全精度 Cohere sidecar（Python venv + 3.9 GB 权重）
pwsh scripts/fetch-cuda12-runtime.ps1      # 进程内 LLamaSharp 的 CUDA 12 运行库（约 740 MB）

# 3) 构建并运行
dotnet build src\LiveCaptions\LiveCaptions.csproj -c Release
src\LiveCaptions\bin\Release\net10.0-windows10.0.26100.0\win-x64\LiveCaptions.exe
```

发布（publish）会自动附带 XAML 编译产物（`.xbf` / `.pri`）与 CUDA 12 运行库：

```powershell
dotnet publish src\LiveCaptions\LiveCaptions.csproj -c Release
```

首次启动打开「设置」，选择识别引擎与翻译模型（可一键下载），或按下文手动放置。

### 关于 Cohere Transcribe（PyTorch）sidecar

- 权重仓库 `CohereLabs/cohere-transcribe-03-2026` 是 **gated** 的：先在模型页同意条款，再把 HuggingFace token 写入 `data/hf-cache/token`（该文件已被 gitignore），然后运行 `scripts/fetch-cohere-asr.ps1`。
- 应用启动识别时会自动拉起 sidecar（`tools/cohere_pytorch/server.py`，默认端口 12360）并轮询 `/health`；sidecar 同时提供 `/vad`（FireRedVAD 流式）。
- 子进程以离线模式运行（`HF_HUB_OFFLINE=1` / `TRANSFORMERS_OFFLINE=1`），不会在启动时访问网络。

### 关于 CUDA 12 运行时

进程内 LLamaSharp 后端链接的是 CUDA 12（`cudart64_12.dll` / `cublas64_12.dll` / `cublasLt64_12.dll`）；托管 llama-server 与 PyTorch sidecar 使用 CUDA 13。若没有 CUDA 12 运行库，进程内后端会自动回退 Vulkan（功能不受影响）。

## 数据目录

源码检出运行时，所有大数据都放在**仓库的 `data/`**（已 gitignore）；发布版回退到 `%LOCALAPPDATA%\LiveCaptions`。

```
data/
  settings.json          配置
  livecaptions.log       运行日志（UTF-8，4 MB 轮转）
  models/                ASR / VAD / LLM 模型文件
  runtime/
    llama.cpp/           托管 llama-server（fetch-llama-server.ps1）
    sherpa-cuda/bin/     sherpa-onnx CUDA 运行库（可选）
  python-env/            Cohere sidecar 的 Python 虚拟环境
  hf-cache/              HuggingFace 缓存与 token
  pip-cache/             离线 pip 缓存
  crashes/               WER 本地转储
```

## 设置说明（settings.json 主要字段）

| 字段 | 说明 |
| --- | --- |
| `AudioSource` | `system`（回环）/ `mic` / `both` |
| `AsrEngine` | `cohere-py`（PyTorch 全精度）/ `sherpa` / `whisper` |
| `AsrModelPath` / `AsrProvider` | sherpa 模型目录或文件；识别后端 `auto`/`cuda`/`cpu` |
| `CohereAsrPort` / `CohereAsrDtype` | sidecar 端口（12360，占用时自动换）；`bfloat16`/`float16`/`float32` |
| `LlmBackend` | `llama`（进程内 GGUF）/ `llamacpp`（托管 llama-server）/ `http`（外部端点） |
| `LlmModelPath` / `LlamaServerPort` / `LlamaServerExtraArgs` | GGUF 路径；托管服务端口（12359）与附加参数 |
| `LlmEndpoint` / `LlmEndpointModel` / `LlmHttpDisableThinking` | HTTP 后端地址、模型名、禁用思考 |
| `GpuBackend` / `GpuLayerCount` / `ContextSize` | 进程内后端的 CUDA/Vulkan/CPU、offload 层数、上下文 |
| `SourceLanguage` / `TargetLanguage` / `TranslateEnabled` | 源语言（`auto` 或具体语言）/ 目标语言 / 翻译开关 |
| `VadEngine` | `auto`（有 sidecar 用 FireRed，否则 TEN）/ `firered` / `ten` / `silero` |
| `PartialIntervalMs` / `FinalSilenceMs` / `MaxUtteranceSeconds` | 部分结果间隔 / 断句静音 / 单窗口上限（内部上限 8 s） |
| `FontSize` / `PanelOpacity` / `MaxLines` / `ShowOriginal` | 外观（字号 / 不透明度 5%–100% / 行数 / 始终显示原文） |
| `BackdropMode` | `acrylic`（默认）/ `blur`（高斯模糊）/ `simple`（简单半透明） |
| `AlwaysOnTop` / `ClickThrough` / `AutoStartCapture` | 置顶 / 鼠标穿透（改回需编辑本文件）/ 启动后自动监听 |
| `WindowX` / `WindowY` / `WindowWidth` / `WindowHeight` | 悬浮窗位置与尺寸（物理像素） |

## 性能参考（RTX 5090 D v2 24 GB）

| 项目 | 结果 |
| --- | --- |
| Cohere Transcribe bf16（PyTorch） | 60 s 音频 0.38 s ≈ **157× 实时**；8 s 窗口 80–110 ms；加载约 11 s |
| FireRedVAD 流式（sidecar，CPU） | 每 100 ms 音频约 15 ms |
| Qwen3.8-27B IQ4_XS（llama-server CUDA） | 200–700 ms/句（`--reasoning off`），首次请求含 CUDA 图预热（已用启动预热抵消） |
| 字幕提交延迟（语音停止→字幕上屏） | 中位约 140 ms（FireRed 细粒度分段） |
| 显存占用（27B + ASR + 桌面） | 约 20–22 GB |

## 验证工具（SmokeTest）

不依赖 UI 的命令行工具，用于离线验证各环节：

```powershell
$exe = "tools\LiveCaptions.SmokeTest\bin\Debug\net10.0-windows10.0.26100.0\LiveCaptions.SmokeTest.exe"

& $exe capture 15 out.wav                  # 系统回环捕获电平诊断
& $exe mic 10 both out.wav                 # 麦克风/混合捕获
& $exe asr-sherpa <模型目录> sample.wav ja auto   # sherpa（auto=有 CUDA 用 CUDA）
& $exe asr-whisper <ggml.bin> sample.wav   # Whisper
& $exe vad <vad.onnx> sample.wav 0.5 0.3   # VAD 分段统计
& $exe mt <model.gguf> "こんにちは" "Simplified Chinese" auto   # 翻译
```

设置环境变量 `SMOKE_TEXT_OUT=<file>` 可把识别/翻译结果按 UTF-8 追加到文件（避免控制台编码问题）。

## 代码结构

```
src/LiveCaptions/
  App.xaml(.cs)                   应用入口、共享 UI 状态
  MainWindow.xaml(.cs)            悬浮窗：字幕渲染、拖拽/缩放、工具栏、引擎编排
  SettingsWindow.xaml(.cs)        设置窗口（模型、语言、后端、外观，实时预览）
  Models/                         AppSettings / CaptionItem / LanguageCatalog / UiState
  Interop/                        Win32、窗口透明（DWM/accent）、背景效果
  Services/
    AudioCaptureService.cs        多源 WASAPI 捕获、混音、看门狗、自动重连
    CaptionPipeline.cs            AGC → VAD → 逐段 ASR → 字幕提交 → 翻译队列
    Asr/                          IAsrEngine 实现：sherpa / cohere-py / whisper
                                  VAD：Silero/TEN（内置）、FireRed（远程）
                                  PyTorchAsrServer / SherpaRuntime 运行时托管
    LocalLlamaServer.cs           托管 llama-server 子进程
    TranslationService.cs         进程内 LLamaSharp 翻译
    HttpTranslationService.cs     OpenAI 兼容端点 + 自修复
    TranslationText.cs            提示词与输出清理
    ModelCatalog.cs / ModelDownloadService.cs   模型清单与续传下载
    SettingsService.cs / Log.cs / AppPaths.cs
tools/LiveCaptions.SmokeTest/     CLI 验证工具
tools/cohere_pytorch/             Cohere sidecar（server.py）+ 评测脚本
scripts/                          下载/安装脚本（llama-server、CUDA12、Cohere）
```

## 已知限制与排查

- **音频服务偶发挂起**：Windows 音频引擎在休眠/设备切换后可能停止供数（表现为所有端点 peak=0、回环 0 字节）。应用会自动重连，但若系统级挂起可执行 `Restart-Service Audiosrv -Force` 恢复。
- **回环捕获的是系统全部声音**：其他应用（浏览器、播放器）的声音都会进入字幕；建议单独播放目标音频。
- **翻译串行执行**：一次只处理一条翻译；后端卡顿时队列会等待（不丢字幕，但延迟上升）。
- **ClickThrough（鼠标穿透）**：开启后窗口不可交互，需编辑 `settings.json` 关闭。
- **字幕历史仅在内存中**：显示最近 N 行，退出即清空。
- **首次请求延迟**：llama-server 首次生成包含 CUDA 图预热，应用已在加载时做一次预热翻译。
- 受限网络下 GitHub / HuggingFace 可用镜像（如 `https://ghfast.top/<原始地址>`）。

## 许可

AGPL-3.0（见 [LICENSE](LICENSE)）。
