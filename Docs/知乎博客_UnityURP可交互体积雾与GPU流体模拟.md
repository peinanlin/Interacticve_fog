# 我在 Unity URP 里做了一套“会被角色推开”的体积雾：Ray Marching、GPU 流体与高中低三档架构

> 项目环境：Unity 2022.3 LTS、URP 14、DirectX 11  
> 核心模块：局部体积雾、2D/3D Eulerian 流体、动态交互源、High/Medium/Low 质量架构

如果只是给场景加一层雾，Unity 的全局 Fog 已经能完成很多工作。但我想做的效果更具体：雾要有可控的局部体积，有高低浓淡和流动细节；角色走进去后，雾会被推开、卷起并逐渐回填；同时这套交互不能把整张开放场景都离散成三维网格。

最终我把问题拆成了两个相对独立的模块：

- 渲染模块负责回答“这里的雾应该长什么样”；
- 流体模块负责回答“角色经过后，密度和速度怎样传播”。

二者通过 GPU 上的密度场和速度场连接。这样做的最大收益不是公式更漂亮，而是渲染和模拟可以分别调试、替换和降级。

但仅仅把 `Step Count` 从 60 改成 32，并不能称为一套完整的高中低档方案。体积雾的成本同时来自着色像素数、每像素采样步数、噪声与光照复杂度，以及流体更新频率和网格规模。因此项目后来设计了真正的三级架构：High 保留原始全分辨率兼容路径，Medium 使用半分辨率重建路径，Low 使用四分之一分辨率和更轻的着色；流体策略则分别面向 60/30/20 Hz。当前工程仍按里程碑逐项启用这些能力，后文会明确区分目标 Profile 与场景中的实际开关。

> 【图 1：首屏动图】放一段 6～10 秒的最终效果 GIF：角色从浓雾中跑过，身后留下短暂低密度通道，雾边缘有柔和卷动。首屏只展示结果，不放 Inspector。

## 1. 先看整体架构

系统先选择质量配置，再驱动流体和渲染两条链路：

```text
Unity Quality Settings / 手动档位
      ↓
InteractiveFogQualityProfile
      ├─ High：全分辨率透明体积路径
      ├─ Medium：半分辨率重建路径
      └─ Low：四分之一分辨率重建路径
      ↓
角色 / 动态物体
      ↓  写入密度与速度
Sphere / Cube 交互源
      ↓
GPU Fluid Simulation
      ├─ Density
      ├─ Velocity
      ├─ Curl
      ├─ Divergence
      └─ Pressure
      ↓
Local Volumetric Fog Shader
      ├─ 体积包围盒求交
      ├─ 深度裁剪
      ├─ 密度塑形与噪声
      ├─ Ray Marching 积分
      └─ High 直接合成 / Medium、Low 离屏重建
      ↓
最终雾效
```

这套方案不是把“雾”本身当作流体从头求解。流体只提供交互变化量，最终可见密度仍由基础雾密度、高度衰减、噪声以及交互场共同决定。这个分层很重要：如果完全依赖模拟密度生成整片雾，初始化、回填和美术控制都会变得更困难。

> 【图 2：架构图】建议按上面的数据流重绘一张横向流程图。用蓝色表示 Compute Shader，用橙色表示 Ray Marching，用绿色表示角色输入，并在渲染端明确画出 High 与 Medium/Low 两条分支。

## 2. 雾不是一个半透明盒子

项目中的局部雾使用一个 Cube 网格限定体积范围。这个网格只负责提供边界和触发一次绘制，真正的雾是在片元着色器中沿视线积分出来的。

材质设置为透明队列、正面剔除、关闭深度写入。相机即使位于体积内部，也能从盒子的背面触发片元。每个片元先把相机和射线变换到体积局部空间，再与单位包围盒求交，得到射线在雾中的入口距离和出口距离：

```text
tNear = max(min(tx0, tx1), min(ty0, ty1), min(tz0, tz1))
tFar  = min(max(tx0, tx1), max(ty0, ty1), max(tz0, tz1))
```

只有 `tFar > max(tNear, 0)` 时，这条射线才真正穿过体积。包围盒因此只是 Ray Marching 的积分区间，而不是一个贴着透明贴图的几何表面。

> 【图 3：原理图】画出相机、视线、雾盒、`tNear`、`tFar` 和场景表面的关系。这里适合用示意图，不要用 Unity 截图。

### 2.1 用场景深度终止射线

如果积分一直走到雾盒背面，雾就会覆盖前方建筑、车辆和地面。项目会读取 `_CameraDepthTexture`，用逆 View-Projection 矩阵重建该像素对应的世界坐标，再计算相机到场景表面的距离：

```hlsl
float3 scenePosition = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
farDistance = min(farDistance, distance(cameraPosition, scenePosition));
```

这一步让 Ray Marching 在第一个不透明表面处停止。Unity 的 URP 文档也给出了通过深度纹理和 `ComputeWorldSpacePosition` 重建世界坐标的标准做法，实际使用时需要在 URP Asset 或 Camera 上启用 Depth Texture。[Unity：从深度纹理重建世界空间位置](https://docs.unity3d.com/cn/Packages/com.unity.render-pipelines.universal%4012.1/manual/writing-shaders-urp-reconstruct-world-position.html)

### 2.2 每个采样点的密度从哪里来

射线区间被均匀分成若干步，每个采样点的最终密度由四部分组成：

```text
sampleDensity
    = BaseDensity
    × InteractionMultiplier
    × ShapedNoise
    × HeightDensity
```

- `BaseDensity` 是整个局部雾的基础浓度；
- `InteractionMultiplier` 来自流体密度场，决定角色附近是变稀还是回填；
- `ShapedNoise` 使用多层 FBM 打散均匀感；
- `HeightDensity` 用指数函数控制雾随高度衰减。

2D 模式只有 XZ 平面的数据，没有真实的 Y 轴变化。用于“地面雾”时，可以沿高度展开并施加高度衰减；用于源生成的尾迹时，则在体积中线附近做软挤出，避免整张二维纹理看起来像一堵直立的墙。3D 模式会直接采样 `Texture3D`，因此上升、下沉和绕过角色等纵向变化都是真实存在的。

噪声坐标还会根据速度场做少量偏移：

```text
noisePosition = worldPosition × NoiseScale
              + NoiseVelocity × time
              - simulationVelocity × VelocityScale
```

它不会替代流体平流，但能让视觉细节跟随流向，减少“密度在动、纹理却钉在世界空间”的割裂感。

> 【图 4：四联图】同一机位依次展示：纯基础密度、加入高度衰减、加入 FBM、加入流体交互。四张图必须保持相同曝光和相机参数。

### 2.3 Beer-Lambert 透射与前向累积

项目使用简化的 Beer-Lambert 形式把密度转成单步不透明度：

```text
extinction = density × stepLength × extinctionScale
alpha      = 1 - exp(-extinction)
```

然后按从前到后的顺序累积颜色和透射率：

```text
accumulated  += transmittance × alpha × sampleColor
transmittance *= 1 - alpha
```

当透射率低于 `0.01` 时提前结束。这既符合“后面的贡献已经很小”的直觉，也能减少浓雾区域的无效采样。经典 GPU 体积渲染资料通常也采用前向合成和提前终止来加速射线积分。[GPU Gems：Volume Rendering Techniques](https://developer.nvidia.com/gpugems/gpugems/part-vi-beyond-triangles/chapter-39-volume-rendering-techniques)

这里需要诚实说明当前原型的边界：它使用的是由密度、噪声、高度和吸光系数组合出来的美术化着色，并没有实现方向光散射、相函数、体积阴影或多光源注入。因此它是“可交互的局部体积雾”，还不是完整的物理体积光框架。Frostbite 的统一体积渲染方案会进一步把消光、光源和体积阴影组织到统一体积中，那是后续升级光照模型时很好的参照。[SIGGRAPH 2015：Towards Unified and Physically-Based Volumetric Lighting in Frostbite](https://www.advances.realtimerendering.com/s2015/index.html)

### 2.4 同一个积分器，两条提交路径

三级方案没有直接重写 High，而是故意保留两条渲染路径。

High 继续由体积 `MeshRenderer` 进入 URP 普通透明物体阶段。Shader 直接输出预乘雾颜色和不透明度，并通过 `Blend One OneMinusSrcAlpha` 合成到 Camera Color。这条路径是项目已经验证过的视觉基准，也是新架构出现问题时的回退路径。

Medium 和 Low 则由 `InteractiveFogRendererFeature` 在透明物体之前接管：

```text
Opaque + Camera Depth
        ↓
Fog Ray March Pass
        ├─ 低分辨率 Scattering + Transmittance
        └─ Representative Fog Depth
        ↓
Scene Depth Downsample
        ↓
Reconstruction / Composite
        ↓
Camera Color
```

重建路径不会先把雾混入低分辨率场景颜色，而是保存“未合成”的体积结果：RGB 为预乘散射，A 为剩余透射率。回到全分辨率后再执行：

```text
cameraFinal = fogScattering + cameraColor × fogTransmittance
```

这能避免低分辨率缓冲的通道语义与透明混合公式不一致。项目早期曾因此出现接近全屏红色或黑色的结果，所以 High 与重建 Shader Pass 现在被明确隔离，Medium/Low 只有在 Renderer Feature 真正接管时才关闭原来的 `MeshRenderer`，防止一帧绘制两次雾。

当前里程碑已经具备离屏 Ray Marching、代表深度、场景深度降采样和当前帧 Composite。为了先稳定颜色、相机进入体积以及尺寸变化，Medium/Low 的 `bilateralRadius` 暂时为 `0`，使用最近邻重建；Shader 中已有深度感知四邻域重建代码，但还没有通过最终边缘验收。时域参数也已写入 Profile，不过运行时明确退回 current-frame 路径，尚未把历史帧参与最终合成。后文会把“已实现”和“设计目标”分别列出。

> 【图 5：双路径示意】左侧画 High 的透明 Draw Call，右侧画 Medium/Low 的低分辨率 MRT、Depth Downsample 和 Composite；两侧共同指向同一套 Ray/Volume、密度映射和流体采样逻辑。

## 3. 为什么要引入流体模拟

只在角色周围挖一个跟随的透明圆洞，能立即产生“雾散开”的视觉结果，但它没有记忆：角色离开后，洞会和角色一起移动；角色快速转向时，也不会留下尾迹或卷流。

流体模拟解决的是这种时序连续性。项目采用网格化的 Eulerian 表示：速度、密度和压力都存储在固定网格纹理中，角色只是向这些场注入扰动。其基础思路来自 Jos Stam 的 Stable Fluids：通过半拉格朗日平流获得大时间步下仍然稳定的实时求解，再用压力投影让速度场趋近不可压缩。[Jos Stam：Stable Fluids](https://graphics.stanford.edu/courses/cs448-01-spring/papers/stam.pdf)

项目每完成一个模拟步时的 GPU 求解顺序是：

```text
Shift Density / Velocity（模拟区域发生整格移动时）
→ Advect Velocity
→ Advect Density
→ Density Mask（可选）
→ Apply Sources
→ Apply Boundary
→ Environment Forces（3D）
→ Buoyancy（3D，可选）
→ Compute Curl
→ Apply Vorticity
→ Compute Divergence
→ Jacobi Pressure Iteration
→ Subtract Pressure Gradient
```

> 【图 6：求解器流程图】这是全文最重要的技术图。每个阶段下面放一张小缩略图，分别对应 Density、Velocity、Curl、Divergence、Pressure。

### 3.1 平流：上一帧的东西去了哪里

对当前网格点 `x`，先沿速度反方向回溯到上一时刻的位置，再采样旧场：

```text
q_new(x) = q_old(x - Δt · u(x))
```

`q` 可以是密度，也可以是速度。因为采样位置通常落在网格之间，GPU 的线性过滤正好完成插值。半拉格朗日方法的优势是稳定，代价是数值耗散会抹掉小尺度旋涡，所以后面还要加入涡度增强。

### 3.2 交互源：把角色行为写进场里

当前实现提供两种源几何：`Sphere` 和 `Cube`。CPU 每帧收集与模拟范围相交的源，将位置、尺寸、局部变换、密度增量和速度写入 StructuredBuffer；`ApplySources` 再在 GPU 上判断每个网格单元是否位于源内部。

源可以写入两类主要信息：

- 模拟密度：正值注入交互信号，负值擦除已有信号；当前清雾演示再通过负的 `Density Scale` 把正信号映射为可见雾密度下降；
- 速度：叠加物体移动速度、固定风、混合速度或定向 Jet。

静止物体同样可以持续交互。只要关闭“静止时停止”，并配置 Density、Wind 或 Jet，源就会每帧向场中注入数据。火箭尾焰、通风口和持续排气都适合这种模式；角色行走则更适合把移动速度写入流场。

这里特意只保留了两种源形状。它们已经覆盖角色、脚部、风口、箱体和大多数碰撞代理，而且 GPU 分支更少、调试更直观。

> 【图 7：源形状对比】左侧展示 Sphere 的圆形注入根部，右侧展示 Cube 的方形注入根部；两张图使用同样的 Jet 参数和模拟分辨率。

### 3.3 涡度增强：把被数值耗散吃掉的卷曲补回来

先由速度场计算 Curl，再根据 Curl 强度的梯度施加 vorticity confinement。它不是无条件地制造噪声，而是在旋转结构附近补充侧向力，让角色边缘和尾迹不至于迅速糊成一团。

涡度过小，交互看起来像柔软的透明橡皮；涡度过大，又会出现持续抖动和不自然的小旋涡。调参时应先固定分辨率和速度消散，再逐步增加 Vorticity，否则很难判断变化来自哪一项。

### 3.4 压力投影：去掉速度场中的发散

外力、交互源和平流都会让速度场产生发散。不可压缩近似要求：

```text
∇ · u = 0
```

实现上先计算 Divergence，再用多次 Jacobi 迭代近似求解压力 Poisson 方程，最后从速度中减去压力梯度：

```text
u_projected = u - ∇p
```

压力迭代次数越高，投影越充分，但每次迭代都是一次全屏或全体积 Compute Dispatch。博客展示效果时可以从 4 次起步；如果网格分辨率较高，应该先观察 Divergence 结果，再决定是否真的需要继续加迭代。

### 3.5 流体更新不再绑定渲染帧率

最初的求解器每个渲染帧固定执行一次 `0.02 s` 模拟。这样在 30 FPS 和 120 FPS 下，单位现实时间内执行的流体步数不同，尾迹速度、衰减和整体成本都会随帧率变化。

当前代码已经加入有上限的定步长累加器：

```text
accumulator += frameDelta
while accumulator >= fixedDelta and substeps < maxCatchUp:
    StepSimulation(fixedDelta)
    accumulator -= fixedDelta
```

High、Medium、Low 的默认求解频率分别为 60、30、20 Hz，并限制单帧最多追赶 2、2、1 步。卡顿后剩余的过量时间会被记录并丢弃，避免下一帧突然提交大量 Compute Dispatch。

原本按每步配置的密度和速度保持率，也会根据实际 `dt` 换算：

```text
effectiveRetention = pow(referenceRetention, dt / 0.02)
```

外力和源强度则按秒表达并乘以 `dt`。因此降低模拟频率主要损失时间细节，而不会简单地让雾在低档位变慢。Low 还把涡度计算改为每两步执行一次；被跳过的步骤不会运行 Curl 与 confinement Dispatch。

这部分保留了 A/B 开关。当前 PolygonTown 与 InteractiveFog3D 场景默认仍关闭 `fixedRateFluid` 和 `reducedVorticityCadence`，先沿用旧时钟验证重建画面；打开对应开发开关后，Profile 中的 60/30/20 Hz 和涡度间隔才会真正生效。

## 4. 2D 和 3D 求解器到底差在哪里

两套求解器共享相同的流程，但数据维度和适用场景不同。

| 项目 | 2D 求解器 | 3D 求解器 |
|---|---|---|
| 密度 | `Texture2D / RHalf` | `Texture3D / RHalf` |
| 速度 | `Texture2D / RGHalf` | `Texture3D / ARGBHalf` |
| Curl | 标量 | 三维向量与模长 |
| 纵向运动 | 不求解，渲染时塑形 | 真实参与求解 |
| 计算量趋势 | 约随分辨率平方增长 | 约随分辨率立方增长 |
| 适合场景 | 地面雾、俯视游戏、大范围角色交互 | 喷流、漂浮烟雾、上下穿行、小范围高质量交互 |

2D 不是“低质量 3D”，而是一种明确的约束：当主要运动发生在地面 XZ 平面时，不计算 Y 轴可以换来更大的覆盖范围和更细的平面分辨率。反过来，如果效果依赖重力下坠、热气上升或垂直喷射，2D 再高分辨率也无法补回缺失的自由度。

> 【图 8：2D/3D 对照】同一个向下喷射源：2D 只显示平面投影，3D 显示真实下坠和侧风偏移。图下注明两者分辨率和显存格式。

## 5. 用有限网格覆盖大场景：Scrolling

开放场景不能为整张地图创建高精度三维流体纹理。项目让求解区域跟随玩家，但不是每帧清空重建。

当区域中心跨过一个或多个网格单元时，CPU 计算整数网格偏移；GPU 用 `ShiftDensity` 和 `ShiftVelocity` 把旧场搬到新坐标，刚进入区域的单元再用默认值填充。之后才执行本帧平流。

这样有三个好处：

1. 模拟成本只与玩家周围的固定范围有关；
2. 玩家缓慢移动时，已有尾迹不会突然消失；
3. 整格移动避免每帧重采样造成额外扩散。

需要接受的边界也很明确：已经离开局部模拟区域的数据不会长期保留。这套方案追求的是“玩家附近持续可信”，不是整张世界的永久流体历史。

> 【图 9：Scrolling 三帧图】从左到右展示模拟区域移动前、整格 Shift、移动后新边界填充。最好叠加网格线和世界坐标箭头。

## 6. 高中低三档不是三个 Step Count，而是三种预算策略

三级配置被保存为独立的 `InteractiveFogQualityProfile` 资产。每个 Profile 同时描述 Render、Reconstruction、Temporal、Shading、Fluid、Compatibility 和 Budget，不再让场景里的组件各自维护一组零散常量。

三份 Profile 资产编码的目标策略如下：

| 策略 | High | Medium | Low |
|---|---:|---:|---:|
| 渲染路径 | 原始全分辨率透明路径 | 重建路径 | 重建路径 |
| 渲染分辨率 | 100% | 50% × 50% | 25% × 25% |
| 最大/最小 Ray Steps | 60 / 24 | 44 / 20 | 32 / 16 |
| 噪声策略 | 4 层程序化 FBM | 烘焙 3D 基础噪声 + 1 层细节 | 仅烘焙 3D 基础噪声 |
| 光照近似 | Full | Full | Simplified |
| 流体频率 | 60 Hz | 30 Hz | 20 Hz |
| 单帧最大追赶步数 | 2 | 2 | 1 |
| 目标 Resolution Scale | 6 | 4 | 3 |
| 压力迭代 | 4 | 3 | 2 |
| 涡度计算间隔 | 每步 | 每步 | 每 2 步 |

这里的 50% 和 25% 是宽、高两个方向的比例，因此仅从像素数看，Medium 约为全分辨率的四分之一，Low 约为十六分之一。实际加速不会严格等于这个比例，因为还有重建、深度降采样、固定开销和不同的早退分布，但它比只减少十几步 Ray Marching 更能改变成本量级。

截至当前里程碑，`PolygonTown/Scenes/Demo.unity` 与 `InteractiveFog3D.unity` 的开发开关状态是：

| 能力 | 当前场景状态 | 实际含义 |
|---|---|---|
| Reconstructed Rendering | 开启 | Medium/Low 使用缩放 RT 和 current-frame Composite |
| Temporal Resolve | 关闭 | 不读取历史帧；Profile 的 History Weight 暂不生效 |
| Baked Noise | 关闭 | 当前仍用程序化噪声基线路径验证颜色与构图 |
| Fixed-rate Fluid | 关闭 | 代码已经实现，但场景仍使用旧调度做 A/B |
| Reduced Vorticity Cadence | 关闭 | Low 暂时不会隔步跳过 Curl |
| State-preserving Resize | 关闭 | 档位切换不执行流体网格重采样 |

因此下面三个小节描述的是“档位最终应该如何分工”，其中低分辨率渲染已经进入场景，其他模块则按各自开关逐项验证。

### 6.1 High：稳定参考和兼容回退

High 的目标不是尝试所有新技术，而是保留原有效果。它使用隔离的 Legacy Shader Pass、全分辨率透明 Draw Call、程序化 FBM 和完整的美术化光照，关闭时域积累。

这条路径非常重要。低分辨率重建出现黑帧、全屏染色或相机进入雾体异常时，可以立即切回 High；同时所有 Medium/Low 对比都有一个不会随新架构一起变化的视觉参考。

### 6.2 Medium：先减少像素，再保留主要细节

Medium 以半分辨率执行 Ray Marching，步数降到 `44/20`；Profile 计划使用烘焙 3D 基础纹理加一层廉价细节，并让流体按 30 Hz 更新。设计上它会使用较保守的时域历史权重和深度感知重建，在轮廓稳定与性能之间取平衡。

当前实现先把 `bilateralRadius` 设为 `0`，使用确定性的最近邻 Composite，以验证低分辨率 RT、相机深度、颜色通道和相机内外体积行为。Profile 中虽然已经保存 `historyWeight = 0.78`，但场景里的 Temporal 开关关闭，所以实际只使用当前帧；如果开发时请求该能力，诊断会明确报告 `Requested; current-frame fallback`，而不是悄悄假装时域模块已经工作。

### 6.3 Low：压缩空间、采样和时间三种预算

Low 以四分之一宽高渲染，步数为 `32/16`；Profile 计划只采样烘焙基础噪声，并使用简化光照。目标流体频率为 20 Hz、压力迭代为 2，Curl 与涡度约束每两步运行一次。

Low 的目标不是近距离逐像素匹配 High，而是保留三件事：雾体的大尺度分布、角色清雾区域的位置、尾迹随时间传播的方向。当前 Profile 中预留了更高的时域历史权重 `0.88`，但和 Medium 一样，现阶段仍只使用当前帧。

### 6.4 档位切换必须报告“请求”和“实际”

控制器同时记录 `Requested Tier` 与 `Active Tier`。如果设备不支持 Compute Shader、流体随机写格式或重建路径需要的浮点 Render Target，就按确定顺序回退：Medium 回退 High，Low 先尝试 Medium，再尝试 High。界面会显示回退原因，不能只改一个枚举就宣称切换成功。

Game View 的 Development 控制使用：

- `Z`：Low；
- `X`：Medium；
- `C`：High。

Editor 菜单 `Tools > Interactive Fog` 中另外提供 `Ctrl+1/2/3` 预览 Low/Medium/High。普通数字 `1/2` 仍用于切换 Sphere/Cube 交互源，与画质快捷键互不冲突。

流体分辨率切换还有一个关键限制：真正无缝的做法应当是“分配新资源 → 世界空间重采样 Density/Velocity → 原子交换 → 释放旧资源”。这部分尚未完成。当前控制器在启用状态保留策略时，会继续使用原网格分辨率并给出提示，避免为了应用 Profile 的 Resolution Scale 而清空已有尾迹。因此表格中的流体 Scale 是目标策略，不能在当前阶段描述为已经无缝切换。

> 【图 10：三档对比】同一相机、同一角色位置、同一帧分别截 High/Medium/Low。图下注明实际输出尺寸、Ray Steps、Active Tier、当前流体调度模式，以及 Temporal 为 Disabled 还是 requested fallback。

## 7. 从流体密度到“雾被推开”

流体里的 Density 并不直接等于屏幕上的不透明度。项目先把世界坐标映射到求解器 UVW，再采样密度和速度：

```text
uvw = (worldPosition - simulationCenter) / solverRange + 0.5
```

随后经过响应、反转阈值、密度缩放、边缘噪声和最小残留等映射，得到 `InteractionMultiplier`。这层映射解决两个实际问题：

- 求解器里的微小密度变化，在画面上可能几乎看不出来；
- 把密度直接减到零会形成生硬的透明洞。

几个最常用参数可以这样理解：

- `Interaction Response`：先放大模拟信号，决定角色动作有多“显眼”；
- `Density Scale`：决定模拟密度对基础雾是削弱还是增强；
- `Density Reverse Threshold / Max Value`：把很弱的正密度反转并限幅；配合负的 `Density Scale`，主通道变稀，而外沿会轻微堆积，形成比硬透明边更自然的过渡；
- `Interaction Feather`：软化低密度通道的内部过渡；
- `Minimum Interaction Density`：保证交互区仍残留一层很薄的雾；
- `Lerp Range`：让模拟区域边缘平滑回到基础雾；
- `Interaction Boundary Noise`：只给边界一点扰动，避免完美几何轮廓。

角色周围还可以叠加一个很弱的局部径向清雾遮罩。它的作用是保证第三人称角色始终可读，而流体场负责更大范围的尾迹和回填。两者职责不同：前者是即时视觉保底，后者才有时间连续性。遮罩必须保留少量残余密度，并使用 Feather 和低强度噪声，否则会重新变成“跟着人物移动的硬洞”。

### 一个可靠的调参顺序

1. 暂时关闭噪声，只看 Debug Window 的 Density 和 Velocity；
2. 确认源真的写入场，再调整源半径、速度和密度增量；
3. 固定求解器后，提高 `Interaction Response`，直到变化可见；
4. 用 `Density Scale` 决定清除强度；
5. 用 `Minimum Interaction Density` 保留极薄雾层；
6. 最后再加 Feather、边界噪声、FBM 和颜色。

如果第一步的 Density 纹理里没有轨迹，继续调雾材质没有意义；如果 Density 有明显轨迹而最终画面不明显，问题才位于映射或 Ray Marching 阶段。

> 【图 11：调参前后】左图是过硬透明洞，中图是流体有效但视觉响应不足，右图是最终软边低密度通道。每张图下只标 3～4 个发生变化的关键参数。

## 8. 用 RenderDoc 把“看起来正确”变成“链路可验证”

只放最终画面很难证明流体和体积积分真的在工作。RenderDoc 最有价值的地方，是把一帧拆成可验证的资源变化。

Unity 2022.3 在 Windows DX11 下可以从 Game View 或 Scene View 加载并触发 RenderDoc 捕获；加载时会重载图形设备，所以应先保存场景。[Unity 2022.3：RenderDoc Integration](https://docs.unity3d.com/ja/2022.3/Manual/RenderDocIntegration.html)

我建议为博客固定一个“角色刚经过浓雾”的帧，并按下面顺序截图。

### 截图 A：雾绘制前的 Scene Color 与深度

在 Event Browser 中定位局部雾 Draw Call 的前一个事件：

- 展示雾绘制前的颜色缓冲；
- 打开 `_CameraDepthTexture`；
- 在 Texture Viewer 中标注地面、角色和车辆的深度轮廓。

这张图证明雾能在哪里终止，也解释为什么透明物体不会自动写入同一份不透明深度。

### 截图 B：ApplySources 前后

找到 `ApplySources` Compute Dispatch，分别查看输入和输出 Density/Velocity：

- Density 用黑到白或伪彩色显示；
- Velocity 分别查看 R/G/B，或使用向量可视化；
- 在 Resource Inspector 中保留尺寸、格式和 2D/3D 类型。

这张图证明变化来自交互源，而不是渲染器临时挖洞。

### 截图 C：平流、Curl、Divergence、Pressure

按求解顺序各截一张：

1. `AdvectDensity` 后的尾迹；
2. `ComputeCurl` 的旋转区域；
3. `ComputeDivergence` 的正负发散；
4. 最后一次 `JacobiPressure` 的压力分布；
5. `SubtractGradient` 后的速度场。

有符号纹理不要使用默认 0～1 显示范围，否则负值会被压黑。应设置对称范围或分通道展示。3D Texture 则固定一个穿过角色中心的 Y Slice，并在所有截图中使用同一层。

### 截图 D：分别验证 High 与重建路径

High 应当只出现原始透明雾 Draw Call，Renderer Feature 不应增加第二次雾绘制。Medium/Low 则应该看到低分辨率 Ray March、Depth Downsample 和 Composite，并且原始透明雾提交被抑制。

在 Pipeline State 和资源列表中展示：

- 当前 Shader 和透明混合状态；
- `_Density2D/_Density3D`、`_Velocity2D/_Velocity3D` 的实际绑定；
- `_CameraDepthTexture`；
- Medium/Low 的 Scattering/Transmittance、Representative Depth 和降采样 Scene Depth；
- Composite 前后的 Camera Color。

当前版本的 Medium/Low 不应该出现真正的 Temporal Resolve 事件；默认场景诊断应显示 Disabled，只有主动打开未完成的开发开关时才显示 requested current-frame fallback。等时域模块完成后，再补充 History、Reprojection、Neighborhood Clamp 和 Reactive Rejection 四组截图，避免用 Profile 里的历史权重误导读者。

最后再放一张最终 Game View。读者就能沿着“档位选择 → 源注入 → 流体传播 → 密度采样 → 射线积分 → 重建合成”的证据链走完一帧。

> 【图 12：RenderDoc Event Browser】并排标出 High 和 Medium 的雾事件；High 只有透明 Draw，Medium 显示 Ray March、Depth Downsample、Composite。  
> 【图 13：资源对照九宫格】Density、Velocity、Curl、Divergence、Pressure、Scene Depth、低分辨率雾、Representative Depth、最终 Game View。  
> 【图 14：Pipeline State】只框出 Shader、SRV/UAV、MRT、Blend 和 Depth 状态，裁掉无关面板。

## 9. 性能：钱花在了哪里

这套效果有两块主要成本。

第一块是流体求解。若单轴分辨率为 `N`，2D 每个 Dispatch 处理约 `N²` 个单元，3D 则是 `N³`；压力迭代还会把其中一部分成本重复多次。因此 3D 模式必须限制最大分辨率，并把模拟范围放在真正需要交互的局部。

第二块是 Ray Marching。它的近似成本是：

```text
被雾体积覆盖的像素数 × 平均采样步数
```

当前场景已经启用的控制手段包括：

- 浓雾处透射率低于阈值后提前退出；
- 用场景深度缩短积分区间；
- 根据实际穿雾距离，在最大/最小值之间自适应减少 Ray Steps；
- 2D 场使用半精度 R/RG 纹理；
- 3D 分辨率设置统一上限；
- Medium/Low 把 Ray March 移到半分辨率或四分之一分辨率目标；
- 三档分别使用 4、3、2 次压力迭代；
- 模拟区域跟随玩家，而不是覆盖整张地图。

代码与 Profile 已经为烘焙 3D 噪声、Low 简化光照、60/30/20 Hz 定步长流体和降频涡度预留路径，其中定步长调度与时间校正已经实现；但当前两个场景的对应开发开关仍关闭，所以不能把这些项目计入现阶段的场景性能收益。

开发过程中，Adaptive Step 曾把一次 1920×1080 固定场景中的雾 Draw 从约 `8.03 ms` 降到约 `3.53 ms`。这个数值只能作为架构改造前的观测基线：标准化的 Development Player、五秒预热、300 帧 median/P95 测试尚未完成，不能写成跨设备性能结论。

Profile 中为 Medium 和 Low 设置的 `2.0 ms`、`1.2 ms` 是验收预算，不是已经测得的成绩。最终博客只有在统一机器上完成相同相机轨迹、相同交互输入和至少 300 帧采样后，才能给出三档柱状图；如果未达标，应记录具体失败 Pass，而不是修改标题里的数字。

时域重投影、历史深度验证、邻域钳制和交互响应拒绝仍属于下一阶段。深度感知 Bilateral Shader 已存在，但当前 Profile 的半径为零，尚未通过细杆、角色轮廓、天空边界和相机进入雾体等测试。流体的跨分辨率 GPU 重采样也没有完成，因此现阶段切档测试还不能用于证明“尾迹无损保留”。

> 【图 15：三档性能图】用 Development Player 的 GPU Profiler 或 RenderDoc Duration 展示 High、Medium、Low 的 Ray March、Depth/Composite 和 Fluid 总耗时。注明 GPU、1920×1080、实际低分辨率目标尺寸、Ray Steps、流体网格、更新频率和压力迭代；没有完整 median/P95 数据时先放事件截图，不放结论性柱状图。

## 10. 当前版本的限制

一个技术项目写清限制，通常比只写优点更有参考价值。

1. 当前雾色和吸光是美术化近似，没有真正的光源散射、相函数和体积阴影；
2. 流体边界目前是模拟区域边界，没有把场景建筑逐体素写成障碍物；
3. 2D 模式不能表示真实纵向运动，只适合近地交互；
4. 角色周围的即时清雾遮罩是视觉保底，不会写回流体历史；
5. High 位于透明队列，需要留意其他透明材质的排序和深度行为；Medium/Low 由 Renderer Feature 在透明物体之前合成，二者的透明排序语义并不完全相同；
6. Medium/Low 当前使用最近邻 current-frame Composite，时域重建和最终 Bilateral 验收尚未完成；
7. 质量切换时的流体资源重采样尚未完成，当前优先保留现有网格以避免清空尾迹；
8. Scrolling 会保留局部历史，但离开模拟区域的数据最终仍会丢失。

接下来的优先级是：先完成重建路径的颜色与边缘验收，再实现时域历史和交互响应拒绝，随后完成流体 allocate-resample-swap；三档在统一基准中稳定后，再继续加入主方向光、体积阴影和场景障碍体素。

## 11. 结语

这个项目最有意思的地方，不是单独实现了一个 Ray Marching Shader，也不是把 Stable Fluids 搬进 Compute Shader，而是把二者之间的职责划清了：

- 流体负责交互的空间传播和时间连续性；
- 渲染负责把抽象密度塑造成可控、可读的雾；
- Scrolling 负责把有限预算集中在玩家附近；
- Quality Profile 负责同时分配像素、采样、着色和模拟时间预算；
- High 兼容路径负责提供稳定参考和随时可用的回退；
- RenderDoc 负责证明每一步的数据确实按预期流动。

当角色穿过雾时，屏幕上的低密度通道只是最后一个结果。它背后依次经过了源注入、速度平流、涡度补偿、压力投影、世界坐标映射、深度裁剪和体积积分。把这条链路拆开之后，调试也会从“为什么看起来不对”变成一个个可以直接验证的问题。

如果你也在做类似效果，我最推荐先完成 2D 求解器和密度 Debug View，再接入全分辨率 Ray Marching；确认链路正确后，把 High 固定为视觉参考，再单独搭建 Medium/Low 的低分辨率路径。这样既能少掉很多“到底是模拟错了，还是渲染没显示出来”的时间，也不会让优化过程破坏唯一可靠的基线。

## 参考资料

- [Jos Stam, Stable Fluids](https://graphics.stanford.edu/courses/cs448-01-spring/papers/stam.pdf)
- [GPU Gems 3, Chapter 30: Real-Time Simulation and Rendering of 3D Fluids](https://developer.nvidia.com/gpugems/gpugems3/part-v-physics-simulation/chapter-30-real-time-simulation-and-rendering-3d-fluids)
- [GPU Gems, Chapter 39: Volume Rendering Techniques](https://developer.nvidia.com/gpugems/gpugems/part-vi-beyond-triangles/chapter-39-volume-rendering-techniques)
- [SIGGRAPH 2014: Volumetric Fog in Assassin's Creed IV: Black Flag](https://www.advances.realtimerendering.com/s2014/wronski/bwronski_volumetric_fog_siggraph2014.pdf)
- [SIGGRAPH 2015: Towards Unified and Physically-Based Volumetric Lighting in Frostbite](https://www.advances.realtimerendering.com/s2015/index.html)
- [Unity URP：从深度纹理重建世界空间位置](https://docs.unity3d.com/cn/Packages/com.unity.render-pipelines.universal%4012.1/manual/writing-shaders-urp-reconstruct-world-position.html)
- [Unity 2022.3：RenderDoc Integration](https://docs.unity3d.com/ja/2022.3/Manual/RenderDocIntegration.html)

---

## 发布前配图清单

这部分是作者工作清单，发布到知乎前可以删除。

| 编号 | 图片内容 | 来源 | 放置位置 |
|---|---|---|---|
| 图 1 | 角色穿雾最终效果 GIF | Unity Game View 录制 | 标题后 |
| 图 2 | 渲染与流体总体架构 | 自绘 | 第 1 节末 |
| 图 3 | Ray/Box/Depth 求交 | 自绘 | 第 2 节开头 |
| 图 4 | 密度构成四联图 | Unity 固定机位 | 第 2.2 节末 |
| 图 5 | High 与重建路径对照 | 自绘 | 第 2.4 节末 |
| 图 6 | GPU 求解流程与中间场 | 自绘 + Debug Window | 第 3 节开头 |
| 图 7 | Sphere / Cube 注入对比 | Debug Demo | 第 3.2 节末 |
| 图 8 | 2D / 3D 同参数对照 | 两个 Demo 固定机位 | 第 4 节末 |
| 图 9 | Scrolling 三帧示意 | 自绘或 Scene Gizmos | 第 5 节末 |
| 图 10 | High / Medium / Low 同帧对比 | Unity 固定机位 | 第 6 节末 |
| 图 11 | 硬边、弱交互、最终效果 | Unity 固定机位 | 第 7 节末 |
| 图 12 | 两条路径的 Event Browser | RenderDoc | 第 8 节 |
| 图 13 | 流体与重建中间资源 | RenderDoc Texture Viewer | 第 8 节 |
| 图 14 | 最终 Draw Pipeline State | RenderDoc | 第 8 节 |
| 图 15 | 三档 GPU 成本 | Unity Profiler / RenderDoc | 第 9 节 |

### RenderDoc 截图统一规范

- 使用同一帧、同一相机、同一窗口尺寸；
- 截图保留事件名、资源名、尺寸和格式；
- Density 使用统一灰度范围；Velocity/Curl/Divergence 使用统一有符号范围；
- 3D Texture 固定同一个 Y Slice；
- 最终效果图关闭 Stats、Gizmos 和运行提示面板；
- 每张图只用一种颜色的箭头和标注框，避免知乎压缩后难以阅读。
