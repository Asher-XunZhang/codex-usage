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

文档位于 `website/content/`，导航与主题位于 `website/.vitepress/`。站点分别提供 Windows 与 macOS 的使用和下载入口；历史 macOS 截图读取 `docs/macos/images/`，共享图标读取 `docs/common/images/`。配置在启动或构建时汇集临时资源，保留原有站点图片 URL。截图必须标明平台与快照，只使用演示数据。依赖固定在 `website/pnpm-lock.yaml`，生成目录和 node_modules 不提交，也不放进 App 安装包。

VitePress 使用 MIT 许可证；完整依赖版本保留在锁文件中。`pnpm-workspace.yaml` 将 Vite 固定为已修复已知开发服务器漏洞的 6.4.3，覆盖 VitePress 1.x 默认的旧版依赖；修改后应重新验证开发服务、构建和链接检查。Mermaid 的 Chevrotain 间接依赖也固定到已修复的 lodash-es 4.18.1。这些依赖只服务文档站，不加入 App。

## Mermaid 流程图

在 Markdown 中保留标准 `mermaid` 代码块，站点将其挂载为图表组件。渲染器仅在打开含图表的页面时加载，随站点深浅主题更新；内部导航返回页面时会重新挂载。生成 SVG 使用严格安全模式，不请求第三方图表服务，也不使用外部 CDN。

图表应包含 `accTitle` 与 `accDescr`，并保持较窄的布局；小屏在图表框内横向滚动。加载失败时显示提示，并保留折叠的图表文本。更新图表后除了 `pnpm build` / `pnpm check`，还应在浏览器检查实际 SVG、主题切换和离开后返回；静态检查不证明客户端绘制成功。

## 发布流程

通过 PR 修改文档。PR 只做构建与链接检查，不发布站点。主分支合并相关文档改动后，`Documentation` workflow 构建并发布到 Pages；也可在主分支手动触发。

仓库 Pages 的发布来源须为 **GitHub Actions**。工作流仅在部署 job 请求 `pages: write` 和 `id-token: write`，使用 `github-pages` environment；该环境应只允许指定的发布分支。不要用 `pull_request_target` 来构建或发布外部 PR，也不要添加个人 Token。

`push.branches`、`deploy.if` 和 `github-pages` 环境的部署分支规则均仅允许 `main`。手动触发非主分支时只进行构建，不发布。不要为普通 PR 放宽环境的分支限制。

## 路径与内容约定

- GitHub 项目站点的根路径固定为 `/codex-usage/`，在配置中设置 `base`。
- Markdown 页面使用相对 `.md` 链接；构建后会成为 `.html`。不使用依赖服务器重写的 clean URLs。
- `pnpm check` 检查全部文档页面、站内锚点和资源路径，防止子路径部署后出现空白页面或 404。
- 安装页为面向用户的安装步骤；`docs/macos/INSTALL.md` 保留仓库与发行包可离线阅读的安装说明。变更安装流程时应同时核对两处；主页只链接安装页，不再复制安装命令。
- 当前下载与功能说明对应 `v1.0.1`，包含 PR #3 的额度弧线配色；源码链接固定到发行 tag。历史 v1.0.0 实机、内存与安装助手验证保留原提交和环境边界，不能改写成 v1.0.1 的新实测。
- 安装入口固定到 Release 的 `install.sh` 资产，并提供 `install.sh.sha256`；先确定发行 ZIP，再生成助手固定摘要并上传，随后独立更新主线脚本。发行 tag 内的安装脚本保留旧版构建快照，不能用于安装新版 ZIP。安装页链接发行的 `.zip.sha256`，不复制旧版摘要，也不替换同名发行包。
- 此站不依赖 GitHub Wiki 的初始化。未来若恢复 Wiki，应明确一处为主文档，避免长期维护两个独立副本。

免费方案仍受 [GitHub Pages 使用限制](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits)约束；不要启用收费 runner、付费服务或把 App ZIP 放入站点构建产物。
