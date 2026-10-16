# 译流 LingoFlow

Windows 桌面语音翻译软件。捕获指定应用及其子进程的播放音频，使用本地语音识别，再通过本地模型或 OpenAI 兼容云端接口翻译，显示双语悬浮字幕。

当前版本：**0.2.4**。支持中文、英语、日语、韩语，包含中英、中日、中韩互译及原语言自动识别。

## 功能

- 本地 Whisper 语音识别、Silero 人声检测与可疑识别复核；语音不上传云端。
- 完全本地或混合方案：文字翻译默认使用 Hy-MT2 1.8B Q4_K_M，也可调用 DeepSeek 等 OpenAI 兼容接口。
- 设备检测与方案推荐；兼容的 NVIDIA 显卡优先 GPU，启动失败可回退 CPU。
- 快速 / 精准两种处理模式，流式译文、打字机效果、多行字幕，可调整尺寸、颜色、透明度，支持窗口最小化和托盘运行。
- 解锁悬浮字幕后可开始、暂停、继续、停止；默认穿透切换快捷键为 `Ctrl + Alt + Shift + L`，可自定义。
- 导入音视频后翻译并导出字幕；停止实时会话后导出双语、原文、译文三份 SRT。
- 云端字幕精修优先通读整段原文，结合上下文校对明显识别错误，再逐条翻译；保留时间轴和校对记录，过长时分批。
- 失败片段保留原文和错误位置，后续继续处理；支持取消后导出已完成部分。
- 首次下载校验所需模型和组件，支持 HF Mirror；组件解压及校验成功后自动删除 ZIP，下次直接复用已解压文件。

## 环境与启动

需要 Windows 10 / Windows 11 **x64**。本地识别引擎要求 AVX2。进程音频捕获的可用性取决于 Windows 版本与补丁；系统不支持时可主动选择“系统全部声音”，这会包含其他应用的声音。Windows 10 的不同设备仍需实际验证。

源码仓库不包含安装包、模型权重、个人设置、用户音视频、字幕和开发验证数据。请先按下方步骤构建。

1. 启动构建后的 `LingoFlow.exe`，进入“设备与模型”，查看配置推荐并选择模型保存位置。
2. 点击“下载 / 校验所需组件”，或开始翻译时让软件自动准备；第一次准备需要网络。
3. 云端文字翻译在“接口与外观”填写基础地址、模型名及 API Key。例如基础地址 `https://api.deepseek.com/v1`，填写服务商当前支持的模型名。密钥仅在本机保存，不要提交到 GitHub。
4. 在“实时翻译”刷新并选择应用（如 Edge），设置原语言与目标语言，再开始翻译。捕获包含所选进程树，浏览器可能包含多个标签页。
5. 关闭主窗口会收起至托盘；彻底退出请使用托盘菜单。

完全本地方案准备好后可断网使用。混合方案仅向所配置的文字接口发送识别文字及必要上下文。含混语音、音乐和特殊口音可能被复核拒绝或误识别；正式发布字幕前请校对。

## 模型放到根目录 model

仓库根目录预留了一个空的 **`model/`** 文件夹，实际模型和引擎文件由 Git 忽略，下载或放入后不会上传。

```text
LingoFlow/
├─ README.md
├─ model/                         # 本地模型目录，不提交权重
│  ├─ ggml-small.bin               # Whisper，可选择 tiny / base / small
│  ├─ ggml-tiny.bin                # 识别复核模型
│  ├─ ggml-silero-v5.1.2.bin        # 人声检测模型
│  ├─ Hy-MT2-1.8B-Q4_K_M.gguf       # 默认本地翻译模型
│  └─ .engines/                    # 自动准备的推理 / 解码组件
├─ src/LingoFlow/                  # WPF 程序源码和必需资源
├─ scripts/                       # 构建与检查
└─ installer/                     # Windows 安装包配置
```

1. 将受支持的模型文件放入根目录 `model/`，也可以先选择空目录后由软件自动下载。
2. 在“设备与模型 → 模型保存位置 → 选择文件夹”中，选择这个 `model` 文件夹。
3. 选择与文件对应的模型档位，再点击校验 / 开始。文件名、大小和 SHA-256 需与内置清单一致；推理组件由软件自动准备。

内置翻译模型可选 Hy-MT2 1.8B 的 Q4_K_M、Q6_K、Q8_0，以及旧版 Qwen3 0.6B / 1.7B Q8。模型清单位于 `src/LingoFlow/Services/LocalTranslationModels.cs`。

接入未列出的模型需要登记下载地址、文件名及校验值，并适配推理提示词；把任意 GGUF 放入目录不会自动变成已支持模型。云端模型在接口设置中配置，无需放置权重。

软件原有默认位置仍兼容程序旁的 `models/`；建议在界面手动选中仓库的 `model/`，现有自定义路径不受影响。已有模型不必移动或重新下载。模型目录、引擎缓存和临时下载尽量放在非系统盘。

## 文件翻译和字幕精修

“文件翻译”支持常见音视频格式，实际以 FFmpeg 可解码的第一条音轨为准。处理在本机进行，不修改输入文件，完成后导出三份 UTF-8 SRT。

“字幕精修”可导入原文轨 SRT，或使用本次文件翻译原文。不要把旧译文 / 双语轨当作原文导入。选择本地或云端文字模型后，点击“AI 二次翻译并导出”。原文件保持不变；云端校对原文有改动时，额外保存校对记录；不确定内容标为待核对。

翻译提示词首行随原语言和目标语言自动更新，正文可自行编辑。实时、文件、精修任务分别使用各自语言方向。

云端切分设置提供三个可编辑值：

| 设置 | 默认值 | 含义 |
| --- | ---: | --- |
| 上下文预算 | 1,019,715 tokens | 输入、预估输出及安全余量共同受此限制；已知型号还按内置上限约束 |
| 每批输入预算 | 0 | 0 表示自动尽量整段发送，可手动限制每批输入量 |
| 单次输出上限 | 65,536 tokens | 给校对原文和译文预留响应空间 |

token 为保守估算，不同模型 / 中转接口限制可能更低。过长、截断、漏项、重复编号或混合语言结果会缩小批次重试。展开后自动显示输入框，下面直接标明默认值；点击“保存切分设置”保存，也可“恢复默认值”。本地精修保持相邻短句合并翻译方式。

## 从源码构建

在 Windows 安装支持 .NET 8 目标的 .NET SDK，以及 Git。首次恢复运行时和构建安装包需要网络。

```powershell
git clone https://github.com/Kami648/LingoFlow.git
cd LingoFlow
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

输出在 `dist/LingoFlow-0.2.4/`，便携 ZIP 在 `dist/`。保留 EXE 旁的 `Assets/` 和生成的依赖文件夹。模型不会随构建打包。

构建 Windows 安装包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Installer
```

脚本会准备 Inno Setup 编译器及微软 VC++ 运行库，并校验微软运行库签名；这些下载文件保留在被忽略的 `artifacts/`，不提交仓库。

自动检查：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

检查使用独立的 `artifacts/` 数据目录，不读取正常使用的 API 密钥。软件自带的性能测试音频是必要程序资源；个人测试音视频与字幕均不在仓库中。

## 数据与第三方组件

个人设置保存在当前 Windows 用户的应用数据目录，API Key 使用 Windows DPAPI 保护。模型保存到界面所选目录；字幕保存在所选导出目录。以上均不属于本仓库内容。

主要第三方项目：[.NET / WPF](https://github.com/dotnet/wpf)、[whisper.cpp](https://github.com/ggml-org/whisper.cpp)、[llama.cpp](https://github.com/ggml-org/llama.cpp)、[Silero VAD](https://github.com/snakers4/silero-vad)、[Hy-MT2](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)、[Qwen3](https://huggingface.co/Qwen)、[FFmpeg](https://ffmpeg.org/)、[Inno Setup](https://jrsoftware.org/isinfo.php)。第三方组件、运行库和模型各自的使用条件以对应项目许可为准。
