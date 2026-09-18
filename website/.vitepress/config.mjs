import { fileURLToPath } from 'node:url'
import { copyFileSync, existsSync, mkdirSync, readdirSync, realpathSync, rmSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { defineConfig } from 'vitepress'

// Preserve published image URLs while keeping source assets with their platform.
const assetCache = resolve(fileURLToPath(new URL('./cache/', import.meta.url)))
const publicImages = join(assetCache, 'documentation-images')
mkdirSync(assetCache, { recursive: true })
if (dirname(realpathSync(assetCache)) !== realpathSync(dirname(fileURLToPath(import.meta.url)))) {
  throw new Error('Documentation asset cache must stay inside the VitePress directory')
}
if (existsSync(publicImages) && dirname(realpathSync(publicImages)) !== realpathSync(assetCache)) {
  throw new Error('Documentation asset staging must stay inside the VitePress cache')
}
rmSync(publicImages, { recursive: true, force: true })
mkdirSync(publicImages)
for (const directory of ['../../docs/macos/images/', '../../docs/common/images/']) {
  const source = fileURLToPath(new URL(directory, import.meta.url))
  for (const entry of readdirSync(source, { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith('.png')) continue
    const target = join(publicImages, entry.name)
    if (existsSync(target)) throw new Error(`Duplicate documentation image: ${entry.name}`)
    copyFileSync(join(source, entry.name), target)
  }
}

export default defineConfig({
  lang: 'zh-CN',
  title: 'Codex 用量',
  description: 'Codex 用量 Windows 与 macOS 使用文档：下载安装、Token 统计、预算提醒、任务监控、桌面浮窗和原生系统入口。',
  srcDir: 'content',
  base: '/codex-usage/',
  cleanUrls: false,
  appearance: true,
  markdown: {
    config(md) {
      const fence = md.renderer.rules.fence
      md.renderer.rules.fence = (tokens, index, options, env, self) => {
        if (tokens[index].info.trim() === 'mermaid') {
          const source = md.utils.escapeHtml(JSON.stringify(tokens[index].content))
          return `<MermaidDiagram :source="${source}" />\n`
        }
        return fence(tokens, index, options, env, self)
      }
    }
  },
  head: [
    ['link', { rel: 'icon', type: 'image/png', href: '/codex-usage/icon.png' }],
    ['meta', { name: 'theme-color', content: '#087c63' }]
  ],
  vite: {
    publicDir: publicImages
  },
  themeConfig: {
    logo: '/icon.png',
    siteTitle: 'Codex 用量 · 文档',
    nav: [
      { text: '下载安装', link: '/installation' },
      { text: '平台差异', link: '/platforms' },
      { text: '发行版本', items: [
        { text: 'Windows · v1.0.2', link: 'https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.2' },
        { text: 'macOS · v1.0.3', link: 'https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.3' }
      ] }
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/Asher-XunZhang/codex-usage' }],
    sidebar: [
      { text: '开始使用', items: [
        { text: '概览', link: '/' },
        { text: '安装与首次启动', link: '/installation' },
        { text: '平台支持与功能差异', link: '/platforms' },
        { text: '问题排查', link: '/troubleshooting' }
      ] },
      { text: '使用文档', items: [
        { text: 'Windows 使用指南', link: '/windows-guide' },
        { text: 'macOS 使用指南', link: '/user-guide' },
        { text: '统计口径与隐私', link: '/metrics-and-privacy' },
        { text: '可选 Token 统计技能', link: '/optional-skill' }
      ] },
      { text: '了解项目', items: [
        { text: '架构与内存', link: '/architecture' },
        { text: '验证与兼容性', link: '/validation' },
        { text: '开发与发布', link: '/development' }
      ] }
    ],
    outline: { level: [2, 3], label: '本页目录' },
    docFooter: { prev: '上一页', next: '下一页' },
    darkModeSwitchLabel: '外观',
    darkModeSwitchTitle: '切换到深色模式',
    lightModeSwitchTitle: '切换到浅色模式',
    sidebarMenuLabel: '文档目录',
    returnToTopLabel: '返回顶部',
    externalLinkIcon: true,
    notFound: { title: '页面不存在', quote: '可以从文档首页继续查找安装、使用和问题排查指南。', linkLabel: '返回文档首页', linkText: '返回文档首页' }
  }
})
