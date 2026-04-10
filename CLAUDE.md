# 项目规则同步

本项目采用统一目录结构：

- `src/` 代码目录
- `tests/` 测试目录
- `script/` 自动化脚本
- `.temp/` 构建中间目录
- `.run/` 运行工作目录（`log/` 和 `config/`）
- `.dist/` 本地发布目录

推荐入口命令：

- `pwsh ./0run.ps1`
- `pwsh ./0run.ps1 --test`
