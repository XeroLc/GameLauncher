# 3.0 云雾磨砂玻璃 UI 全面升级 Spec

## Why
当前应用使用 MicaBackdrop 和标准 WinUI 3 主题资源，视觉风格偏平淡，缺乏层次感和现代感。3.0 大版本更新旨在将界面升级为云雾质感、磨砂玻璃效果和半透明视觉特性的设计语言，提升视觉品质和用户体验。

## What Changes
- 将主窗口背景从 MicaBackdrop 升级为 DesktopAcrylicBackdrop（磨砂玻璃效果）
- 创建全局设计令牌（Design Tokens）资源字典，统一管理云雾质感相关的颜色、透明度、模糊参数
- 重新设计游戏卡片样式，使用半透明磨砂背景 + 微妙边框 + 圆角 + 阴影
- 升级 ContentDialog 弹窗样式，实现磨砂玻璃背景效果
- 重新设计顶部导航栏，使用半透明磨砂背景
- 优化标签（Tag）样式，使用半透明磨砂胶囊设计
- 添加云雾渐变装饰层（装饰性渐变叠加层），营造云雾氛围感
- 升级按钮样式，区分主要/次要/强调按钮的磨砂质感
- 优化空状态页面视觉
- 更新版本号水印为 v3.0
- **BREAKING**: 移除对 MicaBackdrop 的使用，全面切换为 Acrylic 磨砂玻璃

## Impact
- Affected specs: 主窗口视觉、对话框视觉、卡片组件、标签组件、按钮组件、导航栏
- Affected code:
  - `App.xaml` — 新增全局资源字典和设计令牌
  - `MainWindow.xaml` — 主窗口布局和样式全面更新
  - `MainWindow.xaml.cs` — 版本号更新、悬停效果适配
  - `Views/AddGameDialog.xaml` — 弹窗样式升级
  - `Views/GameDetailDialog.xaml` — 弹窗样式升级

## ADDED Requirements

### Requirement: 全局设计令牌系统
系统 SHALL 在 App.xaml 中定义一套统一的设计令牌资源字典，包含以下类别：

#### Scenario: 设计令牌定义完整
- **WHEN** 应用启动加载 App.xaml
- **THEN** 以下令牌资源 SHALL 可用：
  - 磨砂玻璃相关：`FrostedGlassBackgroundBrush`（半透明磨砂背景）、`FrostedGlassCardBrush`（卡片磨砂背景）、`FrostedGlassDialogBrush`（弹窗磨砂背景）
  - 颜色令牌：`CloudMistTint`（云雾色调）、`CloudMistTintOpacity`（云雾色调透明度）、`FrostBorderColor`（磨砂边框色）
  - 圆角令牌：`CardCornerRadius`（卡片圆角 16px）、`DialogCornerRadius`（弹窗圆角 12px）、`TagCornerRadius`（标签圆角 14px）
  - 间距令牌：`CardPadding`（卡片内边距）、`SectionSpacing`（区域间距）
  - 阴影令牌：通过 `ThemeShadow` 实现卡片和弹窗的深度感

### Requirement: 磨砂玻璃窗口背景
系统 SHALL 使用 DesktopAcrylicBackdrop 作为主窗口背景，替代现有的 MicaBackdrop。

#### Scenario: 窗口背景显示磨砂玻璃效果
- **WHEN** 用户打开应用
- **THEN** 主窗口背景 SHALL 呈现磨砂玻璃（Acrylic）效果，桌面内容透过窗口可见并带有模糊处理

### Requirement: 云雾渐变装饰层
系统 SHALL 在主窗口中添加装饰性渐变叠加层，营造云雾氛围感。

#### Scenario: 云雾装饰层可见
- **WHEN** 用户查看主窗口
- **THEN** 窗口内 SHALL 有一个或多个半透明渐变叠加层，呈现柔和的云雾流动感
- **AND** 装饰层 SHALL 不影响交互操作（IsHitTestVisible=False）

### Requirement: 半透明磨砂游戏卡片
系统 SHALL 将游戏卡片重新设计为半透明磨砂玻璃风格。

#### Scenario: 游戏卡片呈现磨砂质感
- **WHEN** 用户查看游戏列表
- **THEN** 每张游戏卡片 SHALL 具有以下视觉特性：
  - 半透明磨砂背景（约 0.6-0.7 透明度的白色/深色底）
  - 微妙的半透明边框（约 0.15 透明度）
  - 16px 圆角
  - 悬停时边框高亮为主题强调色，背景透明度微增
  - 卡片底部启动按钮使用强调色磨砂风格

#### Scenario: 卡片悬停交互
- **WHEN** 用户将鼠标悬停在游戏卡片上
- **THEN** 卡片边框 SHALL 变为主题强调色
- **AND** 卡片背景 SHALL 微微变亮（透明度降低约 0.1）

### Requirement: 磨砂玻璃弹窗样式
系统 SHALL 将所有 ContentDialog 弹窗升级为磨砂玻璃风格。

#### Scenario: 弹窗呈现磨砂玻璃效果
- **WHEN** 用户打开添加游戏、编辑游戏、游戏详情或确认删除弹窗
- **THEN** 弹窗背景 SHALL 呈现磨砂玻璃效果（约 0.8 透明度）
- **AND** 弹窗边框 SHALL 为半透明微妙边框
- **AND** 弹窗圆角 SHALL 为 12px

### Requirement: 半透明磨砂导航栏
系统 SHALL 将顶部导航栏重新设计为半透明磨砂风格。

#### Scenario: 导航栏呈现磨砂质感
- **WHEN** 用户查看主窗口顶部区域
- **THEN** 导航栏 SHALL 具有半透明磨砂背景
- **AND** 导航栏 SHALL 与内容区域有微妙的分隔线

### Requirement: 半透明磨砂标签样式
系统 SHALL 将标签（Tag）组件重新设计为半透明磨砂胶囊风格。

#### Scenario: 标签呈现磨砂胶囊效果
- **WHEN** 用户查看游戏卡片或详情页中的标签
- **THEN** 标签 SHALL 具有半透明磨砂背景
- **AND** 标签 SHALL 为胶囊形状（14px 圆角）
- **AND** 标签边框 SHALL 为半透明微妙边框

### Requirement: 按钮样式升级
系统 SHALL 升级按钮样式，使所有按钮呈现磨砂质感。

#### Scenario: 按钮呈现磨砂质感
- **WHEN** 用户查看界面中的按钮
- **THEN** 默认按钮 SHALL 具有半透明磨砂背景
- **AND** 强调按钮 SHALL 具有主题色半透明磨砂背景
- **AND** 按钮悬停时 SHALL 有微妙的光晕效果

### Requirement: 空状态页面视觉升级
系统 SHALL 优化空状态页面的视觉表现。

#### Scenario: 空状态页面呈现云雾风格
- **WHEN** 游戏列表为空
- **THEN** 空状态提示 SHALL 使用磨砂玻璃卡片包裹
- **AND** 图标和文字 SHALL 有柔和的半透明效果

### Requirement: 版本号更新
系统 SHALL 将版本号从 v2.1.1 更新为 v3.0。

#### Scenario: 版本号显示正确
- **WHEN** 用户查看右下角版本水印
- **THEN** 版本号 SHALL 显示为 "v3.0"

## MODIFIED Requirements

### Requirement: 主题切换功能
现有主题切换功能 SHALL 继续支持，但磨砂玻璃效果在深色/浅色模式下均需正确适配。深色模式下磨砂背景使用深色半透明底，浅色模式下使用浅色半透明底。

## REMOVED Requirements

### Requirement: MicaBackdrop 窗口背景
**Reason**: 3.0 版本全面采用 Acrylic 磨砂玻璃效果，Mica 不再满足设计需求
**Migration**: 将 `MicaBackdrop` 替换为 `DesktopAcrylicBackdrop`
