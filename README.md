# xProtoView

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

## 当前编解码行为

- `Message 类型` 为必填项，解码与编码都必须由用户手动输入或选择。
- 提供 `解码（base64->proto）` 按钮：将上方 Base64 解码为下方 proto 文本。
- 提供 `编码（proto->base64）` 按钮：将下方 proto 文本编码为上方 Base64。
- Base64 文本框默认自动换行；编码结果按 76 列自动分行，解码时会自动忽略空白与换行。
- 提供 `YAML 查看` 按钮：将当前 proto 文本转换为 YAML，并在弹窗中显示语法高亮文本与可折叠树视图。
- 不提供自动候选估分能力。
- 不提供预热与缓存管理能力。
- 不提供 `重新加载 proto` 按钮；程序启动和设置保存后会自动重新加载。
- 不提供 `清空结果` 按钮；可直接覆盖输入后再次进行编解码。
- 主界面不使用 Tab；启动后直接显示编解码界面。
- 启动或重载 proto 成功后，不显示“已加载 proto 文件/message 数量”提示。
