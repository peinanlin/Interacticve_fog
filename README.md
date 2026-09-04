# 可交互体积雾——分档画质实现

基于 Unity 的交互式体积雾渲染与多画质效果对比项目。

## 项目简介

本项目研究交互式体积雾效果，包括场景交互，以及不同渲染画质设置下的性能与视觉表现对比。截图、GIF 和其他结果素材将集中放在 [`Results/`](Results/) 中。

## 项目特点

- Unity 交互式体积雾效果
- 可配置的渲染和画质设置
- 多画质并排对比
- 支持放置演示截图和 GIF

## 环境要求

- Unity **2022.3.62f3c1**（版本记录于 `ProjectSettings/ProjectVersion.txt`）
- Universal Render Pipeline（URP）

请通过 Unity Hub 打开项目，并等待 Unity 根据 `Packages/manifest.json` 自动恢复依赖包。

## 仓库结构

```text
Assets/                 Unity 场景、脚本、材质和资源
Docs/                   项目笔记和相关文档
Img/                    多画质效果对比截图
Editor/                 仅用于编辑器的脚本和工具
Packages/               Unity 包管理清单
ProjectSettings/        Unity 项目配置
Results/                交互演示、画质对比和 GIF
```

## 实验结果

### 交互效果

可以将截图或 GIF 放入 [`Results/interaction/`](Results/interaction/)，并按下面的方式嵌入：

```markdown
![交互式体积雾演示](Results/interaction/your-demo.gif)
```

### 多画质对比

下面的不同画质对比可能不够明显，点击图片可以放大查看。中、低画质由于降低了texture分辨率，可以明显看到锯齿差异。

| | 高画质 | 中画质 | 低画质 |
| --- | --- | --- | --- |
| **GPU 平均帧耗时** | **6.772 ms** | **2.257 ms** | **1.523 ms** |
| 渲染表现 | ![高画质渲染表现](Img/高画质表现.png) | ![中画质渲染表现](Img/中画质表现.png) | ![低画质渲染表现](Img/低画质表现.png) |

数据使用 Unity Profiler 在编辑器 Play Mode 下采集。每档画质采集 300 帧；GPU 平均值使用 **298 个有效 GPU 帧**计算，排除了最后两个没有 GPU 计时数据的帧。数值越低表示 GPU 耗时越少。这里的数值是包含场景和渲染管线在内的**整帧 GPU 耗时**，不是单独的体积雾耗时，也不是独立运行版本的 FPS。截图仅用于展示视觉表现，图片尺寸不代表基准测试时的输出分辨率。

*图片说明：目前提供的中画质和低画质 PNG 文件内容完全相同，后续需要替换为分别采集的截图，才能体现两档画质的视觉差异。*

### GIF 和其他素材

可以将 GIF 放入 [`Results/gif/`](Results/gif/)。较大的视频或构建产物建议放在 GitHub Releases，或使用 Git LFS 管理。

## 项目状态

仓库正在准备公开发布。实验结果和性能测试数据会在确认后持续补充。

## 许可证

待补充。
