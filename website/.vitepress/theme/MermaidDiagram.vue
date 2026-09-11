<script setup>
import { onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import { useData } from 'vitepress'
import { renderDiagram } from './mermaid.mjs'

const props = defineProps({ source: { type: String, required: true } })
const { isDark } = useData()
const svg = ref('')
const failed = ref(false)
const busy = ref(true)
const id = `diagram-${useId().replace(/[^a-zA-Z0-9-]/g, '')}`
let revision = 0
let mounted = false

async function update() {
  if (!mounted) return
  const current = ++revision
  const isCurrent = () => mounted && revision === current
  busy.value = true
  failed.value = false
  try {
    const result = await renderDiagram(`${id}-${current}`, props.source, isDark.value, isCurrent)
    if (result && isCurrent()) svg.value = result
  } catch (error) {
    if (isCurrent()) {
      failed.value = true
      console.error('Mermaid diagram could not render', error)
    }
  } finally {
    if (isCurrent()) busy.value = false
  }
}

onMounted(() => { mounted = true; update() })
watch([() => props.source, isDark], update)
onBeforeUnmount(() => { mounted = false; revision++ })
</script>

<template>
  <div class="mermaid-diagram" :aria-busy="busy">
    <p v-if="failed" role="alert">图表暂时无法显示，请刷新页面重试。下方可查看图表文本。</p>
    <p v-else-if="!svg" role="status">正在绘制图表…</p>
    <div v-if="svg" class="mermaid-canvas" tabindex="0" aria-label="流程图，可横向滚动" v-html="svg" />
    <details :open="failed">
      <summary>查看图表文本</summary>
      <pre>{{ source }}</pre>
    </details>
  </div>
</template>

<style scoped>
.mermaid-diagram { margin: 24px 0; }
.mermaid-canvas { overflow-x: auto; border: 1px solid var(--vp-c-divider); border-radius: 8px; padding: 20px 12px; }
.mermaid-canvas :deep(svg) { display: block; min-width: 640px; margin: auto; height: auto; }
details { margin-top: 10px; color: var(--vp-c-text-2); font-size: .875rem; }
summary { cursor: pointer; width: fit-content; }
pre { overflow-x: auto; padding: 12px; line-height: 1.6; font-size: .875rem; }
</style>
