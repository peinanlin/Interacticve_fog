# 可交互体积雾（URP / Unity 2022.3）

本目录按《流体模拟使用说明》和《可交互体积雾使用说明》实现：流体求解与雾渲染解耦，交互源只写入速度/密度场，`LocalVolumetricFog` 再把密度场转换为可见雾。

## 快速运行

1. 在 Unity 菜单选择 `Tools > Interactive Fog > Create Demo Scenes`。
2. 打开 `Assets/InteractiveFog/Demo/InteractiveFog2D.unity`、`InteractiveFog3D.unity`，或用于复现文档流体示例的 `FluidSimulationDebug2D.unity`。
3. 进入 Play Mode。3D Demo 默认关闭交互球自动运动，可在 Scene 视图直接拖拽 Transform 观察雾的响应；2D Demo 保留自动运动。
4. `T` 切换自动/手动，`WASD` 移动，3D 场景用 `Q/E` 上下移动，`R` 重置，`Space` 在添加密度与擦除密度之间切换。交互球使用半透明绿色材质，便于透过球体观察雾场。

## 必需组件

### FluidSimulation

挂到全局求解器节点。与文档一致，包含：

- `Scrolling`：`Following`、`Follow Offset`。跟随时按网格单元迁移上一帧密度和速度。
- `DensityMask`：`Density Mask2D`、世界中心、尺寸、滚动速度、每帧输入浓度。
- `Boundary`：边界密度、边界速度类型、固定速度、风强度。
- `Solver Parameters`：`K2D Eulerian` / `K3D Eulerian`、Default / Advanced、范围、精度、求解内容。
- `Density / Velocity / Vorticity / Pressure`：消散、上限、外力、涡度、压力迭代。

GPU 求解顺序为：速度平流 → 密度平流 → DensityMask → 扰动源 → 边界 → 重力/环境风(3D) → 浮力(3D，可选) → 涡度 → 散度 → Jacobi 压力迭代 → 压力梯度消除。

文档交互示例参数已作为默认值：`Delta Time 0.02`、`Density Limit 1`、`Density Dissipation 0.9966`、`Velocity Limit 10`、`Velocity Dissipation 0.9992`、`External Force Scale 5`、`Vorticity 0.92`、`Pressure Iteration 4`。

### FluidSimulationInteractiveSource

挂到角色脚、球、箱体或其他交互对象：

- `Geometry`：Sphere / Cube。
- `Source`：Velocity 必选；Density 与 Temperature 可选；`Add Density` 为 `[-1, 1]`。
- `Additional Velocity`：None / Constant / Wind / Hybrid。
- `Jet Emission`（本项目扩展）：从 Geometry 中心施加定向速度；`Local Jet Direction` 控制喷射轴，`Jet Speed` 控制轴向速度，`Jet Spread` 控制喷流锥角。
- `Advanced`：`Center Flow 2D Coeff`、`Diffusion`。

局部雾“被物体排开”时使用 `Velocity + Density`、`Add Density = 1`。擦除已生成的流体密度时使用 `Add Density = -1`。

### LocalVolumetricFog

挂到 Cube 体积上并引用 `FluidSimulation`。交互参数严格使用文档建议值：

| 参数 | 值 |
|---|---:|
| Interactive | 开启 |
| Initial Density | 1 |
| Density Scale | -1.4 |
| Interaction Response | 3（演示场景 2D 为 3.5、3D 为 2.6） |
| Density Reverse Threshold | 0.245 |
| Density Reverse Max Value | -0.519 |
| Velocity Scale | 4.65 |
| Lerp Range | 0.2 |

渲染器在体积包围盒内做带深度遮挡的 ray marching；2D 密度场沿高度扩展并带高度衰减，3D 模式直接采样 Texture3D。低于反转阈值的密度会反号并受最大反值限制，从而形成视频中的明亮卷边，而主扰动区域形成低密度通道。

`Interaction Response` 是可见度校准项，不替代文档规定的 `Density Scale`。球经过后没有明显排开时，先确认 Debug Window 的 Density 中存在红色轨迹，再把该值从 `3` 提高到 `4–6`；若 Density 面板没有轨迹，则应增大交互源 `Radius`、确认 `Source Type` 包含 `Density` 且 `Add Density = 1`。

## Debug 工具

选中 `FluidSimulation` 后，可使用 Inspector 底部三个文档对应工具：

- `Forces`：列出与当前模拟范围相交的交互源。
- `Reset Simulation`：清空并重新计算当前模拟区域。
- `Show Debug Window`：打开实时中间场窗口。

Debug Window 包含 `Preview Size` 以及 `Density`、`Velocity`、`ExternalForce`、`Pressure`、`Curl` 五个面板。3D 求解默认启用 `Follow Source Height`，切片会跟随交互源的世界高度，避免源离开固定切片后预览变黑；关闭后可手动使用 `3D Y Slice` 查看不同高度。`Interactive Source Type` 支持：

- `None`：不显示扰动源。
- `Intersection`：只显示与当前模拟区域相交的扰动源。
- `All`：显示全部扰动源。

`FluidSimulationDebug2D.unity` 为兼容原快捷键和文件引用保留旧文件名，但内部已升级为 `K3D Eulerian`：真正采样世界 Y 轴，才能让空中喷流受重力下坠，而不是把一张 XZ 密度图沿高度拉伸。场景使用 `Initial Density = 0`、`Density Scale = 2.6`、`Interaction Response = 1.5`、`Density Reverse Threshold = 0`。

默认源为持续火箭喷射：球位于空中 `(Y = 5)`，`Kill If Still = false`，无需推动即可持续产生密度；`Sphere Radius = 0.34`，密度只从球体中心附近生成。`Jet Emission = true`、`Local Jet Direction = (0,-1,0)`、`Jet Speed = 5`、`Jet Spread = 1.1`，因此喷射形状由轴向速度与径向展开速度真实形成，而不是预填充一段尾迹体积。

`FluidSimulation > 3D Gravity And Wind` 提供 `Gravity`、`Environment Wind`、`Environment Wind Response`：默认重力 `(0,-2.5,0)`，默认风速 `(1.4,0,0.35)`。风向由向量方向决定，风速由向量长度决定；运行时也可用方向键调整风的 X/Z 分量。

两种 Geometry 均可验证：按 `1` 选择 Sphere、按 `2` 选择 Cube。Cube 使用 `Cube Size = (0.65,0.65,0.65)`。Geometry 决定密度与速度注入区域，火箭喷射轴仍由 `Local Jet Direction` 决定。

### 密度颜色层次

`Local Volumetric Fog > Density Color` 将流体密度同时映射到颜色和吸光，而不只是透明度：

- `Thin Fog Color`：低密度区域与尾迹边缘颜色，使用 HDR 颜色选择器。
- `Dense Fog Color`：高密度区域与尾迹核心颜色，使用 HDR 颜色选择器。
- `Density Color Strength`：密度进入浓雾色的速度；越大，深色核心越宽。
- `Density Color Contrast`：颜色曲线；小于 `1` 更早变深，大于 `1` 保留更多浅色边缘。
- `Light Absorption`：高密度采样对光线的近似吸收；越大，核心越暗。

源生成 Demo 默认使用浅红橙边缘、暗红核心、`Strength = 1.3`、`Contrast = 0.9`、`Light Absorption = 0.35`。透明度仍由原有 Beer-Lambert 体积透射积分计算。

两项雾色均支持 HDR 强度。需要泛光时可将颜色强度提高到 `1` 以上，并在 URP 相机/Volume 中启用 HDR 与 Bloom；没有 Bloom 时，HDR 仍会参与线性空间的体积颜色累积，但不会自动产生光晕。

可通过 `Tools > Interactive Fog > Open Source Generated Debug Demo` 或快捷键 `F8` 打开该场景。

该场景固定使用 `K2D Eulerian`。按 `1/2`（主键盘或小键盘）可切换 `Sphere/Cube`，每次切换都会重置模拟并在左上角显示反馈；也可以直接点击左上角两个按钮。通过 `Tools > Interactive Fog > Rebuild Source Generated Debug Demo` 可在 Unity 内安全重建该场景；按 `F9` 打开并重新绑定 Debug Window，按 `F10` 将 Density、Velocity、ExternalForce、Pressure、Curl 导出为一张 Debug 快照。
进入 Play Mode 后按 `F9` 可直接重新绑定当前场景的 `FluidSimulation` 并打开 Debug Window。
在 Game 视图中从球体上按住鼠标左键拖动；也可使用 `WASD`。该 Demo 默认关闭自动运动。

源生成 Demo 默认使用持续定向源：`Kill If Still = false`、`Jet Emission = true`、`Jet Speed = 5`、`Jet Spread = 0.35`。因此物体静止时也能从 Geometry 区域沿 `Local Jet Direction` 产生文档示意中的稳定雾柱；拖动物体后，交互速度会叠加到喷流上，并由控制器把本地方向旋转到运动反向，形成可操纵的流星尾迹。

两种 Geometry 的注入轮廓与文档示意一致：Sphere 使用圆形中心源，Cube 使用方形中心源，因此两者都会形成一股尾迹，但尾迹根部轮廓分别为圆形和方形。默认 `Local Jet Direction = (0,0,-1)`；交互物体转向运动方向后，该方向就是运动反向。

## 性能

- 2D 网格尺寸为 `Solver Range × Resolution Scale`，文档示例 `20 × 20`、精度 `20` 对应 `400 × 400`。
- 3D 使用相同含义，但为避免误配导致显存暴涨，`Max 3D Resolution` 会等比限制单轴尺寸；默认 96。
- 体积雾 `Step Count` 建议桌面 64–96，移动端 32–56。
- `Adaptive Step Count` 默认开启：长射线仍使用 `Step Count` 的原采样密度，短射线按实际穿雾距离减少采样；`Minimum Step Count` 控制近景最低质量。关闭该项即可逐像素对比旧的固定步数路径。
- 压力迭代越高越接近不可压缩流体；文档交互示例使用 4。

`InteractiveFogQualityController` 可挂在雾与流体的共同父节点，默认跟随 Unity Quality Settings：

| Unity 质量档 | 雾最大/最小步数 | K2D 网格（40m 范围） | 压力迭代 |
|---|---:|---:|---:|
| Performant / Low | 32 / 16 | 120 × 120 | 2 |
| Balanced / Medium | 44 / 20 | 160 × 160 | 3 |
| High Fidelity / High | 60 / 24 | 240 × 240 | 4 |

关闭 `Follow Unity Quality Level` 后可用 `Manual Quality` 单独切换三级档位。切换流体分辨率会重新创建模拟纹理并清空旧场，因此建议在加载界面或质量设置确认时切换。
