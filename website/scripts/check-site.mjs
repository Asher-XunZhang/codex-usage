import assert from 'node:assert/strict'
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { join, resolve, sep } from 'node:path'

// Validate the built site, including GitHub's repository subpath. No network needed.
const root = resolve('.vitepress/dist')
const origin = 'https://asher-xunzhang.github.io'
const base = '/codex-usage/'
const pages = ['index', 'installation', 'troubleshooting', 'user-guide', 'metrics-and-privacy', 'architecture', 'validation', 'development', 'optional-skill']
const files = []
function walk(dir) {
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry)
    if (statSync(path).isDirectory()) walk(path)
    else files.push(path)
  }
}
walk(root)
for (const page of pages) assert.ok(existsSync(join(root, `${page}.html`)), `Missing page: ${page}`)
const anchors = new Map()
function getAnchors(file) {
  if (!anchors.has(file)) {
    anchors.set(file, new Set([...readFileSync(file, 'utf8').matchAll(/\bid="([^"]+)"/g)].map(match => match[1])))
  }
  return anchors.get(file)
}
let checked = 0
for (const file of files.filter(file => file.endsWith('.html'))) {
  const html = readFileSync(file, 'utf8')
  assert.match(html, /<html[^>]*lang="zh-CN"/, `Missing Chinese language: ${file}`)
  assert.match(html, /<title>[^<]+<\/title>/, `Missing title: ${file}`)
  assert.ok(!html.includes('github.com/Asher-XunZhang/codex-usage/wiki/'), `Unmigrated Wiki link: ${file}`)
  assert.ok(!html.includes('/Users/mac/'), `Local machine path in ${file}`)
  const pageUrl = origin + base + file.slice(root.length + 1).split(sep).join('/')
  for (const match of html.matchAll(/<(?:a|link|script|img)\b[^>]*?\b(?:href|src)="([^"]+)"/g)) {
    const href = match[1].replaceAll('&amp;', '&')
    if (/^(?:data:|mailto:|tel:)/.test(href)) continue
    const url = new URL(href, pageUrl)
    if (url.origin !== origin) continue
    assert.ok(url.pathname.startsWith(base), `Escapes Pages subpath: ${href} in ${file}`)
    let target = resolve(root, decodeURIComponent(url.pathname.slice(base.length)))
    assert.ok(target === root || target.startsWith(root + sep), `Escapes site directory: ${href}`)
    if (url.pathname.endsWith('/')) target = join(target, 'index.html')
    assert.ok(existsSync(target), `Broken link/asset: ${href} in ${file}`)
    if (url.hash && target.endsWith('.html')) {
      const id = decodeURIComponent(url.hash.slice(1))
      assert.ok(getAnchors(target).has(id), `Missing anchor ${id} in ${target}`)
    }
    checked++
  }
}
const total = files.reduce((sum, path) => sum + statSync(path).size, 0)
console.log(`Checked ${pages.length} documentation pages, ${checked} local links/assets, ${(total / 1024 / 1024).toFixed(2)} MiB static output.`)
