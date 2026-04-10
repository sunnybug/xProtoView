# xProtoView

## 当前版本

- `0.1.2`

## 目录结构

- `src/` 主程序源码
- `tests/` 单元测试
- `script/` 构建、发布、初始化脚本
- `.temp/` 编译中间文件与输出
- `.run/` 运行目录（日志与配置）
- `.dist/` 本地发布产物
- `doc/` 项目文档
- `aidoc/` AI 生成文档

## 常用命令

- 初始化：`pwsh ./script/init_dev.ps1`
- 构建 Debug：`pwsh ./script/build.ps1`
- 构建 Release：`pwsh ./script/build.ps1 --release`
- 运行：`pwsh ./0run.ps1`
- 运行测试：`pwsh ./0run.ps1 --test`
- 发布到 `.dist/`：`pwsh ./script/publish.ps1`

## GitHub 自动发布

- 工作流文件：`.github/workflows/publish.yml`
- 触发条件：`src/xProtoView/xProtoView.csproj` 被推送变更。
- 发布规则：读取 `<Version>` 生成 tag（格式 `v版本号`）；若远端不存在同名 tag，则自动构建、打包 zip、推送 tag 并创建 GitHub Release。
- 幂等行为：若远端已存在同名 tag，则跳过发布，避免重复发布同一版本。

## 当前编解码行为

- `Message 类型` 为必填项，解码与编码都必须由用户手动输入或选择。
- `Message 类型` 下拉框支持输入快速过滤（忽略大小写匹配），便于在大量类型中定位。
- 提供 `解码（base64->proto）` 按钮：将上方 Base64 解码为下方 proto 文本。
- 提供 `编码（proto->base64）` 按钮：将下方 proto 文本编码为上方 Base64。
- `Message 类型` 下拉框与编解码/YAML 按钮位于同一行，便于连续操作。
- 顶栏新增 `帮助` 菜单，包含 `关于` 与 `更新` 两项。
- `关于` 会显示工程名、GitHub 地址、作者与当前版本号。
- 程序启动时会自动检查 GitHub 最新 Release；只有检测到更高版本且存在可下载 zip 更新包时，`更新` 菜单才会显示。
- 点击 `更新` 后会二次确认；确认后下载最新版本到系统临时目录，程序关闭后自动替换（保留 `config` 目录）并重启到新版本。
- 修复主界面操作行布局，`proto->base64` 与 `YAML 查看` 按钮在常规窗口尺寸下始终可见。
- 修复主界面上下分割条初始化越界问题：最小面板尺寸与 `SplitterDistance` 会在布局完成后按窗口可用空间安全应用，程序启动时不会因此直接退出。
- Base64 文本框默认自动换行；编码结果按 76 列自动分行，解码时会自动忽略空白与换行。
- 提供 `YAML 查看` 按钮：将当前 proto 文本转换为 YAML，并在弹窗中显示语法高亮文本与可折叠树视图。
- 自动记住主窗口大小、位置和最大化状态，重启后自动恢复。
- 自动记住“设置”窗口大小、位置和最大化状态，重启后自动恢复。
- 自动记住 YAML 预览窗口大小、位置和最大化状态，以及左右分栏拖拽位置，重启后自动恢复。
