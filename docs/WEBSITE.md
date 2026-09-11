# 文档站维护

公开文档站：https://asher-xunzhang.github.io/codex-usage/

站点使用 GitHub Pages 免费托管，构建在公开仓库的标准 Ubuntu GitHub Actions runner 上运行。无需购买域名、服务器或第三方搜索服务；App 安装包继续放在 Releases。文档站没有账号系统，不接入用量数据。

## 本地开发与检查

安装 Node.js 24 和 pnpm 11.19.0 后，在仓库根目录运行：

```sh
cd website
pnpm install --frozen-lockfile --ignore-scripts
pnpm dev
```

发布前执行：

```sh
pnpm build
pnpm check
pnpm preview
```

文档位于 `website/content/`，导航与主题位于 `website/.vitepress/`。文档截图共用 `docs/images/`，构建时复制到静态站点；只使用演示数据。依赖固定在 `website/pnpm-lock.yaml`，生成目录和 node_modules 不提交，也不放进 App 安装包。

VitePress 使用 MIT 许可证；完整依赖版本保留在锁文件中。`pnpm-workspace.yaml` 将 Vite 固定为已修复已知开发服务器漏洞的 6.4.3，覆盖 VitePress 1.x 默认的旧版依赖；修改后应重新验证开发服务、构建和链接检查。这些是文档构建依赖，不是 App 的常驻组件。

## 发布流程

通过 PR 修改文档。PR 只做构建与链接检查，不发布站点。主分支合并相关文档改动后，`Documentation` workflow 构建并发布到 Pages；也可在主分支手动触发。

仓库 Pages 的发布来源须为 **GitHub Actions**。工作流仅在部署 job 请求 `pages: write` 和 `id-token: write`，使用 `github-pages` environment；该环境应只允许指定的发布分支。不要用 `pull_request_target` 来构建或发布外部 PR，也不要添加个人 Token。

首次上线可以短暂允许明确指定的 `codex/github-pages-docs` 分支，以便在 PR 合并前验证正式站点。上线后同时移除工作流 `push.branches` 中的临时分支、`deploy.if` 中的临时分支条件，再移除环境中的临时分支许可，后续仅由 `main` 发布。

## 路径与内容约定

- GitHub 项目站点的根路径固定为 `/codex-usage/`，在配置中设置 `base`。
- Markdown 页面使用相对 `.md` 链接；构建后会成为 `.html`。不使用依赖服务器重写的 clean URLs。
- `pnpm check` 检查全部文档页面、站内锚点和资源路径，防止子路径部署后出现空白页面或 404。
- 安装页为面向用户的安装步骤；`docs/INSTALL.md` 保留仓库与发行包可离线阅读的安装说明。变更安装流程时应同时核对两处；主页只链接安装页，不再复制安装命令。
- v1.0.0 App 的功能与验证仍对应 `53c9cf4`，安装助手固定到 `d6a9407`。站点上线不代表 App 重新发布；不要混入未发布分支的功能、改写实测结果或替换同名发行包。
- 此站不依赖 GitHub Wiki 的初始化。未来若恢复 Wiki，应明确一处为主文档，避免长期维护两个独立副本。

免费方案仍受 [GitHub Pages 使用限制](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits)约束；不要启用收费 runner、付费服务或把 App ZIP 放入站点构建产物。
