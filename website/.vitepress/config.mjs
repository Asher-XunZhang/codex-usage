import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitepress'

export default defineConfig({
  lang: 'zh-CN',
  title: 'Codex 用量',
  description: 'Codex 用量 macOS 使用文档：Intel 与 Apple Silicon 安装、主面板、菜单栏、圆形浮窗、统计口径和问题排查。',
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
    publicDir: fileURLToPath(new URL('../../docs/images/', import.meta.url))
  },
  themeConfig: {
    logo: '/icon.png',
    siteTitle: 'Codex 用量 · 文档',
    nav: [
      { text: 'v1.0.1 下载', link: 'https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.1' }
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/Asher-XunZhang/codex-usage' }],
    sidebar: [
      { text: '开始使用', items: [
        { text: '概览', link: '/' },
        { text: '安装与首次启动', link: '/installation' },
        { text: 'Intel 与其他问题排查', link: '/troubleshooting' }
      ] },
      { text: '使用文档', items: [
        { text: '主面板、菜单栏与浮窗', link: '/user-guide' },
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
