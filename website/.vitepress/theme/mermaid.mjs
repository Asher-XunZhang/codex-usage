// Mermaid has global configuration: serialize render jobs across diagrams.
// Loading it here keeps the renderer off pages that contain no diagrams.
let renderer
let queue = Promise.resolve()

export function renderDiagram(id, source, dark, isCurrent) {
  const job = queue.then(async () => {
    if (!isCurrent()) return null
    renderer ??= import('mermaid').then(module => module.default)
    const mermaid = await renderer
    if (!isCurrent()) return null
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      suppressErrorRendering: true,
      theme: 'base',
      look: 'classic',
      themeVariables: {
        darkMode: dark,
        primaryColor: dark ? '#203a32' : '#eaf6f0',
        primaryTextColor: dark ? '#edf2f5' : '#22282d',
        primaryBorderColor: dark ? '#62dbb3' : '#087c63',
        lineColor: dark ? '#aab6c0' : '#5d6872',
        edgeLabelBackground: dark ? '#14171a' : '#ffffff',
        tertiaryColor: dark ? '#1e2529' : '#f7f8f9'
      },
      fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
      flowchart: { htmlLabels: false, useMaxWidth: true }
    })
    const { svg } = await mermaid.render(id, source)
    return isCurrent() ? svg : null
  })
  queue = job.catch(() => {})
  return job
}
