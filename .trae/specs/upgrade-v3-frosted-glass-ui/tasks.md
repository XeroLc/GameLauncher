# Tasks

- [x] Task 1: 在 App.xaml 中创建全局设计令牌资源字典
  - [x] SubTask 1.1: 定义磨砂玻璃画刷资源（FrostedGlassBackgroundBrush、FrostedGlassCardBrush、FrostedGlassDialogBrush），使用 AcrylicBrush 或 SolidColorBrush 配合透明度
  - [x] SubTask 1.2: 定义颜色令牌（CloudMistTint、FrostBorderColor 等），区分深色/浅色模式
  - [x] SubTask 1.3: 定义圆角令牌（CardCornerRadius=16、DialogCornerRadius=12、TagCornerRadius=14）
  - [x] SubTask 1.4: 定义自定义按钮样式（FrostedDefaultButtonStyle、FrostedAccentButtonStyle），使用半透明磨砂背景
  - [x] SubTask 1.5: 定义自定义标签样式（FrostedTagStyle），使用半透明磨砂胶囊设计
  - [x] SubTask 1.6: 更新 DefaultContentDialogStyle 为磨砂玻璃风格

- [x] Task 2: 升级 MainWindow.xaml 主窗口视觉
  - [x] SubTask 2.1: 将 MicaBackdrop 替换为 DesktopAcrylicBackdrop
  - [x] SubTask 2.2: 添加云雾渐变装饰层（Grid 内添加半透明渐变 Border，IsHitTestVisible=False）
  - [x] SubTask 2.3: 重新设计顶部导航栏，使用半透明磨砂背景和微妙分隔线
  - [x] SubTask 2.4: 重新设计游戏卡片 DataTemplate，使用磨砂玻璃背景、16px 圆角、半透明边框
  - [x] SubTask 2.5: 升级卡片内启动按钮为磨砂强调风格
  - [x] SubTask 2.6: 优化空状态页面，使用磨砂玻璃卡片包裹
  - [x] SubTask 2.7: 更新版本号水印为 v3.0
  - [x] SubTask 2.8: 将导航栏按钮样式替换为自定义磨砂按钮样式

- [x] Task 3: 升级 MainWindow.xaml.cs 交互逻辑
  - [x] SubTask 3.1: 更新 GameCard_PointerEntered/Exited 中的悬停效果，适配磨砂卡片样式（使用自定义资源而非硬编码颜色）
  - [x] SubTask 3.2: 更新版本号和更新日志内容，添加 v3.0 条目

- [x] Task 4: 升级 AddGameDialog.xaml 弹窗视觉
  - [x] SubTask 4.1: 应用磨砂玻璃弹窗样式
  - [x] SubTask 4.2: 将标签样式替换为 FrostedTagStyle
  - [x] SubTask 4.3: 将按钮样式替换为磨砂按钮样式
  - [x] SubTask 4.4: 优化预览图边框为半透明磨砂风格

- [x] Task 5: 升级 GameDetailDialog.xaml 弹窗视觉
  - [x] SubTask 5.1: 应用磨砂玻璃弹窗样式
  - [x] SubTask 5.2: 将标签样式替换为 FrostedTagStyle
  - [x] SubTask 5.3: 将按钮样式替换为磨砂按钮样式
  - [x] SubTask 5.4: 优化信息面板边框为半透明磨砂风格
  - [x] SubTask 5.5: 优化预览图悬停覆盖层为磨砂玻璃效果

- [x] Task 6: 构建验证
  - [x] SubTask 6.1: 执行项目构建，确保无编译错误
  - [x] SubTask 6.2: 验证深色/浅色模式下磨砂玻璃效果均正确显示

# Task Dependencies
- [Task 2] depends on [Task 1] — 主窗口样式依赖全局设计令牌
- [Task 3] depends on [Task 2] — 交互逻辑适配依赖样式更新
- [Task 4] depends on [Task 1] — 弹窗样式依赖全局设计令牌
- [Task 5] depends on [Task 1] — 弹窗样式依赖全局设计令牌
- [Task 6] depends on [Task 2, Task 3, Task 4, Task 5] — 构建验证依赖所有样式和逻辑更新完成
